using System.Text.Json;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Features.Identity;

namespace Sol.Infrastructure.Identity;

/// <summary>
/// GitHub OAuth web-application flow. Provider-specific HTTP and profile normalization stays here;
/// the account tables only see a stable provider key and subject.
/// </summary>
internal sealed class GitHubExternalLoginProvider(
    IHttpClientFactory clients,
    IOptions<AuthenticationOptions> configuredOptions)
    : IExternalLoginProvider
{
    private readonly GitHubAuthenticationOptions options = configuredOptions.Value.GitHub;

    public string Key => "github";
    public string DisplayName => "GitHub";
    public bool IsEnabled => options.Enabled &&
                             !string.IsNullOrWhiteSpace(options.ClientId) &&
                             !string.IsNullOrWhiteSpace(options.ClientSecret);

    public string BuildAuthorizationUrl(Uri callbackUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = callbackUri.ToString(),
            ["scope"] = options.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return "https://github.com/login/oauth/authorize?" +
            string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    public async Task<ExternalLoginProfile> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        Uri callbackUri,
        CancellationToken ct)
    {
        var client = clients.CreateClient("github-oauth");
        using var tokenRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", options.ClientId),
                new KeyValuePair<string, string>("client_secret", options.ClientSecret),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", callbackUri.ToString()),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
            ]),
        };
        tokenRequest.Headers.Accept.ParseAdd("application/json");
        tokenRequest.Headers.UserAgent.ParseAdd("Sol/1.0");

        using var tokenResponse = await client.SendAsync(tokenRequest, ct);
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
        if (!tokenResponse.IsSuccessStatusCode ||
            !TryReadString(tokenJson, "access_token", out var accessToken))
        {
            throw new ExternalLoginException("provider_exchange_failed");
        }

        using var userRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/user");
        userRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        userRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        userRequest.Headers.UserAgent.ParseAdd("Sol/1.0");

        using var userResponse = await client.SendAsync(userRequest, ct);
        var userJson = await userResponse.Content.ReadAsStringAsync(ct);
        if (!userResponse.IsSuccessStatusCode ||
            !TryReadString(userJson, "id", out var subject) ||
            !TryReadString(userJson, "login", out var login))
        {
            throw new ExternalLoginException("provider_profile_failed");
        }

        TryReadNullableString(userJson, "name", out var name);
        TryReadNullableString(userJson, "email", out var email);
        TryReadNullableString(userJson, "avatar_url", out var avatarUrl);

        return new ExternalLoginProfile(
            Key,
            subject,
            string.IsNullOrWhiteSpace(name) ? login : name!,
            email,
            avatarUrl);
    }

    private static bool TryReadString(string json, string property, out string value)
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(property, out var element)) return false;

            value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                _ => string.Empty,
            };
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryReadNullableString(
        string json,
        string property,
        out string? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(property, out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
            }
        }
        catch (JsonException)
        {
            // The required fields are validated separately. Optional profile fields are allowed
            // to disappear when a user has hidden their email or GitHub changes the payload.
        }
    }
}
