using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPeriodicReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantReportSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NextPeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantReportSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantReportSchedules_Assistants_AssistantId_Organizatio~",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantReportSchedules_Databases_DatabaseId_OrganizationId",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantReportSchedules_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SkipReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DataState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StatisticsJson = table.Column<string>(type: "jsonb", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SummaryStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SummaryText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SummaryNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SummaryModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SummaryUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseReports", x => x.Id);
                    table.CheckConstraint("CK_DatabaseReports_Period", "\"PeriodTo\" >= \"PeriodFrom\"");
                    table.CheckConstraint("CK_DatabaseReports_Status", "(\"Status\" = 'generated' AND \"StatisticsJson\" IS NOT NULL AND \"DataState\" IS NOT NULL AND \"SkipReason\" IS NULL) OR (\"Status\" = 'skipped' AND \"StatisticsJson\" IS NULL AND \"DataState\" IS NULL AND \"SkipReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_DatabaseReports_Summary", "(\"SummaryStatus\" = 'ready' AND \"SummaryText\" IS NOT NULL AND \"SummaryModel\" IS NOT NULL) OR (\"SummaryStatus\" <> 'ready' AND \"SummaryText\" IS NULL AND \"SummaryModel\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_DatabaseReports_Databases_DatabaseId_OrganizationId",
                        columns: x => new { x.DatabaseId, x.OrganizationId },
                        principalTable: "Databases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseReports_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantReportSchedules_AssistantId",
                table: "AssistantReportSchedules",
                column: "AssistantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantReportSchedules_AssistantId_OrganizationId",
                table: "AssistantReportSchedules",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantReportSchedules_DatabaseId_OrganizationId",
                table: "AssistantReportSchedules",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantReportSchedules_OrganizationId",
                table: "AssistantReportSchedules",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseReports_DatabaseId_OrganizationId",
                table: "DatabaseReports",
                columns: new[] { "DatabaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseReports_DatabaseId_PeriodFrom",
                table: "DatabaseReports",
                columns: new[] { "DatabaseId", "PeriodFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseReports_OnePerPeriod",
                table: "DatabaseReports",
                columns: new[] { "AssistantId", "DatabaseId", "Frequency", "PeriodFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseReports_OrganizationId",
                table: "DatabaseReports",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantReportSchedules");

            migrationBuilder.DropTable(
                name: "DatabaseReports");
        }
    }
}
