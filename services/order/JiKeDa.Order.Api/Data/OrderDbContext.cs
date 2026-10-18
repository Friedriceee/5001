using JiKeDa.Order.Api.Features.Cart;
using JiKeDa.Order.Api.Features.Checkout;
using JiKeDa.Order.Api.Features.Orders;
using JiKeDa.Order.Api.Features.Outbox;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Data;

/// <summary>Owns order_db: carts, delivery addresses, coupons, checkout attempts, orders, payments and the outbox.</summary>
public sealed class OrderDbContext : DbContext
{
    // Seeded rows use fixed ids below this value; generated ids start here so they never collide.
    public const int FirstGeneratedId = 1000;

    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<DeliveryAddress> Addresses => Set<DeliveryAddress>();
    public DbSet<PlatformCoupon> PlatformCoupons => Set<PlatformCoupon>();
    public DbSet<CustomerCoupon> CustomerCoupons => Set<CustomerCoupon>();
    public DbSet<CheckoutAttempt> CheckoutAttempts => Set<CheckoutAttempt>();
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CartItem>(entity =>
        {
            entity.HasIndex(c => new { c.CustomerId, c.DishId }).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_CartItems_Quantity_Positive", "\"Quantity\" > 0"));
        });

        modelBuilder.Entity<DeliveryAddress>(entity =>
        {
            entity.ToTable("Addresses");
            entity.Property(a => a.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: FirstGeneratedId);
            entity.Property(a => a.Address).HasMaxLength(255).IsRequired();
            entity.Property(a => a.HouseNumber).HasMaxLength(50);
            entity.Property(a => a.ContactName).HasMaxLength(50).IsRequired();
            entity.Property(a => a.PhoneNumber).HasMaxLength(20).IsRequired();
            entity.HasIndex(a => a.CustomerId);
        });

        modelBuilder.Entity<PlatformCoupon>(entity =>
        {
            entity.Property(c => c.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: FirstGeneratedId);
            entity.Property(c => c.Code).HasMaxLength(40).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(100).IsRequired();
            entity.Property(c => c.MinimumSpend).HasPrecision(18, 2);
            entity.Property(c => c.DiscountAmount).HasPrecision(18, 2);
            entity.HasIndex(c => c.Code).IsUnique();
        });

        modelBuilder.Entity<CustomerCoupon>(entity =>
        {
            entity.Property(c => c.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: FirstGeneratedId);
            entity.HasIndex(c => new { c.CustomerId, c.CouponId }).IsUnique();
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_CustomerCoupons_IssuedQuantity_Positive", "\"IssuedQuantity\" > 0");
                t.HasCheckConstraint("CK_CustomerCoupons_UsedQuantity_Valid", "\"UsedQuantity\" >= 0 AND \"UsedQuantity\" <= \"IssuedQuantity\"");
            });
            entity.HasOne(c => c.Coupon).WithMany().HasForeignKey(c => c.CouponId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CheckoutAttempt>(entity =>
        {
            entity.Property(a => a.IdempotencyKey).HasMaxLength(64).IsRequired();
            entity.Property(a => a.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(a => new { a.CustomerId, a.IdempotencyKey }).IsUnique();
            entity.HasIndex(a => new { a.Status, a.CreatedAtUtc });
            entity.Ignore(a => a.ReservationId);
        });

        modelBuilder.Entity<CustomerOrder>(entity =>
        {
            entity.ToTable("Orders");
            foreach (var money in new[] { "Subtotal", "MerchantDiscount", "CouponDiscount", "DeliveryFee", "Total" })
                entity.Property<decimal>(money).HasPrecision(18, 2);
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(o => o.ReservationId).HasMaxLength(64).IsRequired();
            entity.HasIndex(o => o.CustomerId);
            entity.HasMany(o => o.Lines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(l => new { l.OrderId, l.DishId });
            entity.Property(l => l.DishName).HasMaxLength(50).IsRequired();
            entity.Property(l => l.UnitPrice).HasPrecision(18, 2);
            entity.ToTable(t => t.HasCheckConstraint("CK_OrderLines_Quantity_Positive", "\"Quantity\" > 0"));
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(p => p.Amount).HasPrecision(18, 2);
            entity.Property(p => p.Method).HasMaxLength(40).IsRequired();
            entity.Property(p => p.Reference).HasMaxLength(64).IsRequired();
            entity.HasIndex(p => p.OrderId).IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("Outbox");
            entity.Property(m => m.Type).HasMaxLength(40).IsRequired();
            entity.HasIndex(m => new { m.Type, m.ProcessedAtUtc });
        });

        Seed(modelBuilder);
    }

    // Demo data matching the Identity seed: account customer1 has profile_id 1.
    private static void Seed(ModelBuilder modelBuilder)
    {
        var start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero);
        modelBuilder.Entity<PlatformCoupon>().HasData(
            new PlatformCoupon { Id = 1, Code = "WELCOME5", Name = "Welcome ¥5 off", MinimumSpend = 20m, DiscountAmount = 5m, StartsAtUtc = start, ExpiresAtUtc = end, IsActive = true },
            new PlatformCoupon { Id = 2, Code = "SAVE12", Name = "Save ¥12 on larger orders", MinimumSpend = 80m, DiscountAmount = 12m, StartsAtUtc = start, ExpiresAtUtc = end, IsActive = true });
        modelBuilder.Entity<CustomerCoupon>().HasData(
            new CustomerCoupon { Id = 1, CustomerId = 1, CouponId = 1, IssuedQuantity = 3, UsedQuantity = 0 },
            new CustomerCoupon { Id = 2, CustomerId = 1, CouponId = 2, IssuedQuantity = 1, UsedQuantity = 0 });
        modelBuilder.Entity<DeliveryAddress>().HasData(
            new DeliveryAddress { Id = 1, CustomerId = 1, Address = "NUS University Town, Residence 1", HouseNumber = "#05-12", ContactName = "Customer One", PhoneNumber = "81234567" });
    }
}
