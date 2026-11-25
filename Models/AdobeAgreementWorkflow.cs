using System;
using System.ComponentModel.DataAnnotations;

namespace DocuWareAdobeSignIntegration.Models;

public class AdobeAgreementWorkflow
{
    // ======================================================
    // PRIMARY KEY
    // ======================================================

    public int Id { get; set; }


    // ======================================================
    // ADOBE SIGN AGREEMENT INFORMATION
    // ======================================================

    [Required]
    [MaxLength(128)]
    public string AgreementId { get; set; } = string.Empty;

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    // Retained for compatibility with existing pages
    // and queries. Represents the first signer.

    [MaxLength(255)]
    public string SignerName { get; set; } = string.Empty;

    [Required]
    [MaxLength(320)]
    public string SignerEmail { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Subject { get; set; } = string.Empty;


    // ======================================================
    // MULTI-RECIPIENT SIGNING CONFIGURATION
    // ======================================================

    // JSON containing all recipients configured
    // for this Adobe Sign agreement.

    [Required]
    public string RecipientsJson { get; set; } = "[]";

    // Supported values:
    // Sequential
    // Parallel

    [Required]
    [MaxLength(30)]
    public string SigningOrderMode { get; set; } = "Sequential";


    // ======================================================
    // ADOBE SIGN ANCHOR FIELD CONFIGURATION
    // ======================================================

    // JSON containing the configured Adobe Sign anchor
    // fields used for automatic signature placement.
    //
    // Examples:
    // Signature
    // Name
    // Date
    // Initials
    //
    // The actual anchor definitions are managed by
    // the Adobe Sign service.

    [Required]
    public string AnchorFieldsJson { get; set; } = "[]";


    // ======================================================
    // ORIGINAL DOCUWARE DOCUMENT INFORMATION
    // ======================================================

    // These identifiers refer to the original DocuWare
    // document selected by the employee.
    //
    // They must remain unchanged throughout the signing
    // and archiving workflow.
    //
    // When replacing an existing DocuWare document,
    // OriginalDocumentId must continue identifying
    // the original document.

    [Required]
    [MaxLength(100)]
    public string OriginalDocumentId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FileCabinetId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? SectionId { get; set; }

    [Required]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;


    // ======================================================
    // APPLICATION WORKFLOW STATUS
    // ======================================================

    // Expected statuses:
    //
    // Preparing in Adobe
    // Awaiting Signature
    // In Progress
    // Signed
    // Completed
    // Failed
    //
    // Signed:
    // Adobe confirms that all required signatures
    // have been completed.
    //
    // Completed:
    // The signed PDF has also been successfully
    // saved into DocuWare.
    //
    // IMPORTANT:
    // Adobe SIGNED status alone must never set
    // the application status to Completed.

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Preparing in Adobe";


    // ======================================================
    // WORKFLOW TIMESTAMPS
    // ======================================================

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    // Only set after DocuWare confirms successful
    // replacement/save of the signed document.

    public DateTime? CompletedAtUtc { get; set; }

    // Only set after DocuWare confirms successful
    // archival.

    public DateTime? ArchivedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;


    // ======================================================
    // RECYCLE BIN
    // ======================================================

    /// <summary>
    /// Indicates whether this workflow has been moved
    /// from the File Cabinet to the Recycle Bin.
    /// </summary>
    public bool IsDeleted { get; set; } = false;

    /// <summary>
    /// UTC timestamp recording when the workflow was
    /// moved to the Recycle Bin.
    /// </summary>
    public DateTime? DeletedAtUtc { get; set; }


    // ======================================================
    // DOCUWARE REPLACEMENT TRACKING
    // ======================================================

    // Identifies the DocuWare document confirmed
    // as successfully saved.
    //
    // When replacing the original document, this
    // should normally remain the original document ID.

    [MaxLength(100)]
    public string? ArchivedDocumentId { get; set; }


    // ======================================================
    // LOCAL SIGNED PDF STORAGE
    // ======================================================

    // Tracks a local/staged copy of the signed PDF.
    //
    // Local storage alone does NOT mean the document
    // has successfully been archived in DocuWare.

    [MaxLength(2048)]
    public string? SignedPdfPath { get; set; }

    public DateTime? SignedPdfSavedAtUtc { get; set; }

    public long? SignedPdfSizeBytes { get; set; }


    // ======================================================
    // DOCUWARE ARCHIVE TRACKING
    // ======================================================

    // Expected values:
    //
    // Not saved
    // Processing
    // Saved
    // Failed
    //
    // Saved must only be assigned after DocuWare
    // confirms that the document replacement/save
    // operation has succeeded.

    [Required]
    [MaxLength(50)]
    public string DocuWareArchiveStatus { get; set; } = "Not saved";

    [MaxLength(4000)]
    public string? DocuWareArchiveError { get; set; }


    // ======================================================
    // ARCHIVE PROCESSING AND RETRY TRACKING
    // ======================================================

    // These properties are exclusively for the
    // DocuWare archive operation.
    //
    // Ordinary Adobe webhook synchronization must
    // not mark the document as archived.

    public bool ArchiveInProgress { get; set; }

    public int ArchiveAttempts { get; set; }

    public DateTime? LastArchiveAttemptUtc { get; set; }


    // ======================================================
    // FAILURE TRACKING
    // ======================================================

    [MaxLength(4000)]
    public string? FailureMessage { get; set; }


    // ======================================================
    // CONVENIENCE PROPERTIES
    // ======================================================

    /// <summary>
    /// Indicates that the signed document has been
    /// successfully saved into DocuWare.
    ///
    /// Local PDF storage and Adobe SIGNED status
    /// alone are not sufficient.
    /// </summary>

    public bool IsArchived =>
        ArchivedAtUtc.HasValue &&
        !string.IsNullOrWhiteSpace(ArchivedDocumentId) &&
        string.Equals(
            DocuWareArchiveStatus,
            "Saved",
            StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Indicates that the complete workflow has
    /// been successfully archived in DocuWare.
    /// </summary>

    public bool IsCompleted =>
        IsArchived &&
        CompletedAtUtc.HasValue &&
        string.Equals(
            Status,
            "Completed",
            StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Indicates that a local copy of the signed PDF
    /// has been saved.
    ///
    /// This does not mean DocuWare archiving succeeded.
    /// </summary>

    public bool IsAvailableInFileCabinet =>
        !string.IsNullOrWhiteSpace(SignedPdfPath) &&
        SignedPdfSavedAtUtc.HasValue &&
        SignedPdfSizeBytes.HasValue &&
        SignedPdfSizeBytes.Value > 0;


    /// <summary>
    /// Indicates that Adobe has completed signing,
    /// but the signed PDF has not yet been confirmed
    /// as saved in DocuWare.
    /// </summary>

    public bool IsSignedAwaitingDocuWareSave =>
        string.Equals(
            Status,
            "Signed",
            StringComparison.OrdinalIgnoreCase) &&
        !IsArchived;
}