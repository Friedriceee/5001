using System.ComponentModel.DataAnnotations;
using JiKeDa.Identity.Api.Common;
using JiKeDa.Identity.Api.Data;
using JiKeDa.Identity.Api.Features.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Identity.Api.Features.Auth;

public sealed class RegisterMerchantRequest
{
    [Required, StringLength(50, MinimumLength = 3)] public string Username { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 8)] public string Password { get; set; } = string.Empty;
    [Required, StringLength(100)] public string MerchantName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string MerchantAddress { get; set; } = string.Empty;
    [Required, StringLength(255)] public string Contact { get; set; } = string.Empty;
    [StringLength(20)] public string? DishType { get; set; }
    [Range(0, 86399)] public int TimeForOpenBusiness { get; set; }
    [Range(0, 86399)] public int TimeForCloseBusiness { get; set; }
    [Required, StringLength(100, MinimumLength = 6)] public string WalletPassword { get; set; } = string.Empty;
}

public sealed class RegisterRiderRequest
{
    [Required, StringLength(50, MinimumLength = 3)] public string Username { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 8)] public string Password { get; set; } = string.Empty;
    [Required, StringLength(80)] public string Name { get; set; } = string.Empty;
    [Required, RegularExpression(@"^\d{8,11}$")] public string PhoneNumber { get; set; } = string.Empty;
    [Required, StringLength(40)] public string Zone { get; set; } = string.Empty;
    [Range(1, 20)] public int Capacity { get; set; } = 3;
}

public sealed record ExternalRegistrationResponse(int AccountId, int ProfileId, string Username, string Role);
public enum ExternalRegistrationOutcome { Success, UsernameConflict }
public sealed record ExternalRegistrationResult(ExternalRegistrationOutcome Outcome, ExternalRegistrationResponse? Registration = null);

public sealed class ExternalRegistrationService(
    IdentityDbContext db,
    IPasswordHasher<Account> passwordHasher,
    IMerchantProfileClient merchants,
    IRiderProfileClient riders,
    TimeProvider timeProvider)
{
    public Task<ExternalRegistrationResult> RegisterMerchantAsync(RegisterMerchantRequest request, CancellationToken cancellationToken) =>
        RegisterAsync(request.Username, request.Password, AccountRole.Merchant,
            ct => merchants.CreateAsync(request, ct), merchants.DeleteAsync, cancellationToken);

    public Task<ExternalRegistrationResult> RegisterRiderAsync(RegisterRiderRequest request, CancellationToken cancellationToken) =>
        RegisterAsync(request.Username, request.Password, AccountRole.Rider,
            ct => riders.CreateAsync(request, ct), riders.DeleteAsync, cancellationToken);

    private async Task<ExternalRegistrationResult> RegisterAsync(
        string rawUsername, string password, AccountRole role,
        Func<CancellationToken, Task<int>> createProfile,
        Func<int, CancellationToken, Task> deleteProfile,
        CancellationToken cancellationToken)
    {
        var username = rawUsername.Trim();
        var normalized = AuthService.NormalizeUsername(username);
        if (await db.Accounts.AnyAsync(item => item.NormalizedUsername == normalized, cancellationToken))
            return new(ExternalRegistrationOutcome.UsernameConflict);

        var profileId = await createProfile(cancellationToken);
        var account = new Account
        {
            Username = username,
            NormalizedUsername = normalized,
            Role = role,
            ProfileId = profileId,
            IsActive = true,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
        account.PasswordHash = passwordHasher.HashPassword(account, password);
        db.Accounts.Add(account);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await deleteProfile(profileId, cancellationToken);
            return new(ExternalRegistrationOutcome.UsernameConflict);
        }

        return new(ExternalRegistrationOutcome.Success,
            new(account.Id, profileId, account.Username, role.ToString()));
    }
}

[ApiController]
public sealed class ExternalRegistrationController(ExternalRegistrationService service) : ControllerBase
{
    [HttpPost("api/merchant/register")]
    public Task<ActionResult<ApiResult<ExternalRegistrationResponse>>> Merchant(
        RegisterMerchantRequest request, CancellationToken cancellationToken) =>
        Respond(service.RegisterMerchantAsync(request, cancellationToken));

    [HttpPost("api/rider/register")]
    public Task<ActionResult<ApiResult<ExternalRegistrationResponse>>> Rider(
        RegisterRiderRequest request, CancellationToken cancellationToken) =>
        Respond(service.RegisterRiderAsync(request, cancellationToken));

    private async Task<ActionResult<ApiResult<ExternalRegistrationResponse>>> Respond(
        Task<ExternalRegistrationResult> operation)
    {
        var result = await operation;
        return result.Outcome == ExternalRegistrationOutcome.UsernameConflict
            ? Conflict(ApiResult<ExternalRegistrationResponse>.Fail("Username already exists."))
            : StatusCode(StatusCodes.Status201Created, ApiResult<ExternalRegistrationResponse>.Ok(result.Registration!));
    }
}
