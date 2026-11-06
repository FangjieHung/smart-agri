using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorAuthorizationLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReauthorizationRequestedAt",
                table: "CoordinatorConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CoordinatorAuthorizationLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Service = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorAuthorizationLinks", x => x.Id);
                    table.CheckConstraint("CK_CoordinatorAuthorizationLinks_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_CoordinatorAuthorizationLinks_Service", "\"Service\" IN ('facebook', 'google')");
                    table.ForeignKey(
                        name: "FK_CoordinatorAuthorizationLinks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CoordinatorConnections_Reauthorization",
                table: "CoordinatorConnections",
                sql: "\"ReauthorizationRequestedAt\" IS NULL OR \"Status\" = 'revoked'");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorAuthorizationLinks_OrganizationId_Service",
                table: "CoordinatorAuthorizationLinks",
                columns: new[] { "OrganizationId", "Service" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorAuthorizationLinks_TokenHash",
                table: "CoordinatorAuthorizationLinks",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinatorAuthorizationLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CoordinatorConnections_Reauthorization",
                table: "CoordinatorConnections");

            migrationBuilder.DropColumn(
                name: "ReauthorizationRequestedAt",
                table: "CoordinatorConnections");
        }
    }
}
