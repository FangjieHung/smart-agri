using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationMonthlyTokenLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MonthlyTokenLimit",
                table: "Organizations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Organizations_MonthlyTokenLimit",
                table: "Organizations",
                sql: "\"MonthlyTokenLimit\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Organizations_MonthlyTokenLimit",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "MonthlyTokenLimit",
                table: "Organizations");
        }
    }
}
