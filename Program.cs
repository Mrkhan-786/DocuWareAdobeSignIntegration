
using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using DocuWareAdobeSignIntegration;
using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ======================================================
// APPLICATION CONFIGURATION
// ======================================================

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
var environment = builder.Environment;

// ======================================================
// CONFIGURATION PATH HELPER
// ======================================================

string ResolveApplicationPath(string path)
{
    return Path.GetFullPath(
        Path.IsPathRooted(path)
            ? path
            : Path.Combine(environment.ContentRootPath, path));
}

// ======================================================
// DOCUWARE OAUTH CONFIGURATION
// ======================================================

var docuWareAuthority =
    configuration["DocuWare:Authority"]
    ?? throw new InvalidOperationException(
        "Missing configuration: DocuWare:Authority");

var docuWareClientId =
    configuration["DocuWare:ClientId"]
    ?? throw new InvalidOperationException(
        "Missing configuration: DocuWare:ClientId");

var docuWareClientSecret =
    configuration["DocuWare:ClientSecret"]
    ?? throw new InvalidOperationException(
        "Missing configuration: DocuWare:ClientSecret");

var docuWareOrganization =
    configuration["DocuWare:Organization"]
    ?? throw new InvalidOperationException(
        "Missing configuration: DocuWare:Organization");

var docuWareScope =
    configuration["DocuWare:Scope"]
    ?? "docuware.platform openid dwprofile offline_access";

// Environment-specific DocuWare callback URLs.

var docuWareRedirectUri = environment.IsDevelopment()
    ? configuration["DocuWare:LocalRedirectUri"]
        ?? "https://localhost:7075/api/docuware/callback"
    : configuration["DocuWare:RedirectUri"]
        ?? throw new InvalidOperationException(
            "Missing configuration: DocuWare:RedirectUri");

// ======================================================
// CONTROLLERS AND RAZOR PAGES
// ======================================================

builder.Services.AddControllers();

builder.Services.AddRazorPages(options =>
{
    // Dashboard requires authentication.
    options.Conventions.AuthorizePage("/Index");

    // FileCabinet pages require authentication.
    options.Conventions.AuthorizeFolder("/FileCabinet");

    // Login and registration remain public.
    options.Conventions.AllowAnonymousToPage("/Account/Login");

    options.Conventions.AllowAnonymousToPage("/Account/Register");
});

builder.Services.AddAuthorization();

// ======================================================
// SWAGGER / OPENAPI
// ======================================================

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

// ======================================================
// DATA PROTECTION - PERSISTENT ENCRYPTION KEYS
// ======================================================

var configuredDataProtectionPath =
    configuration["DataProtection:KeysPath"];

var legacyDataProtectionPath =
    Path.Combine(
        environment.ContentRootPath,
        "App_Data",
        "DataProtection-Keys");

string dataProtectionPath;

if (!string.IsNullOrWhiteSpace(configuredDataProtectionPath))
{
    dataProtectionPath =
        ResolveApplicationPath(configuredDataProtectionPath);
}
else if (
    Directory.Exists(legacyDataProtectionPath) &&
    Directory.GetFiles(
        legacyDataProtectionPath,
        "*.xml").Length > 0)
{
    dataProtectionPath =
        Path.GetFullPath(legacyDataProtectionPath);
}
else if (environment.IsDevelopment())
{
    dataProtectionPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "DocuWareAdobeSignIntegration",
        "DataProtection-Keys");

    dataProtectionPath =
        Path.GetFullPath(dataProtectionPath);
}
else
{
    dataProtectionPath =
        Path.GetFullPath(legacyDataProtectionPath);
}

Directory.CreateDirectory(dataProtectionPath);

builder.Services
    .AddDataProtection()
    .SetApplicationName("DocuWareAdobeSignIntegration")
    .PersistKeysToFileSystem(
        new DirectoryInfo(dataProtectionPath));

var startupLogger =
    LoggerFactory.Create(logging =>
    {
        logging.AddConsole();
        logging.AddDebug();
    }).CreateLogger("ApplicationStartup");

startupLogger.LogInformation(
    "Data Protection keys are stored at {KeyPath}.",
    dataProtectionPath);

if (!environment.IsDevelopment() &&
    string.IsNullOrWhiteSpace(configuredDataProtectionPath))
{
    startupLogger.LogWarning(
        "DataProtection:KeysPath is not configured. " +
        "Configure persistent production storage.");
}

// ======================================================
// DATABASE CONFIGURATION
// ======================================================

var configuredDatabasePath =
    configuration["Database:Path"];

var databasePath =
    string.IsNullOrWhiteSpace(configuredDatabasePath)
        ? Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "docuware-adobe-sign.db")
        : ResolveApplicationPath(configuredDatabasePath);

databasePath = Path.GetFullPath(databasePath);

var databaseFolder =
    Path.GetDirectoryName(databasePath);

if (!string.IsNullOrWhiteSpace(databaseFolder))
{
    Directory.CreateDirectory(databaseFolder);
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseSqlite(
        $"Data Source={databasePath}");
});

startupLogger.LogInformation(
    "Application database configured at {DatabasePath}.",
    databasePath);

// ======================================================
// HTTP CLIENT FACTORY
// ======================================================

// Required by services that inject IHttpClientFactory.

builder.Services.AddHttpClient();

// ======================================================
// ADOBE SIGN CONFIGURATION
// ======================================================

builder.Services.Configure<AdobeSignSettings>(
    configuration.GetSection("AdobeSign"));

// ======================================================
// SIGNED DOCUMENT STORAGE CONFIGURATION
// ======================================================

var configuredFileCabinetPath =
    configuration["FileCabinet:StoragePath"];

var fileCabinetStoragePath =
    string.IsNullOrWhiteSpace(configuredFileCabinetPath)
        ? Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "SignedDocuments")
        : ResolveApplicationPath(configuredFileCabinetPath);

Directory.CreateDirectory(fileCabinetStoragePath);

startupLogger.LogInformation(
    "Signed document storage configured at {StoragePath}.",
    fileCabinetStoragePath);

if (!environment.IsDevelopment() &&
    string.IsNullOrWhiteSpace(configuredFileCabinetPath))
{
    startupLogger.LogWarning(
        "FileCabinet:StoragePath is not configured. " +
        "Local signed-document storage may be ephemeral.");
}

// ======================================================
// DOCUWARE OIDC HTTP LOGGING
// ======================================================

var oidcLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});

// ======================================================
// AUTHENTICATION
// ======================================================

builder.Services
    .AddAuthentication(
        CookieAuthenticationDefaults.AuthenticationScheme)

    // ==================================================
    // APPLICATION AUTHENTICATION COOKIE
    // ==================================================

    .AddCookie(options =>
    {
        options.Cookie.Name =
            "DocuWareAdobeSign.Auth";

        options.Cookie.HttpOnly = true;

        options.Cookie.SecurePolicy =
            CookieSecurePolicy.Always;

        options.Cookie.SameSite =
            SameSiteMode.Lax;

        options.LoginPath =
            "/Account/Login";

        options.ReturnUrlParameter =
            "returnUrl";

        options.AccessDeniedPath =
            "/Account/AccessDenied";

        options.SlidingExpiration = true;

        options.ExpireTimeSpan =
            TimeSpan.FromHours(8);
    })

    // ==================================================
    // DOCUWARE OPENID CONNECT
    // ==================================================

    .AddOpenIdConnect("DocuWare", options =>
    {
        // ----------------------------------------------
        // Backchannel HTTP logging
        // ----------------------------------------------

        options.BackchannelHttpHandler =
            new LoggingHandler(
                new HttpClientHandler(),
                oidcLoggerFactory.CreateLogger<LoggingHandler>());

        // ----------------------------------------------
        // Authentication cookie
        // ----------------------------------------------

        options.SignInScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;

        // ----------------------------------------------
        // DocuWare identity service
        // ----------------------------------------------

        options.Authority =
            docuWareAuthority.TrimEnd('/');

        // ----------------------------------------------
        // App registration credentials
        // ----------------------------------------------

        options.ClientId =
            docuWareClientId;

        options.ClientSecret =
            docuWareClientSecret;

        // ----------------------------------------------
        // Authorization code flow
        // ----------------------------------------------

        options.ResponseType = "code";

        options.UsePkce = true;

        // ----------------------------------------------
        // Callback URL
        // ----------------------------------------------

        options.CallbackPath =
            "/api/docuware/callback";

        // ----------------------------------------------
        // Token handling
        // ----------------------------------------------

        options.SaveTokens = true;

        options.RequireHttpsMetadata = true;

        // ----------------------------------------------
        // OAuth scopes
        // ----------------------------------------------

        options.Scope.Clear();

        foreach (var scope in docuWareScope.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries))
        {
            options.Scope.Add(scope);
        }

        // ----------------------------------------------
        // User claims
        // ----------------------------------------------

        options.GetClaimsFromUserInfoEndpoint = true;

        options.ClaimActions.MapUniqueJsonKey(
            "username",
            "username");

        options.ClaimActions.MapUniqueJsonKey(
            "preferred_username",
            "preferred_username");

        // ----------------------------------------------
        // Authorization request
        // ----------------------------------------------

        options.Events.OnRedirectToIdentityProvider =
            context =>
            {
                context.ProtocolMessage.AcrValues =
                    $"tenant:{docuWareOrganization}";

                context.ProtocolMessage.RedirectUri =
                    docuWareRedirectUri;

                return Task.CompletedTask;
            };

        // ----------------------------------------------
        // Save employee DocuWare OAuth tokens
        // ----------------------------------------------

        options.Events.OnTicketReceived =
            async context =>
            {
                var logger =
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILogger<Program>>();

                var userId =
                    context.Principal?
                        .FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.Principal?
                        .FindFirst("sub")?.Value;

                if (string.IsNullOrWhiteSpace(userId))
                {
                    throw new InvalidOperationException(
                        "DocuWare login succeeded, but the employee user ID could not be identified.");
                }

                var properties = context.Properties;

                if (properties == null)
                {
                    throw new InvalidOperationException(
                        "DocuWare authentication properties were not returned.");
                }

                var accessToken =
                    properties.GetTokenValue("access_token");

                var refreshToken =
                    properties.GetTokenValue("refresh_token");

                var expiresAt =
                    properties.GetTokenValue("expires_at");

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new InvalidOperationException(
                        "DocuWare did not return an access token.");
                }

                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    throw new InvalidOperationException(
                        "DocuWare did not return a refresh token. " +
                        "Check offline_access and the DocuWare App Registration.");
                }

                DateTime expiresUtc;

                if (DateTimeOffset.TryParse(
                    expiresAt,
                    out var parsedExpiry))
                {
                    expiresUtc =
                        parsedExpiry.UtcDateTime;
                }
                else
                {
                    expiresUtc =
                        DateTime.UtcNow.AddMinutes(55);

                    logger.LogWarning(
                        "DocuWare token expiry was unavailable. " +
                        "Using a 55-minute fallback.");
                }

                var tokenStore =
                    context.HttpContext.RequestServices
                        .GetRequiredService<DocuWareTokenStore>();

                await tokenStore.SaveAsync(
                    userId,
                    accessToken,
                    refreshToken,
                    expiresUtc,
                    context.HttpContext.RequestAborted);

                logger.LogInformation(
                    "DocuWare OAuth tokens saved for employee {UserId}.",
                    userId);

                // Return to the dashboard after login.

                if (string.IsNullOrWhiteSpace(context.ReturnUri) ||
                    context.ReturnUri.Contains(
                        "/Account/Login",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.ReturnUri = "/";
                }
            };

        // ----------------------------------------------
        // Login failure handling
        // ----------------------------------------------

        options.Events.OnRemoteFailure =
            context =>
            {
                context.HandleResponse();

                var logger =
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILogger<Program>>();

                logger.LogError(
                    context.Failure,
                    "DocuWare authentication failed.");

                context.Response.Redirect(
                    "/Account/Login?error=login_failed");

                return Task.CompletedTask;
            };
    });

// ======================================================
// ADOBE SIGN SERVICES
// ======================================================

// Typed HTTP clients.

builder.Services.AddHttpClient<AdobeAuthService>();

builder.Services.AddHttpClient<AdobeAgreementService>();

// AdobeSignService injects IHttpClientFactory,
// so it must be registered as a scoped service.

builder.Services.AddScoped<AdobeSignService>();

builder.Services.AddScoped<AdobeUserTokenService>();

builder.Services.AddScoped<AdobeTokenStore>();

// ======================================================
// ADOBE SIGN - SIGNED PDF STORAGE
// ======================================================

builder.Services.AddSingleton<
    IAdobeSignedPdfStorage,
    AdobeSignedPdfStorage>();

// ======================================================
// ADOBE SIGN - AGREEMENT STATUS AND ARCHIVING
// ======================================================

builder.Services.AddScoped<
    IAdobeAgreementArchiveService,
    AdobeAgreementArchiveService>();

// ======================================================
// DOCUWARE SERVICES
// ======================================================

builder.Services.AddHttpClient<DocuWareService>();

builder.Services.AddHttpClient<DocuWareAuthService>();

builder.Services.AddHttpClient<DocuWareDocumentService>();

// ======================================================
// DOCUWARE PER-EMPLOYEE OAUTH TOKEN SERVICES
// ======================================================

builder.Services.AddScoped<DocuWareTokenStore>();

builder.Services.AddHttpClient<DocuWareOAuthTokenService>();

// ======================================================
// BUILD APPLICATION
// ======================================================

var app = builder.Build();

// ======================================================
// STATIC FILES
// ======================================================

app.UseStaticFiles();

// ======================================================
// SWAGGER / OPENAPI
// ======================================================

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "DocuWare Adobe Sign Integration API V1");

        options.RoutePrefix = "swagger";

        options.DocumentTitle =
            "DocuWare Adobe Sign Integration";

        options.InjectStylesheet(
            "/swagger/custom.css");

        options.InjectJavascript(
            "/swagger/custom.js");
    });
}

// ======================================================
// HTTPS REDIRECTION
// ======================================================

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// ======================================================
// ROUTING AND AUTHENTICATION
// ======================================================

app.UseRouting();

app.UseAuthentication();

// ======================================================
// ROOT URL REDIRECTION
// ======================================================

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" &&
        (context.User.Identity == null ||
         !context.User.Identity.IsAuthenticated))
    {
        context.Response.Redirect("/Account/Login");
        return;
    }

    await next();
});

app.UseAuthorization();

// ======================================================
// ENDPOINT MAPPING
// ======================================================

app.MapRazorPages();

app.MapControllers();

// ======================================================
// RUN APPLICATION
// ======================================================

app.Run();
