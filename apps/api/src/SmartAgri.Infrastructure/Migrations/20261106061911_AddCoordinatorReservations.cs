using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReservationSlug",
                table: "CoordinatorShopSettings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reservations",
                table: "CoordinatorShopSettings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CoordinatorReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PartySize = table.Column<int>(type: "integer", nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustomerPhone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CustomerEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SubmissionKey = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HandledByMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    HandledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CancelledByCommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    MembersNotified = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorReservations", x => x.Id);
                    table.CheckConstraint("CK_CoordinatorReservations_Cancelled", "(\"Status\" = 'cancelled') = (\"CancelReason\" IS NOT NULL) AND (\"CancelledByCommandId\" IS NULL OR \"Status\" = 'cancelled') AND (\"CancelReason\" IS NULL OR \"CancelReason\" IN ('temporary-closure', 'close-early', 'open-late'))");
                    table.CheckConstraint("CK_CoordinatorReservations_Handled", "(\"HandledByMemberId\" IS NULL) = (\"HandledAt\" IS NULL) AND (\"Status\" NOT IN ('accepted', 'declined') OR \"HandledAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_CoordinatorReservations_MembersNotified", "\"MembersNotified\" IN ('pending', 'sent', 'not-sent')");
                    table.CheckConstraint("CK_CoordinatorReservations_PartySize", "\"PartySize\" >= 1");
                    table.CheckConstraint("CK_CoordinatorReservations_Status", "\"Status\" IN ('pending', 'accepted', 'declined', 'cancelled', 'expired')");
                    table.CheckConstraint("CK_CoordinatorReservations_StatusChanged", "(\"Status\" = 'pending') = (\"StatusChangedAt\" IS NULL)");
                    table.CheckConstraint("CK_CoordinatorReservations_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_CoordinatorReservations_CoordinatorCommands_CancelledByComm~",
                        columns: x => new { x.CancelledByCommandId, x.OrganizationId },
                        principalTable: "CoordinatorCommands",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoordinatorReservations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorShopSettings_ReservationSlug",
                table: "CoordinatorShopSettings",
                column: "ReservationSlug",
                unique: true,
                filter: "\"ReservationSlug\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CoordinatorShopSettings_Reservations",
                table: "CoordinatorShopSettings",
                sql: "\"Reservations\" IS NULL OR jsonb_typeof(\"Reservations\") = 'object'");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorReservations_CancelledByCommandId_OrganizationId",
                table: "CoordinatorReservations",
                columns: new[] { "CancelledByCommandId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorReservations_OrganizationId_CustomerPhone_Status",
                table: "CoordinatorReservations",
                columns: new[] { "OrganizationId", "CustomerPhone", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorReservations_OrganizationId_Date",
                table: "CoordinatorReservations",
                columns: new[] { "OrganizationId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorReservations_OrganizationId_ReservedAt",
                table: "CoordinatorReservations",
                columns: new[] { "OrganizationId", "ReservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorReservations_SubmissionKey",
                table: "CoordinatorReservations",
                column: "SubmissionKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinatorReservations");

            migrationBuilder.DropIndex(
                name: "IX_CoordinatorShopSettings_ReservationSlug",
                table: "CoordinatorShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CoordinatorShopSettings_Reservations",
                table: "CoordinatorShopSettings");

            migrationBuilder.DropColumn(
                name: "ReservationSlug",
                table: "CoordinatorShopSettings");

            migrationBuilder.DropColumn(
                name: "Reservations",
                table: "CoordinatorShopSettings");
        }
    }
}
