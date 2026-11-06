using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlineOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderingItemStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResetsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderingItemStates", x => x.Id);
                    table.CheckConstraint("CK_OrderingItemStates_ResetsAt", "(\"Kind\" = 'sold-out-today') = (\"ResetsAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_OrderingItemStates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderingMenus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderingMenus", x => x.Id);
                    table.CheckConstraint("CK_OrderingMenus_Document", "jsonb_typeof(\"Document\") = 'object'");
                    table.CheckConstraint("CK_OrderingMenus_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_OrderingMenus_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderingShops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReminderNotice = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CheckoutNotice = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TakeoutScheduled = table.Column<bool>(type: "boolean", nullable: false),
                    TakeoutAsap = table.Column<bool>(type: "boolean", nullable: false),
                    OrderingHours = table.Column<string>(type: "jsonb", nullable: false),
                    SlotMinutes = table.Column<int>(type: "integer", nullable: false),
                    DaysAhead = table.Column<int>(type: "integer", nullable: false),
                    MinLeadMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxOrdersPerSlot = table.Column<int>(type: "integer", nullable: true),
                    ShowOrderComment = table.Column<bool>(type: "boolean", nullable: false),
                    ShowTableware = table.Column<bool>(type: "boolean", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    AsapPrepBand = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderingShops", x => x.Id);
                    table.CheckConstraint("CK_OrderingShops_DaysAhead", "\"DaysAhead\" BETWEEN 1 AND 14");
                    table.CheckConstraint("CK_OrderingShops_MaxOrdersPerSlot", "\"MaxOrdersPerSlot\" IS NULL OR \"MaxOrdersPerSlot\" BETWEEN 1 AND 999");
                    table.CheckConstraint("CK_OrderingShops_MinLeadMinutes", "\"MinLeadMinutes\" BETWEEN 0 AND 1440");
                    table.CheckConstraint("CK_OrderingShops_OrderingHours", "jsonb_typeof(\"OrderingHours\") = 'object'");
                    table.CheckConstraint("CK_OrderingShops_SlotMinutes", "\"SlotMinutes\" IN (10, 15, 20, 30, 60)");
                    table.CheckConstraint("CK_OrderingShops_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_OrderingShops_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderingItemStates_OrganizationId_ItemId",
                table: "OrderingItemStates",
                columns: new[] { "OrganizationId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingMenus_OrganizationId_Version",
                table: "OrderingMenus",
                columns: new[] { "OrganizationId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingShops_OrganizationId",
                table: "OrderingShops",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingShops_Slug",
                table: "OrderingShops",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderingItemStates");

            migrationBuilder.DropTable(
                name: "OrderingMenus");

            migrationBuilder.DropTable(
                name: "OrderingShops");
        }
    }
}
