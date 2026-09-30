using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantTestCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantTestCases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedDocumentIds = table.Column<string>(type: "jsonb", nullable: false),
                    FollowUpOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTestCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantTestCases_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantTestCases_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestCases_AssistantId_Ordinal",
                table: "AssistantTestCases",
                columns: new[] { "AssistantId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestCases_AssistantId_OrganizationId",
                table: "AssistantTestCases",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestCases_OrganizationId",
                table: "AssistantTestCases",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantTestCases");
        }
    }
}
