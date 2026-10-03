using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsentTerms = table.Column<string>(type: "jsonb", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseSubmissions", x => x.Id);
                    table.UniqueConstraint("AK_DatabaseSubmissions_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_DatabaseSubmissions_ConsentTerms", "jsonb_typeof(\"ConsentTerms\") = 'object'");
                    table.CheckConstraint("CK_DatabaseSubmissions_FormVersionNumber", "\"FormVersionNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_DatabaseSubmissions_AspNetUsers_SubmittedByAccountId_Organi~",
                        columns: x => new { x.SubmittedByAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseSubmissions_DatabaseFormVersions_FormVersionId_Orga~",
                        columns: x => new { x.FormVersionId, x.OrganizationId },
                        principalTable: "DatabaseFormVersions",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseSubmissions_Databases_DatabaseId_OrganizationId",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseSubmissions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseSubmissionEntries",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FieldType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Display = table.Column<string>(type: "text", nullable: false),
                    TextValue = table.Column<string>(type: "text", nullable: true),
                    NumberValue = table.Column<double>(type: "double precision", nullable: true),
                    ChoiceValues = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseSubmissionEntries", x => new { x.SubmissionId, x.FieldId });
                    table.CheckConstraint("CK_DatabaseSubmissionEntries_Position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_DatabaseSubmissionEntries_DatabaseSubmissions_SubmissionId_~",
                        columns: x => new { x.SubmissionId, x.OrganizationId },
                        principalTable: "DatabaseSubmissions",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseSubmissionEntries_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissionEntries_OrganizationId",
                table: "DatabaseSubmissionEntries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissionEntries_SubmissionId_OrganizationId",
                table: "DatabaseSubmissionEntries",
                columns: new[] { "SubmissionId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissions_DatabaseId_OrganizationId",
                table: "DatabaseSubmissions",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissions_DatabaseId_SubmittedAt",
                table: "DatabaseSubmissions",
                columns: new[] { "DatabaseId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissions_FormVersionId_OrganizationId",
                table: "DatabaseSubmissions",
                columns: new[] { "FormVersionId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissions_OrganizationId_ReceiptNumber",
                table: "DatabaseSubmissions",
                columns: new[] { "OrganizationId", "ReceiptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissions_SubmittedByAccountId_IdempotencyKey",
                table: "DatabaseSubmissions",
                columns: new[] { "SubmittedByAccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSubmissions_SubmittedByAccountId_OrganizationId",
                table: "DatabaseSubmissions",
                columns: new[] { "SubmittedByAccountId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseSubmissionEntries");

            migrationBuilder.DropTable(
                name: "DatabaseSubmissions");
        }
    }
}
