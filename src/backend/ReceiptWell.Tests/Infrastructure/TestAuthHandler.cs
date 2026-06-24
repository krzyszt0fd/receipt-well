using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReceiptWell.Tests.Infrastructure;

/// <summary>
/// Test authentication handler that injects a controllable identity from request
/// headers, so a single <see cref="ReceiptWellWebFactory"/> can serve every auth case
/// without a real CIAM token. Only the authentication step is simulated — the real
/// authorization policy (RequireAuthenticatedUser + RequireClaim) still decides 401/403.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Authenticate as this <c>oid</c> (e.g. <c>X-Test-Oid: user-a</c>).</summary>
    public const string OidHeader = "X-Test-Oid";

    /// <summary>
    /// Authenticate a principal WITHOUT an <c>oid</c> claim — models a structurally
    /// valid token whose <c>oid</c> is missing. Any value (presence) triggers it.
    /// </summary>
    public const string NoOidHeader = "X-Test-No-Oid";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Authenticated, but no oid claim → exercises the .RequireClaim("oid") gate (403).
        if (Request.Headers.ContainsKey(NoOidHeader))
        {
            var identity = new ClaimsIdentity(authenticationType: SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // Authenticated with a chosen oid → the normal scoped-caller case.
        if (Request.Headers.TryGetValue(OidHeader, out var oid) && !string.IsNullOrEmpty(oid))
        {
            // Keep the short claim name — the app sets MapInboundClaims = false.
            var claims = new[] { new Claim("oid", oid.ToString()) };
            var identity = new ClaimsIdentity(claims, authenticationType: SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // No test header → models "no token": the fallback policy challenges → 401.
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
