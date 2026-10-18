using System.Security.Claims;

namespace JiKeDa.Catalog.Api.Common;

/// <summary>Role names issued by the Identity service (see the JWT contract in docs/api-contract.md).</summary>
public static class CatalogRoles
{
    public const string Merchant = "Merchant";
}

public static class ClaimsPrincipalExtensions
{
    public const string ProfileIdClaim = "profile_id";

    /// <summary>For a Merchant token, profile_id is the Catalog merchant id.</summary>
    public static int GetProfileId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ProfileIdClaim);
        return int.TryParse(value, out var id)
            ? id
            : throw new InvalidOperationException("Token does not contain a valid profile_id claim.");
    }
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "JiKeDa";
    public string Audience { get; set; } = "JiKeDa.Client";
    public string Key { get; set; } = string.Empty;
}
