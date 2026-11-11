using System.Text.Json;

namespace DocuWareAdobeSignIntegration.Services;

public class DocuWareAuthService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocuWareAuthService> _logger;

    public DocuWareAuthService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DocuWareAuthService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> LoginAsync()
    {
        try
        {
            var organization =
                _configuration["DocuWare:Organization"];

            var username =
                _configuration["DocuWare:Username"];

            var password =
                _configuration["DocuWare:Password"];

            var baseUrl =
                _configuration["DocuWare:BaseUrl"];

            var requestBody =
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>(
                        "UserName",
                        username!),

                    new KeyValuePair<string, string>(
                        "Password",
                        password!)
                ]);

            var response =
                await _httpClient.PostAsync(
                    $"{baseUrl}/DocuWare/Platform/Account/Logon",
                    requestBody);

            var content =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"DocuWare authentication failed: {content}");
            }

            _logger.LogInformation(
                "Successfully authenticated with DocuWare.");

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "DocuWare authentication failed.");

            throw;
        }
    }
}