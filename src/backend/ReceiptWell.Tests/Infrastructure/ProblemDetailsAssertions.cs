using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReceiptWell.Tests.Infrastructure;

/// <summary>
/// Shared assertion helper for the Risk #7 honest-5xx contract. Call from every failure test
/// instead of re-implementing body checks individually.
/// </summary>
public static class ProblemDetailsAssertions
{
    /// <summary>
    /// Asserts that <paramref name="response"/> is a well-formed, honest RFC 7807
    /// Problem Details 500 — correct status, correct content-type, <c>status</c> field
    /// equal to 500, and no leaked exception detail or stack trace.
    /// </summary>
    public static async Task AssertHonest500Async(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, body.GetProperty("status").GetInt32());

        // Must not carry exception message, stack trace, or any non-standard detail.
        Assert.False(body.TryGetProperty("exception", out _),
            "Response must not leak exception type");
        Assert.False(body.TryGetProperty("stackTrace", out _),
            "Response must not leak stack trace");

        // 'detail' is the RFC 7807 field most likely to carry exception text; it must be
        // absent or null — never populated from the caught exception.
        if (body.TryGetProperty("detail", out var detail))
            Assert.Equal(JsonValueKind.Null, detail.ValueKind);
    }
}
