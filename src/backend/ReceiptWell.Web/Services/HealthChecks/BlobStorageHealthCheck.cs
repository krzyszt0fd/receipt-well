using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReceiptWell.Services.HealthChecks;

internal sealed class BlobStorageHealthCheck(BlobServiceClient client, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            var containerName = config["AzureStorage:StagingContainerName"]!;
            await client.GetBlobContainerClient(containerName).GetPropertiesAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
