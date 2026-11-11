using System.Text.Json.Serialization;

namespace DocuWareAdobeSignIntegration.Models;

public class AdobeTransientDocumentResponse
{
    [JsonPropertyName("transientDocumentId")]
    public string TransientDocumentId { get; set; }
        = string.Empty;
}