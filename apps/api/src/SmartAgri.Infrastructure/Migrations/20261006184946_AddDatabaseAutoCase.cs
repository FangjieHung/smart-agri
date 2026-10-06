using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseAutoCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AutoCaseTypeId",
                table: "Databases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Databases_AutoCaseTypeId_OrganizationId",
                table: "Databases",
                columns: new[] { "AutoCaseTypeId", "OrganizationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Databases_CaseTypes_AutoCaseTypeId_OrganizationId",
                table: "Databases",
                columns: new[] { "AutoCaseTypeId", "OrganizationId" },
                principalTable: "CaseTypes",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Databases_CaseTypes_AutoCaseTypeId_OrganizationId",
                table: "Databases");

            migrationBuilder.DropIndex(
                name: "IX_Databases_AutoCaseTypeId_OrganizationId",
                table: "Databases");

            migrationBuilder.DropColumn(
                name: "AutoCaseTypeId",
                table: "Databases");
        }
    }
}
