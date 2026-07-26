using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MistChess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guest_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_sessions", x => x.id);
                    table.CheckConstraint("ck_guest_sessions_expiry", "expires_at > created_at");
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    red_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    black_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initial_state = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    side_to_move = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    winner = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    result_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    rule_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    time_control = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    red_milliseconds = table.Column<long>(type: "bigint", nullable: true),
                    black_milliseconds = table.Column<long>(type: "bigint", nullable: true),
                    turn_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.id);
                    table.CheckConstraint("ck_games_clock", "(time_control IS NULL AND red_milliseconds IS NULL AND black_milliseconds IS NULL AND turn_started_at IS NULL) OR (time_control IS NOT NULL AND red_milliseconds >= 0 AND black_milliseconds >= 0)");
                    table.CheckConstraint("ck_games_distinct_players", "red_player_id <> black_player_id");
                    table.CheckConstraint("ck_games_result", "(status = 'Finished' AND result_reason IS NOT NULL AND finished_at IS NOT NULL) OR (status = 'Playing' AND result_reason IS NULL AND winner IS NULL AND finished_at IS NULL)");
                    table.CheckConstraint("ck_games_status", "status IN ('Playing', 'Finished')");
                    table.ForeignKey(
                        name: "FK_games_guest_sessions_black_player_id",
                        column: x => x.black_player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_games_guest_sessions_red_player_id",
                        column: x => x.red_player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "draw_offers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offered_by_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_draw_offers", x => x.id);
                    table.CheckConstraint("ck_draw_offers_status", "status IN ('Pending', 'Accepted', 'Rejected', 'Withdrawn')");
                    table.ForeignKey(
                        name: "FK_draw_offers_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_draw_offers_guest_sessions_offered_by_player_id",
                        column: x => x.offered_by_player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "game_players",
                columns: table => new
                {
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_players", x => new { x.game_id, x.player_id });
                    table.CheckConstraint("ck_game_players_side", "side IN ('Red', 'Black')");
                    table.ForeignKey(
                        name: "FK_game_players_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_game_players_guest_sessions_player_id",
                        column: x => x.player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "matchmaking_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    time_control = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    client_request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_stamp = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matchmaking_tickets", x => x.id);
                    table.CheckConstraint("ck_matchmaking_tickets_expiry", "expires_at > created_at AND expires_at >= last_heartbeat_at");
                    table.CheckConstraint("ck_matchmaking_tickets_game", "(status = 'Matched' AND game_id IS NOT NULL) OR (status <> 'Matched' AND game_id IS NULL)");
                    table.CheckConstraint("ck_matchmaking_tickets_status", "status IN ('Searching', 'Matched', 'Cancelled', 'Expired')");
                    table.ForeignKey(
                        name: "FK_matchmaking_tickets_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_matchmaking_tickets_guest_sessions_player_id",
                        column: x => x.player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moves",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ply = table.Column<int>(type: "integer", nullable: false),
                    from_file = table.Column<byte>(type: "smallint", nullable: false),
                    from_rank = table.Column<byte>(type: "smallint", nullable: false),
                    to_file = table.Column<byte>(type: "smallint", nullable: false),
                    to_rank = table.Column<byte>(type: "smallint", nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    moving_piece_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    captured_piece_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    elapsed_milliseconds = table.Column<long>(type: "bigint", nullable: false),
                    client_move_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    position_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state_after = table.Column<string>(type: "jsonb", nullable: false),
                    game_version = table.Column<long>(type: "bigint", nullable: false),
                    winner_after = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    result_reason_after = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    red_milliseconds_after = table.Column<long>(type: "bigint", nullable: true),
                    black_milliseconds_after = table.Column<long>(type: "bigint", nullable: true),
                    turn_started_at_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moves", x => x.id);
                    table.CheckConstraint("ck_moves_from", "from_file BETWEEN 0 AND 8 AND from_rank BETWEEN 0 AND 9");
                    table.CheckConstraint("ck_moves_ply", "ply > 0");
                    table.CheckConstraint("ck_moves_to", "to_file BETWEEN 0 AND 8 AND to_rank BETWEEN 0 AND 9");
                    table.ForeignKey(
                        name: "FK_moves_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    creator_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rule_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    time_control = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rooms", x => x.id);
                    table.CheckConstraint("ck_rooms_game", "(status IN ('Playing', 'Finished') AND game_id IS NOT NULL) OR (status IN ('WaitingForOpponent', 'WaitingForReady') AND game_id IS NULL)");
                    table.CheckConstraint("ck_rooms_status", "status IN ('WaitingForOpponent', 'WaitingForReady', 'Playing', 'Finished')");
                    table.ForeignKey(
                        name: "FK_rooms_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rooms_guest_sessions_creator_player_id",
                        column: x => x.creator_player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "room_players",
                columns: table => new
                {
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat = table.Column<byte>(type: "smallint", nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    is_ready = table.Column<bool>(type: "boolean", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_players", x => new { x.room_id, x.player_id });
                    table.CheckConstraint("ck_room_players_seat", "seat BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_room_players_guest_sessions_player_id",
                        column: x => x.player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_room_players_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_draw_offers_offered_by_player_id",
                table: "draw_offers",
                column: "offered_by_player_id");

            migrationBuilder.CreateIndex(
                name: "ux_draw_offers_pending_game",
                table: "draw_offers",
                column: "game_id",
                unique: true,
                filter: "\"status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_game_players_active_player",
                table: "game_players",
                column: "player_id",
                unique: true,
                filter: "\"is_active\"");

            migrationBuilder.CreateIndex(
                name: "ux_game_players_side",
                table: "game_players",
                columns: new[] { "game_id", "side" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_games_active_black_player",
                table: "games",
                column: "black_player_id",
                filter: "\"status\" <> 'Finished'");

            migrationBuilder.CreateIndex(
                name: "ix_games_active_clock",
                table: "games",
                columns: new[] { "status", "turn_started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_games_active_red_player",
                table: "games",
                column: "red_player_id",
                filter: "\"status\" <> 'Finished'");

            migrationBuilder.CreateIndex(
                name: "ix_guest_sessions_expires_at",
                table: "guest_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_guest_sessions_token_hash",
                table: "guest_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_matchmaking_tickets_expiry",
                table: "matchmaking_tickets",
                column: "expires_at",
                filter: "\"status\" = 'Searching'");

            migrationBuilder.CreateIndex(
                name: "IX_matchmaking_tickets_game_id",
                table: "matchmaking_tickets",
                column: "game_id");

            migrationBuilder.CreateIndex(
                name: "ix_matchmaking_tickets_pool_fifo",
                table: "matchmaking_tickets",
                columns: new[] { "status", "rule_version", "time_control", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_matchmaking_tickets_active_player",
                table: "matchmaking_tickets",
                column: "player_id",
                unique: true,
                filter: "\"status\" = 'Searching'");

            migrationBuilder.CreateIndex(
                name: "ux_matchmaking_tickets_request",
                table: "matchmaking_tickets",
                columns: new[] { "player_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_moves_game_client_move",
                table: "moves",
                columns: new[] { "game_id", "client_move_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_moves_game_ply",
                table: "moves",
                columns: new[] { "game_id", "ply" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_room_players_player_id",
                table: "room_players",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ux_room_players_seat",
                table: "room_players",
                columns: new[] { "room_id", "seat" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_room_players_side",
                table: "room_players",
                columns: new[] { "room_id", "side" },
                unique: true,
                filter: "\"side\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_rooms_creator_player_id",
                table: "rooms",
                column: "creator_player_id");

            migrationBuilder.CreateIndex(
                name: "ux_rooms_code",
                table: "rooms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rooms_game_id",
                table: "rooms",
                column: "game_id",
                unique: true,
                filter: "\"game_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "draw_offers");

            migrationBuilder.DropTable(
                name: "game_players");

            migrationBuilder.DropTable(
                name: "matchmaking_tickets");

            migrationBuilder.DropTable(
                name: "moves");

            migrationBuilder.DropTable(
                name: "room_players");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "games");

            migrationBuilder.DropTable(
                name: "guest_sessions");
        }
    }
}
