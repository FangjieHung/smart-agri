using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderingDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_Asap",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_PersonalData",
                table: "OrderingOrders");

            migrationBuilder.AddColumn<bool>(
                name: "Delivery",
                table: "OrderingShops",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DeliveryAsap",
                table: "OrderingShops",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DeliverySettings",
                table: "OrderingShops",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddress",
                table: "OrderingOrders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryFee",
                table: "OrderingOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryNote",
                table: "OrderingOrders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryZoneId",
                table: "OrderingOrders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryZoneName",
                table: "OrderingOrders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingShops_Delivery",
                table: "OrderingShops",
                sql: "(NOT \"Delivery\" OR \"DeliverySettings\" IS NOT NULL) AND (NOT \"DeliveryAsap\" OR \"Delivery\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingShops_DeliverySettings",
                table: "OrderingShops",
                sql: "\"DeliverySettings\" IS NULL OR jsonb_typeof(\"DeliverySettings\") = 'object'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_Asap",
                table: "OrderingOrders",
                sql: "(\"ServiceType\" IN ('takeout-asap', 'delivery-asap')) = (\"AsapMinMinutes\" IS NOT NULL AND \"AsapMaxMinutes\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_Delivery",
                table: "OrderingOrders",
                sql: "(\"ServiceType\" IN ('delivery-scheduled', 'delivery-asap')) = (\"DeliveryZoneId\" IS NOT NULL AND \"DeliveryZoneName\" IS NOT NULL AND \"DeliveryAddress\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_DeliveryFee",
                table: "OrderingOrders",
                sql: "\"DeliveryFee\" >= 0 AND \"Total\" = \"Subtotal\" + \"DeliveryFee\" AND (\"DeliveryFee\" = 0 OR \"ServiceType\" IN ('delivery-scheduled', 'delivery-asap'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_PersonalData",
                table: "OrderingOrders",
                sql: "\"PersonalDataClearedAt\" IS NULL OR (\"CustomerName\" = '' AND \"CustomerPhone\" = '' AND \"CustomerEmail\" IS NULL AND \"OrderComment\" IS NULL AND COALESCE(\"DeliveryAddress\", '') = '' AND \"DeliveryNote\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingShops_Delivery",
                table: "OrderingShops");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingShops_DeliverySettings",
                table: "OrderingShops");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_Asap",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_Delivery",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_DeliveryFee",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_PersonalData",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "Delivery",
                table: "OrderingShops");

            migrationBuilder.DropColumn(
                name: "DeliveryAsap",
                table: "OrderingShops");

            migrationBuilder.DropColumn(
                name: "DeliverySettings",
                table: "OrderingShops");

            migrationBuilder.DropColumn(
                name: "DeliveryAddress",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeliveryNote",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeliveryZoneId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DeliveryZoneName",
                table: "OrderingOrders");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_Asap",
                table: "OrderingOrders",
                sql: "(\"ServiceType\" = 'takeout-asap') = (\"AsapMinMinutes\" IS NOT NULL AND \"AsapMaxMinutes\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_PersonalData",
                table: "OrderingOrders",
                sql: "\"PersonalDataClearedAt\" IS NULL OR (\"CustomerName\" = '' AND \"CustomerPhone\" = '' AND \"CustomerEmail\" IS NULL AND \"OrderComment\" IS NULL)");
        }
    }
}
