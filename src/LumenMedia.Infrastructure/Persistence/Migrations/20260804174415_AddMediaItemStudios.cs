using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LumenMedia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItemStudios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Studios",
                table: "media_items",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Studios",
                table: "media_items");
        }
    }
}
