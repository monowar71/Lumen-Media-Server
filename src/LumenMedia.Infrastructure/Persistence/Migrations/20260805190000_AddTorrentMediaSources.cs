using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LumenMedia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTorrentMediaSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "media_sources",
                type: "TEXT",
                nullable: false,
                defaultValue: "LocalFile");

            migrationBuilder.AddColumn<string>(
                name: "TorrentPath",
                table: "media_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InfoHash",
                table: "media_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TorrentFileIndex",
                table: "media_sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TorrentRelativePath",
                table: "media_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_sources_InfoHash",
                table: "media_sources",
                column: "InfoHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_sources_InfoHash",
                table: "media_sources");

            migrationBuilder.DropColumn(name: "Kind", table: "media_sources");
            migrationBuilder.DropColumn(name: "TorrentPath", table: "media_sources");
            migrationBuilder.DropColumn(name: "InfoHash", table: "media_sources");
            migrationBuilder.DropColumn(name: "TorrentFileIndex", table: "media_sources");
            migrationBuilder.DropColumn(name: "TorrentRelativePath", table: "media_sources");
        }
    }
}
