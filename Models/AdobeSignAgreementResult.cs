namespace DocuWareAdobeSignIntegration.Models;

public class AdobeSignAgreementResult
{
    public string AgreementId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SignerEmail { get; set; } = string.Empty;

    public string DocumentName { get; set; } = string.Empty;

    public string AuthoringUrl { get; set; } = string.Empty;
}