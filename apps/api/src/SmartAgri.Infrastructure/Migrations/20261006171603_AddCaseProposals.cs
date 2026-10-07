using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaseProposal",
                table: "ChatMessages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProposedCaseId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantCaseTypes",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantCaseTypes", x => new { x.AssistantId, x.CaseTypeId });
                    table.ForeignKey(
                        name: "FK_AssistantCaseTypes_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantCaseTypes_CaseTypes_CaseTypeId_OrganizationId",
                        columns: x => new { x.CaseTypeId, x.OrganizationId },
                        principalTable: "CaseTypes",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantCaseTypes_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantCaseTypes_AssistantId_OrganizationId",
                table: "AssistantCaseTypes",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantCaseTypes_CaseTypeId_OrganizationId",
                table: "AssistantCaseTypes",
                columns: new[] { "CaseTypeId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantCaseTypes_OrganizationId",
                table: "AssistantCaseTypes",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantCaseTypes");

            migrationBuilder.DropColumn(
                name: "CaseProposal",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ProposedCaseId",
                table: "ChatMessages");
        }
    }
}
