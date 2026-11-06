using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderingDineIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DineIn",
                table: "OrderingShops",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DineInSettings",
                table: "OrderingShops",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoAccepted",
                table: "OrderingOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TableId",
                table: "OrderingOrders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableName",
                table: "OrderingOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TableSessionId",
                table: "OrderingOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderingTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Token = table.Column<string>(type: "character(43)", fixedLength: true, maxLength: 43, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TokenIssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderingTables", x => x.Id);
                    table.CheckConstraint("CK_OrderingTables_Token", "length(\"Token\") = 43");
                    table.ForeignKey(
                        name: "FK_OrderingTables_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderingTableSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TableName = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderingTableSessions", x => x.Id);
                    table.CheckConstraint("CK_OrderingTableSessions_Closed", "(\"ClosedAt\" IS NULL) = (\"ClosedByAccountId\" IS NULL)");
                    table.CheckConstraint("CK_OrderingTableSessions_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_OrderingTableSessions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingShops_DineIn",
                table: "OrderingShops",
                sql: "(NOT \"DineIn\" OR \"DineInSettings\" IS NOT NULL) AND (\"DineInSettings\" IS NULL OR jsonb_typeof(\"DineInSettings\") = 'object')");

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_TableId_Status",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "TableId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_TableSessionId",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "TableSessionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_AutoAccepted",
                table: "OrderingOrders",
                sql: "NOT \"AutoAccepted\" OR (\"ServiceType\" = 'dine-in' AND \"AcceptedAt\" IS NOT NULL AND \"AcceptedByAccountId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderingOrders_DineIn",
                table: "OrderingOrders",
                sql: "(\"ServiceType\" = 'dine-in') = (\"TableId\" IS NOT NULL AND \"TableName\" IS NOT NULL AND \"TableSessionId\" IS NOT NULL) AND (\"ServiceType\" <> 'dine-in' OR (\"CustomerPhone\" = '' AND \"CustomerEmail\" IS NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_OrderingTables_OrganizationId_TableId_Active",
                table: "OrderingTables",
                columns: new[] { "OrganizationId", "TableId" },
                unique: true,
                filter: "\"DeactivatedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrderingTables_Token",
                table: "OrderingTables",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingTableSessions_OrganizationId_TableId_Open",
                table: "OrderingTableSessions",
                columns: new[] { "OrganizationId", "TableId" },
                unique: true,
                filter: "\"ClosedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrderingTableSessions_OrganizationId_UpdatedAt",
                table: "OrderingTableSessions",
                columns: new[] { "OrganizationId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderingTables");

            migrationBuilder.DropTable(
                name: "OrderingTableSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingShops_DineIn",
                table: "OrderingShops");

            migrationBuilder.DropIndex(
                name: "IX_OrderingOrders_OrganizationId_TableId_Status",
                table: "OrderingOrders");

            migrationBuilder.DropIndex(
                name: "IX_OrderingOrders_OrganizationId_TableSessionId",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_AutoAccepted",
                table: "OrderingOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderingOrders_DineIn",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "DineIn",
                table: "OrderingShops");

            migrationBuilder.DropColumn(
                name: "DineInSettings",
                table: "OrderingShops");

            migrationBuilder.DropColumn(
                name: "AutoAccepted",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "TableId",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "TableName",
                table: "OrderingOrders");

            migrationBuilder.DropColumn(
                name: "TableSessionId",
                table: "OrderingOrders");
        }
    }
}
