using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantTestRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantTestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RerunRequested = table.Column<bool>(type: "boolean", nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PassedCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MinScore = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTestRuns", x => x.Id);
                    table.UniqueConstraint("AK_AssistantTestRuns_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_AssistantTestRuns_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantTestRuns_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantTestResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    QuestionSnapshot = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ExpectedKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedDocumentIds = table.Column<string>(type: "jsonb", nullable: false),
                    ActualKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AnswerText = table.Column<string>(type: "text", nullable: false),
                    CitedDocumentIds = table.Column<string>(type: "jsonb", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TopScore = table.Column<double>(type: "double precision", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTestResults", x => x.Id);
                    table.CheckConstraint("CK_AssistantTestResults_Passed", "\"Passed\" = (\"FailureReason\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_AssistantTestResults_AssistantTestRuns_RunId_OrganizationId",
                        columns: x => new { x.RunId, x.OrganizationId },
                        principalTable: "AssistantTestRuns",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantTestResults_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestResults_OrganizationId",
                table: "AssistantTestResults",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestResults_RunId_Ordinal",
                table: "AssistantTestResults",
                columns: new[] { "RunId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestResults_RunId_OrganizationId",
                table: "AssistantTestResults",
                columns: new[] { "RunId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestRuns_AssistantId_Active",
                table: "AssistantTestRuns",
                column: "AssistantId",
                unique: true,
                filter: "\"Status\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestRuns_AssistantId_OrganizationId",
                table: "AssistantTestRuns",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestRuns_AssistantId_QueuedAt",
                table: "AssistantTestRuns",
                columns: new[] { "AssistantId", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTestRuns_OrganizationId",
                table: "AssistantTestRuns",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantTestResults");

            migrationBuilder.DropTable(
                name: "AssistantTestRuns");
        }
    }
}
