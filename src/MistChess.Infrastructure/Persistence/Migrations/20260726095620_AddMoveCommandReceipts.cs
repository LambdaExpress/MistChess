using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MistChess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMoveCommandReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "move_command_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_move_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("PK_move_command_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_move_command_receipts_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_move_command_receipts_guest_sessions_player_id",
                        column: x => x.player_id,
                        principalTable: "guest_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_move_command_receipts_player_id",
                table: "move_command_receipts",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ux_move_command_receipts_game_player_client_move",
                table: "move_command_receipts",
                columns: new[] { "game_id", "player_id", "client_move_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "move_command_receipts");
        }
    }
}
