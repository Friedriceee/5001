using System.ComponentModel.DataAnnotations;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Cart;

[ApiController]
[Route("api/cart")]
[Authorize(Roles = OrderRoles.Customer)]
public sealed class CartController : ControllerBase
{
    private readonly CartService _cart;

    public CartController(CartService cart) => _cart = cart;

    [HttpGet]
    public async Task<ActionResult<ApiResult<CartResponse>>> Get(CancellationToken cancellationToken) =>
        Ok(ApiResult<CartResponse>.Ok(await _cart.GetAsync(User.GetProfileId(), cancellationToken)));

    [HttpPost("items")]
    public async Task<ActionResult<ApiResult<CartResponse>>> Add([FromBody] AddCartItemRequest request, CancellationToken cancellationToken) =>
        Map(await _cart.AddAsync(User.GetProfileId(), request, cancellationToken), created: true);

    [HttpPut("items/{cartItemId:int}")]
    public async Task<ActionResult<ApiResult<CartResponse>>> Update(int cartItemId, [FromBody] UpdateCartItemRequest request, CancellationToken cancellationToken) =>
        Map(await _cart.UpdateAsync(User.GetProfileId(), cartItemId, request, cancellationToken));

    [HttpDelete("items/{cartItemId:int}")]
    public async Task<ActionResult<ApiResult<CartResponse>>> Remove(int cartItemId, CancellationToken cancellationToken) =>
        Map(await _cart.RemoveAsync(User.GetProfileId(), cartItemId, cancellationToken));

    [HttpDelete("merchants/{merchantId:int}")]
    public async Task<ActionResult<ApiResult<CartResponse>>> RemoveMerchant(int merchantId, CancellationToken cancellationToken) =>
        Map(await _cart.RemoveMerchantAsync(User.GetProfileId(), merchantId, cancellationToken));

    [HttpDelete]
    public async Task<ActionResult<ApiResult<CartResponse>>> Clear(CancellationToken cancellationToken) =>
        Ok(ApiResult<CartResponse>.Ok(await _cart.ClearAsync(User.GetProfileId(), cancellationToken)));

    private ActionResult<ApiResult<CartResponse>> Map(CartResult result, bool created = false) => result.Outcome switch
    {
        CartOutcome.Success when created => StatusCode(StatusCodes.Status201Created, ApiResult<CartResponse>.Ok(result.Cart!)),
        CartOutcome.Success => Ok(ApiResult<CartResponse>.Ok(result.Cart!)),
        CartOutcome.DishNotFound or CartOutcome.CartItemNotFound => NotFound(ApiResult<CartResponse>.Fail(result.Message!)),
        _ => BadRequest(ApiResult<CartResponse>.Fail(result.Message!, result.Outcome.ToString())),
    };
}

[ApiController]
[Route("api/pricing")]
[Authorize(Roles = OrderRoles.Customer)]
public sealed class PricingController : ControllerBase
{
    private readonly PricingService _pricing;

    public PricingController(PricingService pricing) => _pricing = pricing;

    [HttpGet("coupons")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<CouponOptionResponse>>>> Coupons(CancellationToken cancellationToken) =>
        Ok(ApiResult<IReadOnlyList<CouponOptionResponse>>.Ok(await _pricing.ListCouponsAsync(User.GetProfileId(), cancellationToken)));

    [HttpPost("quote")]
    public async Task<ActionResult<ApiResult<PricingResult>>> Quote([FromBody] PricingQuoteRequest request, CancellationToken cancellationToken)
    {
        var outcome = await _pricing.QuoteCartAsync(User.GetProfileId(), request.CouponCode, cancellationToken);
        return outcome.Succeeded
            ? Ok(ApiResult<PricingResult>.Ok(outcome.Pricing!))
            : BadRequest(ApiResult<PricingResult>.Fail(outcome.Message!, outcome.ErrorCode!.Value.ToString()));
    }
}

public sealed class CreateAddressRequest
{
    [Required, StringLength(255, MinimumLength = 1)] public string? Address { get; set; }
    [StringLength(50)] public string? HouseNumber { get; set; }
    [Required, StringLength(50, MinimumLength = 1)] public string? ContactName { get; set; }
    [Required, RegularExpression("^[0-9+ -]{6,20}$", ErrorMessage = "phoneNumber must be 6-20 digits.")] public string? PhoneNumber { get; set; }
}

public sealed record AddressResponse(int Id, string Address, string? HouseNumber, string ContactName, string PhoneNumber);

[ApiController]
[Route("api/addresses")]
[Authorize(Roles = OrderRoles.Customer)]
public sealed class AddressesController : ControllerBase
{
    private readonly OrderDbContext _db;

    public AddressesController(OrderDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<AddressResponse>>>> List(CancellationToken cancellationToken)
    {
        var customerId = User.GetProfileId();
        var addresses = await _db.Addresses.AsNoTracking().Where(a => a.CustomerId == customerId).OrderBy(a => a.Id)
            .Select(a => new AddressResponse(a.Id, a.Address, a.HouseNumber, a.ContactName, a.PhoneNumber))
            .ToListAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<AddressResponse>>.Ok(addresses));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResult<AddressResponse>>> Create([FromBody] CreateAddressRequest request, CancellationToken cancellationToken)
    {
        var address = new DeliveryAddress
        {
            CustomerId = User.GetProfileId(),
            Address = request.Address!.Trim(),
            HouseNumber = string.IsNullOrWhiteSpace(request.HouseNumber) ? null : request.HouseNumber.Trim(),
            ContactName = request.ContactName!.Trim(),
            PhoneNumber = request.PhoneNumber!.Trim(),
        };
        _db.Addresses.Add(address);
        await _db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResult<AddressResponse>.Ok(
            new AddressResponse(address.Id, address.Address, address.HouseNumber, address.ContactName, address.PhoneNumber)));
    }
}
