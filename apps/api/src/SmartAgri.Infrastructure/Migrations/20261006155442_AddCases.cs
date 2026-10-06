using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ThreadAssistantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: true),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssistantIssueId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousCaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EventCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cases", x => x.Id);
                    table.UniqueConstraint("AK_Cases_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_Cases_CancelledAt", "(\"Status\" = 'cancelled') = (\"CancelledAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_Cases_CompletedAt", "(\"Status\" = 'completed') = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_Cases_Creator", "(\"Origin\" = 'database-submission') = (\"CreatedByAccountId\" IS NULL)");
                    table.CheckConstraint("CK_Cases_Record", "(\"DatabaseId\" IS NULL) = (\"SubmissionId\" IS NULL)");
                    table.CheckConstraint("CK_Cases_Thread", "(\"ThreadAssistantId\" IS NULL) = (\"ThreadId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Cases_AspNetUsers_CreatedByAccountId_OrganizationId",
                        columns: x => new { x.CreatedByAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cases_AspNetUsers_OwnerAccountId_OrganizationId",
                        columns: x => new { x.OwnerAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cases_CaseGroups_GroupId_OrganizationId",
                        columns: x => new { x.GroupId, x.OrganizationId },
                        principalTable: "CaseGroups",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cases_CaseTypes_TypeId_OrganizationId",
                        columns: x => new { x.TypeId, x.OrganizationId },
                        principalTable: "CaseTypes",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cases_Cases_PreviousCaseId_OrganizationId",
                        columns: x => new { x.PreviousCaseId, x.OrganizationId },
                        principalTable: "Cases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cases_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseEvents_AspNetUsers_ActorAccountId_OrganizationId",
                        columns: x => new { x.ActorAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseEvents_AspNetUsers_OwnerAccountId_OrganizationId",
                        columns: x => new { x.OwnerAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseEvents_CaseGroups_FromGroupId_OrganizationId",
                        columns: x => new { x.FromGroupId, x.OrganizationId },
                        principalTable: "CaseGroups",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseEvents_CaseGroups_ToGroupId_OrganizationId",
                        columns: x => new { x.ToGroupId, x.OrganizationId },
                        principalTable: "CaseGroups",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseEvents_Cases_CaseId_OrganizationId",
                        columns: x => new { x.CaseId, x.OrganizationId },
                        principalTable: "Cases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaseEvents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_ActorAccountId_OrganizationId",
                table: "CaseEvents",
                columns: new[] { "ActorAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_CaseId_Ordinal",
                table: "CaseEvents",
                columns: new[] { "CaseId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_CaseId_OrganizationId",
                table: "CaseEvents",
                columns: new[] { "CaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_FromGroupId_OrganizationId",
                table: "CaseEvents",
                columns: new[] { "FromGroupId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_OrganizationId",
                table: "CaseEvents",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_OwnerAccountId_OrganizationId",
                table: "CaseEvents",
                columns: new[] { "OwnerAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_ToGroupId_OrganizationId",
                table: "CaseEvents",
                columns: new[] { "ToGroupId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_AssistantIssueId",
                table: "Cases",
                column: "AssistantIssueId");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_CreatedByAccountId_OrganizationId",
                table: "Cases",
                columns: new[] { "CreatedByAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_GroupId_OrganizationId",
                table: "Cases",
                columns: new[] { "GroupId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_GroupId_Status",
                table: "Cases",
                columns: new[] { "GroupId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_OrganizationId_CreatedAt",
                table: "Cases",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_OwnerAccountId_OrganizationId",
                table: "Cases",
                columns: new[] { "OwnerAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_PreviousCaseId_OrganizationId",
                table: "Cases",
                columns: new[] { "PreviousCaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_SubmissionId",
                table: "Cases",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_ThreadId",
                table: "Cases",
                column: "ThreadId");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_TypeId_OrganizationId",
                table: "Cases",
                columns: new[] { "TypeId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseEvents");

            migrationBuilder.DropTable(
                name: "Cases");
        }
    }
}
