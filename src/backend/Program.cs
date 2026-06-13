using System.Security.Claims;
using System.Text.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ReceiptWell.Services;
using ReceiptWell.Services.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var dataProtection = builder.Services.AddDataProtection();
if (!builder.Environment.IsDevelopment())
{
    var storageAccountName = builder.Configuration["AzureStorage:AccountName"];
    var keyRingContainer = builder.Configuration["AzureStorage:KeyRingContainerName"];
    dataProtection.PersistKeysToAzureBlobStorage(
        new Uri($"https://{storageAccountName}.blob.core.windows.net/{keyRingContainer}/keys.xml"),
        new Azure.Identity.DefaultAzureCredential());
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Authority = builder.Configuration["AzureExternalId:Authority"];
        options.Audience = builder.Configuration["AzureExternalId:ClientId"];
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
var connectionString = builder.Configuration["AzureStorage:ConnectionString"];
builder.Services.AddSingleton(_ => string.IsNullOrEmpty(connectionString)
    ? new BlobServiceClient(
        new Uri(builder.Configuration["AzureStorage:BlobServiceUri"]!),
        new Azure.Identity.DefaultAzureCredential())
    : new BlobServiceClient(connectionString!));

var searchUri = new Uri(builder.Configuration["AzureSearch:ServiceUri"]!);
var searchCredential = new Azure.AzureKeyCredential(builder.Configuration["AzureSearch:ApiKey"]!);
builder.Services.AddSingleton(_ => new SearchIndexClient(searchUri, searchCredential));
builder.Services.AddSingleton(_ => new SearchClient(
    searchUri,
    builder.Configuration["AzureSearch:IndexName"]!,
    searchCredential,
    new SearchClientOptions { Retry = { MaxRetries = 3 } }));
builder.Services.AddHostedService<SearchIndexInitializer>();
builder.Services.AddScoped<ReceiptBlobService>();
builder.Services.AddScoped<ReceiptConfirmService>();

builder.Services.AddHealthChecks()
    .AddCheck<BlobStorageHealthCheck>("blob-storage")
    .AddCheck<SearchHealthCheck>("azure-search");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        });
        await context.Response.WriteAsync(result);
    }
}).AllowAnonymous();

app.MapPost("/receipts/staging-slot", async (
    HttpContext httpContext,
    ReceiptBlobService blobService,
    ILoggerFactory loggerFactory) =>
{
    var endpointLogger = loggerFactory.CreateLogger("receipts-staging-slot");
    var userId = httpContext.User.FindFirstValue("oid")
        ?? throw new InvalidOperationException("oid claim missing");
    try
    {
        var (sasUri, stagingBlobName) = await blobService.CreateStagingSlotAsync(userId);
        return Results.Ok(new { stagingUri = sasUri.ToString(), stagingBlobName });
    }
    catch (Exception ex)
    {
        endpointLogger.LogError(ex, "Failed to create staging slot for user {UserId}", userId);
        return Results.Problem(statusCode: 500);
    }
});

app.MapPost("/receipts/confirm", async (
    HttpContext httpContext,
    ReceiptConfirmService confirmService,
    ILoggerFactory loggerFactory,
    ConfirmRequest request) =>
{
    var confirmLogger = loggerFactory.CreateLogger("receipts-confirm");
    var userId = httpContext.User.FindFirstValue("oid")
        ?? throw new InvalidOperationException("oid claim missing");
    try
    {
        var result = await confirmService.ConfirmUploadAsync(
            request.StagingBlobName, userId, request.OriginalFileName);

        return result switch
        {
            ReceiptConfirmResult.Forbidden { } => Results.Forbid(),
            ReceiptConfirmResult.InvalidBlob invalid =>
                Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = [invalid.Message]
                }),
            ReceiptConfirmResult.Success success =>
                Results.Ok(new
                {
                    receiptId = success.ReceiptId,
                    fileName = success.FileName,
                    fileSize = success.FileSize
                }),
            _ => throw new InvalidOperationException("Unexpected result type")
        };
    }
    catch (Exception ex)
    {
        confirmLogger.LogError(ex, "Failed to confirm receipt upload for user {UserId}", userId);
        return Results.Problem(statusCode: 500);
    }
});

app.Run();

record ConfirmRequest(string StagingBlobName, string OriginalFileName);
