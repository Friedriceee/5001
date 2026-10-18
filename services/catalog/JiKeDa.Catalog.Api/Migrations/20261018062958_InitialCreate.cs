using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace JiKeDa.Catalog.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Merchants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'1000', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Contact = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DishType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TimeForOpenBusiness = table.Column<int>(type: "integer", nullable: true),
                    TimeForCloseBusiness = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dishes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'1000', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MerchantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Inventory = table.Column<int>(type: "integer", nullable: false),
                    ItemType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BundleSize = table.Column<int>(type: "integer", nullable: true),
                    PromotionStartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PromotionEndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dishes", x => x.Id);
                    table.CheckConstraint("CK_Dishes_Inventory_NonNegative", "\"Inventory\" >= 0");
                    table.ForeignKey(
                        name: "FK_Dishes_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpecialOffers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'1000', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MerchantId = table.Column<int>(type: "integer", nullable: false),
                    MinPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountRemission = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecialOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecialOffers_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryReservations",
                columns: table => new
                {
                    ReservationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DishId = table.Column<int>(type: "integer", nullable: false),
                    MerchantId = table.Column<int>(type: "integer", nullable: false),
                    DishName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    IsReleased = table.Column<bool>(type: "boolean", nullable: false),
                    ReservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryReservations", x => new { x.ReservationId, x.DishId });
                    table.CheckConstraint("CK_InventoryReservations_Quantity_Positive", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_InventoryReservations_Dishes_DishId",
                        column: x => x.DishId,
                        principalTable: "Dishes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Merchants",
                columns: new[] { "Id", "Address", "Contact", "DishType", "Name", "TimeForCloseBusiness", "TimeForOpenBusiness" },
                values: new object[,]
                {
                    { 1, "Campus South Gate", "13800000001", "Rice", "Demo Merchant", null, null },
                    { 2, "Campus North Gate", "13800000002", "Noodles", "Sprint 3 Merchant", null, null }
                });

            migrationBuilder.InsertData(
                table: "Dishes",
                columns: new[] { "Id", "BundleSize", "Category", "ImageUrl", "Inventory", "ItemType", "MerchantId", "Name", "Price", "PromotionEndsAtUtc", "PromotionStartsAtUtc" },
                values: new object[,]
                {
                    { 1, null, "Noodles", null, 500, "Standard", 1, "Braised Noodles", 12.00m, null, null },
                    { 2, null, "Rice", null, 500, "Standard", 1, "Chicken Rice Bowl", 15.00m, null, null },
                    { 3, null, "Drinks", null, 500, "Standard", 1, "Iced Lemon Tea", 4.50m, null, null },
                    { 4, null, "Noodles", null, 500, "Standard", 2, "Sichuan Beef Noodles", 16.00m, null, null },
                    { 5, null, "Rice", null, 500, "Standard", 2, "Tofu Vegetable Bowl", 13.00m, null, null },
                    { 6, null, "Drinks", null, 500, "Standard", 2, "Jasmine Tea", 3.50m, null, null }
                });

            migrationBuilder.InsertData(
                table: "SpecialOffers",
                columns: new[] { "Id", "AmountRemission", "MerchantId", "MinPrice" },
                values: new object[,]
                {
                    { 1, 8m, 1, 50m },
                    { 2, 5m, 2, 40m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dishes_MerchantId",
                table: "Dishes",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_DishId",
                table: "InventoryReservations",
                column: "DishId");

            migrationBuilder.CreateIndex(
                name: "IX_SpecialOffers_MerchantId",
                table: "SpecialOffers",
                column: "MerchantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryReservations");

            migrationBuilder.DropTable(
                name: "SpecialOffers");

            migrationBuilder.DropTable(
                name: "Dishes");

            migrationBuilder.DropTable(
                name: "Merchants");
        }
    }
}
