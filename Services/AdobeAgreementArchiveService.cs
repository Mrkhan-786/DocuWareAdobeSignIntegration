
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Services
{
    public class AdobeAgreementArchiveService : IAdobeAgreementArchiveService
    {
        private readonly ApplicationDbContext _db;
        private readonly AdobeUserTokenService _adobeUserTokenService;
        private readonly AdobeSignService _adobeSignService;
        private readonly IAdobeSignedPdfStorage _signedPdfStorage;
        private readonly ILogger<AdobeAgreementArchiveService> _logger;

        public AdobeAgreementArchiveService(
            ApplicationDbContext db,
            AdobeUserTokenService adobeUserTokenService,
            AdobeSignService adobeSignService,
            IAdobeSignedPdfStorage signedPdfStorage,
            ILogger<AdobeAgreementArchiveService> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));

            _adobeUserTokenService =
                adobeUserTokenService
                ?? throw new ArgumentNullException(nameof(adobeUserTokenService));

            _adobeSignService =
                adobeSignService
                ?? throw new ArgumentNullException(nameof(adobeSignService));

            _signedPdfStorage =
                signedPdfStorage
                ?? throw new ArgumentNullException(nameof(signedPdfStorage));

            _logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        // ======================================================
        // MARK AGREEMENT SIGNED
        // ======================================================

        /// <summary>
        /// Checks the Adobe Sign agreement status.
        ///
        /// If signing is complete:
        /// 1. Downloads the completed PDF from Adobe Sign.
        /// 2. Validates the downloaded PDF.
        /// 3. Saves the PDF in the application's local File Cabinet.
        /// 4. Updates the workflow's signed-PDF information.
        ///
        /// This method does NOT archive the signed document into DocuWare.
        /// </summary>
        public async Task MarkAgreementSignedAsync(
            string agreementId,
            string? notificationId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(agreementId))
            {
                throw new ArgumentException(
                    "An Adobe agreement ID is required.",
                    nameof(agreementId));
            }

            agreementId = agreementId.Trim();

            // ==================================================
            // 1. FIND THE WORKFLOW
            // ==================================================

            var workflow = await _db.AdobeAgreementWorkflows
                .FirstOrDefaultAsync(
                    x => x.AgreementId == agreementId,
                    cancellationToken);

            if (workflow == null)
            {
                _logger.LogWarning(
                    "No workflow was found for Adobe agreement {AgreementId}. Notification ID: {NotificationId}.",
                    agreementId,
                    notificationId);

                return;
            }

            // ==================================================
            // 2. CHECK WHETHER THE LOCAL PDF ALREADY EXISTS
            // ==================================================

            if (!string.IsNullOrWhiteSpace(workflow.SignedPdfPath))
            {
                var existingFilePath =
                    _signedPdfStorage.GetAbsolutePath(
                        workflow.SignedPdfPath);

                if (File.Exists(existingFilePath))
                {
                    _logger.LogInformation(
                        "Signed PDF for Adobe agreement {AgreementId} already exists in the application File Cabinet.",
                        agreementId);

                    // IsAvailableInFileCabinet is read-only.
                    // Its value is derived by the workflow model.
                    // The physical file and SignedPdfPath are the
                    // source of truth here.

                    return;
                }

                // The database references a file that no longer exists.
                // Clear the stale information so Adobe can be checked
                // and the signed PDF can be downloaded again.

                _logger.LogWarning(
                    "The local signed PDF for Adobe agreement {AgreementId} was not found. A new download will be attempted.",
                    agreementId);

                workflow.SignedPdfPath = null;
                workflow.SignedPdfSavedAtUtc = null;
                workflow.SignedPdfSizeBytes = null;

                workflow.UpdatedAtUtc = DateTime.UtcNow;

                await _db.SaveChangesAsync(
                    cancellationToken);
            }

            // ==================================================
            // 3. RETRIEVE ADOBE SIGN TOKEN
            // ==================================================

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "Retrieving Adobe Sign access token for agreement {AgreementId}, user {UserId}.",
                    agreementId,
                    workflow.UserId);

                var adobeToken =
                    await _adobeUserTokenService.GetValidTokenAsync(
                        workflow.UserId,
                        cancellationToken);

                if (adobeToken == null ||
                    string.IsNullOrWhiteSpace(adobeToken.AccessToken) ||
                    string.IsNullOrWhiteSpace(adobeToken.ApiAccessPoint))
                {
                    throw new InvalidOperationException(
                        "A valid Adobe Sign access token or API access point could not be obtained.");
                }

                // ==================================================
                // 4. CHECK THE ACTUAL ADOBE AGREEMENT STATUS
                // ==================================================

                _logger.LogInformation(
                    "Checking Adobe Sign status for agreement {AgreementId}.",
                    agreementId);

                var agreementStatus =
                    await _adobeSignService.GetAgreementStatusAsync(
                        agreementId,
                        adobeToken.AccessToken,
                        adobeToken.ApiAccessPoint,
                        cancellationToken);

                if (!IsAdobeAgreementSigned(agreementStatus))
                {
                    _logger.LogInformation(
                        "Adobe agreement {AgreementId} is currently {AgreementStatus}. The signed PDF will not be downloaded.",
                        agreementId,
                        agreementStatus ?? "Unknown");

                    return;
                }

                _logger.LogInformation(
                    "Adobe agreement {AgreementId} has been completed. Preparing to download the signed PDF.",
                    agreementId);

                // ==================================================
                // 5. DOWNLOAD THE COMPLETED SIGNED PDF
                // ==================================================

                var signedPdf =
                    await _adobeSignService.DownloadSignedDocumentAsync(
                        agreementId,
                        adobeToken.AccessToken,
                        adobeToken.ApiAccessPoint,
                        cancellationToken);

                if (signedPdf == null || signedPdf.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Adobe Sign returned an empty signed PDF for agreement {agreementId}.");
                }

                if (!IsPdfContent(signedPdf))
                {
                    throw new InvalidOperationException(
                        $"Adobe Sign returned content that does not appear to be a valid PDF for agreement {agreementId}.");
                }

                _logger.LogInformation(
                    "Signed PDF successfully downloaded from Adobe Sign. Agreement {AgreementId}; file size {FileSizeBytes} bytes.",
                    agreementId,
                    signedPdf.LongLength);

                // ==================================================
                // 6. SAVE SIGNED PDF TO THE APPLICATION FILE CABINET
                // ==================================================

                var relativePath =
                    await _signedPdfStorage.SaveAsync(
                        workflow.UserId,
                        agreementId,
                        signedPdf,
                        cancellationToken);

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    throw new InvalidOperationException(
                        $"The signed PDF for agreement {agreementId} was downloaded, but local storage did not return a file path.");
                }

                // Verify that the storage service actually created
                // the physical file before recording it in the database.

                var absolutePath =
                    _signedPdfStorage.GetAbsolutePath(relativePath);

                if (!File.Exists(absolutePath))
                {
                    throw new InvalidOperationException(
                        $"The signed PDF storage service returned a path, but the physical file was not found for agreement {agreementId}.");
                }

                var savedFileInfo = new FileInfo(absolutePath);

                if (savedFileInfo.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"The signed PDF was saved as an empty file for agreement {agreementId}.");
                }

                // ==================================================
                // 7. UPDATE LOCAL WORKFLOW INFORMATION
                // ==================================================

                var savedAtUtc = DateTime.UtcNow;

                workflow.Status = "Signed";

                workflow.SignedPdfPath = relativePath;

                workflow.SignedPdfSavedAtUtc = savedAtUtc;

                workflow.SignedPdfSizeBytes = savedFileInfo.Length;

                workflow.FailureMessage = null;

                workflow.UpdatedAtUtc = savedAtUtc;

                // Do not assign IsAvailableInFileCabinet.
                // It is a read-only, derived property.
                //
                // Do not mark the DocuWare archive as completed here.
                // DocuWare archiving is handled separately.

                await _db.SaveChangesAsync(
                    cancellationToken);

                _logger.LogInformation(
                    "Signed PDF successfully saved in the application File Cabinet. Agreement {AgreementId}; path {SignedPdfPath}; size {FileSizeBytes} bytes.",
                    agreementId,
                    relativePath,
                    savedFileInfo.Length);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Downloading the signed PDF for Adobe agreement {AgreementId} was cancelled.",
                    agreementId);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to download or save the signed PDF for Adobe agreement {AgreementId}.",
                    agreementId);

                // Record the failure without falsely marking the
                // workflow as completed or archived.

                try
                {
                    workflow.FailureMessage = ex.Message;
                    workflow.UpdatedAtUtc = DateTime.UtcNow;

                    await _db.SaveChangesAsync(
                        CancellationToken.None);
                }
                catch (Exception saveException)
                {
                    _logger.LogError(
                        saveException,
                        "Could not save failure details for Adobe agreement {AgreementId}.",
                        agreementId);
                }

                throw;
            }
        }

        // ======================================================
        // ADOBE AGREEMENT STATUS CHECK
        // ======================================================

        private static bool IsAdobeAgreementSigned(
            string? agreementStatus)
        {
            return string.Equals(
                       agreementStatus?.Trim(),
                       "SIGNED",
                       StringComparison.OrdinalIgnoreCase)
                   ||
                   string.Equals(
                       agreementStatus?.Trim(),
                       "COMPLETED",
                       StringComparison.OrdinalIgnoreCase);
        }

        // ======================================================
        // PDF VALIDATION
        // ======================================================

        private static bool IsPdfContent(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 5)
            {
                return false;
            }

            var bytesToInspect = Math.Min(
                bytes.Length,
                1028);

            return bytes
                .AsSpan(0, bytesToInspect)
                .IndexOf("%PDF-"u8) >= 0;
        }

        // ======================================================
        // SYNC PENDING ADOBE AGREEMENTS
        // ======================================================

        /// <summary>
        /// Checks the user's Adobe agreements and downloads completed
        /// signed PDFs that are not currently available in the
        /// application's local File Cabinet.
        ///
        /// This method does NOT archive documents into DocuWare.
        /// </summary>
        public async Task<int> SyncPendingAgreementsAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException(
                    "A user ID is required.",
                    nameof(userId));
            }

            userId = userId.Trim();

            _logger.LogInformation(
                "Starting Adobe agreement synchronization for user {UserId}.",
                userId);

            // Include workflows with an existing SignedPdfPath because
            // the physical file might have been deleted.
            var pendingWorkflows =
                await _db.AdobeAgreementWorkflows
                    .Where(x =>
                        x.UserId == userId &&
                        x.AgreementId != null &&
                        x.AgreementId != "")
                    .OrderBy(x => x.SentAtUtc)
                    .ToListAsync(cancellationToken);

            var savedCount = 0;
            var alreadyAvailableCount = 0;
            var failedCount = 0;

            foreach (var workflow in pendingWorkflows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Check whether the local signed PDF already exists.
                    if (!string.IsNullOrWhiteSpace(
                            workflow.SignedPdfPath))
                    {
                        var existingPath =
                            _signedPdfStorage.GetAbsolutePath(
                                workflow.SignedPdfPath);

                        if (File.Exists(existingPath))
                        {
                            alreadyAvailableCount++;
                            continue;
                        }
                    }

                    _logger.LogInformation(
                        "Checking Adobe agreement {AgreementId} for user {UserId}.",
                        workflow.AgreementId,
                        userId);

                    // Checks Adobe's actual status. If signing is complete,
                    // downloads and saves the signed PDF locally.
                    await MarkAgreementSignedAsync(
                        workflow.AgreementId!,
                        notificationId: null,
                        cancellationToken);

                    // Reload the workflow to obtain the latest database state.
                    var refreshedWorkflow =
                        await _db.AdobeAgreementWorkflows
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                x =>
                                    x.AgreementId == workflow.AgreementId &&
                                    x.UserId == userId,
                                cancellationToken);

                    if (refreshedWorkflow == null ||
                        string.IsNullOrWhiteSpace(
                            refreshedWorkflow.SignedPdfPath))
                    {
                        continue;
                    }

                    // Confirm that the physical file exists.
                    var absolutePath =
                        _signedPdfStorage.GetAbsolutePath(
                            refreshedWorkflow.SignedPdfPath);

                    if (!File.Exists(absolutePath))
                    {
                        _logger.LogWarning(
                            "Adobe agreement {AgreementId} has a signed PDF path in the database, but the physical file does not exist.",
                            workflow.AgreementId);

                        continue;
                    }

                    // The physical file exists. There is no need to assign
                    // or separately validate the read-only availability property.
                    savedCount++;
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
                        "Could not synchronize Adobe agreement {AgreementId} into the application File Cabinet.",
                        workflow.AgreementId);

                    // Continue processing the remaining agreements.
                }
            }

            _logger.LogInformation(
                "Adobe agreement synchronization completed for user {UserId}. Newly saved: {SavedCount}; already available: {AlreadyAvailableCount}; failed: {FailedCount}; checked: {TotalCount}.",
                userId,
                savedCount,
                alreadyAvailableCount,
                failedCount,
                pendingWorkflows.Count);

            return savedCount;
        }
    }
}
