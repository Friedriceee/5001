using System;
using JiKeDa.Fulfilment.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JiKeDa.Fulfilment.Api.Migrations;

[DbContext(typeof(FulfilmentDbContext))]
[Migration("20261018080000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "EventCheckpoints", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            Consumer = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
            LastEventId = table.Column<long>(type: "bigint", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_EventCheckpoints", x => x.Id));
        migrationBuilder.CreateTable(name: "FulfilmentOrders", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            ExternalOrderId = table.Column<int>(type: "integer", nullable: false), CustomerId = table.Column<int>(type: "integer", nullable: false),
            AddressId = table.Column<int>(type: "integer", nullable: false), NeedUtensils = table.Column<int>(type: "integer", nullable: false),
            DeliveryFee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false), TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true), OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
        }, constraints: table => table.PrimaryKey("PK_FulfilmentOrders", x => x.Id));
        migrationBuilder.CreateTable(name: "Riders", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false), Zone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
            IsActive = table.Column<bool>(type: "boolean", nullable: false), IsAvailable = table.Column<bool>(type: "boolean", nullable: false), Capacity = table.Column<int>(type: "integer", nullable: false), ActiveDeliveryCount = table.Column<int>(type: "integer", nullable: false),
        }, constraints: table => { table.PrimaryKey("PK_Riders", x => x.Id); table.CheckConstraint("CK_Riders_Capacity_Positive", "\"Capacity\" > 0"); });
        migrationBuilder.CreateTable(name: "Lines", columns: table => new
        {
            OrderId = table.Column<int>(type: "integer", nullable: false), DishId = table.Column<int>(type: "integer", nullable: false), MerchantId = table.Column<int>(type: "integer", nullable: false),
            DishName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false), Quantity = table.Column<int>(type: "integer", nullable: false), UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
        }, constraints: table => { table.PrimaryKey("PK_Lines", x => new { x.OrderId, x.DishId }); table.CheckConstraint("CK_FulfilmentLines_Quantity_Positive", "\"Quantity\" > 0"); table.ForeignKey("FK_Lines_FulfilmentOrders_OrderId", x => x.OrderId, "FulfilmentOrders", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable(name: "MerchantFulfilments", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            OrderId = table.Column<int>(type: "integer", nullable: false), MerchantId = table.Column<int>(type: "integer", nullable: false), Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false), RejectionReason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true), UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        }, constraints: table => { table.PrimaryKey("PK_MerchantFulfilments", x => x.Id); table.ForeignKey("FK_MerchantFulfilments_FulfilmentOrders_OrderId", x => x.OrderId, "FulfilmentOrders", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable(name: "Deliveries", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn), OrderId = table.Column<int>(type: "integer", nullable: false), RiderId = table.Column<int>(type: "integer", nullable: false), Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false), AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false), PickedUpAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true), DeliveringAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true), CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        }, constraints: table => { table.PrimaryKey("PK_Deliveries", x => x.Id); table.ForeignKey("FK_Deliveries_Riders_RiderId", x => x.RiderId, "Riders", "Id", onDelete: ReferentialAction.Restrict); });
        migrationBuilder.CreateIndex(name: "IX_EventCheckpoints_Consumer", table: "EventCheckpoints", column: "Consumer", unique: true);
        migrationBuilder.CreateIndex(name: "IX_FulfilmentOrders_ExternalOrderId", table: "FulfilmentOrders", column: "ExternalOrderId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_MerchantFulfilments_OrderId_MerchantId", table: "MerchantFulfilments", columns: new[] { "OrderId", "MerchantId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Deliveries_OrderId", table: "Deliveries", column: "OrderId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Deliveries_RiderId", table: "Deliveries", column: "RiderId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Deliveries"); migrationBuilder.DropTable(name: "EventCheckpoints"); migrationBuilder.DropTable(name: "Lines"); migrationBuilder.DropTable(name: "MerchantFulfilments"); migrationBuilder.DropTable(name: "Riders"); migrationBuilder.DropTable(name: "FulfilmentOrders");
    }
}
