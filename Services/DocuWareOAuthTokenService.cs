
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocuWareAdobeSignIntegration.Services;

public sealed class DocuWareOAuthTokenService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        RefreshLocks = new();

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly DocuWareTokenStore _tokenStore;
    private readonly ILogger<DocuWareOAuthTokenService> _logger;

    public DocuWareOAuthTokenService(
        HttpClient httpClient,
        IConfiguration configuration,
        DocuWareTokenStore tokenStore,
        ILogger<DocuWareOAuthTokenService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    // ======================================================
    // GET VALID ACCESS TOKEN
    // ======================================================

    public async Task<string> GetValidAccessTokenAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        var stored = await _tokenStore.GetAsync(
            userId,
            cancellationToken);

        if (stored == null)
        {
            throw new InvalidOperationException(
                "No DocuWare OAuth tokens are stored for this employee. " +
                "The employee must sign in to DocuWare again.");
        }

        // Use the existing token if it has more than five minutes remaining.
        if (stored.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(5))
        {
            return stored.AccessToken;
        }

        // Prevent simultaneous refresh operations for the same user
        // within this application process.
        var refreshLock = RefreshLocks.GetOrAdd(
            userId,
            _ => new SemaphoreSlim(1, 1));

        await refreshLock.WaitAsync(cancellationToken);

        try
        {
            // Reload because another request may have refreshed the token.
            stored = await _tokenStore.GetAsync(
                userId,
                cancellationToken);

            if (stored == null)
            {
                throw new InvalidOperationException(
                    "The employee's DocuWare token record no longer exists.");
            }

            if (stored.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            {
                return stored.AccessToken;
            }

            return await RefreshAsync(
                stored,
                cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    // ======================================================
    // REFRESH ACCESS TOKEN
    // ======================================================

    private async Task<string> RefreshAsync(
        DocuWareStoredToken stored,
        CancellationToken cancellationToken)
    {
        var tokenUrl = _configuration["DocuWare:TokenUrl"];
        var clientId = _configuration["DocuWare:ClientId"];
        var clientSecret = _configuration["DocuWare:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tokenUrl))
        {
            throw new InvalidOperationException(
                "DocuWare:TokenUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "DocuWare:ClientId is not configured.");
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "DocuWare:ClientSecret is not configured.");
        }

        if (!Uri.TryCreate(
                tokenUrl,
                UriKind.Absolute,
                out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException(
                "DocuWare token endpoint must be a valid HTTPS URL.");
        }

        // ----------------------------------------------
        // OAuth refresh-token request
        // ----------------------------------------------

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = stored.RefreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "DocuWare OAuth refresh failed for user {UserId}. HTTP status: {StatusCode}.",
                stored.UserId,
                (int)response.StatusCode);

            if (response.StatusCode == HttpStatusCode.BadRequest ||
                response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    "DocuWare rejected the refresh token or client credentials. " +
                    "The employee may need to sign in again. " +
                    "Check the DocuWare App Registration and OAuth configuration.");
            }

            throw new HttpRequestException(
                $"DocuWare token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        // ----------------------------------------------
        // Deserialize OAuth response
        // ----------------------------------------------

        DocuWareRefreshResponse? token;

        try
        {
            token = JsonSerializer.Deserialize<DocuWareRefreshResponse>(
                responseBody);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "Unable to deserialize DocuWare OAuth response for user {UserId}.",
                stored.UserId);

            throw new InvalidOperationException(
                "DocuWare returned an invalid OAuth token response.",
                ex);
        }

        if (token == null ||
            string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException(
                "DocuWare did not return a valid access token.");
        }

        // ----------------------------------------------
        // Refresh token rotation
        // ----------------------------------------------

        var refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? stored.RefreshToken
            : token.RefreshToken;

        // ----------------------------------------------
        // Calculate expiration
        // ----------------------------------------------

        var expiresIn = token.ExpiresIn > 0
            ? token.ExpiresIn
            : 3600;

        var expiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);

        // ----------------------------------------------
        // Securely save updated tokens
        // ----------------------------------------------

        await _tokenStore.SaveAsync(
            stored.UserId,
            token.AccessToken,
            refreshToken,
            expiresUtc,
            cancellationToken);

        _logger.LogInformation(
            "Successfully refreshed DocuWare OAuth token for user {UserId}.",
            stored.UserId);

        return token.AccessToken;
    }

    // ======================================================
    // DOCUWARE OAUTH TOKEN RESPONSE
    // ======================================================

    private sealed class DocuWareRefreshResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }
}
