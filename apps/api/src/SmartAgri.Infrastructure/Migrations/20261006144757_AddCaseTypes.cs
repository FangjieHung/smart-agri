using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DefaultGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefaultDueHours = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseTypes", x => x.Id);
                    table.UniqueConstraint("AK_CaseTypes_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_CaseTypes_DefaultDueHours", "\"DefaultDueHours\" BETWEEN 1 AND 2160");
                    table.ForeignKey(
                        name: "FK_CaseTypes_CaseGroups_DefaultGroupId_OrganizationId",
                        columns: x => new { x.DefaultGroupId, x.OrganizationId },
                        principalTable: "CaseGroups",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseTypes_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseTypes_DefaultGroupId_OrganizationId",
                table: "CaseTypes",
                columns: new[] { "DefaultGroupId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseTypes_OrganizationId_Name",
                table: "CaseTypes",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseTypes");
        }
    }
}
