using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace ReceiptWell.Services;

public partial class ReceiptBlobService(
    BlobServiceClient blobServiceClient,
    IConfiguration configuration,
    ILogger<ReceiptBlobService> logger)
{
    private readonly string _stagingContainerName =
        configuration["AzureStorage:StagingContainerName"]!;

    public async Task<(Uri SasUri, string StagingBlobName)> CreateStagingSlotAsync(string userId)
    {
        var stagingBlobName = $"{userId}/{Guid.NewGuid()}";
        var containerClient = blobServiceClient.GetBlobContainerClient(_stagingContainerName);
        var blobClient = containerClient.GetBlobClient(stagingBlobName);

        await blobClient.UploadAsync(new BinaryData(Array.Empty<byte>()), overwrite: false);

        var sasExpiry = DateTimeOffset.UtcNow.AddMinutes(10);
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _stagingContainerName,
            BlobName = stagingBlobName,
            Resource = "b",
            ExpiresOn = sasExpiry
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Write);

        Uri sasUri;
        if (blobClient.CanGenerateSasUri)
        {
            sasUri = blobClient.GenerateSasUri(sasBuilder);
        }
        else
        {
            var key = await blobServiceClient.GetUserDelegationKeyAsync(
                new BlobGetUserDelegationKeyOptions(sasExpiry)
                {
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5)
                });
            var queryParams = sasBuilder.ToSasQueryParameters(key, blobServiceClient.AccountName);
            sasUri = new BlobUriBuilder(blobClient.Uri) { Sas = queryParams }.ToUri();
        }

        LogStagingSlotCreated(logger, userId, stagingBlobName);
        return (sasUri, stagingBlobName);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Staging slot created: user {UserId} blob {StagingBlobName}")]
    private static partial void LogStagingSlotCreated(ILogger logger, string userId, string stagingBlobName);
}
