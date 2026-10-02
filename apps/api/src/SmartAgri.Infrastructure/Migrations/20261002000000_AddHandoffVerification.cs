using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartAgri.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261002000000_AddHandoffVerification")]
public sealed class AddHandoffVerification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(
            name: "HandoffUnverified", table: "AssistantIssues", type: "boolean",
            nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "HandoffUnverified", table: "AssistantIssues");
}
