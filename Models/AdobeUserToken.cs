namespace DocuWareAdobeSignIntegration.Models;

public class AdobeUserToken
{
    public string UserId { get; set; } = string.Empty;

    // These fields contain encrypted values in the database.
    public string EncryptedAccessToken { get; set; } = string.Empty;
    public string EncryptedRefreshToken { get; set; } = string.Empty;

    public string ApiAccessPoint { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}