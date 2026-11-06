using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderingOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderingOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AsapMinMinutes = table.Column<int>(type: "integer", nullable: true),
                    AsapMaxMinutes = table.Column<int>(type: "integer", nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CustomerPhone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CustomerEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    OrderComment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Tableware = table.Column<bool>(type: "boolean", nullable: true),
                    Lines = table.Column<string>(type: "jsonb", nullable: false),
                    Subtotal = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    MenuVersion = table.Column<int>(type: "integer", nullable: false),
                    StatusTokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SubmissionKey = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderingOrders", x => x.Id);
                    table.CheckConstraint("CK_OrderingOrders_Asap", "(\"ServiceType\" = 'takeout-asap') = (\"AsapMinMinutes\" IS NOT NULL AND \"AsapMaxMinutes\" IS NOT NULL)");
                    table.CheckConstraint("CK_OrderingOrders_CancelledAt", "(\"Status\" = 'cancelled') = (\"CancelledAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_OrderingOrders_Lines", "jsonb_typeof(\"Lines\") = 'array'");
                    table.CheckConstraint("CK_OrderingOrders_Number", "\"Number\" >= 1");
                    table.CheckConstraint("CK_OrderingOrders_Total", "\"Subtotal\" >= 0 AND \"Total\" >= \"Subtotal\"");
                    table.CheckConstraint("CK_OrderingOrders_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_OrderingOrders_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_BusinessDate_Number",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "BusinessDate", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_CustomerPhone_Status",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "CustomerPhone", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_PickupAt",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "PickupAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_OrganizationId_SubmissionKey",
                table: "OrderingOrders",
                columns: new[] { "OrganizationId", "SubmissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderingOrders_StatusTokenHash",
                table: "OrderingOrders",
                column: "StatusTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderingOrders");
        }
    }
}
