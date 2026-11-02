using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreCoordinatorBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoordinatorBindingCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedByLineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IssuedByLineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorBindingCodes", x => x.Id);
                    table.CheckConstraint("CK_CoordinatorBindingCodes_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_CoordinatorBindingCodes_Used", "(\"UsedAt\" IS NULL) = (\"UsedByLineUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CoordinatorBindingCodes_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CoordinatorMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BoundAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoordinatorMembers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorBindingCodes_CodeHash",
                table: "CoordinatorBindingCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorBindingCodes_OrganizationId",
                table: "CoordinatorBindingCodes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorMembers_LineUserId",
                table: "CoordinatorMembers",
                column: "LineUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorMembers_OrganizationId_LineUserId",
                table: "CoordinatorMembers",
                columns: new[] { "OrganizationId", "LineUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinatorBindingCodes");

            migrationBuilder.DropTable(
                name: "CoordinatorMembers");
        }
    }
}
