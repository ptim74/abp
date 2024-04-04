using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using IdentityModel.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Volo.Abp.AspNetCore.Components.WebAssembly;

public class WebAssemblyAuthenticationStateProvider<TRemoteAuthenticationState, TAccount, TProviderOptions> : RemoteAuthenticationService<TRemoteAuthenticationState, TAccount, TProviderOptions>
    where TRemoteAuthenticationState : RemoteAuthenticationState
    where TProviderOptions : new()
    where TAccount : RemoteUserAccount
{
    protected ILogger<RemoteAuthenticationService<TRemoteAuthenticationState, TAccount, TProviderOptions>> Logger { get; }
    protected WebAssemblyCachedApplicationConfigurationClient WebAssemblyCachedApplicationConfigurationClient { get; }
    protected IOptions<WebAssemblyAuthenticationStateProviderOptions> WebAssemblyAuthenticationStateProviderOptions { get; }
    protected IHttpClientFactory HttpClientFactory { get; }

    protected readonly static ConcurrentDictionary<string, string> AccessTokens = new ConcurrentDictionary<string, string>();

    public WebAssemblyAuthenticationStateProvider(
        IJSRuntime jsRuntime,
        IOptionsSnapshot<RemoteAuthenticationOptions<TProviderOptions>> options,
        NavigationManager navigation,
        AccountClaimsPrincipalFactory<TAccount> accountClaimsPrincipalFactory,
        ILogger<RemoteAuthenticationService<TRemoteAuthenticationState, TAccount, TProviderOptions>>? logger,
        WebAssemblyCachedApplicationConfigurationClient webAssemblyCachedApplicationConfigurationClient,
        IOptions<WebAssemblyAuthenticationStateProviderOptions> webAssemblyAuthenticationStateProviderOptions,
        IHttpClientFactory httpClientFactory)
        : base(jsRuntime, options, navigation, accountClaimsPrincipalFactory, logger)
    {
        Logger = logger ?? NullLogger<RemoteAuthenticationService<TRemoteAuthenticationState, TAccount, TProviderOptions>>.Instance;

        WebAssemblyCachedApplicationConfigurationClient = webAssemblyCachedApplicationConfigurationClient;
        WebAssemblyAuthenticationStateProviderOptions = webAssemblyAuthenticationStateProviderOptions;
        HttpClientFactory = httpClientFactory;

        AuthenticationStateChanged += async state =>
        {
            var user = await state;
            if (user.User.Identity == null || !user.User.Identity.IsAuthenticated)
            {
                return;
            }

            var accessToken = await FindAccessTokenAsync();
            if (!accessToken.IsNullOrWhiteSpace())
            {
                AccessTokens.TryAdd(accessToken, accessToken);
            }

            await TryRevokeOldAccessTokensAsync();
        };
    }

    public async override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var state = await base.GetAuthenticationStateAsync();
        var applicationConfigurationDto = await WebAssemblyCachedApplicationConfigurationClient.GetAsync();
        if (state.User.Identity != null && state.User.Identity.IsAuthenticated && !applicationConfigurationDto.CurrentUser.IsAuthenticated)
        {
            await WebAssemblyCachedApplicationConfigurationClient.InitializeAsync();
        }

        var accessToken = await FindAccessTokenAsync();
        if (!accessToken.IsNullOrWhiteSpace())
        {
            AccessTokens.TryAdd(accessToken, accessToken);
        }

        await TryRevokeOldAccessTokensAsync();

        return state;
    }

    protected virtual async Task<string?> FindAccessTokenAsync()
    {
        var result = await RequestAccessToken();
        if (result.Status != AccessTokenResultStatus.Success)
        {
            return null;
        }

        result.TryGetToken(out var token);
        return token?.Value;
    }

    protected virtual async Task TryRevokeOldAccessTokensAsync()
    {
        if (AccessTokens.Count <= 1)
        {
            return;
        }

        var oidcProviderOptions = Options.ProviderOptions?.As<OidcProviderOptions>();
        var authority = oidcProviderOptions?.Authority;
        var clientId = oidcProviderOptions?.ClientId;

        if (authority.IsNullOrWhiteSpace() || clientId.IsNullOrWhiteSpace())
        {
            return;
        }

        var revokeAccessTokens = AccessTokens.Select(x => x.Value);
        var currentAccessToken = await FindAccessTokenAsync();
        foreach (var accessToken in revokeAccessTokens)
        {
            if (accessToken == currentAccessToken)
            {
                continue;
            }

            var httpClient = HttpClientFactory.CreateClient(nameof(WebAssemblyAuthenticationStateProvider<TRemoteAuthenticationState, TAccount, TProviderOptions>));
            var result = await httpClient.RevokeTokenAsync(new TokenRevocationRequest
            {
                Address = authority.EnsureEndsWith('/') + WebAssemblyAuthenticationStateProviderOptions.Value.TokenRevocationUrl,
                ClientId = clientId,
                Token = accessToken,
            });

            if (!result.IsError)
            {
                AccessTokens.TryRemove(accessToken, out _);
            }
            else
            {
                Logger.LogError(result.Raw);
            }
        }
    }
}

internal class OidcUser
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }
}
