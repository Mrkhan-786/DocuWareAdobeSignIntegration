using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration;

public class LoggingHandler : DelegatingHandler
{
    private readonly ILogger<LoggingHandler> _logger;

    public LoggingHandler(
        HttpMessageHandler innerHandler,
        ILogger<LoggingHandler> logger) : base(innerHandler)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "OIDC REQUEST: {Method} {Uri}",
            request.Method,
            request.RequestUri);

        if (request.Headers.Authorization != null)
        {
            _logger.LogWarning(
                "OIDC AUTHENTICATION SCHEME: {Scheme}",
                request.Headers.Authorization.Scheme);
        }

        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);

            // Never log credentials or authorization codes.
            body = Regex.Replace(
                body,
                @"(?i)(client_secret|code_verifier|refresh_token|access_token|id_token|code)=([^&]*)",
                "$1=***");

            _logger.LogWarning("OIDC REQUEST BODY: {Body}", body);
        }

        var response = await base.SendAsync(request, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // Never log tokens returned by the identity provider.
        responseBody = Regex.Replace(
            responseBody,
            @"(?i)(""(?:access_token|refresh_token|id_token)""\s*:\s*"")[^""]*("")",
            "$1***$2");

        _logger.LogWarning(
            "OIDC RESPONSE: HTTP {StatusCode} - {Body}",
            (int)response.StatusCode,
            responseBody);

        return response;
    }
}