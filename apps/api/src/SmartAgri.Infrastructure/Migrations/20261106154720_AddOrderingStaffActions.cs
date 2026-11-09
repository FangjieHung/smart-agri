using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderingStaffActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedByAccountId",
                table: "OrderingOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelNote",
                table: "OrderingOrders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "OrderingOrders",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledBy",
                table: "OrderingOrders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByAccountId",
                table: "OrderingOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompletedByAccountId",
                table: "OrderingOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclineNote",
                table: "OrderingOrders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclineReason",
                table: "OrderingOrders",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeclinedAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeclinedByAccountId",
                table: "OrderingOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PromisedAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadyAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReadyByAccountId",
                table: "OrderingOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Orders from before #399: their last change is when they were sent or the customer cancelled them.
            migrationBuilder.Sql(
                """
                UPDATE "OrderingOrders" SET "StatusChangedAt" = COALESCE("CancelledAt", "CreatedAt");
                UPDATE "OrderingOrders" SET "CancelledBy" = 'customer' WHERE "Status" = 'cancelled';
                """);

            // manage-orders by default for the owners and internal employees already there (#399); new members get what
            // the team panel gives them, and setup's first administrator gets every permission.
            migrationBuilder.Sql(
                """
                INSERT INTO "AccountPermissions" ("AccountId", "Permission", "OrganizationId")
                SELECT "Id", 'manage-orders', "OrganizationId" FROM "AspNetUsers"
                WHERE "Role" IN ('smb-admin', 'internal-employee')
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_Status_StatusChangedAt",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "Status", "StatusChangedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_Accepted",
                table: "OrderingOrders",
                sql: "(\"AcceptedAt\" IS NOT NULL) = (\"PromisedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_CancelledBy",
                table: "OrderingOrders",
                sql: "(\"Status\" = 'cancelled') = (\"CancelledBy\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_CancelReason",
                table: "OrderingOrders",
                sql: "COALESCE(\"CancelledBy\" = 'shop', false) = (\"CancelReason\" IS NOT NULL AND \"CancelledByAccountId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_Declined",
                table: "OrderingOrders",
                sql: "(\"Status\" = 'declined') = (\"DeclineReason\" IS NOT NULL AND \"DeclinedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "AccountPermissions" WHERE "Permission" = 'manage-orders';""");

            migrationBuilder.DropIndex(
                name: "IX_OrderingOrders_OrganizationId_Status_StatusChangedAt",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_Accepted",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_CancelledBy",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_CancelReason",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_Declined",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "AcceptedByAccountId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "CancelNote",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "CancelledByAccountId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "CompletedByAccountId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeclineNote",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeclineReason",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeclinedAt",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeclinedByAccountId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "PromisedAt",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "ReadyAt",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "ReadyByAccountId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "OrderingOrders");
        }
    }
}
