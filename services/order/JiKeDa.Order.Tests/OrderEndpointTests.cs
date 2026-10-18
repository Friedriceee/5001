using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JiKeDa.Order.Api.Features.Cart;
using JiKeDa.Order.Api.Features.Checkout;
using JiKeDa.Order.Api.Features.Orders;
using JiKeDa.Order.Api.Features.Outbox;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JiKeDa.Order.Tests;

public sealed class OrderEndpointTests
{
    // Seed: customer 1 owns address 1, WELCOME5 (3 uses, min 20, -5) and SAVE12 (1 use, min 80, -12).
    // Fake Catalog: dish 1 = 12.00 and dish 2 = 15.00 (merchant 1, offer 50/-8), dish 4 = 16.00 (merchant 2, offer 40/-5).
    private ApiTestFactory _factory = null!;
    private HttpClient _customer = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiTestFactory();
        _factory.EnsureDatabase();
        _customer = _factory.CreateClient("Customer", 1);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static object Body(string method = SimulatedPaymentGateway.ApproveMethod, string? coupon = null, int addressId = 1) =>
        new { addressId, couponCode = coupon, needUtensils = 1, paymentMethod = method };

    private async Task AddToCart(int dishId, int quantity) =>
        Assert.That((await _customer.PostAsJsonAsync("/api/cart/items", new { dishId, quantity })).StatusCode, Is.EqualTo(HttpStatusCode.Created));

    private int CartCount() => _factory.WithDb(db => db.CartItems.Count());
    private int CouponUses(int couponId) => _factory.WithDb(db => db.CustomerCoupons.Single(c => c.CustomerId == 1 && c.CouponId == couponId).UsedQuantity);
    private int PendingReleases() => _factory.WithDb(db => db.Outbox.Count(m => m.Type == OutboxTypes.ReleaseReservation && m.ProcessedAtUtc == null));
    private Task RunWorker() => _factory.Services.GetRequiredService<OutboxWorker>().RunOnceAsync(CancellationToken.None);

    // ---- cart and pricing ----

    [Test]
    public async Task Cart_prices_and_offers_come_from_catalog()
    {
        await AddToCart(1, 5);

        var cart = (await (await _customer.GetAsync("/api/cart")).ReadResultAsync<CartResponse>()).Data!;

        Assert.That((cart.Subtotal, cart.Discount, cart.Total), Is.EqualTo((60.00m, 8m, 52.00m)));
        Assert.That(cart.Merchants.Single().MerchantName, Is.EqualTo("Demo Merchant"));
    }

    [Test]
    public async Task Cart_rejects_more_than_the_stock_and_unknown_dishes()
    {
        var tooMany = await _customer.PostAsJsonAsync("/api/cart/items", new { dishId = 1, quantity = 501 });
        var unknown = await _customer.PostAsJsonAsync("/api/cart/items", new { dishId = 99, quantity = 1 });

        Assert.That(tooMany.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Customer_endpoints_require_a_customer_token()
    {
        Assert.That((await _factory.CreateClient().GetAsync("/api/cart")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _factory.CreateClient("Merchant", 1).GetAsync("/api/orders")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _factory.CreateClient().GetAsync("/internal/order/events")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Quote_applies_the_coupon_after_merchant_offers()
    {
        await AddToCart(1, 2); // 24.00, below the 50 offer

        var quote = (await (await _customer.PostAsJsonAsync("/api/pricing/quote", new { couponCode = "welcome5" })).ReadResultAsync<PricingResult>()).Data!;

        Assert.That((quote.Subtotal, quote.MerchantDiscount, quote.CouponDiscount, quote.DeliveryFee, quote.Total),
            Is.EqualTo((24.00m, 0m, 5m, 5m, 24.00m)));
    }

    // ---- checkout ----

    [Test]
    public async Task Checkout_creates_a_paid_order_reserves_stock_uses_the_coupon_and_emits_an_event()
    {
        await AddToCart(1, 2);
        await AddToCart(4, 1);

        var response = await _customer.CheckoutAsync("key-success-001", Body(coupon: "WELCOME5"));
        var result = (await response.ReadResultAsync<CheckoutResponse>()).Data!;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        // 24 + 16 = 40; merchant 2 offer needs 40 on its own 16 -> 0; coupon -5; delivery +5.
        Assert.That(result.AmountPaid, Is.EqualTo(40.00m));
        Assert.That(result.Lines.Select(l => (l.DishId, l.DishName, l.UnitPrice)), Is.EqualTo(new[] { (1, "Braised Noodles", 12.00m), (4, "Sichuan Beef Noodles", 16.00m) }));
        Assert.That((_factory.Catalog.Stock(1), _factory.Catalog.Stock(4)), Is.EqualTo((498, 499)));
        Assert.That(CartCount(), Is.Zero);
        Assert.That(CouponUses(1), Is.EqualTo(1));

        var service = _factory.CreateClient("Service", 0);
        var events = (await (await service.GetAsync("/internal/order/events?after=0")).ReadResultAsync<List<OrderEventResponse>>()).Data!;
        Assert.That(events.Single().Payload.GetProperty("orderId").GetInt32(), Is.EqualTo(result.OrderId));
        Assert.That(events.Single().Payload.GetProperty("items").GetArrayLength(), Is.EqualTo(2));

        var mine = (await (await _customer.GetAsync("/api/orders")).ReadResultAsync<List<OrderResponse>>()).Data!;
        Assert.That(mine.Single().Id, Is.EqualTo(result.OrderId));
        Assert.That((await _factory.CreateClient("Customer", 2).GetAsync($"/api/orders/{result.OrderId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Customer_order_history_uses_the_latest_fulfilment_status()
    {
        await AddToCart(1, 1);
        var placed = (await (await _customer.CheckoutAsync("key-status-0001", Body())).ReadResultAsync<CheckoutResponse>()).Data!;
        _factory.Fulfilment.Statuses[placed.OrderId] = "Completed";

        var mine = (await (await _customer.GetAsync("/api/orders")).ReadResultAsync<List<OrderResponse>>()).Data!;

        Assert.That(mine.Single(order => order.Id == placed.OrderId).Status, Is.EqualTo("Completed"));
    }

    [Test]
    public async Task A_retried_checkout_returns_the_same_order_without_reserving_again()
    {
        await AddToCart(1, 2);
        var first = (await (await _customer.CheckoutAsync("key-replay-0001", Body())).ReadResultAsync<CheckoutResponse>()).Data!;

        var again = await _customer.CheckoutAsync("key-replay-0001", Body());
        var replay = (await again.ReadResultAsync<CheckoutResponse>()).Data!;

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((replay.Replayed, replay.OrderId, replay.PaymentReference), Is.EqualTo((true, first.OrderId, first.PaymentReference)));
        Assert.That(_factory.Catalog.ReserveCalls, Is.EqualTo(1));
        Assert.That(_factory.WithDb(db => db.Orders.Count()), Is.EqualTo(1));
    }

    [Test]
    public async Task The_same_key_with_a_different_request_is_rejected()
    {
        await AddToCart(1, 2);
        await _customer.CheckoutAsync("key-reused-0001", Body());

        var response = await _customer.CheckoutAsync("key-reused-0001", Body(coupon: "WELCOME5"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await response.ReadResultAsync<object>()).Code, Is.EqualTo(nameof(CheckoutOutcome.IdempotencyKeyReused)));
    }

    [Test]
    public async Task A_declined_payment_releases_the_stock_and_changes_nothing()
    {
        await AddToCart(1, 2);

        var declined = await _customer.CheckoutAsync("key-declined-01", Body(SimulatedPaymentGateway.DeclineMethod, "WELCOME5"));
        var retried = await _customer.CheckoutAsync("key-declined-01", Body(SimulatedPaymentGateway.DeclineMethod, "WELCOME5"));

        Assert.That(declined.StatusCode, Is.EqualTo(HttpStatusCode.PaymentRequired));
        Assert.That(retried.StatusCode, Is.EqualTo(HttpStatusCode.PaymentRequired), "a failed attempt does not block the key");
        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(500));
        Assert.That(CartCount(), Is.EqualTo(1));
        Assert.That(CouponUses(1), Is.Zero);
        Assert.That(_factory.WithDb(db => (db.Orders.Count(), db.CheckoutAttempts.Count())), Is.EqualTo((0, 0)));
    }

    [Test]
    public async Task Insufficient_stock_returns_409_and_keeps_the_cart()
    {
        await AddToCart(1, 2);
        _factory.Catalog.SetStock(1, 1);

        var response = await _customer.CheckoutAsync("key-nostock-001", Body());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await response.ReadResultAsync<object>()).Code, Is.EqualTo(nameof(CheckoutOutcome.InsufficientStock)));
        Assert.That(CartCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task A_coupon_that_does_not_apply_releases_the_reservation()
    {
        await AddToCart(1, 2); // 24 < SAVE12 minimum of 80

        var response = await _customer.CheckoutAsync("key-coupon-0001", Body(coupon: "SAVE12"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(500));
        Assert.That(_factory.Catalog.Released, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Another_customers_address_is_not_found()
    {
        var other = _factory.CreateClient("Customer", 2);
        await other.PostAsJsonAsync("/api/cart/items", new { dishId = 1, quantity = 1 });

        var response = await other.CheckoutAsync("key-address-001", Body(addressId: 1));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_factory.Catalog.ReserveCalls, Is.Zero);
    }

    [TestCase(null)]
    [TestCase("short")]
    [TestCase("has spaces in it")]
    public async Task An_invalid_idempotency_key_is_rejected(string? key)
    {
        await AddToCart(1, 1);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout") { Content = JsonContent.Create(Body()) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);

        var response = await _customer.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- compensation when Catalog misbehaves ----

    [Test]
    public async Task Catalog_down_during_reserve_returns_503_and_queues_a_defensive_release()
    {
        await AddToCart(1, 2);
        _factory.Catalog.Down = true;

        var response = await _customer.CheckoutAsync("key-down-000001", Body());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(PendingReleases(), Is.EqualTo(1));
        Assert.That(CartCount(), Is.EqualTo(1));

        _factory.Catalog.Down = false;
        await RunWorker();
        Assert.That(PendingReleases(), Is.Zero, "a release of a reservation that never happened is simply done");
    }

    [Test]
    public async Task Cart_reads_return_503_while_catalog_is_down()
    {
        await AddToCart(1, 1);
        _factory.Catalog.Down = true;

        var response = await _customer.GetAsync("/api/cart");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((await response.ReadResultAsync<object>()).Code, Is.EqualTo("ServiceUnavailable"));
    }

    [Test]
    public async Task A_failed_release_is_retried_by_the_worker()
    {
        await AddToCart(1, 2);
        _factory.Catalog.FailRelease = true;

        var declined = await _customer.CheckoutAsync("key-relfail-001", Body(SimulatedPaymentGateway.DeclineMethod));

        Assert.That(declined.StatusCode, Is.EqualTo(HttpStatusCode.PaymentRequired));
        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(498), "stock is still held until the release succeeds");
        Assert.That(PendingReleases(), Is.EqualTo(1));

        await RunWorker();
        Assert.That(PendingReleases(), Is.EqualTo(1), "still failing: stays queued");

        _factory.Catalog.FailRelease = false;
        await RunWorker();
        Assert.That(PendingReleases(), Is.Zero);
        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(500));
    }

    [Test]
    public async Task A_stale_attempt_is_abandoned_and_its_stock_released()
    {
        // Simulates a process that stopped after reserving stock but before finishing the order.
        var attemptId = _factory.WithDb(db =>
        {
            var attempt = new CheckoutAttempt
            {
                CustomerId = 1, IdempotencyKey = "key-stale-00001", RequestHash = "x",
                Status = CheckoutAttemptStatus.InProgress, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            };
            db.CheckoutAttempts.Add(attempt);
            db.SaveChanges();
            return attempt.Id;
        });
        await _factory.Catalog.ReserveAsync($"chk-{attemptId}", [new(1, 3)], CancellationToken.None);

        await RunWorker();

        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(500));
        Assert.That(_factory.WithDb(db => db.CheckoutAttempts.Count()), Is.Zero);
        var again = await _customer.CheckoutAsync("key-stale-00001", Body());
        Assert.That(again.StatusCode, Is.Not.EqualTo(HttpStatusCode.Conflict), "the key is usable again");
    }
}
