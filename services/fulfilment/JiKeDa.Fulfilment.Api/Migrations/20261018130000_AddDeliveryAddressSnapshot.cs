using JiKeDa.Fulfilment.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiKeDa.Fulfilment.Api.Migrations;

[DbContext(typeof(FulfilmentDbContext))]
[Migration("20261018130000_AddDeliveryAddressSnapshot")]
public sealed class AddDeliveryAddressSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ContactName", "FulfilmentOrders", type: "character varying(50)", maxLength: 50, nullable: true);
        migrationBuilder.AddColumn<string>("PhoneNumber", "FulfilmentOrders", type: "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<string>("DeliveryAddress", "FulfilmentOrders", type: "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("HouseNumber", "FulfilmentOrders", type: "character varying(50)", maxLength: 50, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ContactName", "FulfilmentOrders");
        migrationBuilder.DropColumn("PhoneNumber", "FulfilmentOrders");
        migrationBuilder.DropColumn("DeliveryAddress", "FulfilmentOrders");
        migrationBuilder.DropColumn("HouseNumber", "FulfilmentOrders");
    }
}
