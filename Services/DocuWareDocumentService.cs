
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.WebUtilities;

namespace DocuWareAdobeSignIntegration.Services;

public class DocuWareDocumentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocuWareDocumentService> _logger;

    public DocuWareDocumentService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DocuWareDocumentService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    // =====================================================
    // DOWNLOAD DOCUWARE DOCUMENT
    // =====================================================

    public async Task<string> DownloadDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException(
                "A DocuWare document ID is required.",
                nameof(documentId));
        }

        var urlTemplate =
            _configuration["DocuWare:DocumentDownloadUrlTemplate"];

        if (string.IsNullOrWhiteSpace(urlTemplate))
        {
            throw new InvalidOperationException(
                "DocuWare:DocumentDownloadUrlTemplate is not configured.");
        }

        if (!urlTemplate.Contains(
                "{documentId}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The DocuWare download URL template must contain {documentId}.");
        }

        var documentUrl = urlTemplate.Replace(
            "{documentId}",
            Uri.EscapeDataString(documentId),
            StringComparison.Ordinal);

        if (!Uri.TryCreate(
                documentUrl,
                UriKind.Absolute,
                out var downloadUri))
        {
            throw new InvalidOperationException(
                "The configured DocuWare download URL is not a valid absolute URL.");
        }

        if (downloadUri.Scheme != Uri.UriSchemeHttps &&
            !downloadUri.IsLoopback)
        {
            throw new InvalidOperationException(
                "The DocuWare download URL must use HTTPS.");
        }

        _logger.LogInformation(
            "Downloading DocuWare document {DocumentId}.",
            documentId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            downloadUri);

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/pdf"));

        // Optional bearer token.
        // If your DocuWare authentication is handled by a configured
        // HttpClient handler, leave this setting empty.

        var accessToken =
            _configuration["DocuWare:AccessToken"];

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    accessToken);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "DocuWare rejected the document download request for {DocumentId}.",
                documentId);

            throw new UnauthorizedAccessException(
                "DocuWare authentication failed or the account does not have permission to download this document.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(
                $"DocuWare document '{documentId}' was not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "DocuWare document download failed. HTTP status: {StatusCode}. Document ID: {DocumentId}.",
                (int)response.StatusCode,
                documentId);

            throw new HttpRequestException(
                $"DocuWare document download failed with HTTP status {(int)response.StatusCode}.");
        }

        // Protect the application from unexpectedly large downloads.
        const long maximumFileSize = 100 * 1024 * 1024;

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > maximumFileSize)
        {
            throw new InvalidOperationException(
                "The requested document exceeds the 100 MB download limit.");
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DocuWareAdobeSignIntegration");

        Directory.CreateDirectory(temporaryDirectory);

        var fileName =
            $"{Guid.NewGuid():N}.pdf";

        var filePath = Path.Combine(
            temporaryDirectory,
            fileName);

        try
        {
            await using (var inputStream =
                await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var outputStream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int bytesRead;

                while ((bytesRead = await inputStream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;

                    if (totalBytes > maximumFileSize)
                    {
                        throw new InvalidOperationException(
                            "The downloaded document exceeds the 100 MB limit.");
                    }

                    await outputStream.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken);
                }
            }

            // Verify that the downloaded file is actually a PDF.
            await using (var verificationStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 5,
                useAsync: true))
            {
                var signature = new byte[5];

                var bytesRead = await verificationStream.ReadAsync(
                    signature.AsMemory(0, signature.Length),
                    cancellationToken);

                var isPdf =
                    bytesRead == 5 &&
                    signature[0] == (byte)'%' &&
                    signature[1] == (byte)'P' &&
                    signature[2] == (byte)'D' &&
                    signature[3] == (byte)'F' &&
                    signature[4] == (byte)'-';

                if (!isPdf)
                {
                    throw new InvalidDataException(
                        "The downloaded DocuWare file is not a valid PDF.");
                }
            }

            _logger.LogInformation(
                "DocuWare document {DocumentId} downloaded successfully.",
                documentId);

            return filePath;
        }
        catch
        {
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }

            throw;
        }
    }
}
