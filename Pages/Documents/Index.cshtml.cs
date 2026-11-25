
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Pages;

[Authorize]
public class DocumentsModel : PageModel
{
    private readonly DocuWareService _docuWareService;
    private readonly ILogger<DocumentsModel> _logger;

    public DocumentsModel(
        DocuWareService docuWareService,
        ILogger<DocumentsModel> logger)
    {
        _docuWareService = docuWareService;
        _logger = logger;
    }

    // Documents matching the user's search.
    public List<DocuWareDocumentItem> Documents { get; private set; } = new();

    // Number of matching documents.
    public int TotalDocuments { get; private set; }

    // Error displayed on the page.
    public string? ErrorMessage { get; private set; }

    // Informational message displayed on the page.
    public string? SuccessMessage { get; private set; }

    // Search term supplied through the GET request.
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Search = Search?.Trim();

        // Do not retrieve documents until the user searches.
        if (string.IsNullOrWhiteSpace(Search))
        {
            Documents = new List<DocuWareDocumentItem>();
            TotalDocuments = 0;
            SuccessMessage = null;
            ErrorMessage = null;

            return;
        }

        try
        {
            // Retrieve the DocuWare access token from the user's session.
            var accessToken = await HttpContext.GetTokenAsync("access_token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                ErrorMessage =
                    "Your DocuWare session could not be found. Please sign in again.";

                return;
            }

            // Retrieve documents from the configured DocuWare file cabinet.
            var allDocuments = await _docuWareService.GetDocumentsAsync(
                accessToken,
                cancellationToken);

            // Search document names and document IDs.
            Documents = allDocuments
                .Where(document =>
                    document.Name.Contains(
                        Search,
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    document.Id.ToString().Contains(
                        Search,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(document => document.CreatedAt)
                .ToList();

            // Count only matching documents.
            TotalDocuments = Documents.Count;

            if (Documents.Count == 0)
            {
                SuccessMessage =
                    $"No documents found matching '{Search}'.";
            }

            _logger.LogInformation(
                "DocuWare document search completed. " +
                "Search term: {SearchTerm}. Matching documents: {Count}.",
                Search,
                TotalDocuments);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare authentication failed during document search.");

            ErrorMessage =
                "DocuWare did not accept your session. Please sign in again.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "Network error while searching DocuWare documents.");

            ErrorMessage =
                "Unable to retrieve documents from DocuWare. Please try again later.";
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
                "Unexpected error while searching DocuWare documents.");

            ErrorMessage =
                "An unexpected error occurred while searching documents. " +
                "Please check the application logs.";
        }
    }
}
