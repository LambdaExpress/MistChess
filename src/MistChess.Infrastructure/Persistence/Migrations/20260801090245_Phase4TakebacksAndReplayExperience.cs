using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MistChess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4TakebacksAndReplayExperience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reverted_at",
                table: "moves",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reverted_by_takeback_request_id",
                table: "moves",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "turn_milliseconds_before",
                table: "moves",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_action_actor",
                table: "games",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_action_kind",
                table: "games",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_action_version",
                table: "games",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "negotiation_version",
                table: "games",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "takeback_window_consumed",
                table: "games",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "takeback_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    move_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_ply = table.Column<int>(type: "integer", nullable: false),
                    requested_at_version = table.Column<long>(type: "bigint", nullable: false),
                    resolved_at_version = table.Column<long>(type: "bigint", nullable: true),
                    client_request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_takeback_requests", x => x.id);
                    table.CheckConstraint("ck_takeback_requests_ply", "requested_ply > 0");
                    table.CheckConstraint("ck_takeback_requests_resolution", "(status = 'Pending' AND resolved_at_version IS NULL) OR (status <> 'Pending' AND resolved_at_version IS NOT NULL)");
                    table.CheckConstraint("ck_takeback_requests_status", "status IN ('Pending', 'Accepted', 'Rejected', 'Withdrawn')");
                    table.CheckConstraint("ck_takeback_requests_versions", "requested_at_version >= 0 AND resolved_at_version >= 0");
                    table.ForeignKey(
                        name: "FK_takeback_requests_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_takeback_requests_guest_sessions_requested_by_player_id",
                        column: x => x.requested_by_player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_takeback_requests_moves_move_id",
                        column: x => x.move_id,
                        principalTable: "moves",
                        principalColumn: "id");
                });

            migrationBuilder.DropIndex(
                name: "ux_moves_game_ply",
                table: "moves");

            migrationBuilder.CreateIndex(
                name: "ux_moves_game_ply",
                table: "moves",
                columns: new[] { "game_id", "ply" },
                unique: true,
                filter: "\"reverted_at\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_moves_reverted_takeback_request",
                table: "moves",
                column: "reverted_by_takeback_request_id",
                unique: true,
                filter: "\"reverted_by_takeback_request_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moves_reverted",
                table: "moves",
                sql: "(reverted_at IS NULL AND reverted_by_takeback_request_id IS NULL) OR (reverted_at IS NOT NULL AND reverted_by_takeback_request_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_last_action",
                table: "games",
                sql: "(last_action_version IS NULL AND last_action_kind IS NULL AND last_action_actor IS NULL) OR (last_action_version IS NOT NULL AND last_action_version >= 0 AND last_action_version <= version AND last_action_kind IN ('move', 'capture', 'takebackAccepted') AND last_action_actor IN ('Red', 'Black'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_negotiation_version",
                table: "games",
                sql: "negotiation_version >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_takeback_requests_requested_by_player_id",
                table: "takeback_requests",
                column: "requested_by_player_id");

            migrationBuilder.CreateIndex(
                name: "ux_takeback_requests_game_player_client_request",
                table: "takeback_requests",
                columns: new[] { "game_id", "requested_by_player_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_takeback_requests_move",
                table: "takeback_requests",
                column: "move_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_takeback_requests_pending_game",
                table: "takeback_requests",
                column: "game_id",
                unique: true,
                filter: "\"status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "takeback_requests");

            migrationBuilder.Sql("DELETE FROM moves WHERE reverted_at IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "ux_moves_game_ply",
                table: "moves");

            migrationBuilder.DropIndex(
                name: "ux_moves_reverted_takeback_request",
                table: "moves");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moves_reverted",
                table: "moves");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_last_action",
                table: "games");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_negotiation_version",
                table: "games");

            migrationBuilder.DropColumn(
                name: "reverted_at",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "reverted_by_takeback_request_id",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "turn_milliseconds_before",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "last_action_actor",
                table: "games");

            migrationBuilder.DropColumn(
                name: "last_action_kind",
                table: "games");

            migrationBuilder.DropColumn(
                name: "last_action_version",
                table: "games");

            migrationBuilder.DropColumn(
                name: "negotiation_version",
                table: "games");

            migrationBuilder.DropColumn(
                name: "takeback_window_consumed",
                table: "games");

            migrationBuilder.CreateIndex(
                name: "ux_moves_game_ply",
                table: "moves",
                columns: new[] { "game_id", "ply" },
                unique: true);
        }
    }
}
