using System.Security.Claims;

namespace ReceiptWell.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("oid") ?? throw new InvalidOperationException("oid claim missing");
}
