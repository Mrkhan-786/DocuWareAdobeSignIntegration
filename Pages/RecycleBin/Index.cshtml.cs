
using System;
using System.Collections.Generic;
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

namespace DocuWareAdobeSignIntegration.Pages.RecycleBin;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IAdobeSignedPdfStorage _signedPdfStorage;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ApplicationDbContext db,
        IAdobeSignedPdfStorage signedPdfStorage,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _signedPdfStorage = signedPdfStorage;
        _logger = logger;
    }

    // ------------------------------------------------------
    // Page data
    // ------------------------------------------------------

    public IList<AdobeAgreementWorkflow> DeletedDocuments { get; private set; } =
        new List<AdobeAgreementWorkflow>();

    public int TotalDeletedDocuments =>
        DeletedDocuments.Count;

    public int LocallySavedDocuments =>
        DeletedDocuments.Count(x =>
            !string.IsNullOrWhiteSpace(x.SignedPdfPath));

    // ------------------------------------------------------
    // Current employee identity
    // ------------------------------------------------------

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
    }

    // ------------------------------------------------------
    // Load Recycle Bin
    // ------------------------------------------------------

    public async Task<IActionResult> OnGetAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        DeletedDocuments = await _db.AdobeAgreementWorkflows
            .AsNoTracking()
            .Where(x =>
                x.UserId == userId &&
                x.IsDeleted)
            .OrderByDescending(x => x.DeletedAtUtc)
            .ToListAsync(cancellationToken);

        return Page();
    }

    // ------------------------------------------------------
    // Restore one document
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostRestoreAsync(
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
                    x.IsDeleted,
                cancellationToken);

        if (document == null)
        {
            TempData["ErrorMessage"] =
                "The document could not be found in your Recycle Bin.";

            return RedirectToPage();
        }

        document.IsDeleted = false;
        document.DeletedAtUtc = null;
        document.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Workflow {WorkflowId} was restored from the Recycle Bin by employee {UserId}.",
            document.Id,
            userId);

        TempData["SuccessMessage"] =
            $"'{document.FileName}' was successfully restored to your File Cabinet.";

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // Restore all documents
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostRestoreAllAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var documents = await _db.AdobeAgreementWorkflows
            .Where(x =>
                x.UserId == userId &&
                x.IsDeleted)
            .ToListAsync(cancellationToken);

        if (documents.Count == 0)
        {
            TempData["ErrorMessage"] =
                "There are no documents to restore.";

            return RedirectToPage();
        }

        var restoredAtUtc = DateTime.UtcNow;

        foreach (var document in documents)
        {
            document.IsDeleted = false;
            document.DeletedAtUtc = null;
            document.UpdatedAtUtc = restoredAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{DocumentCount} recycled workflows were restored by employee {UserId}.",
            documents.Count,
            userId);

        TempData["SuccessMessage"] =
            $"{documents.Count} document(s) were successfully restored to your File Cabinet.";

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // Permanently delete one document
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostPermanentlyDeleteAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        // Only allow deletion of this employee's recycled documents.
        var document = await _db.AdobeAgreementWorkflows
            .SingleOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.UserId == userId &&
                    x.IsDeleted,
                cancellationToken);

        if (document == null)
        {
            TempData["ErrorMessage"] =
                "The document could not be found in your Recycle Bin.";

            return RedirectToPage();
        }

        if (document.ArchiveInProgress)
        {
            TempData["ErrorMessage"] =
                "This document is still being processed by DocuWare and cannot be permanently deleted yet.";

            return RedirectToPage();
        }

        var fileName = document.FileName;
        var workflowId = document.Id;
        var agreementId = document.AgreementId;

        try
        {
            // Delete the locally stored signed PDF first.
            DeleteLocalPdf(document);

            // Permanently remove the application workflow record.
            _db.AdobeAgreementWorkflows.Remove(document);

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Workflow {WorkflowId}, agreement {AgreementId}, was permanently deleted by employee {UserId}.",
                workflowId,
                agreementId,
                userId);

            TempData["SuccessMessage"] =
                $"'{fileName}' was permanently deleted from the application.";
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
                "Could not permanently delete recycled workflow {WorkflowId} for employee {UserId}.",
                workflowId,
                userId);

            TempData["ErrorMessage"] =
                "The document could not be permanently deleted. It will remain in the Recycle Bin. Please try again.";
        }

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // Permanently delete all documents
    // ------------------------------------------------------

    public async Task<IActionResult> OnPostPermanentlyDeleteAllAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var documents = await _db.AdobeAgreementWorkflows
            .Where(x =>
                x.UserId == userId &&
                x.IsDeleted)
            .OrderBy(x => x.DeletedAtUtc)
            .ToListAsync(cancellationToken);

        if (documents.Count == 0)
        {
            TempData["ErrorMessage"] =
                "Your Recycle Bin is already empty.";

            return RedirectToPage();
        }

        var deletedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Do not delete a workflow while DocuWare is processing it.
            if (document.ArchiveInProgress)
            {
                skippedCount++;

                _logger.LogWarning(
                    "Workflow {WorkflowId} was skipped during bulk permanent deletion because an archive operation is in progress.",
                    document.Id);

                continue;
            }

            var workflowId = document.Id;
            var agreementId = document.AgreementId;

            try
            {
                // Remove the locally stored signed PDF.
                DeleteLocalPdf(document);

                // Remove only this recycled workflow record.
                _db.AdobeAgreementWorkflows.Remove(document);

                // Save each record separately so one failure does not
                // prevent the remaining eligible records from processing.
                await _db.SaveChangesAsync(cancellationToken);

                deletedCount++;

                _logger.LogInformation(
                    "Workflow {WorkflowId}, agreement {AgreementId}, was permanently deleted during bulk deletion by employee {UserId}.",
                    workflowId,
                    agreementId,
                    userId);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;

                _logger.LogError(
                    ex,
                    "Bulk permanent deletion failed for workflow {WorkflowId}, employee {UserId}.",
                    workflowId,
                    userId);

                // Detach the failed entity so it does not interfere
                // with saving the remaining records.
                _db.Entry(document).State = EntityState.Detached;
            }
        }

        if (deletedCount > 0)
        {
            TempData["SuccessMessage"] =
                $"{deletedCount} document(s) were permanently deleted from your Recycle Bin.";
        }

        if (skippedCount > 0 || failedCount > 0)
        {
            TempData["ErrorMessage"] =
                $"{skippedCount} document(s) were skipped because DocuWare was processing them. " +
                $"{failedCount} document(s) could not be deleted and remain in your Recycle Bin.";
        }

        if (deletedCount == 0 &&
            skippedCount == 0 &&
            failedCount == 0)
        {
            TempData["ErrorMessage"] =
                "No documents were deleted.";
        }

        return RedirectToPage();
    }

    // ------------------------------------------------------
    // Delete locally stored signed PDF
    // ------------------------------------------------------

    private void DeleteLocalPdf(AdobeAgreementWorkflow document)
    {
        if (string.IsNullOrWhiteSpace(document.SignedPdfPath))
        {
            return;
        }

        var absolutePath =
            _signedPdfStorage.GetAbsolutePath(document.SignedPdfPath);

        if (!System.IO.File.Exists(absolutePath))
        {
            return;
        }

        System.IO.File.Delete(absolutePath);

        _logger.LogInformation(
            "Deleted local signed PDF for recycled workflow {WorkflowId}.",
            document.Id);
    }
}
