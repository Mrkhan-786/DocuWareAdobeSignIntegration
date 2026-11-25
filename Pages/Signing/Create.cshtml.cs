using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Pages.Signing;

public class SendForSignatureModel : PageModel
{
    private const long MaxFileSize = 10 * 1024 * 1024;
    private const int MaxSigners = 10;
    private const int PendingUploadLifetimeHours = 24;

    private readonly DocuWareDocumentService _docuWareDocumentService;
    private readonly DocuWareOAuthTokenService _docuWareOAuthTokenService;
    private readonly AdobeSignService _adobeSignService;
    private readonly AdobeUserTokenService _adobeUserTokenService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SendForSignatureModel> _logger;
    private readonly IWebHostEnvironment _environment;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SendForSignatureModel(
        DocuWareDocumentService docuWareDocumentService,
        DocuWareOAuthTokenService docuWareOAuthTokenService,
        AdobeSignService adobeSignService,
        AdobeUserTokenService adobeUserTokenService,
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        ILogger<SendForSignatureModel> logger,
        IWebHostEnvironment environment)
    {
        _docuWareDocumentService =
            docuWareDocumentService
            ?? throw new ArgumentNullException(nameof(docuWareDocumentService));

        _docuWareOAuthTokenService =
            docuWareOAuthTokenService
            ?? throw new ArgumentNullException(nameof(docuWareOAuthTokenService));

        _adobeSignService =
            adobeSignService
            ?? throw new ArgumentNullException(nameof(adobeSignService));

        _adobeUserTokenService =
            adobeUserTokenService
            ?? throw new ArgumentNullException(nameof(adobeUserTokenService));

        _dbContext =
            dbContext
            ?? throw new ArgumentNullException(nameof(dbContext));

        _configuration =
            configuration
            ?? throw new ArgumentNullException(nameof(configuration));

        _logger =
            logger
            ?? throw new ArgumentNullException(nameof(logger));

        _environment =
            environment
            ?? throw new ArgumentNullException(nameof(environment));
    }

    // ======================================================
    // STEP 1: PDF UPLOAD
    // ======================================================

    [BindProperty]
    [StringLength(32)]
    public string? PendingUploadToken { get; set; }

    [BindProperty]
    [StringLength(255)]
    public string? PendingUploadFileName { get; set; }

    [BindProperty]
    public IFormFile? UploadedFile { get; set; }

    // ======================================================
    // STEP 2: DOCUMENT INFORMATION
    // ======================================================

    [BindProperty]
    [Required(ErrorMessage = "Please enter the document type.")]
    [StringLength(100)]
    public string DocumentType { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Please select the request date.")]
    [DataType(DataType.Date)]
    public DateTime? RequestDate { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Please enter the recipient company.")]
    [StringLength(200)]
    public string RecipientCompany { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Please enter the recipient email address.")]
    [EmailAddress(ErrorMessage = "Please enter a valid recipient email address.")]
    [StringLength(320)]
    public string RecipientEmail { get; set; } = string.Empty;

    // ======================================================
    // STEP 3: SIGNERS
    // ======================================================

    [BindProperty]
    public int SignerCount { get; set; } = 1;

    [BindProperty]
    public List<SignerFormInput> Signers { get; set; } = new();

    [BindProperty]
    [Required(ErrorMessage = "Choose a signing order.")]
    public string SigningOrderMode { get; set; } = "Sequential";

    // Backward compatibility with the older single-signer form.
    [BindProperty]
    public string? SignerName { get; set; }

    [BindProperty]
    public string? SignerEmail { get; set; }

    // ======================================================
    // STEP 4: EMAIL INFORMATION
    // ======================================================

    [BindProperty]
    [Required(ErrorMessage = "Please enter an email subject.")]
    [StringLength(200)]
    public string EmailSubject { get; set; } =
        "Please sign this document";

    [BindProperty]
    [StringLength(4000)]
    public string? Message { get; set; }

    // ======================================================
    // PAGE MESSAGES
    // ======================================================

    public string? ErrorMessage { get; set; }

    public string? SuccessMessage { get; set; }

    public string? UploadedDocumentId { get; set; }

    public string? AgreementId { get; set; }

    // ======================================================
    // GET
    // ======================================================

    public IActionResult OnGet()
    {
        var error = Request.Query["error"].ToString();

        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorMessage = error;
        }

        RequestDate = DateTime.Today;
        SigningOrderMode = "Sequential";
        SignerCount = 1;

        Signers = new List<SignerFormInput>
        {
            new()
            {
                SignatureAnchorText = "Signature:",
                NameAnchorText = "Full Name:",
                DateAnchorText = "Date:",
                DateFieldValue = null
            }
        };

        return Page();
    }

    // ======================================================
    // POST
    // ======================================================

    public async Task<IActionResult> OnPostAsync(
        CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        SuccessMessage = null;
        UploadedDocumentId = null;
        AgreementId = null;

        var userId =
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            User.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(userId))
        {
            ErrorMessage =
                "Your user session could not be identified. Please sign in again.";

            _logger.LogWarning(
                "Signature submission stopped because the authenticated user ID was missing.");

            EnsureSignerForRedisplay();

            return Page();
        }

        _logger.LogInformation(
            "Signature submission started for employee {UserId}.",
            userId);

        byte[]? pdfBytes = null;
        string fileName = string.Empty;

        // Once the workflow row exists, failures must return the user
        // to the Dashboard instead of inviting a duplicate submission.
        var workflowSaved = false;

        // ==================================================
        // 1. PRESERVE OR RETRIEVE THE UPLOADED PDF
        // ==================================================

        try
        {
            _logger.LogInformation(
                "Checking uploaded PDF or pending upload token for employee {UserId}.",
                userId);

            if (UploadedFile != null)
            {
                fileName = NormalizePdfFileName(
                    UploadedFile.FileName);

                if (fileName.Length > 255)
                {
                    ModelState.AddModelError(
                        nameof(UploadedFile),
                        "The PDF filename cannot exceed 255 characters.");
                }
                else if (UploadedFile.Length == 0)
                {
                    ModelState.AddModelError(
                        nameof(UploadedFile),
                        "The selected document is empty.");
                }
                else if (UploadedFile.Length > MaxFileSize)
                {
                    ModelState.AddModelError(
                        nameof(UploadedFile),
                        "The PDF file cannot exceed 10 MB.");
                }
                else
                {
                    await using var uploadStream = new MemoryStream();

                    await UploadedFile.CopyToAsync(
                        uploadStream,
                        cancellationToken);

                    pdfBytes = uploadStream.ToArray();

                    if (pdfBytes.Length == 0)
                    {
                        ModelState.AddModelError(
                            nameof(UploadedFile),
                            "The uploaded document is empty.");
                    }
                    else if (pdfBytes.Length > MaxFileSize)
                    {
                        ModelState.AddModelError(
                            nameof(UploadedFile),
                            "The PDF file cannot exceed 10 MB.");
                    }
                    else if (!IsPdfContent(pdfBytes))
                    {
                        ModelState.AddModelError(
                            nameof(UploadedFile),
                            "The selected file is not recognised as a PDF. Please select an actual PDF document.");
                    }
                    else
                    {
                        await DeletePendingUploadAsync(
                            userId,
                            PendingUploadToken);

                        PendingUploadToken =
                            await SavePendingUploadAsync(
                                userId,
                                pdfBytes,
                                cancellationToken);

                        PendingUploadFileName = fileName;

                        ModelState.Remove(nameof(PendingUploadToken));
                        ModelState.Remove(nameof(PendingUploadFileName));

                        _logger.LogInformation(
                            "PDF temporarily saved for employee {UserId}. Filename: {FileName}, size: {FileSize} bytes.",
                            userId,
                            fileName,
                            pdfBytes.Length);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(PendingUploadToken))
            {
                fileName = NormalizePdfFileName(
                    PendingUploadFileName);

                if (fileName.Length > 255)
                {
                    ModelState.AddModelError(
                        nameof(UploadedFile),
                        "The PDF filename cannot exceed 255 characters.");
                }
                else
                {
                    pdfBytes = await LoadPendingUploadAsync(
                        userId,
                        PendingUploadToken,
                        cancellationToken);

                    if (pdfBytes == null)
                    {
                        ModelState.AddModelError(
                            nameof(UploadedFile),
                            "Your previously uploaded PDF has expired or is unavailable. Please select the PDF again.");
                    }
                    else if (pdfBytes.Length > MaxFileSize)
                    {
                        ModelState.AddModelError(
                            nameof(UploadedFile),
                            "The PDF file cannot exceed 10 MB.");
                    }
                    else if (!IsPdfContent(pdfBytes))
                    {
                        ModelState.AddModelError(
                            nameof(UploadedFile),
                            "The preserved document is not recognised as a valid PDF. Please select it again.");
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Previously uploaded PDF restored for employee {UserId}. Filename: {FileName}.",
                            userId,
                            fileName);
                    }
                }
            }
            else
            {
                ModelState.AddModelError(
                    nameof(UploadedFile),
                    "Please select a PDF document.");
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage =
                "The upload was cancelled. Please try again.";

            EnsureSignerForRedisplay();

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to preserve or restore the uploaded PDF for employee {UserId}.",
                userId);

            ErrorMessage =
                "The uploaded PDF could not be processed temporarily. Please select the PDF again.";

            EnsureSignerForRedisplay();

            return Page();
        }

        // ==================================================
        // 2. PREPARE AND VALIDATE SIGNERS
        // ==================================================

        PrepareSignerCollection();

        // Remove the legacy single-signer binding errors.
        ModelState.Remove(nameof(SignerName));
        ModelState.Remove(nameof(SignerEmail));

        ValidateSigners();

        if (Signers.Count < 1 || Signers.Count > MaxSigners)
        {
            ModelState.AddModelError(
                nameof(Signers),
                $"Choose between 1 and {MaxSigners} signers.");
        }

        if (!string.Equals(
                SigningOrderMode,
                "Sequential",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                SigningOrderMode,
                "Parallel",
                StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                nameof(SigningOrderMode),
                "Choose Sequential or Parallel signing.");
        }
        else
        {
            SigningOrderMode =
                string.Equals(
                    SigningOrderMode,
                    "Parallel",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Parallel"
                    : "Sequential";
        }

        // ==================================================
        // 3. VALIDATE REQUEST AND PDF
        // ==================================================

        if (pdfBytes == null)
        {
            ModelState.AddModelError(
                nameof(UploadedFile),
                "A valid PDF document is required.");
        }
        else if (!IsPdfContent(pdfBytes))
        {
            ModelState.AddModelError(
                nameof(UploadedFile),
                "The selected file does not appear to be a valid PDF.");
        }

        if (RequestDate.HasValue &&
            RequestDate.Value.Date > DateTime.Today)
        {
            ModelState.AddModelError(
                nameof(RequestDate),
                "The request date cannot be in the future.");
        }

        if (!string.IsNullOrWhiteSpace(RecipientEmail) &&
            !System.Net.Mail.MailAddress.TryCreate(
                RecipientEmail.Trim(),
                out _))
        {
            ModelState.AddModelError(
                nameof(RecipientEmail),
                "Please enter a valid recipient email address.");
        }

        _logger.LogInformation(
            "Form validation checkpoint for employee {UserId}. ModelState valid: {IsValid}; PDF available: {PdfAvailable}; signer count: {SignerCount}.",
            userId,
            ModelState.IsValid,
            pdfBytes != null,
            Signers.Count);

        LogModelStateErrors();

        if (!ModelState.IsValid || pdfBytes == null)
        {
            _logger.LogWarning(
                "Signature submission stopped before external service authentication for employee {UserId}.",
                userId);

            EnsureSignerForRedisplay();

            return Page();
        }

        // ==================================================
        // 4. GET EMPLOYEE DOCUWARE AND ADOBE TOKENS
        // ==================================================

        try
        {
            _logger.LogInformation(
                "Starting DocuWare authentication for employee {UserId}.",
                userId);

            string docuWareAccessToken;

            try
            {
                docuWareAccessToken =
                    await _docuWareOAuthTokenService.GetValidAccessTokenAsync(
                        userId,
                        cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Employee {UserId} does not have a valid DocuWare OAuth connection.",
                    userId);

                ErrorMessage =
                    "Your DocuWare connection has expired or is not configured. Please sign in to DocuWare again.";

                EnsureSignerForRedisplay();

                return Page();
            }

            if (string.IsNullOrWhiteSpace(docuWareAccessToken))
            {
                throw new InvalidOperationException(
                    "A valid DocuWare access token could not be obtained.");
            }

            _logger.LogInformation(
                "DocuWare authentication completed for employee {UserId}.",
                userId);

            _logger.LogInformation(
                "Starting Adobe Sign authentication for employee {UserId}.",
                userId);

            AdobeStoredToken adobeToken;

            try
            {
                adobeToken =
                    await _adobeUserTokenService.GetValidTokenAsync(
                        userId,
                        cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Employee {UserId} does not have a valid Adobe Sign OAuth connection.",
                    userId);

                ErrorMessage =
                    "You have not connected Adobe Sign, or your Adobe connection needs attention. Please connect Adobe Sign and try again.";

                EnsureSignerForRedisplay();

                return Page();
            }

            if (adobeToken == null ||
                string.IsNullOrWhiteSpace(adobeToken.AccessToken) ||
                string.IsNullOrWhiteSpace(adobeToken.ApiAccessPoint))
            {
                throw new InvalidOperationException(
                    "Your Adobe Sign connection is incomplete. Please reconnect Adobe Sign.");
            }

            _logger.LogInformation(
                "Adobe Sign authentication completed for employee {UserId}.",
                userId);

            // ==================================================
            // 5. GET DOCUWARE FILE CABINET ID
            // ==================================================

            var fileCabinetId =
                _configuration["DocuWare:FileCabinetId"];

            if (string.IsNullOrWhiteSpace(fileCabinetId))
            {
                throw new InvalidOperationException(
                    "DocuWare:FileCabinetId is not configured.");
            }

            // ==================================================
            // 6. PREPARE RECIPIENTS AND ANCHOR FIELDS
            // ==================================================

            var recipients = Signers
                .Select(signer => new AdobeSignRecipient
                {
                    Name = signer.Name.Trim(),
                    Email = signer.Email.Trim()
                })
                .ToList();

            if (recipients.Count == 0)
            {
                throw new InvalidOperationException(
                    "At least one signing recipient is required.");
            }

            var anchorFields = BuildAnchorFields();

            var firstSigner = recipients[0];

            _logger.LogInformation(
                "Prepared {RecipientCount} Adobe Sign recipients and {AnchorCount} anchor fields for employee {UserId}.",
                recipients.Count,
                anchorFields.Count,
                userId);

            // ==================================================
            // 7. PREPARE DOCUWARE INDEX FIELDS
            // ==================================================

            var indexFields = new Dictionary<string, string>
            {
                ["REQUEST_DATE"] =
                    RequestDate!.Value.Date.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture),

                ["DOCUMENT_TYPE"] =
                    DocumentType.Trim(),

                ["RECIPIENT_COMPANY"] =
                    RecipientCompany.Trim(),

                ["RECIPIENT_EMAIL"] =
                    RecipientEmail.Trim(),

                ["RECIPIENT_NAME"] =
                    firstSigner.Name,

                ["REQUESTED_BY"] =
                    userId,

                ["EMAIL_SUBJECT"] =
                    EmailSubject.Trim(),

                ["EMAIL_MESSAGE"] =
                    Message?.Trim() ?? string.Empty
            };

            _logger.LogInformation(
                "Starting signature submission for {FileName}, employee {UserId}, with {RecipientCount} recipients. Signing order: {SigningOrderMode}.",
                fileName,
                userId,
                recipients.Count,
                SigningOrderMode);

            // ==================================================
            // 8. UPLOAD ORIGINAL PDF TO DOCUWARE
            // ==================================================

            _logger.LogInformation(
                "Starting original PDF upload to DocuWare for employee {UserId}.",
                userId);

            await using (var docuWarePdfStream =
                new MemoryStream(pdfBytes, writable: false))
            {
                UploadedDocumentId =
                    await _docuWareDocumentService.UploadOriginalDocumentAsync(
                        docuWarePdfStream,
                        fileName,
                        docuWareAccessToken,
                        indexFields,
                        cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(UploadedDocumentId))
            {
                throw new InvalidOperationException(
                    "DocuWare did not return a document ID after upload.");
            }

            _logger.LogInformation(
                "Original PDF uploaded successfully to DocuWare. Document ID: {DocumentId}.",
                UploadedDocumentId);

            // ==================================================
            // 9. RETRIEVE DOCUWARE SECTION ID
            // ==================================================

            _logger.LogInformation(
                "Retrieving DocuWare section ID for document {DocumentId}.",
                UploadedDocumentId);

            var sectionId =
                await _docuWareDocumentService.GetDocumentSectionIdAsync(
                    UploadedDocumentId,
                    docuWareAccessToken,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(sectionId))
            {
                throw new InvalidOperationException(
                    "DocuWare did not return a section ID for the uploaded document. Check DocuWare before submitting again.");
            }

            _logger.LogInformation(
                "DocuWare section ID retrieved for document {DocumentId}.",
                UploadedDocumentId);

            // ==================================================
            // 10. CREATE ADOBE AGREEMENT IN AUTHORING STATE
            // ==================================================

            _logger.LogInformation(
                "Starting Adobe agreement creation for employee {UserId}.",
                userId);

            AdobeSignAgreementResult agreementResult;

            await using (var adobePdfStream =
                new MemoryStream(pdfBytes, writable: false))
            {
                agreementResult =
                    await _adobeSignService.SendForSignatureAsync(
                        adobePdfStream,
                        fileName,
                        recipients,
                        EmailSubject.Trim(),
                        Message?.Trim(),
                        SigningOrderMode,
                        anchorFields,
                        adobeToken.AccessToken,
                        adobeToken.ApiAccessPoint,
                        cancellationToken);
            }

            if (agreementResult == null ||
                string.IsNullOrWhiteSpace(agreementResult.AgreementId))
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not return an agreement ID.");
            }

            AgreementId = agreementResult.AgreementId;

            _logger.LogInformation(
                "Adobe agreement {AgreementId} created. Adobe status: {AdobeStatus}.",
                AgreementId,
                agreementResult.Status);

            // ==================================================
            // 11. SAVE WORKFLOW BEFORE REDIRECTING
            // ==================================================

            var workflowCreatedAt = DateTime.UtcNow;

            var workflow = new AdobeAgreementWorkflow
            {
                AgreementId = AgreementId,
                UserId = userId,
                OriginalDocumentId = UploadedDocumentId,
                FileCabinetId = fileCabinetId,
                SectionId = sectionId,
                FileName = fileName,
                SignerName = firstSigner.Name,
                SignerEmail = firstSigner.Email,
                Subject = EmailSubject.Trim(),

                RecipientsJson =
                    JsonSerializer.Serialize(
                        recipients,
                        JsonOptions),

                SigningOrderMode = SigningOrderMode,

                AnchorFieldsJson =
                    JsonSerializer.Serialize(
                        anchorFields,
                        JsonOptions),

                Status = MapInitialWorkflowStatus(
                    agreementResult.Status),

                SentAtUtc = workflowCreatedAt,
                UpdatedAtUtc = workflowCreatedAt,
                ArchiveInProgress = false,
                ArchiveAttempts = 0,
                LastArchiveAttemptUtc = null,
                DocuWareArchiveStatus = "Not saved",
                DocuWareArchiveError = null,
                SignedPdfPath = null,
                SignedPdfSavedAtUtc = null,
                SignedPdfSizeBytes = null,
                ArchivedDocumentId = null,
                ArchivedAtUtc = null,
                CompletedAtUtc = null,
                FailureMessage = null
            };

            _logger.LogInformation(
                "Saving Adobe agreement workflow {AgreementId} to the database.",
                AgreementId);

            _dbContext.AdobeAgreementWorkflows.Add(workflow);

            await _dbContext.SaveChangesAsync(
                cancellationToken);

            workflowSaved = true;

            _logger.LogInformation(
                "Workflow successfully saved. Agreement {AgreementId}, DocuWare document {DocumentId}, section {SectionId}, recipient count {RecipientCount}, status {Status}.",
                AgreementId,
                UploadedDocumentId,
                sectionId,
                recipients.Count,
                workflow.Status);

            // ==================================================
            // 12. DELETE TEMPORARY UPLOAD
            // ==================================================

            await DeletePendingUploadAsync(
                userId,
                PendingUploadToken);

            PendingUploadToken = null;
            PendingUploadFileName = null;

            ModelState.Remove(nameof(PendingUploadToken));
            ModelState.Remove(nameof(PendingUploadFileName));

            // ==================================================
            // 13. SEND THE EXISTING AGREEMENT AUTOMATICALLY
            // ==================================================

            _logger.LogInformation(
                "Sending Adobe agreement {AgreementId} to {RecipientCount} recipients.",
                AgreementId,
                recipients.Count);

            var adobeStatus =
                await _adobeSignService.SendAgreementForSignatureAsync(
                    AgreementId!,
                    adobeToken.AccessToken,
                    adobeToken.ApiAccessPoint,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(adobeStatus))
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not return an agreement status after the send request.");
            }

            adobeStatus = adobeStatus.Trim().ToUpperInvariant();

            var agreementWasSent =
                adobeStatus is "IN_PROCESS" or
                    "OUT_FOR_SIGNATURE" or
                    "OUT_FOR_SIGNING" or
                    "AGREEMENT_VIEWED" or
                    "SIGNED" or
                    "COMPLETED";

            if (!agreementWasSent)
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not confirm that the agreement was sent. " +
                    $"Returned status: {adobeStatus}.");
            }

            // ==================================================
            // 14. UPDATE THE SAVED WORKFLOW
            // ==================================================

            workflow.Status = MapInitialWorkflowStatus(adobeStatus);
            workflow.UpdatedAtUtc = DateTime.UtcNow;
            workflow.FailureMessage = null;

            await _dbContext.SaveChangesAsync(cancellationToken);

            var recipientSummary = string.Join(
                "; ",
                recipients.Select(recipient =>
                    $"{recipient.Name} ({recipient.Email})"));

            TempData["ConfirmSendSuccess"] =
                $"'{fileName}' was accepted by Adobe Acrobat Sign and submitted to these recipients for signature: {recipientSummary}.";

            _logger.LogInformation(
                "Adobe agreement {AgreementId} was sent successfully. Status: {AdobeStatus}; recipients: {RecipientCount}.",
                AgreementId,
                adobeStatus,
                recipients.Count);

            return RedirectToPage("/Index");
        }
        catch (Exception ex)
            when (workflowSaved &&
                  ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Adobe agreement {AgreementId} was created and saved, but the automatic send could not be confirmed for employee {UserId}.",
                AgreementId,
                userId);

            TempData["ConfirmSendError"] =
                $"Agreement {AgreementId} was created and saved, but Adobe did not confirm that it was sent. Do not submit this document again. Open the Dashboard and use the retry action on this existing agreement.";

            return RedirectToPage("/Index");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Adobe agreement {AgreementId} may have been created, but its workflow record could not be saved. DocuWare document ID: {DocumentId}.",
                AgreementId,
                UploadedDocumentId);

            ErrorMessage =
                "The Adobe agreement may have been created, but its workflow record could not be saved. Check Adobe Sign and DocuWare before trying again to avoid duplicate agreements or uploads.";

            EnsureSignerForRedisplay();

            return Page();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "Authorization failed while processing signature request for employee {UserId}.",
                userId);

            ErrorMessage =
                "Access was denied by DocuWare or Adobe Sign. Please reconnect the relevant account and try again.";

            EnsureSignerForRedisplay();

            return Page();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "Signature submission configuration or processing error for employee {UserId}.",
                userId);

            ErrorMessage = ex.Message;

            EnsureSignerForRedisplay();

            return Page();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Signature submission was cancelled for employee {UserId}. DocuWare document ID: {DocumentId}; Adobe agreement ID: {AgreementId}.",
                userId,
                UploadedDocumentId,
                AgreementId);

            if (workflowSaved)
            {
                TempData["ConfirmSendError"] =
                    $"The send operation for agreement {AgreementId} was interrupted. Do not create another request. Check the Dashboard and Adobe Sign before retrying.";

                return RedirectToPage("/Index");
            }

            ErrorMessage =
                "The submission was cancelled. Check DocuWare and Adobe Sign before trying again, because one of the services may already have received the document.";

            EnsureSignerForRedisplay();

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Unexpected exception during signature submission for employee {UserId}. DocuWare document ID: {DocumentId}; Adobe agreement ID: {AgreementId}.",
                userId,
                UploadedDocumentId,
                AgreementId);

            ErrorMessage =
                "An unexpected error occurred while processing the signing request. Check DocuWare and Adobe Sign before trying again to avoid duplicate uploads or agreements.";

            EnsureSignerForRedisplay();

            return Page();
        }
    }

    // ======================================================
    // MODELSTATE DIAGNOSTICS
    // ======================================================

    private void LogModelStateErrors()
    {
        foreach (var entry in ModelState)
        {
            if (entry.Value == null ||
                entry.Value.Errors.Count == 0)
            {
                continue;
            }

            foreach (var error in entry.Value.Errors)
            {
                var errorMessage =
                    !string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? error.ErrorMessage
                        : error.Exception?.GetBaseException().Message
                          ?? "An unspecified validation error occurred.";

                _logger.LogWarning(
                    "FORM VALIDATION ERROR. Field: {Field}; Error: {Error}",
                    entry.Key,
                    errorMessage);
            }
        }
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

        var bytesToInspect = Math.Min(bytes.Length, 1028);

        var header = bytes.AsSpan(0, bytesToInspect);

        return header.IndexOf("%PDF-"u8) >= 0;
    }

    // ======================================================
    // NORMALISE PDF FILENAME
    // ======================================================

    private static string NormalizePdfFileName(
        string? originalFileName)
    {
        var safeFileName = Path.GetFileName(
            (originalFileName ?? string.Empty)
                .Replace('\\', '/'));

        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return "SignatureDocument.pdf";
        }

        if (string.Equals(
                Path.GetExtension(safeFileName),
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            return safeFileName;
        }

        return Path.ChangeExtension(
                   safeFileName,
                   ".pdf")
               ?? "SignatureDocument.pdf";
    }

    // ======================================================
    // TEMPORARY UPLOAD STORAGE
    // ======================================================

    private string GetPendingUploadDirectory()
    {
        var directory = Path.Combine(
            _environment.ContentRootPath,
            "App_Data",
            "PendingUploads");

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static string GetUserFileKey(string userId)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(userId));

        return Convert.ToHexString(hash)[..20];
    }

    private string GetPendingUploadPath(
        string userId,
        string token)
    {
        if (!Guid.TryParseExact(token, "N", out var guid))
        {
            throw new InvalidOperationException(
                "The temporary upload token is invalid.");
        }

        var safeToken = guid.ToString("N");
        var userKey = GetUserFileKey(userId);

        return Path.Combine(
            GetPendingUploadDirectory(),
            $"{safeToken}_{userKey}.pdf");
    }

    private async Task<string> SavePendingUploadAsync(
        string userId,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (bytes == null || bytes.Length == 0)
        {
            throw new InvalidOperationException(
                "Cannot save an empty temporary PDF.");
        }

        if (bytes.Length > MaxFileSize)
        {
            throw new InvalidOperationException(
                "The temporary PDF exceeds the maximum file size.");
        }

        if (!IsPdfContent(bytes))
        {
            throw new InvalidOperationException(
                "The temporary file is not recognised as a PDF.");
        }

        var token = Guid.NewGuid().ToString("N");

        var path = GetPendingUploadPath(
            userId,
            token);

        await System.IO.File.WriteAllBytesAsync(
            path,
            bytes,
            cancellationToken);

        return token;
    }

    private async Task<byte[]?> LoadPendingUploadAsync(
        string userId,
        string token,
        CancellationToken cancellationToken)
    {
        string path;

        try
        {
            path = GetPendingUploadPath(
                userId,
                token);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        var fileInfo = new FileInfo(path);

        if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc >
            TimeSpan.FromHours(PendingUploadLifetimeHours))
        {
            System.IO.File.Delete(path);
            return null;
        }

        if (fileInfo.Length == 0 ||
            fileInfo.Length > MaxFileSize)
        {
            System.IO.File.Delete(path);
            return null;
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(
            path,
            cancellationToken);

        if (!IsPdfContent(bytes))
        {
            System.IO.File.Delete(path);
            return null;
        }

        return bytes;
    }

    private Task DeletePendingUploadAsync(
        string userId,
        string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.CompletedTask;
        }

        try
        {
            var path = GetPendingUploadPath(
                userId,
                token);

            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not delete temporary PDF upload for employee {UserId}.",
                userId);
        }

        return Task.CompletedTask;
    }

    // ======================================================
    // PREPARE SIGNER COLLECTION
    // ======================================================

    private void PrepareSignerCollection()
    {
        Signers ??= new List<SignerFormInput>();

        // Support the legacy single-signer form if it is still
        // submitted by an older page or client.
        if (Signers.Count == 0 &&
            (!string.IsNullOrWhiteSpace(SignerName) ||
             !string.IsNullOrWhiteSpace(SignerEmail)))
        {
            Signers.Add(new SignerFormInput
            {
                Name = SignerName ?? string.Empty,
                Email = SignerEmail ?? string.Empty,
                SignatureAnchorText = "Signature:",
                NameAnchorText = "Full Name:",
                DateAnchorText = "Date:",
                DateFieldValue = null
            });
        }

        SignerCount = Signers.Count;

        // SignerCount is derived from the actual bound collection.
        ModelState.Remove(nameof(SignerCount));
    }

    private void EnsureSignerForRedisplay()
    {
        Signers ??= new List<SignerFormInput>();

        if (Signers.Count == 0)
        {
            Signers.Add(new SignerFormInput
            {
                Name = SignerName ?? string.Empty,
                Email = SignerEmail ?? string.Empty,
                SignatureAnchorText = "Signature:",
                NameAnchorText = "Full Name:",
                DateAnchorText = "Date:",
                DateFieldValue = null
            });
        }

        SignerCount = Signers.Count;

        ModelState.Remove(nameof(SignerCount));
    }

    // ======================================================
    // VALIDATE SIGNERS
    // ======================================================

    private void ValidateSigners()
    {
        if (Signers == null || Signers.Count == 0)
        {
            ModelState.AddModelError(
                nameof(Signers),
                "Add at least one signer.");

            return;
        }

        if (Signers.Count > MaxSigners)
        {
            ModelState.AddModelError(
                nameof(Signers),
                $"A maximum of {MaxSigners} signers is supported.");

            return;
        }

        var seenEmails = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Signers.Count; i++)
        {
            var signer = Signers[i];

            if (signer == null)
            {
                ModelState.AddModelError(
                    $"Signers[{i}]",
                    $"Signer {i + 1} is invalid.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(signer.Name))
            {
                ModelState.AddModelError(
                    $"Signers[{i}].Name",
                    $"Enter the full name for signer {i + 1}.");
            }
            else if (signer.Name.Trim().Length > 200)
            {
                ModelState.AddModelError(
                    $"Signers[{i}].Name",
                    $"The name for signer {i + 1} cannot exceed 200 characters.");
            }

            if (string.IsNullOrWhiteSpace(signer.Email) ||
                !System.Net.Mail.MailAddress.TryCreate(
                    signer.Email.Trim(),
                    out _))
            {
                ModelState.AddModelError(
                    $"Signers[{i}].Email",
                    $"Enter a valid email address for signer {i + 1}.");
            }
            else if (!seenEmails.Add(signer.Email.Trim()))
            {
                ModelState.AddModelError(
                    $"Signers[{i}].Email",
                    "This email address is already assigned to another signer.");
            }

            ValidateRequiredAnchor(
                signer.SignatureAnchorText,
                $"Signers[{i}].SignatureAnchorText",
                "signature",
                i + 1);

            ValidateRequiredAnchor(
                signer.NameAnchorText,
                $"Signers[{i}].NameAnchorText",
                "full-name",
                i + 1);

            ValidateRequiredAnchor(
                signer.DateAnchorText,
                $"Signers[{i}].DateAnchorText",
                "date",
                i + 1);

            ValidateOptionalAnchor(
                signer.InitialsAnchorText,
                $"Signers[{i}].InitialsAnchorText",
                "Initials");

            // The date picker is optional.
            // If a date is supplied, it must use the yyyy-MM-dd format
            // submitted by an HTML input type="date".
            ValidateOptionalDateFieldValue(
                signer,
                $"Signers[{i}].DateFieldValue",
                i + 1);
        }

        ValidateUniqueAnchors();
    }

    private void ValidateRequiredAnchor(
        string? anchor,
        string key,
        string label,
        int signerNumber)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            ModelState.AddModelError(
                key,
                $"Enter the PDF {label} anchor text for signer {signerNumber}.");

            return;
        }

        if (anchor.Trim().Length > 150)
        {
            ModelState.AddModelError(
                key,
                $"The {label} anchor text cannot exceed 150 characters.");
        }
    }

    private void ValidateOptionalAnchor(
        string? anchor,
        string key,
        string label)
    {
        if (anchor != null && anchor.Trim().Length > 150)
        {
            ModelState.AddModelError(
                key,
                $"{label} anchor text cannot exceed 150 characters.");
        }
    }

    // ======================================================
    // VALIDATE OPTIONAL PREFILL DATE
    // ======================================================

    private void ValidateOptionalDateFieldValue(
        SignerFormInput signer,
        string key,
        int signerNumber)
    {
        if (string.IsNullOrWhiteSpace(signer.DateFieldValue))
        {
            signer.DateFieldValue = null;
            return;
        }

        var selectedDate = signer.DateFieldValue.Trim();

        if (!DateOnly.TryParseExact(
                selectedDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            ModelState.AddModelError(
                key,
                $"Select a valid date for signer {signerNumber}.");

            return;
        }

        // Store the normalised date string.
        signer.DateFieldValue = selectedDate;
    }

    // ======================================================
    // VALIDATE ANCHOR TEXT
    // ======================================================

    private void ValidateUniqueAnchors()
    {
        if (Signers == null)
        {
            return;
        }

        // Anchor text may be reused by different signers.
        // For example, a PDF may use "Signature:" for both
        // signers in separate parts of the document.
        //
        // However, one signer must not use the same anchor
        // for multiple different fields.

        for (var i = 0; i < Signers.Count; i++)
        {
            var signer = Signers[i];

            if (signer == null)
            {
                continue;
            }

            var anchors = new[]
            {
                signer.SignatureAnchorText,
                signer.NameAnchorText,
                signer.DateAnchorText,
                signer.InitialsAnchorText
            }
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor))
            .Select(anchor => anchor!.Trim());

            var duplicateAnchors = anchors
                .GroupBy(
                    anchor => anchor,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            if (duplicateAnchors.Count > 0)
            {
                ModelState.AddModelError(
                    $"Signers[{i}]",
                    $"Signer {i + 1} uses the same anchor text for more than one field: {string.Join(", ", duplicateAnchors)}. Use different anchor labels for that signer's fields.");
            }
        }
    }

    // ======================================================
    // BUILD ADOBE SIGN ANCHOR FIELDS
    // ======================================================

    private List<AdobeSignAnchorField> BuildAnchorFields()
    {
        var fields = new List<AdobeSignAnchorField>();

        for (var i = 0; i < Signers.Count; i++)
        {
            var signerNumber = i + 1;
            var signer = Signers[i];

            fields.Add(
                CreateAnchorField(
                    signerNumber,
                    "SIGNATURE",
                    signer.SignatureAnchorText,
                    required: true));

            fields.Add(
                CreateAnchorField(
                    signerNumber,
                    "TEXT",
                    signer.NameAnchorText,
                    required: true));

            fields.Add(
                CreateAnchorField(
                    signerNumber,
                    "DATE",
                    signer.DateAnchorText,
                    required: true));

            if (!string.IsNullOrWhiteSpace(
                    signer.InitialsAnchorText))
            {
                fields.Add(
                    CreateAnchorField(
                        signerNumber,
                        "INITIALS",
                        signer.InitialsAnchorText,
                        required: false));
            }
        }

        return fields;
    }

    private static AdobeSignAnchorField CreateAnchorField(
        int signerNumber,
        string inputType,
        string? anchorText,
        bool required)
    {
        return new AdobeSignAnchorField
        {
            SignerNumber = signerNumber,
            InputType = inputType,
            AnchorText = anchorText?.Trim() ?? string.Empty,
            Required = required,
            OffsetX = 8,
            OffsetY = -12,

            Width = string.Equals(
                inputType,
                "SIGNATURE",
                StringComparison.OrdinalIgnoreCase)
                    ? 140
                    : 120,

            Height = string.Equals(
                inputType,
                "SIGNATURE",
                StringComparison.OrdinalIgnoreCase)
                    ? 24
                    : 20
        };
    }

    // ======================================================
    // MAP INITIAL ADOBE STATUS
    // ======================================================

    private static string MapInitialWorkflowStatus(
        string? adobeStatus)
    {
        return adobeStatus?.Trim().ToUpperInvariant() switch
        {
            "AUTHORING" =>
                "Preparing in Adobe",

            "DRAFT" =>
                "Preparing in Adobe",

            "CREATED" =>
                "Preparing in Adobe",

            "DOCUMENTS_NOT_YET_PROCESSED" =>
                "Preparing in Adobe",

            "OUT_FOR_SIGNATURE" =>
                "Awaiting Signature",

            "OUT_FOR_SIGNING" =>
                "Awaiting Signature",

            "IN_PROCESS" =>
                "In Progress",

            "AGREEMENT_VIEWED" =>
                "In Progress",

            "SIGNED" =>
                "Signed",

            "COMPLETED" =>
                "Signed",

            "APPROVED" =>
                "Signed",

            "CANCELLED" =>
                "Failed",

            "CANCELED" =>
                "Failed",

            "EXPIRED" =>
                "Failed",

            "ABORTED" =>
                "Failed",

            "REJECTED" =>
                "Failed",

            "DECLINED" =>
                "Failed",

            _ =>
                "Preparing in Adobe"
        };
    }
}

// ======================================================
// SIGNER FORM INPUT
// ======================================================

public class SignerFormInput
{
    [Required(ErrorMessage = "Enter the signer's full name.")]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the signer's email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the signature anchor text.")]
    [StringLength(150)]
    public string SignatureAnchorText { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the full-name anchor text.")]
    [StringLength(150)]
    public string NameAnchorText { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the date anchor text.")]
    [StringLength(150)]
    public string DateAnchorText { get; set; } = string.Empty;

    [StringLength(150)]
    public string? InitialsAnchorText { get; set; }

    /// <summary>
    /// Optional date selected by the sender for the signer's
    /// Adobe Sign date field.
    ///
    /// HTML input type="date" posts this value as yyyy-MM-dd.
    /// This is separate from DateAnchorText, which remains the
    /// exact label that Adobe searches for in the PDF.
    /// </summary>
    [StringLength(10)]
    public string? DateFieldValue { get; set; }
}