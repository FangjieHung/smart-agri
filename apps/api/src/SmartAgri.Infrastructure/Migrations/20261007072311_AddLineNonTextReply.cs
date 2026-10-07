using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLineNonTextReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every channel saved before #291 replied with the fixed text this default keeps
            // (AssistantLineChannel.DefaultNonTextReply). The column keeps the default; the app
            // always writes the owner's value.
            migrationBuilder.AddColumn<string>(
                name: "NonTextReply",
                table: "AssistantLineChannels",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "目前只能回答文字問題。");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NonTextReply",
                table: "AssistantLineChannels");
        }
    }
}
