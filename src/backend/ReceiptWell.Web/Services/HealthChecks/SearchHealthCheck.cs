using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReceiptWell.Services.HealthChecks;

internal sealed class SearchHealthCheck(SearchIndexClient client, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            var indexName = configuration["AzureSearch:IndexName"] ?? string.Empty;
            await client.GetIndexAsync(indexName, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
