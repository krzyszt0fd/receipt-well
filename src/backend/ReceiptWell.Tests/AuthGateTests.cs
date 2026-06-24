using System.Net;
using System.Net.Http.Json;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #2 (auth gate): every protected receipt route must reject a missing token
/// with <b>401</b> and an authenticated-but-<c>oid</c>-less principal with <b>403</b>
/// — never 200, never 500. Both outcomes come from the REAL authorization policy
/// (<c>RequireAuthenticatedUser</c> + <c>RequireClaim("oid")</c>); only the
/// authentication step is simulated by <see cref="TestAuthHandler"/>. The positive
/// control proves a valid <c>oid</c> is let through to the handler, so the 401/403
/// assertions are not vacuously true.
/// </summary>
public class AuthGateTests(ReceiptWellWebFactory factory) : IClassFixture<ReceiptWellWebFactory>
{
    /// <summary>The three protected receipt routes (method, path).</summary>
    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { HttpMethod.Post.Method, "/receipts/staging-slot" },
        { HttpMethod.Post.Method, "/receipts/confirm" },
        { HttpMethod.Get.Method, "/receipts" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task No_token_is_rejected_with_401(string method, string path)
    {
        var client = factory.CreateClient();

        var response = await client.SendAsync(BuildRequest(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Authenticated_without_oid_is_rejected_with_403(string method, string path)
    {
        var client = factory.CreateClient();
        var request = BuildRequest(method, path);
        request.Headers.Add(TestAuthHandler.NoOidHeader, "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_with_oid_is_not_gate_rejected()
    {
        var client = factory.CreateClient();
        var request = BuildRequest(HttpMethod.Get.Method, "/receipts");
        request.Headers.Add(TestAuthHandler.OidHeader, "gate-positive-control");

        var response = await client.SendAsync(request);

        // The gate let the request reach the handler; whatever the handler does with
        // the substituted Search client, the response must NOT be an authn/authz reject.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static HttpRequestMessage BuildRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == HttpMethod.Post.Method)
        {
            // Authorization runs ahead of model binding, but a POST still carries a
            // body so the gate assertion is never confounded by content negotiation.
            request.Content = JsonContent.Create(new { });
        }

        return request;
    }
}
