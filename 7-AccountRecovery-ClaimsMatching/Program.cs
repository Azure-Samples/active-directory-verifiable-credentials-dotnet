using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using account_recovery_claim_matching;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Register Entra ID Bearer token validation (OAuth 2.0 client credentials flow).
// Configure EntraId:TenantId and EntraId:ClientId to enable.
// When not configured, token validation is skipped (function key auth only).
builder.Services.AddSingleton<TokenValidationService>();

// Register the claims validator based on configuration.
// Set "ClaimsValidator:Provider" to "hrapi" for production HR API integration,
// or "excel" (default) for HTTP-hosted Excel file validation.
var provider = builder.Configuration["ClaimsValidator:Provider"] ?? "excel";

if (string.Equals(provider, "excel", StringComparison.OrdinalIgnoreCase))
{
    // Excel validator — downloads .xlsx from any HTTP(S) URL (OneDrive, Azure Blob, custom host, etc.)
    builder.Services.AddHttpClient<HttpExcelClaimsValidator>();
    builder.Services.AddSingleton<IClaimsValidator, HttpExcelClaimsValidator>();
}
else
{
    // HR API validator — production default
    builder.Services.AddHttpClient<HrApiClaimsValidator>();
    builder.Services.AddSingleton<IClaimsValidator, HrApiClaimsValidator>();
}

builder.Build().Run();
