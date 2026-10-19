using JiKeDa.Identity.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiKeDa.Identity.Api.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261018120000_AddSecondaryDemoAccounts")]
public sealed class AddSecondaryDemoAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "Accounts" ("Id", "CreatedAtUtc", "IsActive", "NormalizedUsername", "PasswordHash", "ProfileId", "Role", "Username")
            VALUES
              (1004, TIMESTAMPTZ '2026-01-01T00:00:00Z', TRUE, 'MERCHANT2', 'AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==', 2, 'Merchant', 'merchant2'),
              (1005, TIMESTAMPTZ '2026-01-01T00:00:00Z', TRUE, 'RIDER2', 'AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==', 2, 'Rider', 'rider2');
            SELECT setval(
              pg_get_serial_sequence('"Accounts"', 'Id'),
              GREATEST(
                (SELECT MAX("Id") FROM "Accounts") + 1,
                nextval(pg_get_serial_sequence('"Accounts"', 'Id'))),
              false);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM \"Accounts\" WHERE \"Id\" IN (1004, 1005);");
    }
}
