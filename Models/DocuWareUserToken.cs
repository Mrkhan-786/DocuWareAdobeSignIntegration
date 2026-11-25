
namespace DocuWareAdobeSignIntegration.Models;

public class DocuWareUserToken
{
    public string UserId { get; set; } = string.Empty;

    public string EncryptedAccessToken { get; set; } = string.Empty;

    public string EncryptedRefreshToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
