
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;

namespace DocuWareAdobeSignIntegration.Pages;

public class IndexModel : PageModel
{
    private readonly DocuWareService _docuWareService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAdobeAgreementArchiveService _archiveService;
    private readonly AdobeSignService _adobeSignService;
    private readonly AdobeUserTokenService _adobeUserTokenService;
    private readonly ILogger<IndexModel> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IndexModel(
        DocuWareService docuWareService,
        ApplicationDbContext dbContext,
        IAdobeAgreementArchiveService archiveService,
        AdobeSignService adobeSignService,
        AdobeUserTokenService adobeUserTokenService,
        ILogger<IndexModel> logger)
    {
        _docuWareService = docuWareService
            ?? throw new ArgumentNullException(nameof(docuWareService));

        _dbContext = dbContext
            ?? throw new ArgumentNullException(nameof(dbContext));

        _archiveService = archiveService
            ?? throw new ArgumentNullException(nameof(archiveService));

        _adobeSignService = adobeSignService
            ?? throw new ArgumentNullException(nameof(adobeSignService));

        _adobeUserTokenService = adobeUserTokenService
            ?? throw new ArgumentNullException(nameof(adobeUserTokenService));

        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    // =====================================================
    // DASHBOARD DATA
    // =====================================================

    public List<DocuWareDocumentItem> DocuWareDocuments
    {
        get;
        private set;
    } = new();

    public List<DashboardDocumentItem> DashboardDocuments
    {
        get;
        private set;
    } = new();

    public List<ActivityItem> RecentActivity
    {
        get;
        private set;
    } = new();

    public string? DocuWareError { get; private set; }

    public DateTime? DashboardClearedAtUtc { get; private set; }

    // =====================================================
    // DASHBOARD STATISTICS
    // =====================================================

    public int TotalUploaded => DocuWareDocuments.Count;

    public int AwaitingSignatures =>
        DashboardDocuments.Count(x =>
            x.Status == "Awaiting Signature");

    public int InProgress =>
        DashboardDocuments.Count(x =>
            x.Status == "In Progress");

    public int Signed =>
        DashboardDocuments.Count(x =>
            x.Status == "Signed");

    // Successfully archived documents are counted separately.
    // They do not remain on the active dashboard.
    public int Completed { get; private set; }

    public int Failed =>
        DashboardDocuments.Count(x =>
            x.Status == "Failed");

    // =====================================================
    // CURRENT USER
    // =====================================================

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
    }

    // =====================================================
    // CONFIRM AND SEND EXISTING ADOBE AGREEMENT
    // =====================================================

    public async Task<IActionResult> OnPostConfirmAndSendAsync(
        string? agreementId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(agreementId))
        {
            TempData["ConfirmSendError"] =
                "The Adobe agreement ID is missing. Please refresh your Dashboard.";

            return RedirectToPage();
        }

        agreementId = agreementId.Trim();

        try
        {
            // -------------------------------------------------
            // STEP 1: FIND THE AGREEMENT BELONGING TO THIS USER
            // -------------------------------------------------

            var workflow = await _dbContext.AdobeAgreementWorkflows
                .FirstOrDefaultAsync(
                    x => x.UserId == userId &&
                         x.AgreementId == agreementId,
                    cancellationToken);

            if (workflow == null)
            {
                _logger.LogWarning(
                    "Employee {UserId} attempted to confirm an unknown or inaccessible Adobe agreement.",
                    userId);

                TempData["ConfirmSendError"] =
                    "The agreement could not be found for your account.";

                return RedirectToPage();
            }

            // -------------------------------------------------
            // STEP 2: CHECK THE SAVED WORKFLOW STATUS
            // -------------------------------------------------

            var workflowStatus =
                NormalizeWorkflowStatus(workflow.Status);

            if (workflowStatus != "Authoring")
            {
                TempData["ConfirmSendError"] =
                    "This agreement is no longer awaiting confirmation. Please refresh your Dashboard.";

                return RedirectToPage();
            }

            if (string.IsNullOrWhiteSpace(workflow.AgreementId))
            {
                TempData["ConfirmSendError"] =
                    "The Adobe agreement ID is missing from this workflow.";

                return RedirectToPage();
            }

            // -------------------------------------------------
            // STEP 3: GET A VALID ADOBE SIGN TOKEN
            // -------------------------------------------------

            var adobeToken =
                await _adobeUserTokenService.GetValidTokenAsync(
                    userId,
                    cancellationToken);

            if (adobeToken == null ||
                string.IsNullOrWhiteSpace(adobeToken.AccessToken))
            {
                TempData["ConfirmSendError"] =
                    "Your Adobe Sign access token is unavailable. Please reconnect your Adobe Sign account.";

                return RedirectToPage();
            }

            var apiAccessPoint = adobeToken.ApiAccessPoint;

            if (string.IsNullOrWhiteSpace(apiAccessPoint))
            {
                throw new InvalidOperationException(
                    "Your Adobe Sign API access point is missing. Please reconnect your Adobe Sign account.");
            }

            // -------------------------------------------------
            // STEP 4: SEND THE EXISTING ADOBE AGREEMENT
            // -------------------------------------------------
            //
            // This must send the agreement already created
            // during the SendForSignature process.
            //
            // It must NOT:
            // - Create another Adobe agreement.
            // - Upload another PDF.
            // - Create another workflow record.
            //
            // AdobeSignService is responsible for checking
            // the agreement's current state and requesting
            // the transition from AUTHORING to IN_PROCESS.
            // -------------------------------------------------

            var adobeStatus =
                await _adobeSignService.SendAgreementForSignatureAsync(
                    workflow.AgreementId,
                    adobeToken.AccessToken,
                    apiAccessPoint,
                    cancellationToken);

            // -------------------------------------------------
            // STEP 5: VALIDATE ADOBE'S RESPONSE
            // -------------------------------------------------

            if (string.IsNullOrWhiteSpace(adobeStatus))
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not return an agreement status after the send request.");
            }

            adobeStatus = adobeStatus.Trim();

            var agreementWasSent =
                string.Equals(
                    adobeStatus,
                    "IN_PROCESS",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    adobeStatus,
                    "OUT_FOR_SIGNATURE",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    adobeStatus,
                    "OUT_FOR_SIGNING",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    adobeStatus,
                    "AGREEMENT_VIEWED",
                    StringComparison.OrdinalIgnoreCase);

            if (!agreementWasSent)
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not confirm that the agreement was sent. " +
                    $"Returned status: {adobeStatus}.");
            }

            // -------------------------------------------------
            // STEP 6: UPDATE THE EXISTING WORKFLOW
            // -------------------------------------------------
            //
            // Save only after Adobe confirms the transition.
            // Do not mark the agreement as completed here.
            //
            // It is still awaiting signatures.
            // -------------------------------------------------

            workflow.Status = "OUT_FOR_SIGNATURE";
            workflow.UpdatedAtUtc = DateTime.UtcNow;
            workflow.FailureMessage = null;

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Employee {UserId} confirmed and sent existing Adobe agreement {AgreementId}. Adobe status: {AdobeStatus}.",
                userId,
                workflow.AgreementId,
                adobeStatus);

            var recipientSummary = GetRecipientSummary(workflow);

            TempData["ConfirmSendSuccess"] =
                $"Your document has been sent to the following recipients for signature: {recipientSummary}";

            return RedirectToPage();
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
                "Unable to confirm and send Adobe agreement {AgreementId} for employee {UserId}.",
                agreementId,
                userId);

            TempData["ConfirmSendError"] =
                "The agreement could not be sent. Please refresh the Dashboard and try again.";

            return RedirectToPage();
        }
    }

    private static string GetRecipientSummary(
        AdobeAgreementWorkflow workflow)
    {
        try
        {
            var recipients =
                JsonSerializer.Deserialize<List<AdobeSignRecipient>>(
                    workflow.RecipientsJson,
                    JsonOptions);

            if (recipients != null && recipients.Count > 0)
            {
                return string.Join(
                    "; ",
                    recipients.Select(recipient =>
                        $"{recipient.Name} ({recipient.Email})"));
            }
        }
        catch (JsonException)
        {
            // Use legacy first-signer fields below.
        }

        return $"{workflow.SignerName} ({workflow.SignerEmail})";
    }

    // =====================================================
    // CLEAR DASHBOARD HANDLER
    // =====================================================

    public async Task<IActionResult> OnPostClearDashboardAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        try
        {
            var state = await _dbContext.DashboardUserStates
                .FirstOrDefaultAsync(
                    x => x.UserId == userId,
                    cancellationToken);

            if (state == null)
            {
                state = new DashboardUserState
                {
                    UserId = userId,
                    DashboardClearedAtUtc = DateTime.UtcNow
                };

                _dbContext.DashboardUserStates.Add(state);
            }
            else
            {
                state.DashboardClearedAtUtc = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Dashboard was cleared for employee {UserId}.",
                userId);
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
                "Unable to save Dashboard clear state for employee {UserId}.",
                userId);

            TempData["DashboardClearError"] =
                "The Dashboard could not be cleared. Please try again.";

            return RedirectToPage();
        }

        TempData["DashboardClearSuccess"] =
            "Dashboard cleared. Your documents have not been deleted.";

        return RedirectToPage();
    }

    // =====================================================
    // LOAD DASHBOARD
    // =====================================================

    public async Task OnGetAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            DocuWareError =
                "Unable to identify your signed-in account.";

            return;
        }

        // =================================================
        // STEP 1: SYNCHRONISE SIGNED ADOBE AGREEMENTS
        // =================================================
        //
        // This downloads signed PDFs that are not yet saved
        // locally.
        //
        // Downloading a signed PDF is NOT the same as
        // successfully archiving it in DocuWare.
        // =================================================

        try
        {
            await _archiveService.SyncPendingAgreementsAsync(
                userId,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Adobe Sign synchronisation failed for employee {UserId}. Showing stored workflow statuses.",
                userId);
        }

        List<AdobeAgreementWorkflow> workflows;

        var recipientStatusesByAgreement =
            new Dictionary<string, List<ActivityRecipient>>(
                StringComparer.OrdinalIgnoreCase);

        // =================================================
        // STEP 2: LOAD EMPLOYEE WORKFLOWS AND CLEAR STATE
        // =================================================

        try
        {
            workflows = await _dbContext.AdobeAgreementWorkflows
                .AsNoTracking()
                .Where(workflow => workflow.UserId == userId)
                .OrderByDescending(workflow => workflow.SentAtUtc)
                .ToListAsync(cancellationToken);

            var dashboardState = await _dbContext.DashboardUserStates
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.UserId == userId,
                    cancellationToken);

            DashboardClearedAtUtc =
                dashboardState?.DashboardClearedAtUtc;

            // Count only documents actually archived in DocuWare.
            Completed = workflows
                .Where(IsSavedInDocuWare)
                .Select(x => x.ArchivedDocumentId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            // Retrieve each participant's current Adobe status.
            // RecipientsJson remains the persisted source for names
            // and email addresses; statuses are refreshed from Adobe.
            recipientStatusesByAgreement =
                await LoadRecipientStatusesAsync(
                    userId,
                    workflows,
                    cancellationToken);

            // Successfully archived workflows are displayed
            // on the Completed page, not in active activity.
            RecentActivity = workflows
                .Where(workflow => !IsSavedInDocuWare(workflow))
                .Select(workflow => CreateActivityItem(
                    workflow,
                    GetRecipientsForWorkflow(
                        workflow,
                        recipientStatusesByAgreement)))
                .ToList();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Dashboard database request was cancelled for employee {UserId}.",
                userId);

            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to retrieve employee signing workflows.");

            DocuWareError =
                "Unable to retrieve your signing activity.";

            return;
        }

        // =================================================
        // STEP 3: GET DOCUWARE ACCESS TOKEN
        // =================================================

        var accessToken =
            await HttpContext.GetTokenAsync("access_token");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            DocuWareError =
                "Please connect to DocuWare to retrieve your documents.";

            BuildDashboardFromWorkflows(workflows, recipientStatusesByAgreement);
            ApplyDashboardClearState(workflows);

            return;
        }

        // =================================================
        // STEP 4: RETRIEVE DOCUWARE DOCUMENTS
        // =================================================

        try
        {
            DocuWareDocuments =
                await _docuWareService.GetDocumentsAsync(
                    accessToken,
                    cancellationToken);

            _logger.LogInformation(
                "Retrieved {DocumentCount} DocuWare documents for employee {UserId}.",
                DocuWareDocuments.Count,
                userId);

            BuildDashboardFromDocuWareAndWorkflows(workflows, recipientStatusesByAgreement);
            ApplyDashboardClearState(workflows);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested ||
                  HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Dashboard document retrieval was cancelled for employee {UserId}.",
                userId);

            return;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare document retrieval timed out for employee {UserId}.",
                userId);

            DocuWareError =
                "DocuWare is taking too long to respond. Your signing activity is still available below.";

            BuildDashboardFromWorkflows(workflows, recipientStatusesByAgreement);
            ApplyDashboardClearState(workflows);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "DocuWare document retrieval failed for employee {UserId}.",
                userId);

            DocuWareError =
                "Unable to retrieve documents from DocuWare. Your signing activity is still available below.";

            BuildDashboardFromWorkflows(workflows, recipientStatusesByAgreement);
            ApplyDashboardClearState(workflows);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare authentication failed for employee {UserId}.",
                userId);

            DocuWareError =
                "Your DocuWare session is unavailable. Please reconnect to DocuWare. Your signing activity is still available.";

            BuildDashboardFromWorkflows(workflows, recipientStatusesByAgreement);
            ApplyDashboardClearState(workflows);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected dashboard loading error for employee {UserId}.",
                userId);

            DocuWareError =
                "Unable to retrieve dashboard information. Your saved signing activity is still available.";

            BuildDashboardFromWorkflows(workflows, recipientStatusesByAgreement);
            ApplyDashboardClearState(workflows);
        }
    }

    // =====================================================
    // APPLY PERSISTENT DASHBOARD CLEAR STATE
    // =====================================================

    private void ApplyDashboardClearState(
        IReadOnlyCollection<AdobeAgreementWorkflow> workflows)
    {
        if (!DashboardClearedAtUtc.HasValue)
        {
            return;
        }

        var clearedAtUtc =
            EnsureUtc(DashboardClearedAtUtc.Value);

        // Workflow cards must be filtered using the application's
        // persisted workflow timestamp, not DocuWare's CreatedAt.
        // DocuWare can omit CreatedAt for a document, which previously
        // caused every active signing card to disappear after Clear Dashboard.
        var workflowAgreementsCreatedAfterClear = workflows
            .Where(workflow =>
                !string.IsNullOrWhiteSpace(workflow.AgreementId) &&
                EnsureUtc(workflow.SentAtUtc) > clearedAtUtc)
            .Select(workflow => workflow.AgreementId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var beforeCount = DashboardDocuments.Count;

        DashboardDocuments = DashboardDocuments
            .Where(item =>
            {
                if (item.HasWorkflow &&
                    !string.IsNullOrWhiteSpace(item.AgreementId))
                {
                    return workflowAgreementsCreatedAfterClear.Contains(
                        item.AgreementId.Trim());
                }

                // For ordinary DocuWare documents without a signing
                // workflow, continue using the DocuWare creation date.
                return item.Document.CreatedAt.HasValue &&
                       EnsureUtc(item.Document.CreatedAt.Value) > clearedAtUtc;
            })
            .ToList();

        RecentActivity = RecentActivity
            .Where(item =>
                EnsureUtc(item.SentAt) > clearedAtUtc)
            .ToList();

        _logger.LogInformation(
            "Dashboard clear-state filter retained {VisibleCount} of {BeforeCount} document cards. {WorkflowCount} signing workflows were created after the clear timestamp.",
            DashboardDocuments.Count,
            beforeCount,
            workflowAgreementsCreatedAfterClear.Count);
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    // =====================================================
    // CHECK WHETHER DOCUMENT WAS SUCCESSFULLY ARCHIVED
    // =====================================================

    private static bool IsSavedInDocuWare(
        AdobeAgreementWorkflow workflow)
    {
        return workflow.IsArchived;
    }

    // =====================================================
    // GET DOCUWARE DOCUMENT IDS THAT MUST BE HIDDEN
    // =====================================================

    private static HashSet<string> GetArchivedDocumentIds(
        List<AdobeAgreementWorkflow> workflows)
    {
        var archivedIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var workflow in workflows)
        {
            if (!IsSavedInDocuWare(workflow))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(
                workflow.OriginalDocumentId))
            {
                archivedIds.Add(
                    workflow.OriginalDocumentId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(
                workflow.ArchivedDocumentId))
            {
                archivedIds.Add(
                    workflow.ArchivedDocumentId.Trim());
            }
        }

        return archivedIds;
    }

    // =====================================================
    // BUILD DASHBOARD USING DOCUWARE + WORKFLOWS
    // =====================================================

    private void BuildDashboardFromDocuWareAndWorkflows(
        List<AdobeAgreementWorkflow> workflows,
        IReadOnlyDictionary<string, List<ActivityRecipient>> recipientStatusesByAgreement)
    {
        var archivedDocumentIds =
            GetArchivedDocumentIds(workflows);

        // Workflows are already ordered newest first.
        // Grouping therefore selects the latest workflow
        // for each original DocuWare document.
        var latestWorkflowByDocument = workflows
            .Where(workflow =>
                !string.IsNullOrWhiteSpace(
                    workflow.OriginalDocumentId))
            .GroupBy(
                workflow => workflow.OriginalDocumentId!,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var displayedDocumentIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        DashboardDocuments = new List<DashboardDocumentItem>();

        // -------------------------------------------------
        // FIRST: DOCUWARE DOCUMENTS
        // -------------------------------------------------

        foreach (var document in DocuWareDocuments)
        {
            var documentId = document.Id.ToString(
                CultureInfo.InvariantCulture);

            displayedDocumentIds.Add(documentId);

            // Successfully archived documents must not appear
            // in the active Dashboard.
            if (archivedDocumentIds.Contains(documentId))
            {
                continue;
            }

            latestWorkflowByDocument.TryGetValue(
                documentId,
                out var workflow);

            DashboardDocuments.Add(
                CreateDashboardDocument(
                    document,
                    workflow,
                    recipientStatusesByAgreement));
        }

        // -------------------------------------------------
        // SECOND: WORKFLOWS NOT RETURNED BY DOCUWARE
        // -------------------------------------------------

        foreach (var workflow in workflows)
        {
            if (string.IsNullOrWhiteSpace(
                workflow.OriginalDocumentId))
            {
                continue;
            }

            var originalDocumentId =
                workflow.OriginalDocumentId.Trim();

            if (IsSavedInDocuWare(workflow) ||
                archivedDocumentIds.Contains(originalDocumentId))
            {
                continue;
            }

            if (!int.TryParse(
                originalDocumentId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var documentId))
            {
                continue;
            }

            // Avoid duplicate cards for the same original document.
            if (!displayedDocumentIds.Add(originalDocumentId))
            {
                continue;
            }

            var document = new DocuWareDocumentItem(
                documentId,
                string.IsNullOrWhiteSpace(workflow.FileName)
                    ? $"Document {documentId}"
                    : workflow.FileName,
                workflow.SentAtUtc,
                workflow.SentAtUtc);

            DashboardDocuments.Add(
                CreateDashboardDocument(
                    document,
                    workflow,
                    recipientStatusesByAgreement));
        }
    }

    // =====================================================
    // BUILD DASHBOARD WHEN DOCUWARE IS UNAVAILABLE
    // =====================================================

    private void BuildDashboardFromWorkflows(
        List<AdobeAgreementWorkflow> workflows,
        IReadOnlyDictionary<string, List<ActivityRecipient>> recipientStatusesByAgreement)
    {
        var archivedDocumentIds =
            GetArchivedDocumentIds(workflows);

        var displayedDocumentIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        DashboardDocuments = new List<DashboardDocumentItem>();

        foreach (var workflow in workflows)
        {
            if (IsSavedInDocuWare(workflow))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(
                workflow.OriginalDocumentId) ||
                !int.TryParse(
                    workflow.OriginalDocumentId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var documentId))
            {
                _logger.LogWarning(
                    "Workflow {WorkflowId} has an invalid DocuWare document ID.",
                    workflow.Id);

                continue;
            }

            var originalDocumentId =
                workflow.OriginalDocumentId.Trim();

            if (archivedDocumentIds.Contains(originalDocumentId))
            {
                continue;
            }

            // Only show the newest workflow for each document.
            if (!displayedDocumentIds.Add(originalDocumentId))
            {
                continue;
            }

            var document = new DocuWareDocumentItem(
                documentId,
                string.IsNullOrWhiteSpace(workflow.FileName)
                    ? $"Document {documentId}"
                    : workflow.FileName,
                workflow.SentAtUtc,
                workflow.SentAtUtc);

            DashboardDocuments.Add(
                CreateDashboardDocument(
                    document,
                    workflow,
                    recipientStatusesByAgreement));
        }
    }

    // =====================================================
    // MAP DOCUWARE DOCUMENT + WORKFLOW STATUS
    // =====================================================

    private static DashboardDocumentItem CreateDashboardDocument(
        DocuWareDocumentItem document,
        AdobeAgreementWorkflow? workflow,
        IReadOnlyDictionary<string, List<ActivityRecipient>> recipientStatusesByAgreement)
    {
        // A DocuWare document with no Adobe workflow
        // has only been uploaded.
        if (workflow == null)
        {
            return new DashboardDocumentItem(
                document,
                "Uploaded",
                "uploaded",
                25,
                "Document uploaded and waiting to be sent for signature.",
                false,
                null,
                new List<ActivityRecipient>());
        }

        var recipients =
            GetRecipientsForWorkflow(
                workflow,
                recipientStatusesByAgreement);

        var status =
            NormalizeWorkflowStatus(workflow.Status);

        // If one or more recipients have completed their part while
        // other recipients remain, present the agreement as In Progress.
        if (status == "Awaiting Signature" &&
            recipients.Any(recipient => recipient.IsSigned) &&
            recipients.Any(recipient => !recipient.IsSigned))
        {
            status = "In Progress";
        }

        // Completed means DocuWare archival succeeded.
        // If an older workflow has Completed status but is
        // not actually archived, show it as Signed instead.
        if (status == "Completed" &&
            !IsSavedInDocuWare(workflow))
        {
            status = "Signed";
        }

        var progress = status switch
        {
            "Uploaded" => 0,
            "Authoring" => 0,
            "Awaiting Signature" => 0,
            "In Progress" => 0,
            "Signed" => 100,
            "Completed" => 100,
            "Failed" => 0,
            _ => 0
        };

        // When Adobe returns a status for every configured recipient,
        // show actual signer completion (for example, 1 of 2 = 50%).
        // If the Adobe status lookup fails, keep the percentage at the
        // safe fallback below rather than pretending to know progress.
        var recipientStatusesAvailable =
            recipients.Count > 0 &&
            recipients.All(recipient =>
                !string.Equals(
                    recipient.Status,
                    "UNKNOWN",
                    StringComparison.OrdinalIgnoreCase));

        if (status == "Signed" || status == "Completed")
        {
            progress = 100;
        }
        else if (recipientStatusesAvailable && recipients.Count > 0)
        {
            progress = (int)Math.Round(
                recipients.Count(recipient => recipient.IsSigned) * 100d /
                recipients.Count,
                MidpointRounding.AwayFromZero);
        }
        else
        {
            progress = status switch
            {
                "Authoring" => 10,
                "Awaiting Signature" => 20,
                "In Progress" => 35,
                "Failed" => 0,
                _ => 0
            };
        }

        var description = status switch
        {
            "Uploaded" =>
                "Document uploaded and waiting to be sent for signature.",

            "Authoring" =>
                "The agreement was created in Adobe, but the automatic send has not been confirmed. Use the retry action below to send this existing agreement; do not create a duplicate.",

            "Awaiting Signature" =>
                "The agreement has been sent to Adobe Sign and is waiting for the recipients to sign.",

            "In Progress" =>
                "The agreement is in progress. One or more recipients have opened or are working through the signing process.",

            "Signed" =>
                GetSignedWorkflowDescription(workflow),

            "Completed" =>
                "The signed PDF has been successfully saved to DocuWare.",

            "Failed" =>
                !string.IsNullOrWhiteSpace(
                    workflow.DocuWareArchiveError)
                    ? workflow.DocuWareArchiveError
                    : !string.IsNullOrWhiteSpace(
                        workflow.FailureMessage)
                        ? workflow.FailureMessage
                        : "The signing or document-saving process failed.",

            _ =>
                "Document uploaded and waiting to be sent for signature."
        };

        return new DashboardDocumentItem(
            document,
            status,
            status.ToLowerInvariant().Replace(" ", "-"),
            progress,
            description,
            true,
            workflow.AgreementId,
            recipients);
    }

    // =====================================================
    // SIGNED WORKFLOW DESCRIPTION
    // =====================================================

    private static string GetSignedWorkflowDescription(
        AdobeAgreementWorkflow workflow)
    {
        // The PDF may already be saved locally, but the
        // employee must still explicitly save it to DocuWare.
        if (workflow.IsAvailableInFileCabinet)
        {
            if (string.Equals(
                workflow.DocuWareArchiveStatus,
                "Failed",
                StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(
                    workflow.DocuWareArchiveError)
                    ? "The agreement is signed and its PDF is saved locally, but the DocuWare save failed: " +
                      workflow.DocuWareArchiveError
                    : "The agreement is signed and saved locally, but the DocuWare save failed. Please retry from the File Cabinet.";
            }

            if (string.Equals(
                workflow.DocuWareArchiveStatus,
                "Processing",
                StringComparison.OrdinalIgnoreCase))
            {
                return "The agreement is signed. The signed PDF is being saved to DocuWare.";
            }

            return "Adobe Sign confirms that the agreement is signed and the PDF is saved locally. Choose Save to DocuWare to complete the process.";
        }

        return "Adobe Sign confirms that the agreement is signed. The signed PDF is waiting to be saved in the File Cabinet and then archived in DocuWare.";
    }

    // =====================================================
    // NORMALISE ADOBE SIGN / APPLICATION STATUS
    // =====================================================

    private static string NormalizeWorkflowStatus(
        string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "UPLOADED" => "Uploaded",

            // Agreement exists in Adobe but has not yet
            // been sent to the recipients.
            "AUTHORING" => "Authoring",
            "PREPARING IN ADOBE" => "Authoring",
            "DRAFT" => "Authoring",
            "CREATED" => "Authoring",
            "DOCUMENTS_NOT_YET_PROCESSED" => "Authoring",

            "AWAITING SIGNATURE" => "Awaiting Signature",
            "OUT_FOR_SIGNATURE" => "Awaiting Signature",
            "OUT_FOR_SIGNING" => "Awaiting Signature",
            "SENT" => "Awaiting Signature",

            "IN PROGRESS" => "In Progress",
            "IN_PROCESS" => "In Progress",
            "AGREEMENT_VIEWED" => "In Progress",

            "SIGNED" => "Signed",
            "SIGNATURE COMPLETED" => "Signed",
            "PROCESSING SIGNED PDF" => "Signed",

            "COMPLETED" => "Completed",
            "COMPLETE" => "Completed",

            "FAILED" => "Failed",
            "CANCELLED" => "Failed",
            "CANCELED" => "Failed",
            "EXPIRED" => "Failed",
            "ABORTED" => "Failed",
            "REJECTED" => "Failed",

            _ => "Awaiting Signature"
        };
    }

    // =====================================================
    // CREATE RECENT ACTIVITY ITEM
    // =====================================================

    private static ActivityItem CreateActivityItem(
        AdobeAgreementWorkflow workflow,
        List<ActivityRecipient> recipients)
    {
        var status =
            NormalizeWorkflowStatus(workflow.Status);

        // A workflow that is not actually archived must
        // not be presented as fully completed.
        if (status == "Completed" &&
            !IsSavedInDocuWare(workflow))
        {
            status = "Signed";
        }

        if (status == "Awaiting Signature" &&
            recipients.Any(recipient => recipient.IsSigned) &&
            recipients.Any(recipient => !recipient.IsSigned))
        {
            status = "In Progress";
        }

        // Agreement-level progress:
        // 0 = not sent / authoring
        // 1 = emailed
        // 2 = opened / in progress
        // 3 = signed
        // 4 = saved in DocuWare
        var progress = status switch
        {
            "Uploaded" => 0,
            "Authoring" => 0,
            "Awaiting Signature" => 1,
            "In Progress" => 2,
            "Signed" => 3,
            "Completed" => 4,
            "Failed" => 0,
            _ => 0
        };

        var finalStep = status switch
        {
            "Completed" => "Saved in DocuWare",
            "Signed" => "Signed - awaiting employee save",
            "Failed" => "Processing failed",
            "In Progress" => "Signature in progress",
            "Awaiting Signature" => "Waiting for signer",
            "Authoring" => "Authoring - awaiting confirmation",
            "Uploaded" => "Uploaded - awaiting signature",
            _ => "Submitted"
        };

        var dateLabel = status == "Authoring"
            ? "Created "
            : "Started ";

        var searchText = string.Join(
            " ",
            new[]
            {
                workflow.FileName ?? string.Empty,
                workflow.SignerName ?? string.Empty,
                workflow.SignerEmail ?? string.Empty,
                status,
                string.Join(
                    " ",
                    recipients.Select(x => $"{x.Name} {x.Email}"))
            })
            .ToLowerInvariant();

        return new ActivityItem(
            workflow.FileName ?? "Untitled document",
            status,
            status.ToLowerInvariant().Replace(" ", "-"),
            workflow.SentAtUtc,
            dateLabel,
            progress,
            finalStep,
            recipients,
            searchText);
    }

    // =====================================================
    // LOAD LIVE RECIPIENT STATUSES FROM ADOBE
    // =====================================================

    private async Task<Dictionary<string, List<ActivityRecipient>>>
        LoadRecipientStatusesAsync(
            string userId,
            List<AdobeAgreementWorkflow> workflows,
            CancellationToken cancellationToken)
    {
        var result =
            new Dictionary<string, List<ActivityRecipient>>(
                StringComparer.OrdinalIgnoreCase);

        var workflowsToRefresh = workflows
            .Where(workflow =>
                !IsSavedInDocuWare(workflow) &&
                !string.IsNullOrWhiteSpace(workflow.AgreementId))
            .ToList();

        if (workflowsToRefresh.Count == 0)
        {
            return result;
        }

        AdobeStoredToken adobeToken;

        try
        {
            adobeToken =
                await _adobeUserTokenService.GetValidTokenAsync(
                    userId,
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not retrieve an Adobe token to refresh recipient statuses for employee {UserId}. Stored recipient details will be displayed.",
                userId);

            return result;
        }

        if (adobeToken == null ||
            string.IsNullOrWhiteSpace(adobeToken.AccessToken) ||
            string.IsNullOrWhiteSpace(adobeToken.ApiAccessPoint))
        {
            _logger.LogWarning(
                "Adobe token is unavailable while refreshing recipient statuses for employee {UserId}.",
                userId);

            return result;
        }

        foreach (var workflow in workflowsToRefresh)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var liveStatuses =
                    await _adobeSignService.GetAgreementRecipientStatusesAsync(
                        workflow.AgreementId!,
                        adobeToken.AccessToken,
                        adobeToken.ApiAccessPoint,
                        cancellationToken);

                result[workflow.AgreementId!] =
                    GetActivityRecipients(
                        workflow,
                        liveStatuses);

                _logger.LogDebug(
                    "Refreshed {RecipientCount} recipient statuses for Adobe agreement {AgreementId}.",
                    result[workflow.AgreementId!].Count,
                    workflow.AgreementId);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not refresh recipient statuses for Adobe agreement {AgreementId}. Stored recipient details will be displayed.",
                    workflow.AgreementId);
            }
        }

        return result;
    }

    // =====================================================
    // GET RECIPIENTS FOR A WORKFLOW
    // =====================================================

    private static List<ActivityRecipient> GetRecipientsForWorkflow(
        AdobeAgreementWorkflow workflow,
        IReadOnlyDictionary<string, List<ActivityRecipient>> recipientStatusesByAgreement)
    {
        if (!string.IsNullOrWhiteSpace(workflow.AgreementId) &&
            recipientStatusesByAgreement.TryGetValue(
                workflow.AgreementId,
                out var recipients))
        {
            return recipients;
        }

        return GetActivityRecipients(workflow);
    }

    // =====================================================
    // DESERIALISE RECIPIENTS AND APPLY LIVE STATUS
    // =====================================================

    private static List<ActivityRecipient> GetActivityRecipients(
        AdobeAgreementWorkflow workflow,
        IEnumerable<AdobeSignRecipientStatus>? liveStatuses = null)
    {
        var savedRecipients = new List<AdobeSignRecipient>();

        // RecipientsJson is the persisted source for all signers.
        if (!string.IsNullOrWhiteSpace(workflow.RecipientsJson))
        {
            try
            {
                savedRecipients =
                    JsonSerializer.Deserialize<List<AdobeSignRecipient>>(
                        workflow.RecipientsJson,
                        JsonOptions)
                    ?? new List<AdobeSignRecipient>();
            }
            catch (JsonException)
            {
                // Fall back to the legacy single-signer fields below.
                savedRecipients.Clear();
            }
        }

        // Backward compatibility for older single-signer workflows.
        if (savedRecipients.Count == 0)
        {
            savedRecipients.Add(new AdobeSignRecipient
            {
                Name = string.IsNullOrWhiteSpace(workflow.SignerName)
                    ? "Not assigned"
                    : workflow.SignerName,

                Email = workflow.SignerEmail ?? string.Empty
            });
        }

        var liveByEmail = (liveStatuses ?? Enumerable.Empty<AdobeSignRecipientStatus>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(
                x => x.Email.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        return savedRecipients
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Email) ||
                !string.IsNullOrWhiteSpace(x.Name))
            .Select(saved =>
            {
                var email = saved.Email?.Trim() ?? string.Empty;

                liveByEmail.TryGetValue(
                    email,
                    out var live);

                var rawStatus =
                    live?.Status ?? "UNKNOWN";

                var name =
                    !string.IsNullOrWhiteSpace(saved.Name)
                        ? saved.Name.Trim()
                        : !string.IsNullOrWhiteSpace(live?.Name)
                            ? live.Name
                            : "Recipient";

                return new ActivityRecipient(
                    name,
                    email,
                    rawStatus,
                    GetRecipientStatusLabel(rawStatus),
                    IsRecipientSigned(rawStatus));
            })
            .ToList();
    }

    private static bool IsRecipientSigned(string? status)
    {
        var normalized = status?.Trim();

        // WAITING_FOR_OTHERS means this participant has completed
        // their own action and Adobe is waiting for later participants.
        return string.Equals(normalized, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "SIGNED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "APPROVED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "WAITING_FOR_OTHERS", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRecipientStatusLabel(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "COMPLETED" or "SIGNED" or "APPROVED" =>
                "Signed",

            "WAITING_FOR_MY_SIGNATURE" =>
                "Awaiting signature",

            "NOT_YET_VISIBLE" =>
                "Waiting for previous signer",

            "WAITING_FOR_OTHERS" =>
                "Signed — waiting for other signers",

            "WAITING_FOR_MY_APPROVAL" =>
                "Awaiting approval",

            "WAITING_FOR_MY_ACKNOWLEDGEMENT" =>
                "Awaiting acknowledgement",

            "WAITING_FOR_MY_ACCEPTANCE" =>
                "Awaiting acceptance",

            "WAITING_FOR_MY_FORM_FILLING" =>
                "Awaiting form completion",

            "CANCELLED" or "CANCELED" =>
                "Cancelled",

            "EXPIRED" =>
                "Expired",

            "ABORTED" or "REJECTED" or "DECLINED" =>
                "Signing failed",

            "ACTIVE" =>
                "Invitation active",

            _ =>
                "Status unavailable"
        };
    }

}

// =====================================================
// DASHBOARD VIEW MODELS
// =====================================================

public record DashboardDocumentItem(
    DocuWareDocumentItem Document,
    string Status,
    string StatusClass,
    int Progress,
    string Description,
    bool HasWorkflow,
    string? AgreementId,
    List<ActivityRecipient> Recipients);

public record ActivityRecipient(
    string Name,
    string Email,
    string Status,
    string StatusLabel,
    bool IsSigned);

public record ActivityItem(
    string Name,
    string Status,
    string StatusClass,
    DateTime SentAt,
    string DateLabel,
    int Progress,
    string FinalStep,
    List<ActivityRecipient> Recipients,
    string SearchText);
