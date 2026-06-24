using System.Text.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ReceiptWell.Extensions;
using ReceiptWell.Services;
using ReceiptWell.Services.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Windows App Service's "Application Logging (Filesystem)" feature only captures
// output from this provider (ETW-based) — the default Console logger is invisible
// to it. No-op outside Azure App Service (checks for the App Service environment).
builder.Logging.AddAzureWebAppDiagnostics();

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

var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
var extractionQueueName = builder.Configuration["AzureStorage:ExtractionQueueName"]!;
builder.Services.AddSingleton(_ =>
{
    // Azure: the queue is provisioned by Terraform (infra/storage.tf) and the API's
    // MI deliberately holds only "Storage Queue Data Message Sender" (send-only,
    // see role_assignments.tf) — it lacks the permission to create a queue, so
    // CreateIfNotExists must not run against Azure. Azurite has no such
    // provisioning step, so local dev still creates the queue on first use.
    if (string.IsNullOrEmpty(connectionString))
    {
        var queueServiceClient = new QueueServiceClient(
            new Uri(builder.Configuration["AzureStorage:QueueServiceUri"]!),
            new Azure.Identity.DefaultAzureCredential(),
            queueOptions);
        return queueServiceClient.GetQueueClient(extractionQueueName);
    }

    var localQueueServiceClient = new QueueServiceClient(connectionString, queueOptions);
    var localQueueClient = localQueueServiceClient.GetQueueClient(extractionQueueName);
    localQueueClient.CreateIfNotExists();
    return localQueueClient;
});

var searchUri = new Uri(builder.Configuration["AzureSearch:ServiceUri"]!);
var searchCredential = new Azure.AzureKeyCredential(builder.Configuration["AzureSearch:ApiKey"]!);
builder.Services.AddSingleton(_ => new SearchIndexClient(searchUri, searchCredential));
builder.Services.AddSingleton(_ => new SearchClient(
    searchUri,
    builder.Configuration["AzureSearch:IndexName"]!,
    searchCredential,
    new SearchClientOptions { Retry = { MaxRetries = 3 } }));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<DelegationTokenProvider>();
builder.Services.AddHostedService<SearchIndexInitializer>();
builder.Services.AddScoped<ReceiptBlobService>();
builder.Services.AddScoped<ReceiptConfirmService>();
builder.Services.AddScoped<ReceiptQueryService>();

builder.Services.AddHealthChecks()
    .AddCheck<BlobStorageHealthCheck>("blob-storage")
    .AddCheck<SearchHealthCheck>("azure-search")
    .AddCheck<QueueHealthCheck>("storage-queue");

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
    var userId = httpContext.User.GetUserId();
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
    var userId = httpContext.User.GetUserId();
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

app.MapGet("/receipts", async (
    HttpContext httpContext,
    ReceiptQueryService queryService,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var endpointLogger = loggerFactory.CreateLogger("receipts-list");
    var userId = httpContext.User.GetUserId();
    try
    {
        var summaries = await queryService.GetReceiptsAsync(userId, cancellationToken);
        return Results.Ok(summaries);
    }
    catch (Exception ex)
    {
        endpointLogger.LogError(ex, "Failed to list receipts for user {UserId}", userId);
        return Results.Problem(statusCode: 500);
    }
});

app.Run();

record ConfirmRequest(string StagingBlobName, string OriginalFileName);

// Exposes the implicitly-internal top-level Program type to the test project so
// WebApplicationFactory<Program> can boot the real app for integration tests.
public partial class Program { }
