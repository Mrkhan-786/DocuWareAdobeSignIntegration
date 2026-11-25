
using System;

namespace DocuWareAdobeSignIntegration.Models;

public sealed record DocuWareDocumentItem(
    int Id,
    string Name,
    DateTime? CreatedAt,
    DateTime? LastModified)
{
    // DocuWare DOCUMENT_TYPE index field.
    public string? DocumentType { get; init; }

    // Category assigned using the document type.
    public string Category { get; init; } = "Uncategorised";

    // CSS class used to colour the category badge.
    public string CategoryClass { get; init; } = "uncategorised";

    // Recipient details entered on the Send for Signature page.
    public string? RecipientName { get; init; }

    public string? RecipientEmail { get; init; }

    // Request Date entered on the Send for Signature page.
    public DateTime? RequestDate { get; init; }
}
