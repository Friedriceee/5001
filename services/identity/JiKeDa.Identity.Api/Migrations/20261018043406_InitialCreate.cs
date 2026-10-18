using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace JiKeDa.Identity.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DisplayName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Accounts",
                columns: new[] { "Id", "CreatedAtUtc", "IsActive", "NormalizedUsername", "PasswordHash", "ProfileId", "Role", "Username" },
                values: new object[,]
                {
                    { 1000, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "ADMIN", "AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==", 0, "Administrator", "admin" },
                    { 1001, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "MERCHANT1", "AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==", 1, "Merchant", "merchant1" },
                    { 1002, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "RIDER1", "AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==", 1, "Rider", "rider1" },
                    { 1003, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "CUSTOMER1", "AQAAAAIAAYagAAAAEN7+2J+E29U2utsYYga5BWLdWDID66KndU9LQsHZURyttKcEeg9HGwBXkt1Sl9dtpA==", 1, "Customer", "customer1" }
                });

            migrationBuilder.InsertData(
                table: "UserProfiles",
                columns: new[] { "Id", "CreatedAtUtc", "DisplayName", "PhoneNumber" },
                values: new object[] { 1, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Demo Customer", "13800000000" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_NormalizedUsername",
                table: "Accounts",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Role_ProfileId",
                table: "Accounts",
                columns: new[] { "Role", "ProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
