using System.ComponentModel.DataAnnotations;
using JiKeDa.Catalog.Api.Common;
using JiKeDa.Catalog.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Catalog.Api.Features.Merchants;

public sealed class CreateMerchantProfileRequest
{
    [Required, StringLength(100)] public string MerchantName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string MerchantAddress { get; set; } = string.Empty;
    [Required, StringLength(255)] public string Contact { get; set; } = string.Empty;
    [StringLength(20)] public string? DishType { get; set; }
    [Range(0, 86399)] public int TimeForOpenBusiness { get; set; }
    [Range(0, 86399)] public int TimeForCloseBusiness { get; set; }
}

public sealed record MerchantProfileCreatedResponse(int Id, string MerchantName);

// Internal profile provisioning used by Identity during public registration.
// The gateway deliberately does not expose /internal routes.
[ApiController, Route("internal/catalog/merchants")]
public sealed class MerchantRegistrationController(CatalogDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResult<MerchantProfileCreatedResponse>>> Create(
        CreateMerchantProfileRequest request, CancellationToken cancellationToken)
    {
        var merchant = new Merchant
        {
            Name = request.MerchantName.Trim(),
            Address = request.MerchantAddress.Trim(),
            Contact = request.Contact.Trim(),
            DishType = string.IsNullOrWhiteSpace(request.DishType) ? null : request.DishType.Trim(),
            TimeForOpenBusiness = request.TimeForOpenBusiness,
            TimeForCloseBusiness = request.TimeForCloseBusiness,
        };
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            ApiResult<MerchantProfileCreatedResponse>.Ok(new(merchant.Id, merchant.Name)));
    }

    [HttpDelete("{merchantId:int}")]
    public async Task<IActionResult> Delete(int merchantId, CancellationToken cancellationToken)
    {
        await db.Merchants.Where(item => item.Id == merchantId).ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }
}
