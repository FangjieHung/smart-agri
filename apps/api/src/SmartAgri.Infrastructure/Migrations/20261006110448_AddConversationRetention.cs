using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatThreads_OrganizationId",
                table: "ChatThreads");

            migrationBuilder.AddColumn<int>(
                name: "PendingRetentionDays",
                table: "Organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingRetentionEffectiveAt",
                table: "Organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionCleanupNextRunAt",
                table: "Organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "Organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Organizations_PendingRetention",
                table: "Organizations",
                sql: "(\"PendingRetentionDays\" IS NULL) = (\"PendingRetentionEffectiveAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Organizations_RetentionDays",
                table: "Organizations",
                sql: "\"RetentionDays\" > 0 AND (\"PendingRetentionDays\" IS NULL OR \"PendingRetentionDays\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_OrganizationId_LastActivityAt",
                table: "ChatThreads",
                columns: new[] { "OrganizationId", "LastActivityAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Organizations_PendingRetention",
                table: "Organizations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Organizations_RetentionDays",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_ChatThreads_OrganizationId_LastActivityAt",
                table: "ChatThreads");

            migrationBuilder.DropColumn(
                name: "PendingRetentionDays",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "PendingRetentionEffectiveAt",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "RetentionCleanupNextRunAt",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                table: "Organizations");

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_OrganizationId",
                table: "ChatThreads",
                column: "OrganizationId");
        }
    }
}
