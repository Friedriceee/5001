namespace JiKeDa.Identity.Api.Features.Accounts;

/// <summary>
/// Authentication account owned by the Identity service.
/// ProfileId references a profile owned by the relevant service (customer profile lives here;
/// merchant/rider business profiles are owned by Catalog/Fulfilment and only referenced by id).
/// </summary>
public sealed class Account
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public AccountRole Role { get; set; }
    public int ProfileId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Disabled accounts are rejected at login; controlled by administrators.</summary>
    public bool IsActive { get; set; } = true;
}
