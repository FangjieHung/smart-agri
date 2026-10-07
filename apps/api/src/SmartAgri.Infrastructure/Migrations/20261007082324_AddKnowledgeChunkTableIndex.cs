using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeChunkTableIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Which table of its unit a table-row chunk belongs to (#324). Null for every chunk
            // written so far: `rechunk` fills it in for table rows (KnowledgeChunkFormat.TableIdentity)
            // without re-embedding; other chunks keep null.
            migrationBuilder.AddColumn<int>(
                name: "TableIndex",
                table: "KnowledgeChunks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TableIndex",
                table: "KnowledgeChunks");
        }
    }
}
