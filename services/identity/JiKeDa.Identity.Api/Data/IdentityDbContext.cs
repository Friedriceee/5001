using JiKeDa.Identity.Api.Features.Accounts;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Identity.Api.Data;

/// <summary>
/// Identity service database context. Owns only accounts and generic customer profiles.
/// No other service reads these tables; cross-service ids are plain references.
/// </summary>
public sealed class IdentityDbContext : DbContext
{
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Seed password hash for "Passw0rd!123" (staging/test demo accounts only, not a production secret).
        const string demoHash = "AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==";
        var seedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Username).HasMaxLength(50).IsRequired();
            entity.Property(a => a.NormalizedUsername).HasMaxLength(50).IsRequired();
            entity.Property(a => a.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(a => a.Role).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(a => a.NormalizedUsername).IsUnique();
            entity.HasIndex(a => new { a.Role, a.ProfileId }).IsUnique();

            entity.HasData(
                new Account { Id = 1000, Username = "admin", NormalizedUsername = "ADMIN", PasswordHash = demoHash, Role = AccountRole.Administrator, ProfileId = 0, CreatedAtUtc = seedTime, IsActive = true },
                new Account { Id = 1001, Username = "merchant1", NormalizedUsername = "MERCHANT1", PasswordHash = demoHash, Role = AccountRole.Merchant, ProfileId = 1, CreatedAtUtc = seedTime, IsActive = true },
                new Account { Id = 1002, Username = "rider1", NormalizedUsername = "RIDER1", PasswordHash = demoHash, Role = AccountRole.Rider, ProfileId = 1, CreatedAtUtc = seedTime, IsActive = true },
                new Account { Id = 1003, Username = "customer1", NormalizedUsername = "CUSTOMER1", PasswordHash = demoHash, Role = AccountRole.Customer, ProfileId = 1, CreatedAtUtc = seedTime, IsActive = true },
                new Account { Id = 1004, Username = "merchant2", NormalizedUsername = "MERCHANT2", PasswordHash = demoHash, Role = AccountRole.Merchant, ProfileId = 2, CreatedAtUtc = seedTime, IsActive = true },
                new Account { Id = 1005, Username = "rider2", NormalizedUsername = "RIDER2", PasswordHash = demoHash, Role = AccountRole.Rider, ProfileId = 2, CreatedAtUtc = seedTime, IsActive = true });
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.DisplayName).HasMaxLength(50).IsRequired();
            entity.Property(p => p.PhoneNumber).HasMaxLength(11);

            entity.HasData(new UserProfile
            {
                Id = 1,
                DisplayName = "Demo Customer",
                PhoneNumber = "13800000000",
                CreatedAtUtc = seedTime,
            });
        });
    }
}
