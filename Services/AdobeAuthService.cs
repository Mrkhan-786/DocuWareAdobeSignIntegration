using System.Text.Json;
using DocuWareAdobeSignIntegration.Models;

namespace DocuWareAdobeSignIntegration.Services;

public class AdobeAuthService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdobeAuthService> _logger;

    public AdobeAuthService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AdobeAuthService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public string BuildAuthorizationUrl()
    {
        var clientId =
            _configuration["AdobeSign:ClientId"];

        var redirectUri =
            _configuration["AdobeSign:RedirectUri"];

        return
            $"https://secure.na3.adobesign.com/public/oauth/v2" +
            $"?response_type=code" +
            $"&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri!)}" +
            $"&scope=user_login:self agreement_read:account agreement_write:account agreement_send:account";
    }

    public async Task<AdobeTokenResponse> ExchangeCodeForTokenAsync(
        string authorizationCode)
    {
        var clientId =
            _configuration["AdobeSign:ClientId"];

        var clientSecret =
            _configuration["AdobeSign:ClientSecret"];

        var redirectUri =
            _configuration["AdobeSign:RedirectUri"];

        var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>(
                "grant_type",
                "authorization_code"),

            new KeyValuePair<string, string>(
                "client_id",
                clientId!),

            new KeyValuePair<string, string>(
                "client_secret",
                clientSecret!),

            new KeyValuePair<string, string>(
                "redirect_uri",
                redirectUri!),

            new KeyValuePair<string, string>(
                "code",
                authorizationCode)
        ]);

        var response =
            await _httpClient.PostAsync(
                "https://api.na3.adobesign.com/oauth/v2/token",
                content);

        var json =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe token exchange failed: {Response}",
                json);

            throw new Exception(json);
        }

        var token =
            JsonSerializer.Deserialize<AdobeTokenResponse>(
                json);

        return token!;
    }

    public async Task<string> GetBaseUriAsync(
        string accessToken)
    {
        _httpClient.DefaultRequestHeaders.Clear();

        _httpClient.DefaultRequestHeaders.Add(
            "Authorization",
            $"Bearer {accessToken}");

        var response =
            await _httpClient.GetAsync(
                "https://api.na3.adobesign.com/api/rest/v6/baseUris");

        var json =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe Base URI lookup failed: {Response}",
                json);

            throw new Exception(json);
        }

        return json;
    }

    internal async Task<string?> GetAccessTokenAsync()
    {
        throw new NotImplementedException();
    }
}