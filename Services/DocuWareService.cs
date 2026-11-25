
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DocuWareAdobeSignIntegration.Models;

namespace DocuWareAdobeSignIntegration.Services;

public class DocuWareService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DocuWareService> _logger;
    private readonly IConfiguration _configuration;

    private const string DocumentTypeField = "DOCUMENT_TYPE";

    private const int DocumentPageSize = 100;
    private const int MaxConcurrentFieldRequests = 3;
    private const int MaxRetryAttempts = 2;

    private static readonly TimeSpan DocumentRequestTimeout =
        TimeSpan.FromSeconds(60);

    private static readonly TimeSpan FieldRequestTimeout =
        TimeSpan.FromSeconds(15);

    public DocuWareService(
        HttpClient httpClient,
        ILogger<DocuWareService> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    // ======================================================
    // RETRIEVE DOCUMENTS FROM DOCUWARE
    // ======================================================

    public async Task<List<DocuWareDocumentItem>> GetDocumentsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new UnauthorizedAccessException(
                "A DocuWare access token is required.");
        }

        var baseUrl = _configuration["DocuWare:BaseUrl"];
        var cabinetId = _configuration["DocuWare:FileCabinetId"];

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "DocuWare:BaseUrl is missing.");
        }

        if (string.IsNullOrWhiteSpace(cabinetId))
        {
            throw new InvalidOperationException(
                "DocuWare:FileCabinetId is missing.");
        }

        baseUrl = baseUrl.TrimEnd('/');

        var requestUrl =
            $"{baseUrl}/FileCabinets/" +
            $"{Uri.EscapeDataString(cabinetId)}/Documents" +
            $"?start=0&count={DocumentPageSize}&calculateTotalCount=true";

        _logger.LogInformation(
            "Retrieving documents from DocuWare cabinet {CabinetId}.",
            cabinetId);

        var responseContent = await SendGetWithRetryAsync(
            requestUrl,
            accessToken,
            DocumentRequestTimeout,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            _logger.LogWarning(
                "DocuWare returned an empty document response.");

            return new List<DocuWareDocumentItem>();
        }

        using var json = JsonDocument.Parse(responseContent);

        var documentElements = FindDocumentArray(json.RootElement);

        if (documentElements == null)
        {
            _logger.LogWarning(
                "No document collection was found in the DocuWare response.");

            return new List<DocuWareDocumentItem>();
        }

        var documents = new List<DocuWareDocumentItem>();

        foreach (var element in documentElements.Value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var idValue = GetJsonValue(
                element,
                "Id",
                "ID",
                "DocumentId",
                "DWDOCID");

            if (!int.TryParse(
                idValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var documentId))
            {
                _logger.LogDebug(
                    "Skipping a DocuWare document because its ID could not be read.");

                continue;
            }

            var name = GetJsonValue(
                element,
                "Title",
                "Name",
                "FileName",
                "DocumentName");

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Document {documentId}";
            }

            var createdAt = ParseDate(
                GetJsonValue(
                    element,
                    "CreatedAt",
                    "CreatedOn",
                    "CreationDate",
                    "Created"));

            var lastModified = ParseDate(
                GetJsonValue(
                    element,
                    "LastModified",
                    "ModifiedAt",
                    "ModifiedOn",
                    "ModificationDate"));

            var documentType = FindIndexFieldValue(
                element,
                DocumentTypeField);

            documents.Add(
                new DocuWareDocumentItem(
                    documentId,
                    name,
                    createdAt,
                    lastModified)
                {
                    DocumentType = documentType
                });
        }

        _logger.LogInformation(
            "DocuWare returned {DocumentCount} documents from cabinet {CabinetId}.",
            documents.Count,
            cabinetId);

        // Only retrieve index fields for documents where the
        // DOCUMENT_TYPE field was not included in the listing.
        var enrichedDocuments =
            new DocuWareDocumentItem[documents.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, documents.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentFieldRequests,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var document = documents[index];

                var documentType = document.DocumentType;

                if (string.IsNullOrWhiteSpace(documentType))
                {
                    documentType = await GetDocumentTypeAsync(
                        baseUrl,
                        cabinetId,
                        document.Id,
                        accessToken,
                        token);
                }

                var categoryInfo = GetCategoryInfo(documentType);

                enrichedDocuments[index] = document with
                {
                    DocumentType = documentType,
                    Category = categoryInfo.Category,
                    CategoryClass = categoryInfo.CssClass
                };
            });

        cancellationToken.ThrowIfCancellationRequested();

        var result = enrichedDocuments
            .Where(document => document != null)
            .OrderByDescending(document => document.CreatedAt)
            .ToList();

        _logger.LogInformation(
            "Successfully prepared {DocumentCount} DocuWare documents.",
            result.Count);

        return result;
    }

    // ======================================================
    // RETRIEVE DOCUMENT TYPE FROM DOCUWARE
    // ======================================================

    private async Task<string?> GetDocumentTypeAsync(
        string baseUrl,
        string cabinetId,
        int documentId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var requestUrl =
            $"{baseUrl}/FileCabinets/" +
            $"{Uri.EscapeDataString(cabinetId)}/Documents/" +
            $"{documentId}/Fields";

        try
        {
            var content = await SendGetWithRetryAsync(
                requestUrl,
                accessToken,
                FieldRequestTimeout,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            using var json = JsonDocument.Parse(content);

            return FindIndexFieldValue(
                json.RootElement,
                DocumentTypeField);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to retrieve DOCUMENT_TYPE for DocuWare document {DocumentId}.",
                documentId);

            // The document can still be displayed without its category.
            return null;
        }
    }

    // ======================================================
    // HTTP GET WITH TIMEOUT AND RETRIES
    // ======================================================

    private async Task<string> SendGetWithRetryAsync(
        string requestUrl,
        string accessToken,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(timeout);

            try
            {
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

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                var responseContent =
                    await response.Content.ReadAsStringAsync(
                        timeoutCts.Token);

                _logger.LogInformation(
                    "DocuWare GET returned HTTP {StatusCode} on attempt {Attempt}.",
                    (int)response.StatusCode,
                    attempt);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new UnauthorizedAccessException(
                        "DocuWare rejected the access token. " +
                        "The token may have expired.");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException(
                        "The authenticated user does not have permission " +
                        "to access this DocuWare resource.");
                }

                if (IsTransientStatusCode(response.StatusCode))
                {
                    if (attempt < MaxRetryAttempts)
                    {
                        var delay = GetRetryDelay(response, attempt);

                        _logger.LogWarning(
                            "DocuWare returned HTTP {StatusCode}. Retrying in {DelaySeconds} seconds.",
                            (int)response.StatusCode,
                            delay.TotalSeconds);

                        await Task.Delay(
                            delay,
                            cancellationToken);

                        continue;
                    }

                    throw new HttpRequestException(
                        $"DocuWare returned HTTP {(int)response.StatusCode} " +
                        "after the configured retry attempts.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "DocuWare GET failed with HTTP {StatusCode}.",
                        (int)response.StatusCode);

                    throw new HttpRequestException(
                        $"DocuWare returned HTTP {(int)response.StatusCode}.");
                }

                return responseContent;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // The original request was cancelled, for example
                // because the user navigated away from the dashboard.
                _logger.LogInformation(
                    "DocuWare request was cancelled by the caller.");

                throw;
            }
            catch (OperationCanceledException ex)
            {
                // The linked timeout expired, but the original
                // dashboard request was not cancelled.
                _logger.LogWarning(
                    ex,
                    "DocuWare request timed out after {TimeoutSeconds} seconds. Attempt {Attempt} of {MaxAttempts}.",
                    timeout.TotalSeconds,
                    attempt,
                    MaxRetryAttempts);

                if (attempt >= MaxRetryAttempts)
                {
                    throw new TimeoutException(
                        $"DocuWare did not respond within {timeout.TotalSeconds} seconds " +
                        "after the configured retry attempts.",
                        ex);
                }
            }
            catch (HttpRequestException ex)
                when (attempt < MaxRetryAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Temporary DocuWare HTTP error. Attempt {Attempt} of {MaxAttempts}.",
                    attempt,
                    MaxRetryAttempts);
            }

            if (attempt < MaxRetryAttempts)
            {
                var delay = TimeSpan.FromSeconds(attempt);

                await Task.Delay(
                    delay,
                    cancellationToken);
            }
        }

        throw new HttpRequestException(
            "DocuWare document retrieval failed after the configured retry attempts.");
    }

    private static bool IsTransientStatusCode(
        HttpStatusCode statusCode)
    {
        var status = (int)statusCode;

        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == (HttpStatusCode)429 ||
               status >= 500;
    }

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage response,
        int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is TimeSpan delta &&
            delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(10)
                ? TimeSpan.FromSeconds(10)
                : delta;
        }

        if (retryAfter?.Date is DateTimeOffset retryDate)
        {
            var delay = retryDate - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero)
            {
                return delay > TimeSpan.FromSeconds(10)
                    ? TimeSpan.FromSeconds(10)
                    : delay;
            }
        }

        return TimeSpan.FromSeconds(attempt);
    }

    // ======================================================
    // DOCUMENT TYPE CATEGORY MAPPING
    // ======================================================

    private static (string Category, string CssClass) GetCategoryInfo(
        string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
        {
            return ("Uncategorised", "uncategorised");
        }

        var type = documentType.Trim();

        return type.ToUpperInvariant() switch
        {
            // FINANCIAL
            "INVOICE" =>
                ("Financial", "financial"),

            "RECEIPT" =>
                ("Financial", "financial"),

            "PURCHASE ORDER" =>
                ("Financial", "financial"),

            "QUOTATION" =>
                ("Financial", "financial"),

            "BUDGET REPORT" =>
                ("Financial", "financial"),

            "FINANCIAL STATEMENT" =>
                ("Financial", "financial"),

            // LEGAL
            "CONTRACT" =>
                ("Legal", "legal"),

            "NON-DISCLOSURE AGREEMENT (NDA)" or "NDA" =>
                ("Legal", "legal"),

            "SERVICE LEVEL AGREEMENT (SLA)" or "SLA" =>
                ("Legal", "legal"),

            "TERMS AND CONDITIONS" =>
                ("Legal", "legal"),

            // HUMAN RESOURCES
            "EMPLOYMENT CONTRACT" =>
                ("Human Resources", "human-resources"),

            "JOB DESCRIPTION" =>
                ("Human Resources", "human-resources"),

            "LEAVE REQUEST FORM" =>
                ("Human Resources", "human-resources"),

            "PERFORMANCE REVIEW" =>
                ("Human Resources", "human-resources"),

            "DISCIPLINARY NOTICE" =>
                ("Human Resources", "human-resources"),

            // OPERATIONS
            "STANDARD OPERATING PROCEDURE (SOP)" or "SOP" =>
                ("Operations", "operations"),

            "WORK INSTRUCTION" =>
                ("Operations", "operations"),

            "INCIDENT REPORT" =>
                ("Operations", "operations"),

            "RISK ASSESSMENT" =>
                ("Operations", "operations"),

            // PROJECT MANAGEMENT
            "PROJECT CHARTER" =>
                ("Project Management", "project-management"),

            "PROJECT PLAN" =>
                ("Project Management", "project-management"),

            "STATUS REPORT" =>
                ("Project Management", "project-management"),

            "MEETING MINUTES" =>
                ("Project Management", "project-management"),

            // SALES AND MARKETING
            "BUSINESS PROPOSAL" =>
                ("Sales & Marketing", "sales-marketing"),

            "MARKETING PLAN" =>
                ("Sales & Marketing", "sales-marketing"),

            "BROCHURE" =>
                ("Sales & Marketing", "sales-marketing"),

            "CUSTOMER AGREEMENT" =>
                ("Sales & Marketing", "sales-marketing"),

            // ADMINISTRATION
            "MEMO" =>
                ("Administration", "administration"),

            "BUSINESS LETTER" =>
                ("Administration", "administration"),

            "POLICY DOCUMENT" =>
                ("Administration", "administration"),

            "ANNUAL REPORT" =>
                ("Administration", "administration"),

            // COMPLIANCE
            "AUDIT REPORT" =>
                ("Compliance", "compliance"),

            "COMPLIANCE CERTIFICATE" =>
                ("Compliance", "compliance"),

            "REGULATORY FILING" =>
                ("Compliance", "compliance"),

            // PROCUREMENT
            "VENDOR AGREEMENT" =>
                ("Procurement", "procurement"),

            "SUPPLIER CONTRACT" =>
                ("Procurement", "procurement"),

            "REQUEST FOR PROPOSAL (RFP)" or "RFP" =>
                ("Procurement", "procurement"),

            // CUSTOMER SERVICE
            "CUSTOMER COMPLAINT FORM" =>
                ("Customer Service", "customer-service"),

            "SERVICE REQUEST FORM" =>
                ("Customer Service", "customer-service"),

            "CUSTOMER FEEDBACK REPORT" =>
                ("Customer Service", "customer-service"),

            _ => ("Uncategorised", "uncategorised")
        };
    }

    // ======================================================
    // FIND DOCUWARE DOCUMENT COLLECTION
    // ======================================================

    private static JsonElement? FindDocumentArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string[] collectionNames =
        {
            "Items",
            "Documents",
            "Results",
            "Data",
            "DocumentList"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (collectionNames.Any(name =>
                    string.Equals(
                        property.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value;
            }
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var result = FindDocumentArray(property.Value);

                if (result.HasValue)
                {
                    return result;
                }
            }
        }

        return null;
    }

    // ======================================================
    // FIND DOCUWARE INDEX FIELD VALUE
    // ======================================================

    private static string? FindIndexFieldValue(
        JsonElement element,
        string fieldName)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var result = FindIndexFieldValue(
                    item,
                    fieldName);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var actualFieldName = GetJsonValue(
            element,
            "FieldName",
            "DBFieldName");

        if (string.Equals(
            actualFieldName,
            fieldName,
            StringComparison.OrdinalIgnoreCase))
        {
            return GetJsonValue(
                element,
                "Item",
                "Value");
        }

        var directValue = GetJsonValue(
            element,
            fieldName);

        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is
                JsonValueKind.Object or JsonValueKind.Array)
            {
                var result = FindIndexFieldValue(
                    property.Value,
                    fieldName);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }
        }

        return null;
    }

    // ======================================================
    // READ JSON VALUES
    // ======================================================

    private static string? GetJsonValue(
        JsonElement element,
        params string[] fieldNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!fieldNames.Any(name =>
                    string.Equals(
                        property.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return ReadScalarValue(property.Value);
        }

        return null;
    }

    private static string? ReadScalarValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),

            JsonValueKind.Number => value.ToString(),

            JsonValueKind.True => "true",

            JsonValueKind.False => "false",

            JsonValueKind.Array => string.Join(
                ", ",
                value.EnumerateArray()
                    .Select(ReadScalarValue)
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))),

            _ => null
        };
    }

    // ======================================================
    // PARSE DOCUWARE DATES
    // ======================================================

    private static DateTime? ParseDate(string? value)
    {
        if (DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out var result))
        {
            return result;
        }

        return null;
    }
}
