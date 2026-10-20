using JiKeDa.Fulfilment.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiKeDa.Fulfilment.Api.Migrations;

[DbContext(typeof(FulfilmentDbContext))]
[Migration("20261020100000_AddRiderContact")]
public partial class AddRiderContact : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PhoneNumber",
            table: "Riders",
            type: "character varying(11)",
            maxLength: 11,
            nullable: true,
            defaultValue: "13800000000");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "PhoneNumber", table: "Riders");
}
