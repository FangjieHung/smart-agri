using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Assistants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TemplateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Tone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RoleInstructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    KnowledgeScope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RefusalMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ShowCitations = table.Column<bool>(type: "boolean", nullable: false),
                    KeepConversations = table.Column<bool>(type: "boolean", nullable: false),
                    MinScore = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assistants", x => x.Id);
                    table.UniqueConstraint("AK_Assistants_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_Assistants_AspNetUsers_OwnerAccountId_OrganizationId",
                        columns: x => new { x.OwnerAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assistants_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantKnowledgeBases",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantKnowledgeBases", x => new { x.AssistantId, x.KnowledgeBaseId });
                    table.ForeignKey(
                        name: "FK_AssistantKnowledgeBases_Assistants_AssistantId_Organization~",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantKnowledgeBases_KnowledgeBases_KnowledgeBaseId_Orga~",
                        columns: x => new { x.KnowledgeBaseId, x.OrganizationId },
                        principalTable: "KnowledgeBases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantKnowledgeBases_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantKnowledgeBases_AssistantId_OrganizationId",
                table: "AssistantKnowledgeBases",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantKnowledgeBases_KnowledgeBaseId_OrganizationId",
                table: "AssistantKnowledgeBases",
                columns: new[] { "KnowledgeBaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantKnowledgeBases_OrganizationId",
                table: "AssistantKnowledgeBases",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_OrganizationId",
                table: "Assistants",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_OwnerAccountId_OrganizationId",
                table: "Assistants",
                columns: new[] { "OwnerAccountId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantKnowledgeBases");

            migrationBuilder.DropTable(
                name: "Assistants");
        }
    }
}
