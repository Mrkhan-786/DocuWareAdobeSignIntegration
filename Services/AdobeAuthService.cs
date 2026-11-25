
using System.Net.Http.Headers;
using System.Text.Json;
using DocuWareAdobeSignIntegration.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Services;

public class AdobeAuthService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdobeAuthService> _logger;

    private const string DefaultScopes =
        "user_login:account " +
        "agreement_read:account " +
        "agreement_write:account " +
        "agreement_send:account " +
        "webhook_write " +
        "offline_access";

    public AdobeAuthService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AdobeAuthService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    // ======================================================
    // 1. BUILD ADOBE SIGN AUTHORIZATION URL
    // ======================================================

    public string BuildAuthorizationUrl(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException(
                "OAuth state is required.",
                nameof(state));
        }

        var authorizationUrl =
            GetRequiredSetting("AdobeSign:AuthorizationUrl");

        var clientId =
            GetRequiredSetting("AdobeSign:ClientId");

        var redirectUri =
            GetRequiredSetting("AdobeSign:RedirectUri");

        if (!Uri.TryCreate(
                authorizationUrl,
                UriKind.Absolute,
                out var authorizationEndpoint) ||
            authorizationEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Adobe Sign authorization URL must be a valid HTTPS URL.");
        }

        ValidateHttpsUri(
            redirectUri,
            "Adobe Sign Redirect URI");

        var scope = GetConfiguredScopes();

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state
        };

        _logger.LogInformation(
            "Building Adobe Sign authorization URL. " +
            "Authorization host: {AuthorizationHost}. " +
            "Webhook permission requested: {WebhookPermission}. " +
            "Offline access requested: {OfflineAccessPermission}.",
            authorizationEndpoint.Host,
            HasScope(scope, "webhook_write"),
            HasScope(scope, "offline_access"));

        return QueryHelpers.AddQueryString(
            authorizationUrl,
            query);
    }

    // ======================================================
    // 2. EXCHANGE AUTHORIZATION CODE FOR ACCESS TOKEN
    // ======================================================

    public async Task<AdobeTokenResponse> ExchangeCodeForTokenAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException(
                "Authorization code is required.",
                nameof(code));
        }

        var tokenUrl =
            GetRequiredSetting("AdobeSign:TokenUrl");

        var clientId =
            GetRequiredSetting("AdobeSign:ClientId");

        var clientSecret =
            GetRequiredSetting("AdobeSign:ClientSecret");

        var redirectUri =
            GetRequiredSetting("AdobeSign:RedirectUri");

        if (!Uri.TryCreate(
                tokenUrl,
                UriKind.Absolute,
                out var tokenEndpoint) ||
            tokenEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Adobe Sign token URL must be a valid HTTPS URL.");
        }

        ValidateHttpsUri(
            redirectUri,
            "Adobe Sign Redirect URI");

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            tokenUrl)
        {
            Content = new FormUrlEncodedContent(values)
        };

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe Sign authorization-code exchange failed. " +
                "HTTP status: {StatusCode}.",
                (int)response.StatusCode);

            throw new HttpRequestException(
                $"Adobe Sign token exchange failed. " +
                $"HTTP {(int)response.StatusCode}.");
        }

        var responseBody =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        var token = DeserializeToken(responseBody);

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException(
                "Adobe Sign returned an empty access token.");
        }

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            _logger.LogError(
                "Adobe Sign authorization succeeded, but no refresh token was returned. " +
                "Verify that offline_access is enabled for the Adobe OAuth application " +
                "and included in the authorization request.");

            throw new InvalidOperationException(
                "Adobe Sign did not return a refresh token. " +
                "Check that offline_access is enabled in your Adobe OAuth application " +
                "and that the user approved the requested permission.");
        }

        if (!string.IsNullOrWhiteSpace(token.ApiAccessPoint))
        {
            token.ApiAccessPoint =
                ValidateApiAccessPoint(token.ApiAccessPoint);
        }
        else
        {
            token.ApiAccessPoint = await GetBaseUriAsync(
                token.AccessToken,
                cancellationToken);
        }

        _logger.LogInformation(
            "Adobe Sign authorization-code exchange completed successfully. " +
            "Access token and refresh token were returned.");

        return token;
    }

    // ======================================================
    // 3. GET ADOBE SIGN API ACCESS POINT
    // ======================================================

    public async Task<string> GetBaseUriAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException(
                "An access token is required.",
                nameof(accessToken));
        }

        var configuredBaseUrl =
            GetRequiredSetting("AdobeSign:BaseUrl").TrimEnd('/');

        if (!Uri.TryCreate(
                configuredBaseUrl,
                UriKind.Absolute,
                out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Adobe Sign Base URL must be a valid HTTPS URL.");
        }

        var baseUrisUrl =
            $"{configuredBaseUrl}/api/rest/v6/baseUris";

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            baseUrisUrl);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                accessToken);

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe Sign API access point lookup failed. " +
                "HTTP status: {StatusCode}.",
                (int)response.StatusCode);

            throw new HttpRequestException(
                $"Adobe Sign base URI lookup failed. " +
                $"HTTP {(int)response.StatusCode}.");
        }

        var responseBody =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        using var json = JsonDocument.Parse(responseBody);

        if (!json.RootElement.TryGetProperty(
                "apiAccessPoint",
                out var apiAccessPointElement))
        {
            throw new InvalidOperationException(
                "Adobe Sign did not return an apiAccessPoint.");
        }

        var apiAccessPoint =
            apiAccessPointElement.GetString();

        if (string.IsNullOrWhiteSpace(apiAccessPoint))
        {
            throw new InvalidOperationException(
                "Adobe Sign returned an empty API access point.");
        }

        return ValidateApiAccessPoint(apiAccessPoint);
    }

    // ======================================================
    // 4. REFRESH EMPLOYEE'S ADOBE SIGN ACCESS TOKEN
    // ======================================================

    public async Task<AdobeTokenResponse> RefreshTokenAsync(
        string refreshToken,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException(
                "A refresh token is required.",
                nameof(refreshToken));
        }

        var validatedApiAccessPoint =
            ValidateApiAccessPoint(apiAccessPoint);

        var clientId =
            GetRequiredSetting("AdobeSign:ClientId");

        var clientSecret =
            GetRequiredSetting("AdobeSign:ClientSecret");

        var refreshUrl =
            $"{validatedApiAccessPoint}/oauth/v2/refresh";

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            refreshUrl)
        {
            Content = new FormUrlEncodedContent(values)
        };

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe Sign token refresh failed. " +
                "HTTP status: {StatusCode}.",
                (int)response.StatusCode);

            throw new HttpRequestException(
                $"Adobe Sign token refresh failed. " +
                $"HTTP {(int)response.StatusCode}.");
        }

        var responseBody =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        var token = DeserializeToken(responseBody);

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException(
                "Adobe Sign returned an invalid refreshed access token.");
        }

        // Adobe may not return a new refresh token.
        // Retain the existing one if necessary.
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            token.RefreshToken = refreshToken;
        }

        // Retain the API access point belonging to this employee.
        token.ApiAccessPoint = validatedApiAccessPoint;

        _logger.LogInformation(
            "Adobe Sign access token refreshed successfully.");

        return token;
    }

    // ======================================================
    // 5. CONFIGURED OAUTH SCOPES
    // ======================================================

    private string GetConfiguredScopes()
    {
        var configuredScope =
            _configuration["AdobeSign:Scope"];

        var scopes = string.IsNullOrWhiteSpace(configuredScope)
            ? DefaultScopes.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
                .ToList()
            : configuredScope.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        // Required for Acrobat Sign webhook registration.
        if (!scopes.Contains(
                "webhook_write",
                StringComparer.Ordinal))
        {
            scopes.Add("webhook_write");
        }

        // Required to request a refresh token.
        if (!scopes.Contains(
                "offline_access",
                StringComparer.Ordinal))
        {
            scopes.Add("offline_access");
        }

        return string.Join(" ", scopes);
    }

    private static bool HasScope(
        string scopes,
        string requiredScope)
    {
        return scopes.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(
                requiredScope,
                StringComparer.Ordinal);
    }

    // ======================================================
    // 6. DESERIALIZE ADOBE TOKEN RESPONSE
    // ======================================================

    private static AdobeTokenResponse DeserializeToken(
        string responseBody)
    {
        var token = JsonSerializer.Deserialize<AdobeTokenResponse>(
            responseBody,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        return token ?? throw new InvalidOperationException(
            "Adobe Sign returned an invalid token response.");
    }

    // ======================================================
    // 7. VALIDATE ADOBE API ACCESS POINT
    // ======================================================

    private static string ValidateApiAccessPoint(string value)
    {
        if (!Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Adobe returned an invalid API access point.");
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    // ======================================================
    // 8. VALIDATE HTTPS CONFIGURATION VALUES
    // ======================================================

    private static void ValidateHttpsUri(
        string value,
        string settingName)
    {
        if (!Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"{settingName} must be a valid HTTPS URL.");
        }
    }

    // ======================================================
    // 9. REQUIRED CONFIGURATION HELPER
    // ======================================================

    private string GetRequiredSetting(string key)
    {
        var value = _configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Missing or empty configuration: {key}");
        }

        return value.Trim();
    }
}
