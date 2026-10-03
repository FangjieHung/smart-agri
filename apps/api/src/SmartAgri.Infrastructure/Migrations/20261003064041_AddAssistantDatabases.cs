using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantDatabases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FormDatabaseId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantDatabases",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CollectsForms = table.Column<bool>(type: "boolean", nullable: false),
                    CollectionPurpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantDatabases", x => new { x.AssistantId, x.DatabaseId });
                    table.CheckConstraint("CK_AssistantDatabases_CollectionPurpose", "(\"CollectsForms\" AND length(btrim(\"CollectionPurpose\")) > 0) OR (NOT \"CollectsForms\" AND \"CollectionPurpose\" = '')");
                    table.ForeignKey(
                        name: "FK_AssistantDatabases_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantDatabases_Databases_DatabaseId_OrganizationId",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantDatabases_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDatabases_AssistantId_CollectsForms",
                table: "AssistantDatabases",
                column: "AssistantId",
                unique: true,
                filter: "\"CollectsForms\"");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDatabases_AssistantId_OrganizationId",
                table: "AssistantDatabases",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDatabases_DatabaseId_OrganizationId",
                table: "AssistantDatabases",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDatabases_OrganizationId",
                table: "AssistantDatabases",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantDatabases");

            migrationBuilder.DropColumn(
                name: "FormDatabaseId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "SubmissionId",
                table: "ChatMessages");
        }
    }
}
