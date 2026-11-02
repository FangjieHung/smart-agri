using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoordinatorCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByLineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scenario = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    AnnouncementText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByLineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReopensCommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrectionBroadcast = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorCommands", x => x.Id);
                    table.UniqueConstraint("AK_CoordinatorCommands_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_CoordinatorCommands_Confirmed", "(\"Status\" = 'confirmed') = (\"ConfirmedAt\" IS NOT NULL) AND (\"ConfirmedAt\" IS NULL) = (\"ConfirmedByLineUserId\" IS NULL)");
                    table.CheckConstraint("CK_CoordinatorCommands_Details", "\"Status\" IN ('awaiting-details', 'cancelled') OR (\"Date\" IS NOT NULL AND (\"Time\" IS NOT NULL) = (\"Scenario\" IN ('close-early', 'open-late')))");
                    table.CheckConstraint("CK_CoordinatorCommands_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_CoordinatorCommands_Reopen", "(\"Scenario\" = 'reopen') = (\"ReopensCommandId\" IS NOT NULL) AND (\"Scenario\" = 'reopen' OR NOT \"CorrectionBroadcast\")");
                    table.CheckConstraint("CK_CoordinatorCommands_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_CoordinatorCommands_CoordinatorCommands_ReopensCommandId_Or~",
                        columns: x => new { x.ReopensCommandId, x.OrganizationId },
                        principalTable: "CoordinatorCommands",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoordinatorCommands_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommands_OrganizationId_RequestedByLineUserId_St~",
                table: "CoordinatorCommands",
                columns: new[] { "OrganizationId", "RequestedByLineUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommands_OrganizationId_Status_Date",
                table: "CoordinatorCommands",
                columns: new[] { "OrganizationId", "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommands_ReopensCommandId",
                table: "CoordinatorCommands",
                column: "ReopensCommandId",
                unique: true,
                filter: "\"Status\" = 'confirmed'");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommands_ReopensCommandId_OrganizationId",
                table: "CoordinatorCommands",
                columns: new[] { "ReopensCommandId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinatorCommands");
        }
    }
}
