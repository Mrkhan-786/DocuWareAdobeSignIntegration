using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Pages.FileCabinet;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IAdobeSignedPdfStorage _signedPdfStorage;
    private readonly DocuWareOAuthTokenService _docuWareTokenService;
    private readonly DocuWareDocumentService _docuWareDocumentService;
    private readonly AdobeUserTokenService _adobeUserTokenService;
    private readonly AdobeSignService _adobeSignService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ApplicationDbContext db,
        IAdobeSignedPdfStorage signedPdfStorage,
        DocuWareOAuthTokenService docuWareTokenService,
        DocuWareDocumentService docuWareDocumentService,
        AdobeUserTokenService adobeUserTokenService,
        AdobeSignService adobeSignService,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _signedPdfStorage = signedPdfStorage;
        _docuWareTokenService = docuWareTokenService;
        _docuWareDocumentService = docuWareDocumentService;
        _adobeUserTokenService = adobeUserTokenService;
        _adobeSignService = adobeSignService;
        _logger = logger;
    }

    // ------------------------------------------------------
    // Page data
    // ------------------------------------------------------

    public IList<AdobeAgreementWorkflow> Documents { get; private set; } =
        new List<AdobeAgreementWorkflow>();

    public int TotalDocuments =>
        Documents.Count(x =>
            IsAdobeSigned(x.Status) ||
            x.IsAvailableInFileCabinet);

    public int SavedDocuments =>
        Documents.Count(x => x.IsAvailableInFileCabinet);

    public int ArchivedDocuments =>
        Documents.Count(x => x.IsArchived);

    public int PendingArchiveDocuments =>
        Documents.Count(x =>
            x.IsAvailableInFileCabinet &&
            !x.IsArchived &&
            !string.Equals(
                x.DocuWareArchiveStatus,
                "Failed",
                StringComparison.OrdinalIgnoreCase));

    public int FailedArchiveDocuments =>
        Documents.Count(x =>
            string.Equals(
                x.DocuWareArchiveStatus,
                "Failed",
                StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------
    // Current employee identity
    // ------------------------------------------------------

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
    }

    // ------------------------------------------------------
    // Adobe status helper
    // ------------------------------------------------------

    public static bool IsAdobeSigned(string? status)
    {
        return string.Equals(
                   status,
                   "SIGNED",
                   StringComparison.OrdinalIgnoreCase)
               ||
               string.Equals(
                   status,
                   "COMPLETED",
                   StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------
    // Load File Cabinet
    // ------------------------------------------------------

    public async Task<IActionResult> OnGetAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        // Recycled documents are not displayed in the File Cabinet.
        Documents = await _db.AdobeAgreementWorkflows
            .AsNoTracking()
            .Where(x =>
                x.UserId == userId &&
                !x.IsDeleted)
            .OrderByDescending(x => x.SentAtUtc)
            .ToListAsync(cancellationToken);

        return Page();
    }

    // ------------------------------------------------------
    // Move document to Recycle Bin
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostMoveToRecycleBinAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var document = await _db.AdobeAgreementWorkflows
            .SingleOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.UserId == userId &&
                    !x.IsDeleted,
                cancellationToken);

        if (document == null)
        {
            return NotFound();
        }

        // Do not move a document while its DocuWare archive
        // replacement is actively running.
        if (document.ArchiveInProgress)
        {
            TempData["ErrorMessage"] =
                "This document is currently being saved to DocuWare. Please try moving it to the Recycle Bin again after that operation finishes.";

            return RedirectToPage();
        }

        var deletedAt = DateTime.UtcNow;

        document.IsDeleted = true;
        document.DeletedAtUtc = deletedAt;
        document.UpdatedAtUtc = deletedAt;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Workflow {WorkflowId}, agreement {AgreementId}, was moved to the Recycle Bin by employee {UserId}.",
            document.Id,
            document.AgreementId,
            userId);

        TempData["SuccessMessage"] =
            $"'{document.FileName}' was moved to the Recycle Bin. You can permanently delete it from there.";

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // Save completed Adobe PDF locally
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostSaveToFileCabinetAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var document = await _db.AdobeAgreementWorkflows
            .SingleOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.UserId == userId &&
                    !x.IsDeleted,
                cancellationToken);

        if (document == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(document.AgreementId))
        {
            TempData["ErrorMessage"] =
                "The Adobe Sign agreement ID is missing.";

            return RedirectToPage();
        }

        // --------------------------------------------------
        // 1. Check whether the PDF is already saved locally
        // --------------------------------------------------

        if (!string.IsNullOrWhiteSpace(document.SignedPdfPath))
        {
            try
            {
                var existingPath =
                    _signedPdfStorage.GetAbsolutePath(
                        document.SignedPdfPath);

                if (System.IO.File.Exists(existingPath))
                {
                    TempData["SuccessMessage"] =
                        "The signed PDF is already saved locally. You can download it or save it to DocuWare.";

                    return RedirectToPage();
                }

                _logger.LogWarning(
                    "Stored signed PDF is missing for workflow {WorkflowId}. A new download will be attempted.",
                    document.Id);

                document.SignedPdfPath = null;
                document.SignedPdfSavedAtUtc = null;
                document.SignedPdfSizeBytes = null;

                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not validate local PDF storage for workflow {WorkflowId}.",
                    document.Id);

                TempData["ErrorMessage"] =
                    "The existing local PDF could not be checked. Please try again.";

                return RedirectToPage();
            }
        }

        try
        {
            // --------------------------------------------------
            // 2. Get employee's Adobe Sign token
            // --------------------------------------------------

            var adobeToken =
                await _adobeUserTokenService.GetValidTokenAsync(
                    userId,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(adobeToken.AccessToken) ||
                string.IsNullOrWhiteSpace(adobeToken.ApiAccessPoint))
            {
                throw new InvalidOperationException(
                    "Your Adobe Sign connection is incomplete. Please reconnect Adobe Sign.");
            }

            // --------------------------------------------------
            // 3. Check agreement status with Adobe Sign
            // --------------------------------------------------

            var adobeStatus =
                await _adobeSignService.GetAgreementStatusAsync(
                    document.AgreementId,
                    adobeToken.AccessToken,
                    adobeToken.ApiAccessPoint,
                    cancellationToken);

            if (!IsAdobeSigned(adobeStatus))
            {
                if (!string.IsNullOrWhiteSpace(adobeStatus))
                {
                    document.Status = adobeStatus;
                    document.UpdatedAtUtc = DateTime.UtcNow;

                    await _db.SaveChangesAsync(cancellationToken);
                }

                TempData["ErrorMessage"] =
                    $"The agreement is not ready to save yet. Current Adobe Sign status: {adobeStatus ?? "Unknown"}. The signed PDF can only be downloaded after all required signatures are completed.";

                return RedirectToPage();
            }

            // --------------------------------------------------
            // 4. Download completed signed PDF from Adobe Sign
            // --------------------------------------------------

            var signedPdf =
                await _adobeSignService.DownloadSignedDocumentAsync(
                    document.AgreementId,
                    adobeToken.AccessToken,
                    adobeToken.ApiAccessPoint,
                    cancellationToken);

            if (signedPdf == null || signedPdf.Length == 0)
            {
                throw new InvalidOperationException(
                    "Adobe Sign returned an empty signed PDF.");
            }

            // --------------------------------------------------
            // 5. Save the PDF in local File Cabinet storage
            // --------------------------------------------------

            var relativePath =
                await _signedPdfStorage.SaveAsync(
                    userId,
                    document.AgreementId,
                    signedPdf,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidOperationException(
                    "The signed PDF storage service did not return a valid file path.");
            }

            // --------------------------------------------------
            // 6. Update database
            // --------------------------------------------------

            var savedAt = DateTime.UtcNow;

            document.SignedPdfPath = relativePath;
            document.SignedPdfSavedAtUtc = savedAt;
            document.SignedPdfSizeBytes = signedPdf.LongLength;

            document.Status = "Signed";
            document.FailureMessage = null;
            document.UpdatedAtUtc = savedAt;

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Signed PDF saved locally for workflow {WorkflowId}, agreement {AgreementId}. Size: {SizeBytes} bytes.",
                document.Id,
                document.AgreementId,
                signedPdf.LongLength);

            TempData["SuccessMessage"] =
                "The signed PDF was successfully saved to the local File Cabinet. You can now download it or separately save it to DocuWare.";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to download and save signed PDF for workflow {WorkflowId}.",
                document.Id);

            document.FailureMessage =
                ex.Message.Length > 2000
                    ? ex.Message[..2000]
                    : ex.Message;

            document.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception saveException)
            {
                _logger.LogError(
                    saveException,
                    "Could not save local PDF failure status for workflow {WorkflowId}.",
                    document.Id);
            }

            TempData["ErrorMessage"] =
                "The signed PDF could not be saved locally. Please check the Adobe Sign connection and try again.";
        }

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // Download locally saved signed PDF
    // ------------------------------------------------------

    public async Task<IActionResult> OnGetDownloadAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var document = await _db.AdobeAgreementWorkflows
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.UserId == userId &&
                    !x.IsDeleted,
                cancellationToken);

        if (document == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(document.SignedPdfPath))
        {
            return NotFound(
                "The signed PDF is not available in local storage.");
        }

        try
        {
            var stream =
                _signedPdfStorage.OpenRead(
                    document.SignedPdfPath);

            var downloadFileName =
                Path.GetFileName(document.FileName);

            if (string.IsNullOrWhiteSpace(downloadFileName))
            {
                downloadFileName =
                    $"SignedDocument-{document.Id}.pdf";
            }

            if (!downloadFileName.EndsWith(
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
            {
                downloadFileName += ".pdf";
            }

            return File(
                stream,
                "application/pdf",
                downloadFileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound(
                "The signed PDF file could not be found in local storage.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to download signed PDF for workflow {WorkflowId}.",
                id);

            TempData["ErrorMessage"] =
                "The signed PDF could not be downloaded. Please try again.";

            return RedirectToPage();
        }
    }

    // ------------------------------------------------------
    // Save locally stored PDF to original DocuWare document
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostSaveToDocuWareAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var document = await _db.AdobeAgreementWorkflows
            .SingleOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.UserId == userId &&
                    !x.IsDeleted,
                cancellationToken);

        if (document == null)
        {
            return NotFound();
        }

        // --------------------------------------------------
        // 1. Validate local signed PDF and DocuWare identifiers
        // --------------------------------------------------

        if (string.IsNullOrWhiteSpace(document.SignedPdfPath))
        {
            TempData["ErrorMessage"] =
                "Save the signed PDF to the local File Cabinet before saving it to DocuWare.";

            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(document.OriginalDocumentId))
        {
            TempData["ErrorMessage"] =
                "The original DocuWare document ID is missing.";

            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(document.SectionId))
        {
            TempData["ErrorMessage"] =
                "The original DocuWare document section ID is missing. The document cannot be replaced.";

            return RedirectToPage();
        }

        // --------------------------------------------------
        // 2. Check whether it is already archived
        // --------------------------------------------------

        if (document.IsArchived)
        {
            TempData["SuccessMessage"] =
                "This signed PDF has already been saved to DocuWare.";

            return RedirectToPage();
        }

        // --------------------------------------------------
        // 3. Check that the local file exists
        // --------------------------------------------------

        try
        {
            var absolutePath =
                _signedPdfStorage.GetAbsolutePath(
                    document.SignedPdfPath);

            if (!System.IO.File.Exists(absolutePath))
            {
                TempData["ErrorMessage"] =
                    "The locally saved signed PDF could not be found. Please download it again from Adobe Sign.";

                return RedirectToPage();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not validate the stored PDF path for workflow {WorkflowId}.",
                document.Id);

            TempData["ErrorMessage"] =
                "The stored PDF path is invalid. Please save the signed PDF locally again.";

            return RedirectToPage();
        }

        // --------------------------------------------------
        // 4. Prevent concurrent archive attempts
        // --------------------------------------------------

        var now = DateTime.UtcNow;
        var staleBefore = now.AddMinutes(-20);

        if (document.ArchiveInProgress &&
            document.LastArchiveAttemptUtc.HasValue &&
            document.LastArchiveAttemptUtc.Value > staleBefore)
        {
            TempData["ErrorMessage"] =
                "This document is already being saved to DocuWare. Please wait before trying again.";

            return RedirectToPage();
        }

        // --------------------------------------------------
        // 5. Mark archive operation as in progress
        // --------------------------------------------------

        var previousArchiveStatus = document.DocuWareArchiveStatus;
        var previousArchiveError = document.DocuWareArchiveError;

        document.DocuWareArchiveStatus = "Processing";
        document.DocuWareArchiveError = null;
        document.ArchiveInProgress = true;
        document.ArchiveAttempts++;
        document.LastArchiveAttemptUtc = now;
        document.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            // --------------------------------------------------
            // 6. Obtain employee's DocuWare access token
            // --------------------------------------------------

            var docuWareAccessToken =
                await _docuWareTokenService.GetValidAccessTokenAsync(
                    userId,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(docuWareAccessToken))
            {
                throw new InvalidOperationException(
                    "A valid DocuWare access token could not be obtained.");
            }

            // --------------------------------------------------
            // 7. Open locally saved signed PDF
            // --------------------------------------------------

            await using var signedPdfStream =
                _signedPdfStorage.OpenRead(
                    document.SignedPdfPath);

            // --------------------------------------------------
            // 8. Prepare replacement filename
            // --------------------------------------------------

            var replacementFileName =
                Path.GetFileName(document.FileName);

            if (string.IsNullOrWhiteSpace(replacementFileName))
            {
                replacementFileName =
                    $"SignedDocument-{document.Id}.pdf";
            }

            if (!replacementFileName.EndsWith(
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
            {
                replacementFileName += ".pdf";
            }

            // --------------------------------------------------
            // 9. Replace original DocuWare document section
            // --------------------------------------------------

            await _docuWareDocumentService.ReplaceDocumentSectionAsync(
                document.OriginalDocumentId,
                document.SectionId,
                signedPdfStream,
                replacementFileName,
                docuWareAccessToken,
                cancellationToken);

            // --------------------------------------------------
            // 10. Record successful DocuWare archive
            // --------------------------------------------------

            var archivedAt = DateTime.UtcNow;

            document.ArchivedDocumentId =
                document.OriginalDocumentId;

            document.ArchivedAtUtc = archivedAt;
            document.CompletedAtUtc = archivedAt;

            document.DocuWareArchiveStatus = "Saved";
            document.DocuWareArchiveError = null;
            document.ArchiveInProgress = false;

            document.Status = "Completed";

            document.FailureMessage = null;
            document.UpdatedAtUtc = archivedAt;

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Signed PDF for workflow {WorkflowId} replaced the original section in DocuWare document {DocumentId} for employee {UserId}.",
                document.Id,
                document.OriginalDocumentId,
                userId);

            TempData["SuccessMessage"] =
                "The signed PDF was successfully saved to the original DocuWare document. The original DocuWare document ID has been preserved.";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            document.DocuWareArchiveStatus = previousArchiveStatus;
            document.DocuWareArchiveError = previousArchiveError;
            document.ArchiveInProgress = false;
            document.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveException)
            {
                _logger.LogError(
                    saveException,
                    "Could not reset archive state after cancellation for workflow {WorkflowId}.",
                    document.Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to save signed PDF for workflow {WorkflowId} to DocuWare document {DocumentId}.",
                document.Id,
                document.OriginalDocumentId);

            document.DocuWareArchiveStatus = "Failed";

            document.DocuWareArchiveError =
                ex.Message.Length > 4000
                    ? ex.Message[..4000]
                    : ex.Message;

            document.ArchiveInProgress = false;
            document.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveException)
            {
                _logger.LogError(
                    saveException,
                    "Could not save DocuWare archive failure status for workflow {WorkflowId}.",
                    document.Id);
            }

            TempData["ErrorMessage"] =
                "The signed PDF remains safely saved in the local File Cabinet, but DocuWare could not be updated. You can retry the DocuWare save.";
        }

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // File size display helper
    // ------------------------------------------------------

    public static string FormatFileSize(long? sizeInBytes)
    {
        if (!sizeInBytes.HasValue || sizeInBytes.Value <= 0)
        {
            return "Unknown size";
        }

        double size = sizeInBytes.Value;

        string[] units =
        {
            "B",
            "KB",
            "MB",
            "GB"
        };

        var unitIndex = 0;

        while (size >= 1024 &&
               unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}