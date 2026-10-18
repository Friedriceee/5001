using System.Net;
using System.Net.Http.Json;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Features.Admin;
using JiKeDa.Order.Api.Features.Orders;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JiKeDa.Order.Tests;

[TestFixture]
public sealed class AdminEndpointTests
{
    private ApiTestFactory _factory = null!;
    private HttpClient _admin = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiTestFactory();
        _factory.EnsureDatabase();
        _admin = _factory.CreateClient(OrderRoles.Administrator, 0);
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task Administrator_can_list_and_issue_coupons()
    {
        var list = await _admin.GetAsync("/api/admin/coupons");
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var coupons = (await list.ReadResultAsync<List<AdminCouponResponse>>()).Data!;
        var welcome = coupons.Single(item => item.Code == "WELCOME5");

        var issue = await _admin.PostAsJsonAsync($"/api/admin/coupons/{welcome.Id}/issue", new { userId = 2, quantity = 3 });
        Assert.That(issue.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(_factory.WithDb(db => db.CustomerCoupons.Single(item => item.CustomerId == 2 && item.CouponId == welcome.Id).IssuedQuantity), Is.EqualTo(3));
    }

    [Test]
    public async Task Administrator_can_query_orders_with_fulfilment_status()
    {
        var orderId = _factory.WithDb(db =>
        {
            var order = new CustomerOrder
            {
                CustomerId = 1, AddressId = 1, Total = 22m, Subtotal = 17m, DeliveryFee = 5m,
                ReservationId = "admin-test", CreatedAtUtc = DateTime.UtcNow,
                Lines = [new OrderLine { DishId = 1, MerchantId = 1, DishName = "Noodles", UnitPrice = 17m, Quantity = 1 }],
            };
            db.Orders.Add(order);
            db.SaveChanges();
            return order.Id;
        });
        _factory.Fulfilment.Statuses[orderId] = "Completed";

        var response = await _admin.GetAsync("/api/admin/orders?status=Completed");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var page = (await response.ReadResultAsync<PagedResult<AdminOrderResponse>>()).Data!;
        Assert.Multiple((Action)(() =>
        {
            Assert.That(page.TotalCount, Is.EqualTo(1));
            Assert.That(page.Items.Single().Status, Is.EqualTo("Completed"));
            Assert.That(page.Items.Single().ItemCount, Is.EqualTo(1));
        }));
    }

    [Test]
    public async Task Customer_cannot_access_admin_resources()
    {
        using var customer = _factory.CreateClient(OrderRoles.Customer, 1);
        Assert.That((await customer.GetAsync("/api/admin/coupons")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
