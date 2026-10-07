using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Who the assistant is meant for (#224). Every assistant created before this was
            // organization-internal (M3 only accepted `account-members`), so existing rows get that.
            migrationBuilder.AddColumn<string>(
                name: "Audience",
                table: "Assistants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "account-members");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Audience",
                table: "Assistants");
        }
    }
}
