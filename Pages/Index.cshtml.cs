using Microsoft.AspNetCore.Mvc.RazorPages;
using DocuWareAdobeSignIntegration.Data;
using System.Linq;

namespace DocuWareAdobeSignIntegration.Pages;

public class IndexModel : PageModel
{
    public int TotalUploaded => RecentActivity.Count;

    public int AwaitingSignatures =>
        RecentActivity.Count(x => x.Status == "Awaiting Signature");

    public int InProgress =>
        RecentActivity.Count(x => x.Status == "In Progress");

    public int Completed =>
        RecentActivity.Count(x => x.Status == "Complete");

    public int Failed =>
        RecentActivity.Count(x => x.Status == "Failed");

    public List<ActivityItem> RecentActivity { get; private set; } = new();

    public void OnGet()
    {
        RecentActivity = DocumentStore.Documents
            .OrderByDescending(x => x.SentAt)
            .ToList();
    }
}

public record ActivityItem(
    string Name,
    string Recipient,
    string Email,
    string Status,
    string StatusClass,
    DateTime SentAt,
    int Progress,
    string FinalStep);