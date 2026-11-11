using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http.Json;
using DocuWareAdobeSignIntegration.Models;

namespace DocuWareAdobeSignIntegration.Services;

public class AdobeAgreementService
{
    private readonly AdobeAuthService _authService;
    private readonly DocuWareService _docuWareService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AdobeAgreementService> _logger;

    public AdobeAgreementService(
        AdobeAuthService authService,
        DocuWareService docuWareService,
        HttpClient httpClient,
        ILogger<AdobeAgreementService> logger)
    {
        _authService = authService;
        _docuWareService = docuWareService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> UploadTransientDocumentAsync(
        string accessToken,
        string filePath)
    {
        using var form = new MultipartFormDataContent();

        form.Add(
            new StringContent("Agreement.pdf"),
            "File-Name");

        var fileBytes =
            await File.ReadAllBytesAsync(filePath);

        form.Add(
            new ByteArrayContent(fileBytes),
            "File",
            "Agreement.pdf");

        _httpClient.DefaultRequestHeaders.Clear();

        _httpClient.DefaultRequestHeaders.Add(
            "Authorization",
            $"Bearer {accessToken}");

        var response =
            await _httpClient.PostAsync(
                "https://api.na3.adobesign.com/api/rest/v6/transientDocuments",
                form);

        var json =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe transient document upload failed: {Response}",
                json);

            throw new Exception(json);
        }

        var result =
            JsonSerializer.Deserialize<AdobeTransientDocumentResponse>(
                json);

        return result!.TransientDocumentId;
    }

    public async Task<string> CreateAdobeAgreementAsync(
        string accessToken,
        string transientDocumentId,
        string recipientEmail)
    {
        _httpClient.DefaultRequestHeaders.Clear();

        _httpClient.DefaultRequestHeaders.Add(
            "Authorization",
            $"Bearer {accessToken}");

        var requestBody = new
        {
            fileInfos = new[]
            {
                new
                {
                    transientDocumentId = transientDocumentId
                }
            },

            name = "DocuWare Agreement",

            participantSetsInfo = new[]
            {
                new
                {
                    memberInfos = new[]
                    {
                        new
                        {
                            email = recipientEmail
                        }
                    },

                    order = 1,
                    role = "SIGNER"
                }
            },

            signatureType = "ESIGN",

            state = "IN_PROCESS"
        };

        var response =
            await _httpClient.PostAsJsonAsync(
                "https://api.na3.adobesign.com/api/rest/v6/agreements",
                requestBody);

        var json =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe agreement creation failed: {Response}",
                json);

            throw new Exception(json);
        }

        var agreement =
            JsonSerializer.Deserialize<AdobeAgreementResponse>(
                json);

        return agreement!.Id;
    }

    public async Task<object> CreateAgreementAsync(
        SignRequest request)
    {
        try
        {
            _logger.LogInformation(
                "Processing agreement request for {Email}",
                request.RecipientEmail);

            // STEP 1
            // Download document from DocuWare

            var filePath =
                await _docuWareService
                    .DownloadDocumentAsync(
                        request.DocumentId);

            _logger.LogInformation(
                "Document downloaded: {FilePath}",
                filePath);

            // STEP 2
            // Temporary Access Token

            string accessToken =
                "PASTE_YOUR_CURRENT_ACCESS_TOKEN_HERE";

            // STEP 3
            // Upload PDF to Adobe

            string transientDocumentId =
                await UploadTransientDocumentAsync(
                    accessToken,
                    filePath);

            _logger.LogInformation(
                "Transient Document Created: {TransientDocumentId}",
                transientDocumentId);

            // STEP 4
            // Create Adobe Agreement

            string adobeAgreementId =
                await CreateAdobeAgreementAsync(
                    accessToken,
                    transientDocumentId,
                    request.RecipientEmail);

            _logger.LogInformation(
                "Adobe Agreement Created: {AgreementId}",
                adobeAgreementId);

            return new
            {
                Success = true,
                Message = "Agreement sent successfully.",

                DocumentId = request.DocumentId,

                AgreementNumber = request.AgreementNumber,

                RecipientEmail = request.RecipientEmail,

                TransientDocumentId = transientDocumentId,

                AdobeAgreementId = adobeAgreementId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Agreement creation failed for {Email}",
                request.RecipientEmail);

            return new
            {
                Success = false,
                Error = ex.Message
            };
        }
    }
}