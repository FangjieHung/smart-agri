using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssigneeAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReporterAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TestRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    TestResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    TestFailureReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    QuestionSnapshot = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AnswerSnapshot = table.Column<string>(type: "text", nullable: true),
                    ResolutionNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EventCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantIssues", x => x.Id);
                    table.UniqueConstraint("AK_AssistantIssues_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_AssistantIssues_ResolvedAt", "(\"Status\" = 'resolved') = (\"ResolvedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AssistantIssues_AspNetUsers_AssigneeAccountId_OrganizationId",
                        columns: x => new { x.AssigneeAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantIssues_AspNetUsers_ReporterAccountId_OrganizationId",
                        columns: x => new { x.ReporterAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantIssues_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantIssues_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantIssueEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssigneeAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantIssueEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantIssueEvents_AspNetUsers_ActorAccountId_Organizatio~",
                        columns: x => new { x.ActorAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantIssueEvents_AspNetUsers_AssigneeAccountId_Organiza~",
                        columns: x => new { x.AssigneeAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantIssueEvents_AssistantIssues_IssueId_OrganizationId",
                        columns: x => new { x.IssueId, x.OrganizationId },
                        principalTable: "AssistantIssues",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantIssueEvents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssueEvents_ActorAccountId_OrganizationId",
                table: "AssistantIssueEvents",
                columns: new[] { "ActorAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssueEvents_AssigneeAccountId_OrganizationId",
                table: "AssistantIssueEvents",
                columns: new[] { "AssigneeAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssueEvents_IssueId_Ordinal",
                table: "AssistantIssueEvents",
                columns: new[] { "IssueId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssueEvents_IssueId_OrganizationId",
                table: "AssistantIssueEvents",
                columns: new[] { "IssueId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssueEvents_OrganizationId",
                table: "AssistantIssueEvents",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_AssigneeAccountId_OrganizationId",
                table: "AssistantIssues",
                columns: new[] { "AssigneeAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_AssigneeAccountId_Status",
                table: "AssistantIssues",
                columns: new[] { "AssigneeAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_AssistantId_CreatedAt",
                table: "AssistantIssues",
                columns: new[] { "AssistantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_AssistantId_OrganizationId",
                table: "AssistantIssues",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_OrganizationId",
                table: "AssistantIssues",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_ReporterAccountId_OrganizationId",
                table: "AssistantIssues",
                columns: new[] { "ReporterAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_ReporterAccountId_Source",
                table: "AssistantIssues",
                columns: new[] { "ReporterAccountId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIssues_TestResultId",
                table: "AssistantIssues",
                column: "TestResultId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantIssueEvents");

            migrationBuilder.DropTable(
                name: "AssistantIssues");
        }
    }
}
