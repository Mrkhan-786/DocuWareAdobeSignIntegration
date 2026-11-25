
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DocuWareAdobeSignIntegration.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Pages.Documents;

[Authorize]
public class EditModel : PageModel
{
    private readonly DocuWareDocumentService _docuWareDocumentService;
    private readonly ILogger<EditModel> _logger;

    public EditModel(
        DocuWareDocumentService docuWareDocumentService,
        ILogger<EditModel> logger)
    {
        _docuWareDocumentService = docuWareDocumentService;
        _logger = logger;
    }

    [BindProperty]
    public int DocumentId { get; set; }

    [BindProperty]
    [DataType(DataType.Date)]
    public DateTime? RequestDate { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Please enter the document type.")]
    [StringLength(100)]
    public string? DocumentType { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Please enter the recipient company.")]
    [StringLength(200)]
    public string? RecipientCompany { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Please enter the recipient name.")]
    [StringLength(200)]
    public string? RecipientName { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Please enter the recipient email address.")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    [StringLength(320)]
    public string? RecipientEmail { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnSearch { get; set; }

    public string? ErrorMessage { get; private set; }

    public string? SuccessMessage { get; private set; }

    // =====================================================
    // GET - LOAD EXISTING DOCUWARE FIELD VALUES
    // =====================================================

    public async Task<IActionResult> OnGetAsync(
        int id,
        string? returnSearch,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest();
        }

        DocumentId = id;
        ReturnSearch = returnSearch?.Trim();

        try
        {
            var accessToken =
                await HttpContext.GetTokenAsync("access_token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                ErrorMessage =
                    "Your DocuWare session has expired. Please sign in to DocuWare again.";

                return Page();
            }

            var fields =
                await _docuWareDocumentService.GetDocumentIndexFieldsAsync(
                    DocumentId.ToString(CultureInfo.InvariantCulture),
                    accessToken,
                    cancellationToken);

            fields.TryGetValue("REQUEST_DATE", out var requestDate);
            fields.TryGetValue("DOCUMENT_TYPE", out var documentType);
            fields.TryGetValue("RECIPIENT_COMPANY", out var recipientCompany);
            fields.TryGetValue("RECIPIENT_NAME", out var recipientName);
            fields.TryGetValue("RECIPIENT_EMAIL", out var recipientEmail);

            if (!string.IsNullOrWhiteSpace(requestDate) &&
                DateTime.TryParse(
                    requestDate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                RequestDate = parsedDate.Date;
            }

            DocumentType = documentType;
            RecipientCompany = recipientCompany;
            RecipientName = recipientName;
            RecipientEmail = recipientEmail;

            _logger.LogInformation(
                "Loaded index fields for DocuWare document {DocumentId}.",
                DocumentId);

            return Page();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare authentication failed while loading document {DocumentId}.",
                DocumentId);

            ErrorMessage =
                "DocuWare rejected your session. Please sign in again.";

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to load index fields for document {DocumentId}.",
                DocumentId);

            ErrorMessage =
                "Unable to load the existing document details from DocuWare. Please check the application logs.";

            return Page();
        }
    }

    // =====================================================
    // POST - SAVE CHANGES TO EXISTING DOCUWARE DOCUMENT
    // =====================================================

    public async Task<IActionResult> OnPostSaveAsync(
        CancellationToken cancellationToken)
    {
        if (DocumentId <= 0)
        {
            return BadRequest();
        }

        ReturnSearch = ReturnSearch?.Trim();

        if (!ModelState.IsValid)
        {
            ErrorMessage =
                "Please correct the validation errors before saving.";

            return Page();
        }

        try
        {
            var accessToken =
                await HttpContext.GetTokenAsync("access_token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                ErrorMessage =
                    "Your DocuWare session has expired. Please sign in again.";

                return Page();
            }

            var indexFields =
                new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["REQUEST_DATE"] =
                        RequestDate?.ToString(
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture),

                    ["DOCUMENT_TYPE"] =
                        DocumentType?.Trim(),

                    ["RECIPIENT_COMPANY"] =
                        RecipientCompany?.Trim(),

                    ["RECIPIENT_NAME"] =
                        RecipientName?.Trim(),

                    ["RECIPIENT_EMAIL"] =
                        RecipientEmail?.Trim()
                };

            await _docuWareDocumentService.UpdateDocumentIndexFieldsAsync(
                DocumentId.ToString(CultureInfo.InvariantCulture),
                accessToken,
                indexFields,
                cancellationToken);

            _logger.LogInformation(
                "Successfully saved edited index fields for DocuWare document {DocumentId}.",
                DocumentId);

            return RedirectToPage(
                "/Documents",
                new { Search = ReturnSearch });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare authentication failed while saving document {DocumentId}.",
                DocumentId);

            ErrorMessage =
                "DocuWare rejected your session or you do not have permission to update this document.";

            return Page();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "DocuWare update failed for document {DocumentId}.",
                DocumentId);

            ErrorMessage =
                "DocuWare could not save your changes. Please check the application logs and try again.";

            return Page();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage =
                "The save operation was cancelled. Please check the document details before trying again.";

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while saving document {DocumentId}.",
                DocumentId);

            ErrorMessage =
                "An unexpected error occurred while saving the document details.";

            return Page();
        }
    }
}
