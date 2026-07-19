using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LumenMedia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalPlaybackHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_playback_history",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SeriesTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Watched = table.Column<bool>(type: "INTEGER", nullable: false),
                    PositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ViewedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TvdbId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_playback_history", x => new { x.UserId, x.DedupeKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_playback_history_UserId_UpdatedAt",
                table: "external_playback_history",
                columns: new[] { "UserId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_playback_history");
        }
    }
}
