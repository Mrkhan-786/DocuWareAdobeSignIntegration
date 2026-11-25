using System.Text.Json.Serialization;

namespace DocuWareAdobeSignIntegration.Models;

public class AdobeTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("api_access_point")]
    public string ApiAccessPoint { get; set; } = string.Empty;

    [JsonPropertyName("web_access_point")]
    public string WebAccessPoint { get; set; } = string.Empty;
}