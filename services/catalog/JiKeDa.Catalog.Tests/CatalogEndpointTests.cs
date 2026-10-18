using System.Net;
using System.Net.Http.Json;
using JiKeDa.Catalog.Api.Features.Dishes;
using JiKeDa.Catalog.Api.Features.Inventory;
using JiKeDa.Catalog.Api.Features.Merchants;
using NUnit.Framework;

namespace JiKeDa.Catalog.Tests;

public sealed class CatalogEndpointTests
{
    private ApiTestFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiTestFactory();
        _factory.EnsureDatabase();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private HttpClient Merchant(int id = 1) => _factory.CreateClient("Merchant", id);
    private HttpClient Customer() => _factory.CreateClient("Customer", 1);

    private static object NewDish(string name = "Mapo Tofu", int inventory = 10) =>
        new { name, price = 18.50m, category = "Sichuan", inventory, itemType = "Standard" };

    // ---- public menu ----

    [Test]
    public async Task Merchants_and_menus_are_public()
    {
        var client = _factory.CreateClient();

        var merchants = await (await client.GetAsync("/api/merchants")).ReadResultAsync<List<MerchantResponse>>();
        var dishes = await (await client.GetAsync("/api/merchants/1/dishes")).ReadResultAsync<List<DishResponse>>();
        var offers = await (await client.GetAsync("/api/merchants/2/special-offers")).ReadResultAsync<List<SpecialOfferResponse>>();
        var dish = await (await client.GetAsync("/api/catalog/dishes/4")).ReadResultAsync<DishResponse>();

        Assert.That(merchants.Data!.Select(m => m.Id), Is.EquivalentTo(new[] { 1, 2 }));
        Assert.That(dishes.Data!.Select(d => d.Name), Is.EqualTo(new[] { "Braised Noodles", "Chicken Rice Bowl", "Iced Lemon Tea" }));
        Assert.That(dishes.Data!.All(d => d.IsAvailable), Is.True);
        Assert.That(offers.Data!.Single().MinPrice, Is.EqualTo(40m));
        Assert.That((dish.Data!.MerchantId, dish.Data.Price), Is.EqualTo((2, 16.00m)));
    }

    [TestCase("/api/merchants/99")]
    [TestCase("/api/merchants/99/dishes")]
    [TestCase("/api/merchants/99/special-offers")]
    [TestCase("/api/catalog/dishes/999")]
    public async Task Unknown_ids_return_404(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- merchant dish management ----

    [Test]
    public async Task Dish_management_requires_a_merchant_token()
    {
        var anonymous = await _factory.CreateClient().PostAsJsonAsync("/api/catalog/merchant/dishes", NewDish());
        var customer = await Customer().PostAsJsonAsync("/api/catalog/merchant/dishes", NewDish());

        Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(customer.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Merchant_creates_updates_and_deletes_own_dish()
    {
        var client = Merchant(2);

        var created = await client.PostAsJsonAsync("/api/catalog/merchant/dishes", NewDish());
        var dish = (await created.ReadResultAsync<DishResponse>()).Data!;
        var updated = await client.PutAsJsonAsync($"/api/catalog/merchant/dishes/{dish.Id}",
            new { name = "Mapo Tofu (large)", price = 22m, inventory = 0, itemType = "Standard" });
        var mine = (await (await client.GetAsync("/api/catalog/merchant/dishes")).ReadResultAsync<List<DishResponse>>()).Data!;
        var deleted = await client.DeleteAsync($"/api/catalog/merchant/dishes/{dish.Id}");

        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(dish.MerchantId, Is.EqualTo(2), "merchant comes from the token");
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(mine.Single(d => d.Id == dish.Id).IsAvailable, Is.False, "zero stock is not available");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Merchant_cannot_change_another_merchants_dish()
    {
        var update = await Merchant(2).PutAsJsonAsync("/api/catalog/merchant/dishes/1", NewDish());
        var delete = await Merchant(2).DeleteAsync("/api/catalog/merchant/dishes/1");

        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Merchant_without_a_catalog_profile_gets_404()
    {
        var response = await Merchant(77).PostAsJsonAsync("/api/catalog/merchant/dishes", NewDish());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase("Bundle", null, "Bundle size must be a whole number of at least 2.")]
    [TestCase("Pizza", null, "Item type must be Standard, Bundle, or Promotional.")]
    [TestCase("Standard", 3, "Standard items cannot have bundle or promotion settings.")]
    public async Task Invalid_menu_item_returns_400_with_message(string itemType, int? bundleSize, string message)
    {
        var response = await Merchant().PostAsJsonAsync("/api/catalog/merchant/dishes",
            new { name = "Combo", price = 10m, inventory = 5, itemType, bundleSize });
        var body = await response.ReadResultAsync<object>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.Message, Is.EqualTo(message));
    }

    // ---- internal reservations ----

    [Test]
    public async Task Reservations_require_a_token()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/internal/catalog/reservations",
            new { reservationId = "r-anon", items = new[] { new { dishId = 1, quantity = 1 } } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Reserve_replay_conflict_and_release_status_codes()
    {
        var client = Customer();
        var body = new { reservationId = "order-1001", items = new[] { new { dishId = 1, quantity = 2 } } };

        var first = await client.PostAsJsonAsync("/internal/catalog/reservations", body);
        var replay = await client.PostAsJsonAsync("/internal/catalog/reservations", body);
        var conflict = await client.PostAsJsonAsync("/internal/catalog/reservations",
            new { reservationId = "order-1001", items = new[] { new { dishId = 1, quantity = 5 } } });
        var release = await client.PostAsync("/internal/catalog/reservations/order-1001/release", null);
        var releaseAgain = await client.PostAsync("/internal/catalog/reservations/order-1001/release", null);
        var missing = await client.PostAsync("/internal/catalog/reservations/nope/release", null);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await first.ReadResultAsync<ReservationResponse>()).Data!.TotalAmount, Is.EqualTo(24.00m));
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await replay.ReadResultAsync<ReservationResponse>()).Data!.Replayed, Is.True);
        Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(release.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await releaseAgain.ReadResultAsync<ReleaseResponse>()).Data!.AlreadyReleased, Is.True);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Insufficient_stock_returns_400_with_code()
    {
        var response = await Customer().PostAsJsonAsync("/internal/catalog/reservations",
            new { reservationId = "order-big", items = new[] { new { dishId = 1, quantity = 501 } } });
        var body = await response.ReadResultAsync<object>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.Code, Is.EqualTo("INSUFFICIENT_STOCK"));
    }

    [TestCase("")]
    [TestCase("has space")]
    public async Task Invalid_reservation_id_returns_400(string reservationId)
    {
        var response = await Customer().PostAsJsonAsync("/internal/catalog/reservations",
            new { reservationId, items = new[] { new { dishId = 1, quantity = 1 } } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
