using System;
using System.Collections.Generic;

namespace DocuWareAdobeSignIntegration.Services;

public sealed class DocuWareTrashBinDocument
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime? DeletedAtUtc { get; init; }
    public string? DeletedBy { get; init; }
    public string? Location { get; init; }
    public string? OriginalDocumentId { get; init; }
    public int? RemainingDays { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; }
        = new Dictionary<string, string?>();
}
