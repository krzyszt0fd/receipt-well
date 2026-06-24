using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReceiptWell.Services;

namespace ReceiptWell.Tests.Infrastructure;

/// <summary>
/// Boots the real ReceiptWell.Web app offline: the Azure clients are NSubstitute
/// substitutes, the startup index-initializer hosted service is removed, the eager
/// startup config reads get dummy values, and the "Test" auth scheme is the default
/// so identity comes from request headers (see <see cref="TestAuthHandler"/>).
/// The substituted clients are exposed so tests can assert interactions.
/// </summary>
public class ReceiptWellWebFactory : WebApplicationFactory<Program>
{
    static ReceiptWellWebFactory()
    {
        // Program.cs reads these three keys eagerly from builder.Configuration (lines 89-90, 65)
        // before WebApplicationFactory.ConfigureAppConfiguration fires. Setting them as env vars
        // makes them available from process start regardless of user secrets or CI environment.
        Environment.SetEnvironmentVariable("AzureSearch__ServiceUri", "https://search.localhost.test/");
        Environment.SetEnvironmentVariable("AzureSearch__ApiKey", "test-api-key");
        Environment.SetEnvironmentVariable("AzureStorage__ExtractionQueueName", "test-extraction-queue");
    }

    public BlobServiceClient BlobServiceClient { get; } = Substitute.For<BlobServiceClient>();
    public SearchClient SearchClient { get; } = Substitute.For<SearchClient>();
    public SearchIndexClient SearchIndexClient { get; } = Substitute.For<SearchIndexClient>();
    public QueueClient QueueClient { get; } = Substitute.For<QueueClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development skips the DataProtection-to-Azure-Blob wiring (Program.cs) so no
        // storage account is contacted at startup.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // These three keys are read eagerly at startup, OUTSIDE any DI lambda
            // (Program.cs) — without dummy values the host throws before any test runs.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSearch:ServiceUri"] = "https://search.localhost.test/",
                ["AzureSearch:ApiKey"] = "test-api-key",
                ["AzureStorage:ExtractionQueueName"] = "test-extraction-queue",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Boot offline: drop the index initializer that would call Azure on start.
            var hostedService = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(SearchIndexInitializer));
            if (hostedService is not null)
            {
                services.Remove(hostedService);
            }

            // Swap every Azure client for a substitute so no network call is possible
            // and tests can assert (e.g. DidNotReceive) on them.
            Replace(services, BlobServiceClient);
            Replace(services, SearchClient);
            Replace(services, SearchIndexClient);
            Replace(services, QueueClient);

            // Inject identity from request headers; the REAL authorization policy still
            // runs, so 401 (no token) vs 403 (no oid) come from production code.
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(instance);
    }
}
