using JiKeDa.Fulfilment.Api.Features;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Fulfilment.Api.Data;

/// <summary>Owns fulfilment_db only: imported order projections, merchant work, riders and deliveries.</summary>
public sealed class FulfilmentDbContext(DbContextOptions<FulfilmentDbContext> options) : DbContext(options)
{
    public DbSet<FulfilmentOrder> Orders => Set<FulfilmentOrder>();
    public DbSet<FulfilmentLine> Lines => Set<FulfilmentLine>();
    public DbSet<MerchantOrderFulfilment> MerchantFulfilments => Set<MerchantOrderFulfilment>();
    public DbSet<Rider> Riders => Set<Rider>();
    public DbSet<RiderDelivery> Deliveries => Set<RiderDelivery>();
    public DbSet<EventCheckpoint> EventCheckpoints => Set<EventCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FulfilmentOrder>(entity =>
        {
            entity.ToTable("FulfilmentOrders");
            entity.HasIndex(o => o.ExternalOrderId).IsUnique();
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(o => o.TotalAmount).HasPrecision(18, 2);
            entity.Property(o => o.CorrelationId).HasMaxLength(128);
            entity.HasMany(o => o.Lines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(o => o.MerchantFulfilments).WithOne(f => f.Order).HasForeignKey(f => f.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<FulfilmentLine>(entity =>
        {
            entity.HasKey(l => new { l.OrderId, l.DishId });
            entity.Property(l => l.DishName).HasMaxLength(100).IsRequired();
            entity.Property(l => l.UnitPrice).HasPrecision(18, 2);
            entity.ToTable(t => t.HasCheckConstraint("CK_FulfilmentLines_Quantity_Positive", "\"Quantity\" > 0"));
        });
        modelBuilder.Entity<MerchantOrderFulfilment>(entity =>
        {
            entity.HasIndex(f => new { f.OrderId, f.MerchantId }).IsUnique();
            entity.Property(f => f.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(f => f.RejectionReason).HasMaxLength(250);
        });
        modelBuilder.Entity<Rider>(entity =>
        {
            entity.Property(r => r.Name).HasMaxLength(80).IsRequired();
            entity.Property(r => r.PhoneNumber).HasMaxLength(11);
            entity.Property(r => r.Zone).HasMaxLength(40).IsRequired();
            entity.ToTable(t => t.HasCheckConstraint("CK_Riders_Capacity_Positive", "\"Capacity\" > 0"));
            entity.HasData(new Rider { Id = 1, Name = "Rider One", PhoneNumber = "13800000000", Zone = "ZoneA", IsActive = true, IsAvailable = true, Capacity = 3 });
        });
        modelBuilder.Entity<RiderDelivery>(entity =>
        {
            entity.HasIndex(d => d.OrderId).IsUnique();
            entity.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(d => d.Rider).WithMany(r => r.Deliveries).HasForeignKey(d => d.RiderId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<EventCheckpoint>(entity =>
        {
            entity.HasIndex(c => c.Consumer).IsUnique();
            entity.Property(c => c.Consumer).HasMaxLength(80).IsRequired();
        });
    }
}
