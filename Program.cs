using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;

var builder = WebApplication.CreateBuilder(args);

// ======================================================
// Controllers
// ======================================================

builder.Services.AddControllers();

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
// Middleware Pipeline
// ======================================================

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "DocuWare Adobe Sign Integration API V1");

        c.RoutePrefix = "swagger";

        c.DocumentTitle =
            "DocuWare Adobe Sign Integration";
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();