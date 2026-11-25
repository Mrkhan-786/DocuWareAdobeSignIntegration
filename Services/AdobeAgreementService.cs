
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;

namespace DocuWareAdobeSignIntegration.Services;

public class AdobeAgreementService
{
    private const string DevelopmentDefaultApiAccessPoint =
        "https://api.na3.adobesign.com";

    private readonly DocuWareDocumentService _docuWareDocumentService;
    private readonly ApplicationDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AdobeAgreementService> _logger;
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AdobeAgreementService(
        DocuWareDocumentService docuWareDocumentService,
        ApplicationDbContext dbContext,
        HttpClient httpClient,
        ILogger<AdobeAgreementService> logger,
        IConfiguration configuration)
    {
        _docuWareDocumentService = docuWareDocumentService;
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    // =====================================================
    // STEP 1: UPLOAD TRANSIENT DOCUMENT
    // Development-only compatibility overload.
    // =====================================================

    public Task<string> UploadTransientDocumentAsync(
        string accessToken,
        string filePath)
    {
        return UploadTransientDocumentAsync(
            accessToken,
            DevelopmentDefaultApiAccessPoint,
            filePath);
    }

    // =====================================================
    // STEP 1: UPLOAD TRANSIENT DOCUMENT
    // =====================================================

    public async Task<string> UploadTransientDocumentAsync(
        string accessToken,
        string apiAccessPoint,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException(
                "Adobe access token is required.",
                nameof(accessToken));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                "The PDF file could not be found.",
                filePath);
        }

        var baseUri = ValidateApiAccessPoint(apiAccessPoint);

        var fileName = Path.GetFileName(filePath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "Agreement.pdf";
        }

        using var form = new MultipartFormDataContent();

        form.Add(
            new StringContent(fileName),
            "File-Name");

        var fileBytes = await File.ReadAllBytesAsync(
            filePath,
            cancellationToken);

        var fileContent = new ByteArrayContent(fileBytes);

        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue("application/pdf");

        form.Add(
            fileContent,
            "File",
            fileName);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUri}/api/rest/v6/transientDocuments");

        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                accessToken);

        httpRequest.Content = form;

        using var response = await _httpClient.SendAsync(
            httpRequest,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe transient document upload failed. HTTP status: {StatusCode}. Response: {Response}",
                (int)response.StatusCode,
                json);

            throw new HttpRequestException(
                $"Adobe transient document upload failed with HTTP status {(int)response.StatusCode}.");
        }

        var result =
            JsonSerializer.Deserialize<AdobeTransientDocumentResponse>(
                json,
                JsonOptions);

        if (result == null ||
            string.IsNullOrWhiteSpace(result.TransientDocumentId))
        {
            _logger.LogError(
                "Adobe returned an invalid transient document response.");

            throw new InvalidOperationException(
                "Adobe did not return a transient document ID.");
        }

        _logger.LogInformation(
            "Adobe transient document uploaded successfully.");

        return result.TransientDocumentId;
    }

    // =====================================================
    // STEP 2: CREATE ADOBE AGREEMENT
    // Development-only compatibility overload.
    // =====================================================

    public Task<string> CreateAdobeAgreementAsync(
        string accessToken,
        string transientDocumentId,
        string recipientEmail)
    {
        return CreateAdobeAgreementAsync(
            accessToken,
            DevelopmentDefaultApiAccessPoint,
            transientDocumentId,
            recipientEmail);
    }

    // =====================================================
    // STEP 2: CREATE ADOBE AGREEMENT
    // =====================================================

    public async Task<string> CreateAdobeAgreementAsync(
        string accessToken,
        string apiAccessPoint,
        string transientDocumentId,
        string recipientEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException(
                "Adobe access token is required.",
                nameof(accessToken));
        }

        if (string.IsNullOrWhiteSpace(transientDocumentId))
        {
            throw new ArgumentException(
                "Adobe transient document ID is required.",
                nameof(transientDocumentId));
        }

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            throw new ArgumentException(
                "Recipient email is required.",
                nameof(recipientEmail));
        }

        var baseUri = ValidateApiAccessPoint(apiAccessPoint);

        var postSignRedirectUrl =
            _configuration["AdobeSign:PostSignRedirectUrl"];

        if (!Uri.TryCreate(
                postSignRedirectUrl,
                UriKind.Absolute,
                out var redirectUri) ||
            redirectUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(redirectUri.UserInfo) ||
            !string.IsNullOrEmpty(redirectUri.Query) ||
            !string.IsNullOrEmpty(redirectUri.Fragment))
        {
            throw new InvalidOperationException(
                "AdobeSign:PostSignRedirectUrl must be configured with a valid HTTPS URL without query strings or fragments.");
        }

        var requestBody = new
        {
            fileInfos = new[]
            {
                new
                {
                    transientDocumentId
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

            state = "IN_PROCESS",

            postSignOption = new
            {
                redirectUrl = redirectUri.AbsoluteUri,
                redirectDelay = 0
            }
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUri}/api/rest/v6/agreements");

        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                accessToken);

        httpRequest.Content = JsonContent.Create(requestBody);

        using var response = await _httpClient.SendAsync(
            httpRequest,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Adobe agreement creation failed. HTTP status: {StatusCode}. Response: {Response}",
                (int)response.StatusCode,
                json);

            throw new HttpRequestException(
                $"Adobe agreement creation failed with HTTP status {(int)response.StatusCode}.");
        }

        var agreement =
            JsonSerializer.Deserialize<AdobeAgreementResponse>(
                json,
                JsonOptions);

        if (agreement == null ||
            string.IsNullOrWhiteSpace(agreement.Id))
        {
            _logger.LogError(
                "Adobe returned an invalid agreement response.");

            throw new InvalidOperationException(
                "Adobe did not return an agreement ID.");
        }

        _logger.LogInformation(
            "Adobe agreement created successfully. Agreement ID: {AgreementId}",
            agreement.Id);

        return agreement.Id;
    }

    // =====================================================
    // STEP 3: DOCUWARE -> ADOBE SIGN -> DATABASE WORKFLOW
    //
    // The employee explicitly chooses when to save the
    // signed PDF. Webhook/status sync only changes the
    // workflow status to Signed.
    //
    // No automatic signed PDF download.
    // No automatic DocuWare replacement.
    // =====================================================

    public async Task<object> CreateAgreementAsync(
        SignRequest request,
        string userId,
        string accessToken,
        string apiAccessPoint,
        string docuWareAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "The authenticated employee user ID is required.",
                nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException(
                "Adobe access token is required.",
                nameof(accessToken));
        }

        if (string.IsNullOrWhiteSpace(docuWareAccessToken))
        {
            throw new ArgumentException(
                "DocuWare access token is required.",
                nameof(docuWareAccessToken));
        }

        if (string.IsNullOrWhiteSpace(request.DocumentId))
        {
            throw new ArgumentException(
                "DocuWare document ID is required.",
                nameof(request.DocumentId));
        }

        if (string.IsNullOrWhiteSpace(request.RecipientEmail))
        {
            throw new ArgumentException(
                "Recipient email is required.",
                nameof(request.RecipientEmail));
        }

        if (string.IsNullOrWhiteSpace(request.RecipientName))
        {
            throw new ArgumentException(
                "Recipient name is required.",
                nameof(request.RecipientName));
        }

        var baseUri = ValidateApiAccessPoint(apiAccessPoint);

        var fileCabinetId =
            _configuration["DocuWare:FileCabinetId"];

        if (string.IsNullOrWhiteSpace(fileCabinetId))
        {
            throw new InvalidOperationException(
                "DocuWare:FileCabinetId is not configured.");
        }

        string? filePath = null;

        try
        {
            _logger.LogInformation(
                "Starting Adobe agreement processing for DocuWare document {DocumentId}, employee {UserId}.",
                request.DocumentId,
                userId);

            // STEP 1: DOWNLOAD THE ORIGINAL DOCUMENT FROM DOCUWARE.

            filePath =
                await _docuWareDocumentService.DownloadDocumentAsync(
                    request.DocumentId,
                    docuWareAccessToken,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(filePath) ||
                !File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    "The DocuWare document could not be downloaded.");
            }

            var fileName = Path.GetFileName(filePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"Document-{request.DocumentId}.pdf";
            }

            _logger.LogInformation(
                "Original DocuWare document downloaded successfully.");

            // STEP 2: GET THE ORIGINAL DOCUWARE SECTION ID.
            // Retain this information for the employee's
            // optional Save to DocuWare action.

            var sectionId =
                await _docuWareDocumentService.GetDocumentSectionIdAsync(
                    request.DocumentId,
                    docuWareAccessToken);

            if (string.IsNullOrWhiteSpace(sectionId))
            {
                throw new InvalidOperationException(
                    "Could not identify the original DocuWare document section.");
            }

            // STEP 3: UPLOAD THE DOCUMENT TO ADOBE.

            var transientDocumentId =
                await UploadTransientDocumentAsync(
                    accessToken,
                    baseUri,
                    filePath,
                    cancellationToken);

            _logger.LogInformation(
                "Adobe transient document uploaded successfully.");

            // STEP 4: CREATE THE ADOBE AGREEMENT.

            var adobeAgreementId =
                await CreateAdobeAgreementAsync(
                    accessToken,
                    baseUri,
                    transientDocumentId,
                    request.RecipientEmail,
                    cancellationToken);

            _logger.LogInformation(
                "Adobe agreement created. Agreement ID: {AgreementId}",
                adobeAgreementId);

            // STEP 5: SAVE THE WORKFLOW IN THE APPLICATION DATABASE.

            var now = DateTime.UtcNow;

            var workflow = new AdobeAgreementWorkflow
            {
                AgreementId = adobeAgreementId,
                UserId = userId,

                SignerName = request.RecipientName,
                SignerEmail = request.RecipientEmail,

                Subject = string.IsNullOrWhiteSpace(request.AgreementNumber)
                    ? "DocuWare Agreement"
                    : request.AgreementNumber,

                OriginalDocumentId = request.DocumentId,
                FileCabinetId = fileCabinetId,
                SectionId = sectionId,

                FileName = fileName,

                Status = "Awaiting Signature",

                SentAtUtc = now,
                UpdatedAtUtc = now,

                DocuWareArchiveStatus = "Not saved",
                ArchiveInProgress = false,
                ArchiveAttempts = 0
            };

            _dbContext.AdobeAgreementWorkflows.Add(workflow);

            await _dbContext.SaveChangesAsync(
                cancellationToken);

            _logger.LogInformation(
                "Adobe workflow saved. Workflow ID: {WorkflowId}, Agreement ID: {AgreementId}, Original document ID: {DocumentId}.",
                workflow.Id,
                workflow.AgreementId,
                workflow.OriginalDocumentId);

            // STEP 6: RETURN TO THE DASHBOARD.
            //
            // Do not wait for the signer.
            // Do not download the signed PDF here.
            // The Adobe webhook/status sync will later
            // change the workflow status to Signed.
            //
            // The employee can then explicitly choose
            // the Save action.

            return new
            {
                Success = true,

                Message = "Agreement submitted successfully. The recipient has been sent the signing request.",

                DocumentId = request.DocumentId,

                AgreementNumber = request.AgreementNumber,

                RecipientName = request.RecipientName,

                RecipientEmail = request.RecipientEmail,

                TransientDocumentId = transientDocumentId,

                AdobeAgreementId = adobeAgreementId,

                WorkflowId = workflow.Id,

                Status = workflow.Status
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Agreement processing failed for DocuWare document {DocumentId}, employee {UserId}.",
                request.DocumentId,
                userId);

            throw;
        }
        finally
        {
            // The temporary original PDF is no longer needed
            // after it has been uploaded to Adobe.

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(
                        cleanupException,
                        "Could not delete temporary PDF file.");
                }
            }
        }
    }

    // =====================================================
    // API ACCESS POINT VALIDATION
    // =====================================================

    private static string ValidateApiAccessPoint(string value)
    {
        if (!Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Adobe returned an invalid API access point.");
        }

        return uri.GetLeftPart(UriPartial.Authority)
            .TrimEnd('/');
    }
}
