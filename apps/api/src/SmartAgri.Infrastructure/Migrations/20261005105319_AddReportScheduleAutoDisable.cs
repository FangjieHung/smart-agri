using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReportScheduleAutoDisable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AutoDisabledAt",
                table: "AssistantReportSchedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoDisabledReason",
                table: "AssistantReportSchedules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveSkips",
                table: "AssistantReportSchedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantReportSchedules_AutoDisabled",
                table: "AssistantReportSchedules",
                sql: "(\"AutoDisabledAt\" IS NULL) = (\"AutoDisabledReason\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantReportSchedules_ConsecutiveSkips",
                table: "AssistantReportSchedules",
                sql: "\"ConsecutiveSkips\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantReportSchedules_AutoDisabled",
                table: "AssistantReportSchedules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantReportSchedules_ConsecutiveSkips",
                table: "AssistantReportSchedules");

            migrationBuilder.DropColumn(
                name: "AutoDisabledAt",
                table: "AssistantReportSchedules");

            migrationBuilder.DropColumn(
                name: "AutoDisabledReason",
                table: "AssistantReportSchedules");

            migrationBuilder.DropColumn(
                name: "ConsecutiveSkips",
                table: "AssistantReportSchedules");
        }
    }
}
