using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseDataManagers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseDataManagerChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Assigned = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseDataManagerChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseDataManagerChanges_Databases_DatabaseId_Organizatio~",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseDataManagerChanges_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseDataManagers",
                columns: table => new
                {
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseDataManagers", x => new { x.DatabaseId, x.AccountId });
                    table.ForeignKey(
                        name: "FK_DatabaseDataManagers_AspNetUsers_AccountId_OrganizationId",
                        columns: x => new { x.AccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseDataManagers_Databases_DatabaseId_OrganizationId",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseDataManagers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagerChanges_DatabaseId_ChangedAt",
                table: "DatabaseDataManagerChanges",
                columns: new[] { "DatabaseId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagerChanges_DatabaseId_OrganizationId",
                table: "DatabaseDataManagerChanges",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagerChanges_OrganizationId",
                table: "DatabaseDataManagerChanges",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagers_AccountId",
                table: "DatabaseDataManagers",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagers_AccountId_OrganizationId",
                table: "DatabaseDataManagers",
                columns: new[] { "AccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagers_DatabaseId_OrganizationId",
                table: "DatabaseDataManagers",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseDataManagers_OrganizationId",
                table: "DatabaseDataManagers",
                column: "OrganizationId");

            // Databases created before data managers existed get the same starting point as new
            // ones: the owner is the one designated data manager (the owner still reads nothing
            // without read-consented-submissions).
            migrationBuilder.Sql(
                """
                INSERT INTO "DatabaseDataManagers" ("DatabaseId", "AccountId", "OrganizationId", "AssignedByAccountId", "AssignedAt")
                SELECT "Id", "OwnerAccountId", "OrganizationId", "OwnerAccountId", "CreatedAt" FROM "Databases";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseDataManagerChanges");

            migrationBuilder.DropTable(
                name: "DatabaseDataManagers");
        }
    }
}
