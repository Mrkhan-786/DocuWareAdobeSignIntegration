
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;

var builder = WebApplication.CreateBuilder(args);

// ======================================================
// Controllers and Razor Pages
// ======================================================

builder.Services.AddControllers();

// Enable Razor Pages frontend
builder.Services.AddRazorPages();

// ======================================================
// Swagger / OpenAPI
// ======================================================

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ======================================================
// HttpClient Factory
// ======================================================

builder.Services.AddHttpClient();

// ======================================================
// Configuration Binding
// ======================================================

builder.Services.Configure<AdobeSignSettings>(
    builder.Configuration.GetSection("AdobeSign"));

// ======================================================
// Dependency Injection
// ======================================================

// Adobe Services
builder.Services.AddScoped<AdobeAuthService>();
builder.Services.AddScoped<AdobeAgreementService>();

// DocuWare Services
builder.Services.AddScoped<DocuWareService>();
builder.Services.AddScoped<DocuWareAuthService>();
builder.Services.AddScoped<DocuWareDocumentService>();

// ======================================================
// Build Application
// ======================================================

var app = builder.Build();

// ======================================================
// Static Files
// ======================================================

app.UseStaticFiles();

// ======================================================
// Swagger / OpenAPI Middleware
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
// HTTPS Redirection
// ======================================================

app.UseHttpsRedirection();

// ======================================================
// Authorization
// ======================================================

app.UseAuthorization();

// ======================================================
// Map Razor Pages
// ======================================================

app.MapRazorPages();

// ======================================================
// Controller Mapping
// ======================================================

app.MapControllers();

// ======================================================
// Run Application
// ======================================================

app.Run();
