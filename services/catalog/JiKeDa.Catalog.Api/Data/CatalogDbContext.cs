using JiKeDa.Catalog.Api.Features.Dishes;
using JiKeDa.Catalog.Api.Features.Inventory;
using JiKeDa.Catalog.Api.Features.Merchants;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Catalog.Api.Data;

/// <summary>Owns catalog_db: merchants, dishes, special offers and the inventory reservation ledger.</summary>
public sealed class CatalogDbContext : DbContext
{
    // Seeded rows use fixed ids below this value; generated ids start here so they never collide.
    public const int FirstGeneratedId = 1000;

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Dish> Dishes => Set<Dish>();
    public DbSet<SpecialOffer> SpecialOffers => Set<SpecialOffer>();
    public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Merchant>(entity =>
        {
            entity.Property(m => m.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: FirstGeneratedId);
            entity.Property(m => m.Name).HasMaxLength(100).IsRequired();
            entity.Property(m => m.Address).HasMaxLength(255);
            entity.Property(m => m.Contact).HasMaxLength(255);
            entity.Property(m => m.DishType).HasMaxLength(20);
        });

        modelBuilder.Entity<Dish>(entity =>
        {
            entity.Property(d => d.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: FirstGeneratedId);
            entity.Property(d => d.Name).HasMaxLength(50).IsRequired();
            entity.Property(d => d.Price).HasPrecision(18, 2);
            entity.Property(d => d.Category).HasMaxLength(20);
            entity.Property(d => d.ImageUrl).HasMaxLength(500);
            entity.Property(d => d.ItemType).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(d => d.MerchantId);
            entity.ToTable(table => table.HasCheckConstraint("CK_Dishes_Inventory_NonNegative", "\"Inventory\" >= 0"));
            entity.HasOne(d => d.Merchant)
                .WithMany(m => m.Dishes)
                .HasForeignKey(d => d.MerchantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SpecialOffer>(entity =>
        {
            entity.Property(o => o.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: FirstGeneratedId);
            entity.Property(o => o.MinPrice).HasPrecision(18, 2);
            entity.Property(o => o.AmountRemission).HasPrecision(18, 2);
            entity.HasIndex(o => o.MerchantId);
            entity.HasOne(o => o.Merchant)
                .WithMany(m => m.SpecialOffers)
                .HasForeignKey(o => o.MerchantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InventoryReservation>(entity =>
        {
            entity.HasKey(r => new { r.ReservationId, r.DishId });
            entity.Property(r => r.ReservationId).HasMaxLength(64);
            entity.Property(r => r.DishName).HasMaxLength(50).IsRequired();
            entity.Property(r => r.UnitPrice).HasPrecision(18, 2);
            entity.HasIndex(r => r.DishId);
            // A dish with reservation history cannot be hard-deleted.
            entity.HasOne<Dish>().WithMany().HasForeignKey(r => r.DishId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("CK_InventoryReservations_Quantity_Positive", "\"Quantity\" > 0"));
        });

        Seed(modelBuilder);
    }

    // Demo data matching the Identity seed: account merchant1 has profile_id 1.
    // Contains no secrets; values mirror the monolith's staging seeder.
    private static void Seed(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Merchant>().HasData(
            new Merchant { Id = 1, Name = "Demo Merchant", Address = "Campus South Gate", Contact = "13800000001", DishType = "Rice" },
            new Merchant { Id = 2, Name = "Sprint 3 Merchant", Address = "Campus North Gate", Contact = "13800000002", DishType = "Noodles" });

        modelBuilder.Entity<Dish>().HasData(
            new Dish { Id = 1, MerchantId = 1, Name = "Braised Noodles", Category = "Noodles", Price = 12.00m, Inventory = 500 },
            new Dish { Id = 2, MerchantId = 1, Name = "Chicken Rice Bowl", Category = "Rice", Price = 15.00m, Inventory = 500 },
            new Dish { Id = 3, MerchantId = 1, Name = "Iced Lemon Tea", Category = "Drinks", Price = 4.50m, Inventory = 500 },
            new Dish { Id = 4, MerchantId = 2, Name = "Sichuan Beef Noodles", Category = "Noodles", Price = 16.00m, Inventory = 500 },
            new Dish { Id = 5, MerchantId = 2, Name = "Tofu Vegetable Bowl", Category = "Rice", Price = 13.00m, Inventory = 500 },
            new Dish { Id = 6, MerchantId = 2, Name = "Jasmine Tea", Category = "Drinks", Price = 3.50m, Inventory = 500 });

        modelBuilder.Entity<SpecialOffer>().HasData(
            new SpecialOffer { Id = 1, MerchantId = 1, MinPrice = 50m, AmountRemission = 8m },
            new SpecialOffer { Id = 2, MerchantId = 2, MinPrice = 40m, AmountRemission = 5m });
    }
}
