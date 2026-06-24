using System.Security.Claims;
using ReceiptWell.Extensions;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #1 (claim→scope contract): <c>GetUserId()</c> is the single identity key every
/// scoped call depends on. It must return the <c>oid</c> claim when present and throw
/// when absent — a regression here (e.g. <c>MapInboundClaims</c> flips back to <c>true</c>
/// and the short <c>oid</c> name stops resolving) would silently break every owner check.
/// Pure unit: no factory, no HTTP.
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void GetUserId_returns_the_oid_claim_when_present()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("oid", "user-123")], authenticationType: "Test"));

        Assert.Equal("user-123", principal.GetUserId());
    }

    [Fact]
    public void GetUserId_throws_when_the_oid_claim_is_absent()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"));

        Assert.Throws<InvalidOperationException>(() => principal.GetUserId());
    }
}
