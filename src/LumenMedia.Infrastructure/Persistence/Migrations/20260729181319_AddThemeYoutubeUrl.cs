using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LumenMedia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThemeYoutubeUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThemeYoutubeUrl",
                table: "media_items",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThemeYoutubeUrl",
                table: "media_items");
        }
    }
}
