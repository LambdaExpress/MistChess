using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MistChess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3AdminPresenceAndBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ban_reason",
                table: "guest_sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "banned_at",
                table: "guest_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "banned_by",
                table: "guest_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_banned",
                table: "guest_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "guest_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE guest_sessions SET last_seen_at = created_at WHERE last_seen_at IS NULL");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "guest_sessions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_sessions_ban_last_seen",
                table: "guest_sessions",
                columns: new[] { "is_banned", "last_seen_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_guest_sessions_last_seen",
                table: "guest_sessions",
                columns: new[] { "last_seen_at", "id" },
                descending: new bool[0]);

            migrationBuilder.AddCheckConstraint(
                name: "ck_guest_sessions_ban_state",
                table: "guest_sessions",
                sql: "(NOT is_banned AND banned_at IS NULL AND ban_reason IS NULL AND banned_by IS NULL) OR (is_banned AND banned_at IS NOT NULL AND ban_reason IS NOT NULL AND banned_by IS NOT NULL AND char_length(btrim(ban_reason)) BETWEEN 1 AND 200)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_guest_sessions_ban_last_seen",
                table: "guest_sessions");

            migrationBuilder.DropIndex(
                name: "ix_guest_sessions_last_seen",
                table: "guest_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_guest_sessions_ban_state",
                table: "guest_sessions");

            migrationBuilder.DropColumn(
                name: "ban_reason",
                table: "guest_sessions");

            migrationBuilder.DropColumn(
                name: "banned_at",
                table: "guest_sessions");

            migrationBuilder.DropColumn(
                name: "banned_by",
                table: "guest_sessions");

            migrationBuilder.DropColumn(
                name: "is_banned",
                table: "guest_sessions");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "guest_sessions");
        }
    }
}
