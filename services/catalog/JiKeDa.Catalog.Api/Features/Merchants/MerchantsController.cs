using JiKeDa.Catalog.Api.Common;
using JiKeDa.Catalog.Api.Data;
using JiKeDa.Catalog.Api.Features.Dishes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Catalog.Api.Features.Merchants;

/// <summary>Public restaurant directory and menus. No authentication required.</summary>
[ApiController]
[Route("api/merchants")]
public sealed class MerchantsController : ControllerBase
{
    private const string MerchantNotFound = "Merchant was not found.";
    private readonly CatalogDbContext _db;
    private readonly DishService _dishes;

    public MerchantsController(CatalogDbContext db, DishService dishes)
    {
        _db = db;
        _dishes = dishes;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<MerchantResponse>>>> List(CancellationToken cancellationToken)
    {
        var merchants = await _db.Merchants
            .AsNoTracking()
            .OrderBy(m => m.Name)
            .Select(m => new MerchantResponse(
                m.Id, m.Name, m.Address, m.Contact, m.DishType, m.TimeForOpenBusiness, m.TimeForCloseBusiness))
            .ToListAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<MerchantResponse>>.Ok(merchants));
    }

    [HttpGet("{merchantId:int}")]
    public async Task<ActionResult<ApiResult<MerchantResponse>>> Get(int merchantId, CancellationToken cancellationToken)
    {
        var merchant = await _db.Merchants
            .AsNoTracking()
            .Where(m => m.Id == merchantId)
            .Select(m => new MerchantResponse(
                m.Id, m.Name, m.Address, m.Contact, m.DishType, m.TimeForOpenBusiness, m.TimeForCloseBusiness))
            .SingleOrDefaultAsync(cancellationToken);
        return merchant is null
            ? NotFound(ApiResult<MerchantResponse>.Fail(MerchantNotFound))
            : Ok(ApiResult<MerchantResponse>.Ok(merchant));
    }

    [HttpGet("{merchantId:int}/dishes")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<DishResponse>>>> Dishes(
        int merchantId,
        CancellationToken cancellationToken)
    {
        var dishes = await _dishes.ListForMerchantAsync(merchantId, cancellationToken);
        return dishes is null
            ? NotFound(ApiResult<IReadOnlyList<DishResponse>>.Fail(MerchantNotFound))
            : Ok(ApiResult<IReadOnlyList<DishResponse>>.Ok(dishes));
    }

    [HttpGet("{merchantId:int}/special-offers")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<SpecialOfferResponse>>>> SpecialOffers(
        int merchantId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Merchants.AnyAsync(m => m.Id == merchantId, cancellationToken))
            return NotFound(ApiResult<IReadOnlyList<SpecialOfferResponse>>.Fail(MerchantNotFound));

        // decimal ordering is not supported by SQLite in tests, so order by id only.
        var offers = await _db.SpecialOffers
            .AsNoTracking()
            .Where(o => o.MerchantId == merchantId)
            .OrderBy(o => o.Id)
            .Select(o => new SpecialOfferResponse(o.Id, o.MerchantId, o.MinPrice, o.AmountRemission))
            .ToListAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<SpecialOfferResponse>>.Ok(offers));
    }
}
