using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreePlex.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "genres",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_genres", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ParsedJson = table.Column<string>(type: "TEXT", nullable: true),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: true),
                    LinkedMediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "libraries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PreferredLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    AutoScan = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastScanAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MetadataProviders = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "playback_progress",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaKind = table.Column<string>(type: "TEXT", nullable: false),
                    PositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Watched = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playback_progress", x => new { x.UserId, x.MediaId });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    PinHash = table.Column<string>(type: "TEXT", nullable: true),
                    LibraryAccessAll = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowTranscoding = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxBitrateRemoteKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AllowedLibraryIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "library_paths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_paths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_library_paths_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    SortTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    CommunityRating = table.Column<double>(type: "REAL", nullable: true),
                    OfficialRating = table.Column<string>(type: "TEXT", nullable: true),
                    TmdbId = table.Column<string>(type: "TEXT", nullable: true),
                    TvdbId = table.Column<string>(type: "TEXT", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                    MetadataLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 13, nullable: false),
                    Tagline = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RuntimeMs = table.Column<long>(type: "INTEGER", nullable: true),
                    EndYear = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_items_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_genres",
                columns: table => new
                {
                    GenresId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_genres", x => new { x.GenresId, x.MediaItemId });
                    table.ForeignKey(
                        name: "FK_media_genres_genres_GenresId",
                        column: x => x.GenresId,
                        principalTable: "genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_genres_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_people",
                columns: table => new
                {
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_people", x => new { x.MediaItemId, x.PersonId, x.Type });
                    table.ForeignKey(
                        name: "FK_media_people_media_items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_people_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_seasons_media_items_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    AirDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RuntimeMs = table.Column<long>(type: "INTEGER", nullable: true),
                    TvdbId = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_episodes_seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "artworks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    media_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    episode_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LocalPath = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artworks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_artworks_episodes_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_artworks_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    media_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    episode_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Container = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    OverallBitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    FileMtime = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_sources", x => x.Id);
                    table.CheckConstraint("ck_media_sources_owner", "(media_item_id IS NULL) <> (episode_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_media_sources_episodes_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_sources_media_items_media_item_id",
                        column: x => x.media_item_id,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_streams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaSourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    StreamIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    Profile = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsForced = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsExternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    FrameRate = table.Column<double>(type: "REAL", nullable: true),
                    BitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    Hdr = table.Column<string>(type: "TEXT", nullable: true),
                    Channels = table.Column<int>(type: "INTEGER", nullable: true),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: true),
                    SubtitleFormat = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_streams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_streams_media_sources_MediaSourceId",
                        column: x => x.MediaSourceId,
                        principalTable: "media_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artworks_episode_id_Kind",
                table: "artworks",
                columns: new[] { "episode_id", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_artworks_media_item_id_Kind",
                table: "artworks",
                columns: new[] { "media_item_id", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_background_jobs_State",
                table: "background_jobs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_background_jobs_Type_CreatedAt",
                table: "background_jobs",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_episodes_SeasonId",
                table: "episodes",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_episodes_SeriesId",
                table: "episodes",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_episodes_SeriesId_SeasonNumber_EpisodeNumber",
                table: "episodes",
                columns: new[] { "SeriesId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_genres_Name",
                table: "genres",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_SourcePath",
                table: "import_jobs",
                column: "SourcePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_Status",
                table: "import_jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_library_paths_LibraryId",
                table: "library_paths",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_library_paths_LibraryId_Path",
                table: "library_paths",
                columns: new[] { "LibraryId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_genres_MediaItemId",
                table: "media_genres",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_LibraryId_AddedAt",
                table: "media_items",
                columns: new[] { "LibraryId", "AddedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_media_items_LibraryId_SortTitle",
                table: "media_items",
                columns: new[] { "LibraryId", "SortTitle" });

            migrationBuilder.CreateIndex(
                name: "IX_media_items_Title",
                table: "media_items",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_TmdbId",
                table: "media_items",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_media_people_PersonId",
                table: "media_people",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_media_sources_ContentHash",
                table: "media_sources",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_media_sources_Path",
                table: "media_sources",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_sources_episode_id",
                table: "media_sources",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_sources_media_item_id",
                table: "media_sources",
                column: "media_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_streams_MediaSourceId",
                table: "media_streams",
                column: "MediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_people_Name",
                table: "people",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_playback_progress_UserId_UpdatedAt",
                table: "playback_progress",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_ExpiresAt",
                table: "refresh_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_seasons_SeriesId_SeasonNumber",
                table: "seasons",
                columns: new[] { "SeriesId", "SeasonNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artworks");

            migrationBuilder.DropTable(
                name: "background_jobs");

            migrationBuilder.DropTable(
                name: "import_jobs");

            migrationBuilder.DropTable(
                name: "library_paths");

            migrationBuilder.DropTable(
                name: "media_genres");

            migrationBuilder.DropTable(
                name: "media_people");

            migrationBuilder.DropTable(
                name: "media_streams");

            migrationBuilder.DropTable(
                name: "playback_progress");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "genres");

            migrationBuilder.DropTable(
                name: "people");

            migrationBuilder.DropTable(
                name: "media_sources");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "episodes");

            migrationBuilder.DropTable(
                name: "seasons");

            migrationBuilder.DropTable(
                name: "media_items");

            migrationBuilder.DropTable(
                name: "libraries");
        }
    }
}
