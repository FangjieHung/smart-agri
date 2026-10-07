using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueOpenCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cases_AssistantIssueId",
                table: "Cases");

            migrationBuilder.AddColumn<Guid>(
                name: "LinkedCaseId",
                table: "AssistantIssues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionKind",
                table: "AssistantIssues",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Every issue resolved before M7-7 was resolved by fixing the assistant.
            migrationBuilder.Sql("UPDATE \"AssistantIssues\" SET \"ResolutionKind\" = 'fixed' WHERE \"Status\" = 'resolved';");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_AssistantIssueId",
                table: "Cases",
                column: "AssistantIssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_LinkedCaseId_OrganizationId",
                table: "AssistantIssues",
                columns: new[] { "LinkedCaseId", "OrganizationId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantIssues_LinkedCaseId",
                table: "AssistantIssues",
                sql: "COALESCE(\"ResolutionKind\" = 'not-assistant-issue', FALSE) = (\"LinkedCaseId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantIssues_ResolutionKind",
                table: "AssistantIssues",
                sql: "(\"Status\" = 'resolved') = (\"ResolutionKind\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantIssues_Cases_LinkedCaseId_OrganizationId",
                table: "AssistantIssues",
                columns: new[] { "LinkedCaseId", "OrganizationId" },
                principalTable: "Cases",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantIssues_Cases_LinkedCaseId_OrganizationId",
                table: "AssistantIssues");

            migrationBuilder.DropIndex(
                name: "IX_Cases_AssistantIssueId",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_AssistantIssues_LinkedCaseId_OrganizationId",
                table: "AssistantIssues");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantIssues_LinkedCaseId",
                table: "AssistantIssues");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantIssues_ResolutionKind",
                table: "AssistantIssues");

            migrationBuilder.DropColumn(
                name: "LinkedCaseId",
                table: "AssistantIssues");

            migrationBuilder.DropColumn(
                name: "ResolutionKind",
                table: "AssistantIssues");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_AssistantIssueId",
                table: "Cases",
                column: "AssistantIssueId");
        }
    }
}
