using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantLineChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantLineChannels",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficialAccountId = table.Column<string>(type: "character varying(21)", maxLength: 21, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BotUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    WelcomeMessage = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectionChecks = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    PushFallbackMonth = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    PushFallbackCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AccessToken_Ciphertext = table.Column<string>(type: "text", nullable: false),
                    AccessToken_LastFour = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AccessToken_SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChannelSecret_Ciphertext = table.Column<string>(type: "text", nullable: false),
                    ChannelSecret_LastFour = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ChannelSecret_SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantLineChannels", x => x.AssistantId);
                    table.CheckConstraint("CK_AssistantLineChannels_ConnectionCheckedAt", "(\"ConnectionCheckedAt\" IS NULL) = (\"ConnectionChecks\" = '[]'::jsonb)");
                    table.CheckConstraint("CK_AssistantLineChannels_ConnectionChecks", "jsonb_typeof(\"ConnectionChecks\") = 'array'");
                    table.CheckConstraint("CK_AssistantLineChannels_Published", "(\"State\" = 'draft') = (\"PublishedAt\" IS NULL AND \"PublishedByAccountId\" IS NULL)");
                    table.CheckConstraint("CK_AssistantLineChannels_PushFallback", "\"PushFallbackCount\" >= 0 AND (\"PushFallbackMonth\" IS NULL) = (\"PushFallbackCount\" = 0)");
                    table.CheckConstraint("CK_AssistantLineChannels_Revision", "\"Revision\" >= 1");
                    table.CheckConstraint("CK_AssistantLineChannels_TestedBeforePublished", "\"State\" = 'draft' OR \"ConnectionCheckedAt\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AssistantLineChannels_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantLineChannels_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantLineChannels_AssistantId_OrganizationId",
                table: "AssistantLineChannels",
                columns: new[] { "AssistantId", "OrganizationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantLineChannels_OrganizationId",
                table: "AssistantLineChannels",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantLineChannels");
        }
    }
}
