using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DocuWareAdobeSignIntegration.Pages;

public class IndexModel : PageModel
{
    public int TotalUploaded => RecentActivity.Count;
    public int AwaitingSignatures => RecentActivity.Count(x => x.Status == "Awaiting Action");
    public int InProgress => RecentActivity.Count(x => x.Status == "In Progress");
    public int Completed => RecentActivity.Count(x => x.Status == "Complete");
    public int Failed => RecentActivity.Count(x => x.Status == "Failed");

    // Demonstration data only. Replace this collection with records from your transaction store/API.
    public List<ActivityItem> RecentActivity { get; private set; } = new();

    public void OnGet()
    {
        RecentActivity = new()
        {
            new("Invoice Template.pdf", "John Smith", "john.smith@example.com", "In Progress", "info", DateTime.Now.AddMinutes(-35), 3, "Signed"),
            new("Employee Contract.pdf", "Mary Jones", "mary.jones@example.com", "Complete", "success", DateTime.Now.AddHours(-2), 4, "Signed"),
            new("Tenancy Agreement.pdf", "Peter Brown", "peter.brown@example.com", "Awaiting Action", "warning", DateTime.Now.AddDays(-1), 2, "Signed"),
            new("Supplier Agreement.pdf", "Sarah Adams", "sarah.adams@example.com", "Failed", "danger", DateTime.Now.AddDays(-2), 2, "Rejected")
        };
    }
}

public record ActivityItem(string Name, string Recipient, string Email, string Status,
    string StatusClass, DateTime SentAt, int Progress, string FinalStep);
