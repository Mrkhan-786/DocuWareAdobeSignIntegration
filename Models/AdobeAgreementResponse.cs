using System.Text.Json.Serialization;

namespace DocuWareAdobeSignIntegration.Models;

public class AdobeAgreementResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}