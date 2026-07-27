using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MistChess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2RatingsHistoryAndClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rating_snapshot",
                table: "matchmaking_tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "clock_expires_at",
                table: "games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_rated",
                table: "games",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE matchmaking_tickets
                SET rating_snapshot = 1500,
                    time_control = CASE
                        WHEN status = 'Searching' THEN '600+5'
                        ELSE time_control
                    END
                """);

            migrationBuilder.AlterColumn<int>(
                name: "rating_snapshot",
                table: "matchmaking_tickets",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.Sql(
                """
                UPDATE games
                SET clock_expires_at =
                    turn_started_at
                    + (
                        CASE side_to_move
                            WHEN 'Red' THEN red_milliseconds
                            ELSE black_milliseconds
                        END
                    ) * INTERVAL '1 millisecond'
                WHERE status = 'Playing'
                  AND time_control IS NOT NULL
                  AND turn_started_at IS NOT NULL
                """);

            migrationBuilder.CreateTable(
                name: "player_ratings",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    time_control = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false, defaultValue: 1500),
                    games_played = table.Column<int>(type: "integer", nullable: false),
                    wins = table.Column<int>(type: "integer", nullable: false),
                    draws = table.Column<int>(type: "integer", nullable: false),
                    losses = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    concurrency_stamp = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_ratings", x => new { x.player_id, x.rule_version, x.time_control });
                    table.CheckConstraint("ck_player_ratings_rating", "rating >= 100");
                    table.CheckConstraint("ck_player_ratings_statistics", "games_played >= 0 AND wins >= 0 AND draws >= 0 AND losses >= 0 AND games_played = wins + draws + losses");
                    table.CheckConstraint("ck_player_ratings_time_control", "time_control = '600+5'");
                    table.ForeignKey(
                        name: "FK_player_ratings_guest_sessions_player_id",
                        column: x => x.player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rating_settlements",
                columns: table => new
                {
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    red_rating_before = table.Column<int>(type: "integer", nullable: false),
                    red_rating_after = table.Column<int>(type: "integer", nullable: false),
                    black_rating_before = table.Column<int>(type: "integer", nullable: false),
                    black_rating_after = table.Column<int>(type: "integer", nullable: false),
                    red_score = table.Column<decimal>(type: "numeric(2,1)", precision: 2, scale: 1, nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rating_settlements", x => x.game_id);
                    table.CheckConstraint("ck_rating_settlements_ratings", "red_rating_before >= 100 AND red_rating_after >= 100 AND black_rating_before >= 100 AND black_rating_after >= 100");
                    table.CheckConstraint("ck_rating_settlements_score", "red_score IN (0.0, 0.5, 1.0)");
                    table.ForeignKey(
                        name: "FK_rating_settlements_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "replay_shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replay_shares", x => x.id);
                    table.CheckConstraint("ck_replay_shares_revocation", "revoked_at IS NULL OR revoked_at >= created_at");
                    table.ForeignKey(
                        name: "FK_replay_shares_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_replay_shares_guest_sessions_owner_player_id",
                        column: x => x.owner_player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_matchmaking_tickets_rating_snapshot",
                table: "matchmaking_tickets",
                sql: "rating_snapshot >= 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_matchmaking_tickets_time_control",
                table: "matchmaking_tickets",
                sql: "status <> 'Searching' OR time_control = '600+5'");

            migrationBuilder.CreateIndex(
                name: "ix_games_expired_clock",
                table: "games",
                columns: new[] { "clock_expires_at", "id" },
                filter: "\"status\" = 'Playing' AND \"clock_expires_at\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_games_history_black",
                table: "games",
                columns: new[] { "black_player_id", "finished_at", "id" },
                descending: new[] { false, true, true },
                filter: "\"status\" = 'Finished'");

            migrationBuilder.CreateIndex(
                name: "ix_games_history_red",
                table: "games",
                columns: new[] { "red_player_id", "finished_at", "id" },
                descending: new[] { false, true, true },
                filter: "\"status\" = 'Finished'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_clock_expiry",
                table: "games",
                sql: "(time_control IS NULL AND clock_expires_at IS NULL) OR (time_control IS NOT NULL AND ((status = 'Playing' AND clock_expires_at IS NOT NULL) OR (status = 'Finished' AND clock_expires_at IS NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_rated_time_control",
                table: "games",
                sql: "NOT is_rated OR time_control = '600+5'");

            migrationBuilder.CreateIndex(
                name: "IX_replay_shares_owner_player_id",
                table: "replay_shares",
                column: "owner_player_id");

            migrationBuilder.CreateIndex(
                name: "ux_replay_shares_active_owner",
                table: "replay_shares",
                columns: new[] { "game_id", "owner_player_id" },
                unique: true,
                filter: "\"revoked_at\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_replay_shares_token_hash",
                table: "replay_shares",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_ratings");

            migrationBuilder.DropTable(
                name: "rating_settlements");

            migrationBuilder.DropTable(
                name: "replay_shares");

            migrationBuilder.DropCheckConstraint(
                name: "ck_matchmaking_tickets_rating_snapshot",
                table: "matchmaking_tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_matchmaking_tickets_time_control",
                table: "matchmaking_tickets");

            migrationBuilder.DropIndex(
                name: "ix_games_expired_clock",
                table: "games");

            migrationBuilder.DropIndex(
                name: "ix_games_history_black",
                table: "games");

            migrationBuilder.DropIndex(
                name: "ix_games_history_red",
                table: "games");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_clock_expiry",
                table: "games");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_rated_time_control",
                table: "games");

            migrationBuilder.DropColumn(
                name: "rating_snapshot",
                table: "matchmaking_tickets");

            migrationBuilder.DropColumn(
                name: "clock_expires_at",
                table: "games");

            migrationBuilder.DropColumn(
                name: "is_rated",
                table: "games");
        }
    }
}
