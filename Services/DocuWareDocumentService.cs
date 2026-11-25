using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Services;

public class DocuWareDocumentService
{
    private const long MaxFileSizeBytes = 100L * 1024L * 1024L;
    private const long MaxOriginalUploadSizeBytes = 10L * 1024L * 1024L;
    private const int BufferSize = 81920;
    private const string RequestDateField = "REQUEST_DATE";

    private static readonly string[] EditableFields =
    {
        "REQUEST_DATE",
        "DOCUMENT_TYPE",
        "RECIPIENT_COMPANY",
        "RECIPIENT_NAME",
        "RECIPIENT_EMAIL"
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocuWareDocumentService> _logger;

    public DocuWareDocumentService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DocuWareDocumentService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ======================================================
    // GET EXISTING DOCUMENT INDEX FIELDS
    // ======================================================

    public async Task<Dictionary<string, string?>> GetDocumentIndexFieldsAsync(
        string documentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateAccessToken(accessToken);

        using var request = CreateRequest(
            HttpMethod.Get,
            GetDocumentFieldsUrl(documentId),
            accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        HandleDocuWareResponse(response, responseBody, "retrieve document index fields");

        using var json = JsonDocument.Parse(responseBody);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldName in EditableFields)
        {
            result[fieldName] = null;
        }

        var root = json.RootElement;
        if (!TryGetPropertyIgnoreCase(root, "Field", out var fieldsElement) &&
            !TryGetPropertyIgnoreCase(root, "Fields", out fieldsElement))
        {
            _logger.LogWarning(
                "DocuWare returned no Field or Fields collection for document {DocumentId}.",
                documentId);
            return result;
        }

        if (fieldsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "DocuWare returned an unexpected index-field response.");
        }

        foreach (var field in fieldsElement.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(field, "FieldName", out var nameElement))
            {
                continue;
            }

            var fieldName = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(fieldName) || !result.ContainsKey(fieldName))
            {
                continue;
            }

            if (TryGetPropertyIgnoreCase(field, "Item", out var item))
            {
                result[fieldName] = ReadFieldValue(item);
            }
        }

        _logger.LogInformation(
            "Retrieved editable index fields for DocuWare document {DocumentId}.",
            documentId);

        return result;
    }

    // ======================================================
    // UPDATE EXISTING DOCUMENT INDEX FIELDS
    // ======================================================

    public async Task UpdateDocumentIndexFieldsAsync(
        string documentId,
        string accessToken,
        IReadOnlyDictionary<string, string?> indexFields,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateAccessToken(accessToken);

        if (indexFields == null)
        {
            throw new ArgumentNullException(nameof(indexFields));
        }

        var suppliedFields = new Dictionary<string, string?>(
            indexFields,
            StringComparer.OrdinalIgnoreCase);

        foreach (var fieldName in EditableFields)
        {
            if (!suppliedFields.ContainsKey(fieldName))
            {
                throw new ArgumentException(
                    $"The required index field '{fieldName}' was not supplied.",
                    nameof(indexFields));
            }
        }

        var fields = EditableFields.Select(fieldName =>
        {
            var value = suppliedFields[fieldName];
            var isDate = fieldName.Equals(
                RequestDateField,
                StringComparison.OrdinalIgnoreCase);

            return new
            {
                FieldName = fieldName,
                Item = isDate
                    ? (string.IsNullOrWhiteSpace(value) ? null : NormalizeDocuWareDate(value))
                    : value?.Trim(),
                ItemElementName = isDate ? "Date" : "String"
            };
        }).ToArray();

        var payload = new { Field = fields };
        var json = JsonSerializer.Serialize(payload);

        using var request = CreateRequest(
            HttpMethod.Put,
            GetDocumentFieldsUrl(documentId),
            accessToken);

        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        HandleDocuWareResponse(response, responseBody, "update document index fields");

        _logger.LogInformation(
            "Successfully updated index fields for existing DocuWare document {DocumentId}.",
            documentId);
    }

    // ======================================================
    // DOWNLOAD ORIGINAL DOCUMENT FROM DOCUWARE
    // ======================================================

    public async Task<string> DownloadDocumentAsync(
        string documentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateAccessToken(accessToken);

        var urlTemplate = _configuration["DocuWare:DocumentDownloadUrlTemplate"];
        if (string.IsNullOrWhiteSpace(urlTemplate) ||
            !urlTemplate.Contains("{documentId}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DocuWare:DocumentDownloadUrlTemplate must be configured and contain {documentId}.");
        }

        var downloadUrl = urlTemplate.Replace(
            "{documentId}",
            Uri.EscapeDataString(documentId),
            StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
            (downloadUri.Scheme != Uri.UriSchemeHttps && !downloadUri.IsLoopback))
        {
            throw new InvalidOperationException(
                "The DocuWare document download URL is invalid.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(
                "DocuWare rejected the download access token.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(
                $"DocuWare document '{documentId}' was not found.");
        }

        var responseBody = response.IsSuccessStatusCode
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);

        HandleDocuWareResponse(response, responseBody, "download document");
        ValidateContentLength(response);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DocuWare returned unexpected content type: {mediaType}");
        }

        var directory = Path.Combine(Path.GetTempPath(), "DocuWareAdobeSignIntegration");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{Guid.NewGuid():N}.pdf");

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous);

            var buffer = new byte[BufferSize];
            var firstBytes = new byte[5];
            var firstBytesCount = 0;
            long totalBytes = 0;
            int bytesRead;

            while ((bytesRead = await input.ReadAsync(
                buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > MaxFileSizeBytes)
                {
                    throw new InvalidOperationException(
                        "The DocuWare document exceeds the 100 MB download limit.");
                }

                if (firstBytesCount < firstBytes.Length)
                {
                    var count = Math.Min(firstBytes.Length - firstBytesCount, bytesRead);
                    Array.Copy(buffer, 0, firstBytes, firstBytesCount, count);
                    firstBytesCount += count;
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            if (totalBytes == 0)
            {
                throw new InvalidDataException("DocuWare returned an empty document.");
            }

            ValidatePdfHeader(firstBytes, firstBytesCount);
            return filePath;
        }
        catch
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            throw;
        }
    }

    // ======================================================
    // GET DOCUWARE DOCUMENT SECTION ID
    // ======================================================

    public async Task<string> GetDocumentSectionIdAsync(
        string documentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateAccessToken(accessToken);

        var section = await GetDocumentSectionAsync(
            documentId,
            null,
            accessToken,
            cancellationToken);

        if (!TryGetPropertyIgnoreCase(section, "Id", out var idElement) &&
            !TryGetPropertyIgnoreCase(section, "SectionId", out idElement))
        {
            throw new InvalidOperationException(
                $"DocuWare did not return a section ID for document '{documentId}'.");
        }

        var sectionId = idElement.ToString();
        if (string.IsNullOrWhiteSpace(sectionId))
        {
            throw new InvalidOperationException(
                $"DocuWare returned an empty section ID for document '{documentId}'.");
        }

        _logger.LogInformation(
            "Retrieved section {SectionId} for DocuWare document {DocumentId}.",
            sectionId,
            documentId);

        return sectionId;
    }

    // ======================================================
    // REPLACE EXISTING DOCUWARE DOCUMENT SECTION CONTENT
    //
    // Uses DocuWare's section-data endpoint:
    // POST /FileCabinets/{FileCabinetId}/Sections/{SectionId}/Data?fileName={fileName}
    //
    // It updates the existing section; it does not create a
    // new document and does not change the document ID.
    // ======================================================

    public async Task ReplaceDocumentSectionAsync(
        string documentId,
        string sectionId,
        Stream signedPdfStream,
        string fileName,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateAccessToken(accessToken);

        if (string.IsNullOrWhiteSpace(sectionId))
        {
            throw new ArgumentException(
                "A DocuWare section ID is required.",
                nameof(sectionId));
        }

        ValidatePdfStream(signedPdfStream, fileName);
        var originalPosition = signedPdfStream.Position;

        try
        {
            var fileBytes = await ReadPdfBytesAsync(
                signedPdfStream,
                MaxFileSizeBytes,
                cancellationToken);

            var (baseUrl, cabinetId) = GetUploadConfiguration();
            var safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                throw new ArgumentException(
                    "A valid PDF filename is required.",
                    nameof(fileName));
            }

            var contentUrl =
                $"{baseUrl}/FileCabinets/{Uri.EscapeDataString(cabinetId)}" +
                $"/Sections/{Uri.EscapeDataString(sectionId)}/Data" +
                $"?fileName={Uri.EscapeDataString(safeFileName)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, contentUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            request.Content = new ByteArrayContent(fileBytes);
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/pdf");

            _logger.LogInformation(
                "Uploading signed PDF to DocuWare section. DocumentId={DocumentId}, SectionId={SectionId}, FileName={FileName}, Size={Size} bytes.",
                documentId,
                sectionId,
                safeFileName,
                fileBytes.Length);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            HandleDocuWareResponse(
                response,
                responseBody,
                "replace document section content");

            _logger.LogInformation(
                "DocuWare accepted the signed PDF replacement. DocumentId={DocumentId}, SectionId={SectionId}, FileName={FileName}, HTTP={StatusCode}.",
                documentId,
                sectionId,
                safeFileName,
                (int)response.StatusCode);
        }
        finally
        {
            if (signedPdfStream.CanSeek)
            {
                signedPdfStream.Position = originalPosition;
            }
        }
    }

    // ======================================================
    // INTERNAL: RETRIEVE A DOCUMENT SECTION
    // ======================================================

    private async Task<JsonElement> GetDocumentSectionAsync(
        string documentId,
        string? requestedSectionId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var documentRequest = CreateRequest(
            HttpMethod.Get,
            GetDocumentUrl(documentId),
            accessToken);

        using var documentResponse = await _httpClient.SendAsync(
            documentRequest,
            cancellationToken);

        var documentBody = await documentResponse.Content.ReadAsStringAsync(cancellationToken);
        HandleDocuWareResponse(documentResponse, documentBody, "retrieve document sections");

        using var documentJson = JsonDocument.Parse(documentBody);
        var documentRoot = documentJson.RootElement;

        if (TryFindSectionsCollection(documentRoot, out var inlineSections))
        {
            var matchingSection = FindMatchingSection(inlineSections, requestedSectionId);
            if (matchingSection.HasValue)
            {
                return matchingSection.Value.Clone();
            }
        }

        var sectionsRelation = FindLinkProperty(documentRoot, "SectionsRelationLink");
        if (string.IsNullOrWhiteSpace(sectionsRelation))
        {
            throw new InvalidOperationException(
                $"DocuWare did not return a Sections collection or SectionsRelationLink for document '{documentId}'.");
        }

        var sectionsUrl = ResolveAndValidateDocuWareLink(sectionsRelation);
        using var sectionsRequest = CreateRequest(
            HttpMethod.Get,
            sectionsUrl,
            accessToken);

        using var sectionsResponse = await _httpClient.SendAsync(
            sectionsRequest,
            cancellationToken);

        var sectionsBody = await sectionsResponse.Content.ReadAsStringAsync(cancellationToken);
        HandleDocuWareResponse(
            sectionsResponse,
            sectionsBody,
            "retrieve document section details");

        using var sectionsJson = JsonDocument.Parse(sectionsBody);
        if (!TryFindSectionsCollection(sectionsJson.RootElement, out var sections))
        {
            if (IsSectionElement(sectionsJson.RootElement) &&
                SectionMatches(sectionsJson.RootElement, requestedSectionId))
            {
                return sectionsJson.RootElement.Clone();
            }

            throw new InvalidOperationException(
                $"DocuWare returned no section collection for document '{documentId}'.");
        }

        var section = FindMatchingSection(sections, requestedSectionId);
        if (!section.HasValue)
        {
            throw new InvalidOperationException(
                requestedSectionId == null
                    ? $"DocuWare returned no sections for document '{documentId}'."
                    : $"Section '{requestedSectionId}' was not found in DocuWare document '{documentId}'.");
        }

        return section.Value.Clone();
    }

    private static bool TryFindSectionsCollection(
        JsonElement element,
        out JsonElement sections)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(element, "Sections", out var sectionsElement))
            {
                if (sectionsElement.ValueKind == JsonValueKind.Array)
                {
                    sections = sectionsElement;
                    return true;
                }

                if (sectionsElement.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyIgnoreCase(sectionsElement, "Section", out var nestedSection) &&
                    (nestedSection.ValueKind == JsonValueKind.Array ||
                     nestedSection.ValueKind == JsonValueKind.Object))
                {
                    sections = nestedSection;
                    return true;
                }
            }

            if (TryGetPropertyIgnoreCase(element, "Section", out var directSection) &&
                (directSection.ValueKind == JsonValueKind.Array ||
                 directSection.ValueKind == JsonValueKind.Object))
            {
                sections = directSection;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindSectionsCollection(property.Value, out sections))
                {
                    return true;
                }
            }
        }

        sections = default;
        return false;
    }

    private static JsonElement? FindMatchingSection(
        JsonElement sections,
        string? requestedSectionId)
    {
        if (sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in sections.EnumerateArray())
            {
                if (IsSectionElement(section) && SectionMatches(section, requestedSectionId))
                {
                    return section;
                }
            }

            return null;
        }

        if (sections.ValueKind == JsonValueKind.Object)
        {
            if (IsSectionElement(sections) && SectionMatches(sections, requestedSectionId))
            {
                return sections;
            }

            if (TryGetPropertyIgnoreCase(sections, "Section", out var nested))
            {
                return FindMatchingSection(nested, requestedSectionId);
            }
        }

        return null;
    }

    private static bool IsSectionElement(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               (TryGetPropertyIgnoreCase(element, "Id", out _) ||
                TryGetPropertyIgnoreCase(element, "SectionId", out _));
    }

    private static bool SectionMatches(
        JsonElement section,
        string? requestedSectionId)
    {
        if (string.IsNullOrWhiteSpace(requestedSectionId))
        {
            return true;
        }

        if (TryGetPropertyIgnoreCase(section, "Id", out var idElement) ||
            TryGetPropertyIgnoreCase(section, "SectionId", out idElement))
        {
            return string.Equals(
                idElement.ToString(),
                requestedSectionId,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // ======================================================
    // COMMON URL AND REQUEST HELPERS
    // ======================================================

    private string GetDocumentFieldsUrl(string documentId)
    {
        var (baseUrl, cabinetId) = GetUploadConfiguration();
        return $"{baseUrl}/FileCabinets/{Uri.EscapeDataString(cabinetId)}" +
               $"/Documents/{Uri.EscapeDataString(documentId)}/Fields";
    }

    private string GetDocumentUrl(string documentId)
    {
        var (baseUrl, cabinetId) = GetUploadConfiguration();
        return $"{baseUrl}/FileCabinets/{Uri.EscapeDataString(cabinetId)}" +
               $"/Documents/{Uri.EscapeDataString(documentId)}";
    }

    private (string BaseUrl, string CabinetId) GetUploadConfiguration()
    {
        var configuredBaseUrl = _configuration["DocuWare:BaseUrl"];
        var cabinetId = _configuration["DocuWare:FileCabinetId"];

        if (string.IsNullOrWhiteSpace(configuredBaseUrl) ||
            string.IsNullOrWhiteSpace(cabinetId))
        {
            throw new InvalidOperationException(
                "DocuWare:BaseUrl or DocuWare:FileCabinetId is not configured.");
        }

        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "DocuWare:BaseUrl must be a valid HTTPS URL.");
        }

        return (configuredBaseUrl.TrimEnd('/'), cabinetId);
    }

    private string ResolveAndValidateDocuWareLink(string link)
    {
        var (baseUrl, _) = GetUploadConfiguration();
        if (!Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                "The configured DocuWare base URL is invalid.");
        }

        if (!Uri.TryCreate(baseUri, link, out var resolvedUri))
        {
            throw new InvalidOperationException(
                "DocuWare returned an invalid relation URL.");
        }

        // Never send the DocuWare bearer token to an unrelated host.
        if (resolvedUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(
                resolvedUri.Host,
                baseUri.Host,
                StringComparison.OrdinalIgnoreCase) ||
            resolvedUri.Port != baseUri.Port)
        {
            throw new InvalidOperationException(
                "DocuWare returned a relation URL outside the configured DocuWare host.");
        }

        return resolvedUri.AbsoluteUri;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void HandleDocuWareResponse(
        HttpResponseMessage response,
        string responseBody,
        string operation)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(
                $"DocuWare rejected the request to {operation}. Check the session and permissions.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Unable to {operation} in DocuWare. HTTP " +
                $"{(int)response.StatusCode} ({response.StatusCode}). " +
                $"Response: {Truncate(responseBody)}",
                null,
                response.StatusCode);
        }
    }

    // ======================================================
    // JSON FIELD AND RELATION HELPERS
    // ======================================================

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? FindLinkProperty(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var linkElement))
            {
                var link = ReadLinkValue(linkElement);
                if (!string.IsNullOrWhiteSpace(link))
                {
                    return link;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nestedLink = FindLinkProperty(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nestedLink))
                {
                    return nestedLink;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nestedLink = FindLinkProperty(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nestedLink))
                {
                    return nestedLink;
                }
            }
        }

        return null;
    }

    private static string? ReadLinkValue(JsonElement linkElement)
    {
        if (linkElement.ValueKind == JsonValueKind.String)
        {
            return linkElement.GetString();
        }

        if (linkElement.ValueKind == JsonValueKind.Object)
        {
            string[] possibleLinkNames = { "href", "Href", "Uri", "Url" };
            foreach (var name in possibleLinkNames)
            {
                if (TryGetPropertyIgnoreCase(linkElement, name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }

        return null;
    }

    private static string? ReadFieldValue(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Null ||
            item.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (item.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(item, "Value", out var value))
            {
                return ReadFieldValue(value);
            }

            if (TryGetPropertyIgnoreCase(item, "Item", out var nestedItem))
            {
                return ReadFieldValue(nestedItem);
            }

            return item.ToString();
        }

        if (item.ValueKind == JsonValueKind.Array)
        {
            return string.Join(
                ", ",
                item.EnumerateArray()
                    .Select(ReadFieldValue)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return item.ToString();
    }

    // ======================================================
    // VALIDATION AND FILE HELPERS
    // ======================================================

    private static void ValidateDocumentId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException(
                "A DocuWare document ID is required.",
                nameof(documentId));
        }
    }

    private static void ValidateAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new UnauthorizedAccessException(
                "A DocuWare access token is required.");
        }
    }

    private static void ValidatePdfStream(Stream pdfStream, string fileName)
    {
        if (pdfStream == null || !pdfStream.CanRead || !pdfStream.CanSeek)
        {
            throw new ArgumentException(
                "A readable, seekable PDF stream is required.",
                nameof(pdfStream));
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Only PDF files are supported.",
                nameof(fileName));
        }
    }

    private static async Task<byte[]> ReadPdfBytesAsync(
        Stream stream,
        long maximumSize,
        CancellationToken cancellationToken)
    {
        await using var memoryStream = new MemoryStream();
        var buffer = new byte[BufferSize];
        long totalBytes = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(
            buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            totalBytes += bytesRead;
            if (totalBytes > maximumSize)
            {
                throw new InvalidOperationException(
                    $"The PDF exceeds the maximum allowed size of {maximumSize / (1024 * 1024)} MB.");
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        var bytes = memoryStream.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("The PDF file is empty.");
        }

        ValidatePdfHeader(bytes, bytes.Length);
        return bytes;
    }

    private static string NormalizeDocuWareDate(string value)
    {
        if (!DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate))
        {
            throw new ArgumentException($"'{value}' is not a valid DocuWare date.");
        }

        return parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static void ValidatePdfHeader(byte[] bytes, int bytesRead)
    {
        if (bytesRead < 5 ||
            bytes[0] != 0x25 ||
            bytes[1] != 0x50 ||
            bytes[2] != 0x44 ||
            bytes[3] != 0x46 ||
            bytes[4] != 0x2D)
        {
            throw new InvalidDataException("The document is not a valid PDF file.");
        }
    }

    private static void ValidateContentLength(HttpResponseMessage response)
    {
        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > MaxFileSizeBytes)
        {
            throw new InvalidOperationException(
                "The DocuWare document exceeds the 100 MB download limit.");
        }
    }

    private static string Truncate(string value, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }

    // ======================================================
    // EXTRACT DOCUMENT ID FROM UPLOAD RESPONSE
    // ======================================================

    private static string ExtractDocumentId(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new InvalidOperationException(
                "DocuWare returned an empty upload response.");
        }

        using var json = JsonDocument.Parse(responseBody);
        string[] possibleNames = { "Id", "DocumentId", "DWDOCID", "DocId" };

        if (TryExtractId(json.RootElement, possibleNames, out var id))
        {
            return id;
        }

        throw new InvalidOperationException(
            "No document ID was found in the DocuWare upload response.");
    }

    private static bool TryExtractId(
        JsonElement element,
        string[] names,
        out string id)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyIgnoreCase(element, name, out var value) &&
                    value.ValueKind != JsonValueKind.Null)
                {
                    id = value.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        return true;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryExtractId(property.Value, names, out id))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryExtractId(item, names, out id))
                {
                    return true;
                }
            }
        }

        id = string.Empty;
        return false;
    }

    // ======================================================
    // UPLOAD ORIGINAL PDF WITH INDEX FIELDS
    // ======================================================

    public async Task<string> UploadOriginalDocumentAsync(
        Stream pdfStream,
        string fileName,
        string accessToken,
        IReadOnlyDictionary<string, string> indexFields,
        CancellationToken cancellationToken = default)
    {
        ValidatePdfStream(pdfStream, fileName);
        ValidateAccessToken(accessToken);

        if (indexFields == null || indexFields.Count == 0)
        {
            throw new ArgumentException(
                "DocuWare index fields are required.",
                nameof(indexFields));
        }

        var (baseUrl, cabinetId) = GetUploadConfiguration();
        var originalPosition = pdfStream.Position;

        try
        {
            var fileBytes = await ReadPdfBytesAsync(
                pdfStream,
                MaxOriginalUploadSizeBytes,
                cancellationToken);

            var metadataFields = indexFields.Select(field =>
            {
                var isDate = field.Key.Equals(
                    RequestDateField,
                    StringComparison.OrdinalIgnoreCase);

                return new
                {
                    FieldName = field.Key,
                    Item = isDate
                        ? NormalizeDocuWareDate(field.Value)
                        : field.Value,
                    ItemElementName = isDate ? "Date" : "String"
                };
            }).ToArray();

            var metadataJson = JsonSerializer.Serialize(new { Fields = metadataFields });
            var url =
                $"{baseUrl}/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Documents";

            using var request = CreateRequest(HttpMethod.Post, url, accessToken);
            using var multipart = new MultipartFormDataContent();

            multipart.Add(
                new StringContent(metadataJson, Encoding.UTF8, "application/json"),
                "document");

            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            multipart.Add(fileContent, "file[]", Path.GetFileName(fileName));
            request.Content = multipart;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            HandleDocuWareResponse(response, body, "upload original document");

            return ExtractDocumentId(body);
        }
        finally
        {
            if (pdfStream.CanSeek)
            {
                pdfStream.Position = originalPosition;
            }
        }
    }

    // ======================================================
    // UPLOAD SIGNED PDF AS A NEW DOCUWARE DOCUMENT
    // Retained for compatibility with existing calling code.
    // This creates a new document; it does not replace one.
    // ======================================================

    public async Task<string> UploadSignedDocumentAsync(
        Stream pdfStream,
        string fileName,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidatePdfStream(pdfStream, fileName);
        ValidateAccessToken(accessToken);

        var (baseUrl, cabinetId) = GetUploadConfiguration();
        var originalPosition = pdfStream.Position;

        try
        {
            var fileBytes = await ReadPdfBytesAsync(
                pdfStream,
                MaxFileSizeBytes,
                cancellationToken);

            var url =
                $"{baseUrl}/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Documents";

            using var request = CreateRequest(HttpMethod.Post, url, accessToken);
            using var multipart = new MultipartFormDataContent();

            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            multipart.Add(fileContent, "file[]", Path.GetFileName(fileName));
            request.Content = multipart;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            HandleDocuWareResponse(response, body, "upload signed document");

            return ExtractDocumentId(body);
        }
        finally
        {
            if (pdfStream.CanSeek)
            {
                pdfStream.Position = originalPosition;
            }
        }
    }


    // ======================================================
    // DOCUWARE RECYCLE BIN OPERATIONS
    // ======================================================
    /// <summary>
    /// Moves an existing document into DocuWare's Trash Bin.
    /// This is a soft delete; DocuWare retains the document for its
    /// configured Trash Bin retention period (30 days for DocuWare Cloud).
    /// </summary>
    public async Task<string> MoveDocumentToTrashBinAsync(
        string documentId,
        string fileCabinetId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentId(documentId);
        ValidateAccessToken(accessToken);

        if (string.IsNullOrWhiteSpace(fileCabinetId))
        {
            throw new ArgumentException("A DocuWare file cabinet ID is required.", nameof(fileCabinetId));
        }

        var (baseUrl, configuredCabinetId) = GetUploadConfiguration();

        // The service is configured for one cabinet. Refuse to delete a document
        // if the workflow record points to another cabinet.
        if (!string.Equals(configuredCabinetId, fileCabinetId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The workflow's File Cabinet ID does not match DocuWare:FileCabinetId.");
        }

        var url =
            $"{baseUrl}/FileCabinets/{Uri.EscapeDataString(configuredCabinetId)}" +
            $"/Documents/{Uri.EscapeDataString(documentId)}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        HandleDocuWareResponse(response, responseBody, "move document to the Trash Bin");

        // DocuWare's documented Trash Bin identifier format for a file cabinet
        // is tr_{FileCabinetGuid}-{originalDocumentId}. The final hyphen separates
        // the original document ID from the cabinet GUID.
        var trashBinDocumentId = $"tr_{configuredCabinetId}-{documentId}";

        _logger.LogInformation(
            "Moved DocuWare document {DocumentId} from cabinet {FileCabinetId} to the Trash Bin as {TrashBinDocumentId}.",
            documentId,
            configuredCabinetId,
            trashBinDocumentId);

        return trashBinDocumentId;
    }

    /// <summary>
    /// Retrieves the current user's visible Trash Bin documents.
    /// DocuWare applies its own permissions to this result.
    /// </summary>
    public async Task<IReadOnlyList<DocuWareTrashBinDocument>> GetTrashBinDocumentsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);

        var (baseUrl, _) = GetUploadConfiguration();
        var url = $"{baseUrl}/TrashBin/Query";

        var query = new
        {
            Start = 0,
            Count = 100,
            CalculateTotalCount = true,
            SortOrder = new[]
            {
                new { Field = "DELETEDATETIME", Direction = "Desc" }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(query),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        HandleDocuWareResponse(response, responseBody, "retrieve Trash Bin documents");

        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;

        if (!TryFindArrayProperty(root, "Rows", out var rows))
        {
            _logger.LogWarning("DocuWare Trash Bin query returned no Rows array.");
            return Array.Empty<DocuWareTrashBinDocument>();
        }

        var headers = new List<string>();
        if (TryFindArrayProperty(root, "Headers", out var headerArray))
        {
            foreach (var header in headerArray.EnumerateArray())
            {
                headers.Add(
                    TryGetPropertyIgnoreCase(header, "FieldName", out var fieldName)
                        ? fieldName.ToString()
                        : string.Empty);
            }
        }

        var result = new List<DocuWareTrashBinDocument>();

        foreach (var row in rows.EnumerateArray())
        {
            var trashId = TryGetPropertyIgnoreCase(row, "Id", out var idElement)
                ? idElement.ToString()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(trashId))
            {
                continue;
            }

            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            if (TryGetPropertyIgnoreCase(row, "Items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (index < headers.Count && !string.IsNullOrWhiteSpace(headers[index]))
                    {
                        fields[headers[index]] = ReadTrashBinCell(item);
                    }
                    index++;
                }
            }

            var name = FindField(fields, "DOCUMENTNAME", "NAME", "FILENAME")
                ?? "Deleted DocuWare document";
            var deletedBy = FindField(fields, "USER", "DELETEDBY", "USERNAME", "DELETEDBYUSER");
            var location = FindField(fields, "LOCATION", "FILECABINET", "DOCUMENTTRAY", "STORAGELOCATION");
            var originalDocumentId = FindField(fields, "DWDOCID", "DOCUMENTID", "DOCID")
                ?? ExtractOriginalDocumentId(trashId);
            var deletedAtText = FindField(fields, "DELETEDATETIME", "DELETEDON", "DELETEDAT");
            var remainingDaysText = FindField(fields, "REMAININGDAYS", "RETENTIONDAYS", "DAYSREMAINING");

            DateTime? deletedAt = null;
            if (!string.IsNullOrWhiteSpace(deletedAtText) &&
                DateTimeOffset.TryParse(
                    deletedAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsedDate))
            {
                deletedAt = parsedDate.UtcDateTime;
            }

            int? remainingDays = null;
            if (int.TryParse(remainingDaysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDays))
            {
                remainingDays = parsedDays;
            }

            result.Add(new DocuWareTrashBinDocument
            {
                Id = trashId,
                Name = name,
                DeletedAtUtc = deletedAt,
                DeletedBy = deletedBy,
                Location = location,
                OriginalDocumentId = originalDocumentId,
                RemainingDays = remainingDays,
                Fields = fields
            });
        }

        return result;
    }

    /// <summary>Restores one document from DocuWare's Trash Bin.</summary>
    public async Task RestoreFromTrashBinAsync(
        string trashBinDocumentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        await PostTrashBinBatchOperationAsync(
            "BatchRestore",
            trashBinDocumentId,
            accessToken,
            "restore document from the Trash Bin",
            cancellationToken);
    }

    /// <summary>Permanently deletes one document from DocuWare's Trash Bin.</summary>
    public async Task PermanentlyDeleteFromTrashBinAsync(
        string trashBinDocumentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        await PostTrashBinBatchOperationAsync(
            "BatchDelete",
            trashBinDocumentId,
            accessToken,
            "permanently delete document from the Trash Bin",
            cancellationToken);
    }

    private async Task PostTrashBinBatchOperationAsync(
        string operation,
        string trashBinDocumentId,
        string accessToken,
        string description,
        CancellationToken cancellationToken)
    {
        ValidateAccessToken(accessToken);

        if (string.IsNullOrWhiteSpace(trashBinDocumentId) ||
            !trashBinDocumentId.StartsWith("tr_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A valid DocuWare Trash Bin document ID is required.",
                nameof(trashBinDocumentId));
        }

        var (baseUrl, _) = GetUploadConfiguration();
        var url = $"{baseUrl}/TrashBin/{operation}";

        var payload = new { Id = new[] { trashBinDocumentId } };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        HandleDocuWareResponse(response, responseBody, description);

        _logger.LogInformation(
            "DocuWare operation {Operation} succeeded for Trash Bin document {TrashBinDocumentId}.",
            operation,
            trashBinDocumentId);
    }

    private static bool TryFindArrayProperty(
        JsonElement element,
        string propertyName,
        out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var value) &&
                value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindArrayProperty(property.Value, propertyName, out array))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindArrayProperty(item, propertyName, out array))
                {
                    return true;
                }
            }
        }

        array = default;
        return false;
    }

    private static string? ReadTrashBinCell(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "Value", "Item", "DisplayValue", "Text" })
            {
                if (TryGetPropertyIgnoreCase(element, propertyName, out var nested))
                {
                    return ReadTrashBinCell(nested);
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return string.Join(
                ", ",
                element.EnumerateArray()
                    .Select(ReadTrashBinCell)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();
    }

    private static string? FindField(
        IReadOnlyDictionary<string, string?> fields,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ExtractOriginalDocumentId(string trashBinDocumentId)
    {
        // DocuWare format: tr_{source-file-cabinet-guid}-{original-document-id}.
        var separator = trashBinDocumentId.LastIndexOf('-');
        if (separator < 0 || separator == trashBinDocumentId.Length - 1)
        {
            return null;
        }

        return trashBinDocumentId[(separator + 1)..];
    }
}
