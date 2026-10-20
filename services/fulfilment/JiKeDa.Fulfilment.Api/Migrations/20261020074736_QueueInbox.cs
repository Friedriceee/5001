using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiKeDa.Fulfilment.Api.Migrations;

// Existing migrations were authored without a model snapshot. The generated full model
// must not recreate existing tables: this additive migration creates only the new inbox.
public partial class QueueInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProcessedOrderEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false),
                ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ProcessedOrderEvents", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ProcessedOrderEvents");
}
