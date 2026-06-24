using System.Net;

namespace ReceiptWell.Tests;

/// <summary>
/// Proves the test factory boots the app offline (no Azure contact at startup)
/// before any behavioral test depends on it. Hitting an unmapped route exercises
/// the full pipeline and returns a normal HTTP response rather than a boot exception.
/// </summary>
public class HarnessSmokeTests(ReceiptWellWebFactory factory) : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task Factory_boots_and_serves_requests_offline()
    {
        var client = factory.CreateClient();
        // Authenticate so the global fallback policy lets the request through to routing;
        // an unmapped route then answers 404 — proving the app booted without touching Azure.
        client.DefaultRequestHeaders.Add(TestAuthHandler.OidHeader, "smoke-user");

        var response = await client.GetAsync("/__nonexistent-route__");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
