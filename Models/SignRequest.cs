using System.ComponentModel.DataAnnotations;

namespace DocuWareAdobeSignIntegration.Models;

public class SignRequest
{
    [Required]
    public string DocumentId { get; set; } = string.Empty;

    [Required]
    public string DocumentType { get; set; } = string.Empty;

    [Required]
    public string AgreementNumber { get; set; } = string.Empty;

    [Required]
    public string RecipientCompany { get; set; } = string.Empty;

    [Required]
    public string RecipientName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string RecipientEmail { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string AdobeAgreementId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}