using System.ComponentModel.DataAnnotations;

namespace JiKeDa.Identity.Api.Features.Accounts;

public sealed record AccountResponse(
    int Id,
    string Username,
    string Role,
    int ProfileId,
    bool IsActive,
    DateTime CreatedAtUtc);

public sealed class UpdateAccountStatusRequest
{
    [Required]
    public bool IsActive { get; set; }
}

public sealed class UpdateAccountRoleRequest
{
    [Required(ErrorMessage = "role is required")]
    public string Role { get; set; } = string.Empty;
}
