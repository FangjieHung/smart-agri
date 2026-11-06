using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderingExpiryAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PendingTimeoutMinutes",
                table: "OrderingShops",
                type: "integer",
                nullable: false,
                // The import's default (#400): existing shops wait 15 minutes until they set otherwise.
                defaultValue: 15);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersonalDataClearedAt",
                table: "OrderingOrders",
                type: "timestamp with time zone",
                nullable: true);

            // Orders from before #400. Not the hours-aware rule (OrderingOrder.ExpiryFor needs the shop's weekly hours and
            // time zone, which SQL cannot read): 15 minutes after they were sent, at the latest their pickup — the default
            // timeout from the moment they were sent. Each still pending one gets its expiry job now, so none stays pending
            // forever; one already past its time expires on the job queue's next run (no shop has taken real orders yet).
            migrationBuilder.Sql(
                """
                UPDATE "OrderingOrders" SET "ExpiresAt" = LEAST("CreatedAt" + INTERVAL '15 minutes', "PickupAt");
                INSERT INTO "BackgroundJobs" ("Id", "OrganizationId", "Kind", "Payload", "Status", "Attempts", "MaxAttempts", "RunAfter", "CreatedAt")
                SELECT gen_random_uuid(), "OrganizationId", 'ordering.expire-order', jsonb_build_object('orderId', "Id"), 'queued', 0, 5,
                       "ExpiresAt", now()
                FROM "OrderingOrders" WHERE "Status" = 'pending';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingShops_PendingTimeoutMinutes",
                table: "OrderingShops",
                sql: "\"PendingTimeoutMinutes\" BETWEEN 5 AND 120");

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_PersonalDataClearedAt_PickupAt",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "PersonalDataClearedAt", "PickupAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_PersonalData",
                table: "OrderingOrders",
                sql: "\"PersonalDataClearedAt\" IS NULL OR (\"CustomerName\" = '' AND \"CustomerPhone\" = '' AND \"CustomerEmail\" IS NULL AND \"OrderComment\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "BackgroundJobs" WHERE "Kind" IN ('ordering.expire-order', 'ordering.order-email');""");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingShops_PendingTimeoutMinutes",
                table: "OrderingShops");

            migrationBuilder.DropIndex(
                name: "IX_OrderingOrders_OrganizationId_PersonalDataClearedAt_PickupAt",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_PersonalData",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "PendingTimeoutMinutes",
                table: "OrderingShops");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "PersonalDataClearedAt",
                table: "OrderingOrders");
        }
    }
}
