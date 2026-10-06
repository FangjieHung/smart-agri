using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantWebsiteChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantWebsiteChannels",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WelcomeMessage = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BrandColor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Position = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantWebsiteChannels", x => x.AssistantId);
                    table.UniqueConstraint("AK_AssistantWebsiteChannels_AssistantId_OrganizationId", x => new { x.AssistantId, x.OrganizationId });
                    table.CheckConstraint("CK_AssistantWebsiteChannels_Published", "(\"State\" = 'draft') = (\"PublishedAt\" IS NULL AND \"PublishedByAccountId\" IS NULL)");
                    table.CheckConstraint("CK_AssistantWebsiteChannels_Revision", "\"Revision\" >= 1");
                    table.ForeignKey(
                        name: "FK_AssistantWebsiteChannels_Assistants_AssistantId_Organizatio~",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantWebsiteChannels_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantWebsiteDomains",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantWebsiteDomains", x => new { x.AssistantId, x.Domain });
                    table.CheckConstraint("CK_AssistantWebsiteDomains_Domain", "\"Domain\" = lower(\"Domain\") AND length(\"Domain\") > 0");
                    table.ForeignKey(
                        name: "FK_AssistantWebsiteDomains_AssistantWebsiteChannels_AssistantI~",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "AssistantWebsiteChannels",
                        principalColumns: new[] { "AssistantId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantWebsiteDomains_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantWebsiteChannels_OrganizationId",
                table: "AssistantWebsiteChannels",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantWebsiteDomains_AssistantId_OrganizationId",
                table: "AssistantWebsiteDomains",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantWebsiteDomains_OrganizationId",
                table: "AssistantWebsiteDomains",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantWebsiteDomains");

            migrationBuilder.DropTable(
                name: "AssistantWebsiteChannels");
        }
    }
}
