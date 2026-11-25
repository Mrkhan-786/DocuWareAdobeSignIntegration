
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DocuWareAdobeSignIntegration.Services;

namespace DocuWareAdobeSignIntegration.Controllers;

[ApiController]
[Route("api/docuware")]
public class DocuWareController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly DocuWareDocumentService _documentService;
    private readonly ILogger<DocuWareController> _logger;

    public DocuWareController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        DocuWareDocumentService documentService,
        ILogger<DocuWareController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _documentService = documentService;
        _logger = logger;
    }

    // ======================================================
    // DOCUWARE OAUTH LOGIN
    // ======================================================

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(
        [FromQuery] string? returnUrl = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl) ||
            !Url.IsLocalUrl(returnUrl))
        {
            returnUrl = "/";
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = returnUrl
        };

        _logger.LogInformation(
            "Starting DocuWare authentication.");

        return Challenge(properties, "DocuWare");
    }

    // ======================================================
    // TEST AUTHENTICATED DOCUWARE PLATFORM CONNECTION
    // ======================================================

    [Authorize]
    [HttpGet("test-connection")]
    public async Task<IActionResult> TestConnection(
        CancellationToken cancellationToken)
    {
        try
        {
            var accessToken =
                await HttpContext.GetTokenAsync("access_token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning(
                    "No DocuWare access token was found for the authenticated user.");

                return Unauthorized(new
                {
                    success = false,
                    message = "No DocuWare access token was found. Please sign in again."
                });
            }

            var baseUrl =
                _configuration["DocuWare:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "DocuWare:BaseUrl is not configured."
                });
            }

            var requestUrl =
                $"{baseUrl.TrimEnd('/')}/FileCabinets";

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                requestUrl);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    accessToken);

            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));

            var client =
                _httpClientFactory.CreateClient();

            using var response = await client.SendAsync(
                request,
                cancellationToken);

            var responseBody =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            _logger.LogInformation(
                "DocuWare connection test returned HTTP {StatusCode}.",
                (int)response.StatusCode);

            return StatusCode(
                (int)response.StatusCode,
                new
                {
                    success = response.IsSuccessStatusCode,

                    message = response.IsSuccessStatusCode
                        ? "Successfully connected to DocuWare Platform."
                        : "DocuWare returned an unsuccessful response.",

                    statusCode = (int)response.StatusCode,

                    endpoint = requestUrl,

                    response = responseBody.Length > 3000
                        ? responseBody[..3000]
                        : responseBody
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "DocuWare connection test failed.");

            return StatusCode(500, new
            {
                success = false,
                message = "The DocuWare connection test failed."
            });
        }
    }

    // ======================================================
    // DOWNLOAD DOCUWARE DOCUMENT
    // ======================================================

    [Authorize]
    [HttpGet("documents/{documentId}/download")]
    public async Task<IActionResult> DownloadDocument(
        string documentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return BadRequest(new
            {
                success = false,
                message = "A document ID is required."
            });
        }

        string? filePath = null;

        try
        {
            var accessToken =
                await HttpContext.GetTokenAsync("access_token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "No DocuWare access token was found. Please sign in again."
                });
            }

            filePath =
                await _documentService.DownloadDocumentAsync(
                    documentId,
                    accessToken,
                    cancellationToken);

            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning(
                    "Downloaded file was not found for DocuWare document {DocumentId}.",
                    documentId);

                return NotFound(new
                {
                    success = false,
                    message = "The downloaded document could not be found."
                });
            }

            var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.Asynchronous |
                         FileOptions.SequentialScan);

            Response.OnCompleted(() =>
            {
                try
                {
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not delete temporary PDF file.");
                }

                return Task.CompletedTask;
            });

            return File(
                fileStream,
                "application/pdf",
                $"DocuWare-{documentId}.pdf");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare document {DocumentId} was not found.",
                documentId);

            return NotFound(new
            {
                success = false,
                message = "The requested DocuWare document was not found."
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "DocuWare rejected the document download request.");

            return Unauthorized(new
            {
                success = false,
                message = "DocuWare did not authorize the document download."
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "DocuWare document download failed.");

            return StatusCode(502, new
            {
                success = false,
                message = "DocuWare could not complete the document download."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error downloading document {DocumentId}.",
                documentId);

            return StatusCode(500, new
            {
                success = false,
                message = "An unexpected error occurred while downloading the document."
            });
        }
    }
}
