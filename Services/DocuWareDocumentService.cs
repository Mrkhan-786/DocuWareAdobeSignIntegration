namespace DocuWareAdobeSignIntegration.Services;

public class DocuWareDocumentService
{
    private readonly DocuWareAuthService _authService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DocuWareDocumentService> _logger;

    public DocuWareDocumentService(
        DocuWareAuthService authService,
        HttpClient httpClient,
        ILogger<DocuWareDocumentService> logger)
    {
        _authService = authService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadDocumentAsync(
        string documentId)
    {
        _logger.LogInformation(
            "Downloading DocuWare document {DocumentId}",
            documentId);

        // Temporary placeholder
        await Task.CompletedTask;

        return @"C:\Windows\Temp\Adobe Sign Request Form.pdf";
    }
}