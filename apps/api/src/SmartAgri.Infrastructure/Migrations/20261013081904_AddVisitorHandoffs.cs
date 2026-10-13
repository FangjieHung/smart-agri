using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorHandoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountNotificationSettings",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    HandoffEmails = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountNotificationSettings", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_AccountNotificationSettings_AspNetUsers_AccountId_Organizat~",
                        columns: x => new { x.AccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountNotificationSettings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisitorHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HandledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HandledByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmailSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitorHandoffs", x => x.Id);
                    table.CheckConstraint("CK_VisitorHandoffs_Handled", "(\"HandledAt\" IS NULL) = (\"HandledByAccountId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_VisitorHandoffs_AspNetUsers_HandledByAccountId_Organization~",
                        columns: x => new { x.HandledByAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisitorHandoffs_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VisitorHandoffs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountNotificationSettings_AccountId_OrganizationId",
                table: "AccountNotificationSettings",
                columns: new[] { "AccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountNotificationSettings_OrganizationId",
                table: "AccountNotificationSettings",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitorHandoffs_AssistantId_HandledAt_CreatedAt",
                table: "VisitorHandoffs",
                columns: new[] { "AssistantId", "HandledAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorHandoffs_AssistantId_OrganizationId",
                table: "VisitorHandoffs",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorHandoffs_HandledByAccountId_OrganizationId",
                table: "VisitorHandoffs",
                columns: new[] { "HandledByAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorHandoffs_OrganizationId",
                table: "VisitorHandoffs",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountNotificationSettings");

            migrationBuilder.DropTable(
                name: "VisitorHandoffs");
        }
    }
}
