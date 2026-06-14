using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;

namespace ReceiptWell.Services;

public class DelegationTokenProvider(BlobServiceClient blobServiceClient, IMemoryCache memoryCache)
{
    public async Task<UserDelegationKey> GetOrFetchAsync()
    {
        return (await memoryCache.GetOrCreateAsync("blob-user-delegation-key", async entry =>
        {
            var keyExpiry = DateTimeOffset.UtcNow.AddHours(1);
            var response = await blobServiceClient.GetUserDelegationKeyAsync(
                new BlobGetUserDelegationKeyOptions(keyExpiry)
                {
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5)
                });
            UserDelegationKey delegationKey = response.Value;
            entry.AbsoluteExpiration = delegationKey.SignedExpiresOn.AddMinutes(-5);
            return delegationKey;
        }))!;
    }
}
