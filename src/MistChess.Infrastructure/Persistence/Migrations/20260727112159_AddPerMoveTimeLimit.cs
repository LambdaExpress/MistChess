using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MistChess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerMoveTimeLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_matchmaking_tickets_time_control",
                table: "matchmaking_tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_clock",
                table: "games");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_rated_time_control",
                table: "games");

            migrationBuilder.AddColumn<long>(
                name: "move_time_limit_milliseconds",
                table: "rooms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "turn_milliseconds_after",
                table: "moves",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "turn_milliseconds_after",
                table: "move_command_receipts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "move_time_limit_milliseconds",
                table: "matchmaking_tickets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "move_time_limit_milliseconds",
                table: "games",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "turn_milliseconds",
                table: "games",
                type: "bigint",
                nullable: true);
            migrationBuilder.Sql(
                """
                UPDATE rooms
                SET move_time_limit_milliseconds = 90000
                WHERE time_control IS NOT NULL;

                UPDATE matchmaking_tickets
                SET move_time_limit_milliseconds = 90000
                WHERE time_control IS NOT NULL;

                UPDATE games
                SET move_time_limit_milliseconds = 90000,
                    turn_milliseconds = 90000
                WHERE time_control IS NOT NULL;

                UPDATE moves AS move
                SET turn_milliseconds_after = game.move_time_limit_milliseconds
                FROM games AS game
                WHERE move.game_id = game.id
                  AND game.move_time_limit_milliseconds IS NOT NULL;

                UPDATE move_command_receipts AS receipt
                SET turn_milliseconds_after = game.move_time_limit_milliseconds
                FROM games AS game
                WHERE receipt.game_id = game.id
                  AND game.move_time_limit_milliseconds IS NOT NULL;

                UPDATE games
                SET clock_expires_at =
                    turn_started_at
                    + LEAST(
                        CASE side_to_move
                            WHEN 'Red' THEN red_milliseconds
                            ELSE black_milliseconds
                        END,
                        turn_milliseconds)
                    * INTERVAL '1 millisecond'
                WHERE status = 'Playing'
                  AND time_control IS NOT NULL;
                """);


            migrationBuilder.AddCheckConstraint(
                name: "ck_rooms_move_time_limit",
                table: "rooms",
                sql: "(time_control IS NOT NULL) OR move_time_limit_milliseconds IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rooms_move_time_limit_positive",
                table: "rooms",
                sql: "move_time_limit_milliseconds IS NULL OR move_time_limit_milliseconds > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_matchmaking_tickets_move_time_limit",
                table: "matchmaking_tickets",
                sql: "move_time_limit_milliseconds IS NULL OR move_time_limit_milliseconds > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_matchmaking_tickets_time_control",
                table: "matchmaking_tickets",
                sql: "status <> 'Searching' OR (time_control = '600+5' AND move_time_limit_milliseconds = 90000)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_clock",
                table: "games",
                sql: "(time_control IS NULL AND red_milliseconds IS NULL AND black_milliseconds IS NULL AND turn_started_at IS NULL AND move_time_limit_milliseconds IS NULL AND turn_milliseconds IS NULL) OR (time_control IS NOT NULL AND red_milliseconds >= 0 AND black_milliseconds >= 0 AND ((move_time_limit_milliseconds IS NULL AND turn_milliseconds IS NULL) OR (move_time_limit_milliseconds > 0 AND turn_milliseconds BETWEEN 0 AND move_time_limit_milliseconds)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_rated_time_control",
                table: "games",
                sql: "NOT is_rated OR (time_control = '600+5' AND move_time_limit_milliseconds = 90000)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rooms_move_time_limit",
                table: "rooms");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rooms_move_time_limit_positive",
                table: "rooms");

            migrationBuilder.DropCheckConstraint(
                name: "ck_matchmaking_tickets_move_time_limit",
                table: "matchmaking_tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_matchmaking_tickets_time_control",
                table: "matchmaking_tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_clock",
                table: "games");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_rated_time_control",
                table: "games");

            migrationBuilder.DropColumn(
                name: "move_time_limit_milliseconds",
                table: "rooms");

            migrationBuilder.DropColumn(
                name: "turn_milliseconds_after",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "turn_milliseconds_after",
                table: "move_command_receipts");

            migrationBuilder.DropColumn(
                name: "move_time_limit_milliseconds",
                table: "matchmaking_tickets");

            migrationBuilder.DropColumn(
                name: "move_time_limit_milliseconds",
                table: "games");

            migrationBuilder.DropColumn(
                name: "turn_milliseconds",
                table: "games");

            migrationBuilder.AddCheckConstraint(
                name: "ck_matchmaking_tickets_time_control",
                table: "matchmaking_tickets",
                sql: "status <> 'Searching' OR time_control = '600+5'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_clock",
                table: "games",
                sql: "(time_control IS NULL AND red_milliseconds IS NULL AND black_milliseconds IS NULL AND turn_started_at IS NULL) OR (time_control IS NOT NULL AND red_milliseconds >= 0 AND black_milliseconds >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_rated_time_control",
                table: "games",
                sql: "NOT is_rated OR time_control = '600+5'");
        }
    }
}
