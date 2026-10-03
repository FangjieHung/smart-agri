using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Databases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TemplateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Databases", x => x.Id);
                    table.UniqueConstraint("AK_Databases_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_Databases_AspNetUsers_OwnerAccountId_OrganizationId",
                        columns: x => new { x.OwnerAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Databases_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseFormVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Fields = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseFormVersions", x => x.Id);
                    table.UniqueConstraint("AK_DatabaseFormVersions_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_DatabaseFormVersions_Fields", "jsonb_typeof(\"Fields\") = 'array'");
                    table.CheckConstraint("CK_DatabaseFormVersions_VersionNumber", "\"VersionNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_DatabaseFormVersions_Databases_DatabaseId_OrganizationId",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseFormVersions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseFormVersions_DatabaseId_OrganizationId",
                table: "DatabaseFormVersions",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseFormVersions_DatabaseId_VersionNumber",
                table: "DatabaseFormVersions",
                columns: new[] { "DatabaseId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseFormVersions_OrganizationId",
                table: "DatabaseFormVersions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Databases_OrganizationId",
                table: "Databases",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Databases_OwnerAccountId_OrganizationId",
                table: "Databases",
                columns: new[] { "OwnerAccountId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseFormVersions");

            migrationBuilder.DropTable(
                name: "Databases");
        }
    }
}
