namespace DocuWareAdobeSignIntegration.Services;

public class DocuWareService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DocuWareService> _logger;

    public DocuWareService(
        HttpClient httpClient,
        ILogger<DocuWareService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadDocumentAsync(
        string documentId)
    {
        _logger.LogInformation(
            "Downloading DocuWare document {DocumentId}",
            documentId);

        // Temporary placeholder until we implement
        // the DocuWare Platform API integration.

        await Task.CompletedTask;

        return @"C:\Windows\Temp\Adobe Sign Request Form.pdf";
    }
}