using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeChunkFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every version processed so far was chunked with tables inside their sections'
            // text: KnowledgeChunkFormat.SectionTables (#301), so `rechunk` finds them. The
            // column keeps this default; the app always writes the current format.
            migrationBuilder.AddColumn<int>(
                name: "ChunkFormat",
                table: "KnowledgeDocumentVersions",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChunkFormat",
                table: "KnowledgeDocumentVersions");
        }
    }
}
