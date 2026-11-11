namespace DocuWareAdobeSignIntegration.Models;

public class AdobeSignSettings
{
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string AuthorizationUrl { get; set; } = string.Empty;

    public string TokenUrl { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;
}