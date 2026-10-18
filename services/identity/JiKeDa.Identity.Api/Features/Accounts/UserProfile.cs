namespace JiKeDa.Identity.Api.Features.Accounts;

/// <summary>
/// Generic customer profile owned by the Identity service (display name, contact).
/// Other services reference the id only; they do not read this table.
/// </summary>
public sealed class UserProfile
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
