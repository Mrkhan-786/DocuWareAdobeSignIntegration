
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DocuWareAdobeSignIntegration.Pages;

[Authorize]
public class CompletedModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public CompletedModel(ApplicationDbContext context)
    {
        _context = context
            ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyList<AdobeAgreementWorkflow>
        CompletedDocuments
    { get; private set; }
        = Array.Empty<AdobeAgreementWorkflow>();

    public int CompletedCount => CompletedDocuments.Count;

    public async Task<IActionResult> OnGetAsync()
    {
        // Identify the currently authenticated user.
        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Forbid();
        }

        // Only show documents whose signed PDFs have
        // successfully been saved back into DocuWare.
        //
        // IsCompleted checks:
        // 1. DocuWare archive status is Saved.
        // 2. ArchivedDocumentId is populated.
        // 3. ArchivedAtUtc is populated.
        // 4. CompletedAtUtc is populated.
        // 5. Workflow status is Completed.
        CompletedDocuments = await _context.AdobeAgreementWorkflows
            .AsNoTracking()
            .Where(workflow =>
                workflow.UserId == userId &&
                workflow.Status == "Completed" &&
                workflow.DocuWareArchiveStatus == "Saved" &&
                workflow.ArchivedAtUtc != null &&
                workflow.CompletedAtUtc != null &&
                workflow.ArchivedDocumentId != null &&
                workflow.ArchivedDocumentId != "")
            .OrderByDescending(workflow => workflow.ArchivedAtUtc)
            .ThenByDescending(workflow => workflow.CompletedAtUtc)
            .ToListAsync();

        return Page();
    }
}
