using JiKeDa.Catalog.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiKeDa.Catalog.Api.Features.Dishes;

/// <summary>Public single-dish lookup, used by the customer UI and the cart.</summary>
[ApiController]
[Route("api/catalog/dishes")]
public sealed class DishesController : ControllerBase
{
    private readonly DishService _dishes;

    public DishesController(DishService dishes) => _dishes = dishes;

    [HttpGet("{dishId:int}")]
    public async Task<ActionResult<ApiResult<DishResponse>>> Get(int dishId, CancellationToken cancellationToken)
    {
        var dish = await _dishes.GetAsync(dishId, cancellationToken);
        return dish is null
            ? NotFound(ApiResult<DishResponse>.Fail("Menu item was not found."))
            : Ok(ApiResult<DishResponse>.Ok(dish));
    }
}

/// <summary>Dish management for the signed-in merchant; the merchant id always comes from the JWT.</summary>
[ApiController]
[Route("api/catalog/merchant/dishes")]
[Authorize(Roles = CatalogRoles.Merchant)]
public sealed class MerchantDishesController : ControllerBase
{
    private const string MerchantNotFound = "Merchant profile was not found in Catalog.";
    private readonly DishService _dishes;

    public MerchantDishesController(DishService dishes) => _dishes = dishes;

    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<DishResponse>>>> List(CancellationToken cancellationToken)
    {
        var dishes = await _dishes.ListForMerchantAsync(User.GetProfileId(), cancellationToken);
        return dishes is null
            ? NotFound(ApiResult<IReadOnlyList<DishResponse>>.Fail(MerchantNotFound))
            : Ok(ApiResult<IReadOnlyList<DishResponse>>.Ok(dishes));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResult<DishResponse>>> Create(
        [FromBody] CreateDishRequest request,
        CancellationToken cancellationToken)
    {
        var dish = await _dishes.CreateAsync(User.GetProfileId(), request, cancellationToken);
        return dish is null
            ? NotFound(ApiResult<DishResponse>.Fail(MerchantNotFound))
            : StatusCode(StatusCodes.Status201Created, ApiResult<DishResponse>.Ok(dish));
    }

    [HttpPut("{dishId:int}")]
    public async Task<ActionResult<ApiResult<DishResponse>>> Update(
        int dishId,
        [FromBody] UpdateDishRequest request,
        CancellationToken cancellationToken)
    {
        var dish = await _dishes.UpdateAsync(dishId, User.GetProfileId(), request, cancellationToken);
        return dish is null
            ? NotFound(ApiResult<DishResponse>.Fail("Menu item was not found or belongs to another merchant."))
            : Ok(ApiResult<DishResponse>.Ok(dish));
    }

    [HttpDelete("{dishId:int}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(int dishId, CancellationToken cancellationToken)
    {
        var outcome = await _dishes.DeleteAsync(dishId, User.GetProfileId(), cancellationToken);
        return outcome switch
        {
            DishDeleteOutcome.Deleted => Ok(ApiResult.Ok()),
            DishDeleteOutcome.HasReservations => Conflict(ApiResult.Fail(
                "Menu item has reservation history and cannot be deleted. Set inventory to zero to stop new orders.")),
            _ => NotFound(ApiResult.Fail("Menu item was not found or belongs to another merchant.")),
        };
    }
}
