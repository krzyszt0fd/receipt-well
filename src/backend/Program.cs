using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using ReceiptWell.Services;

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

builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
