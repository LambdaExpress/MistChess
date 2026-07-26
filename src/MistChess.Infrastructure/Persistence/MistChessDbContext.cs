using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MistChess.Domain;

namespace MistChess.Infrastructure.Persistence;

public sealed class MistChessDbContext(DbContextOptions<MistChessDbContext> options) : DbContext(options)
{
    public DbSet<GuestSessionEntity> GuestSessions => Set<GuestSessionEntity>();
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<RoomPlayerEntity> RoomPlayers => Set<RoomPlayerEntity>();
    public DbSet<MatchmakingTicketEntity> MatchmakingTickets => Set<MatchmakingTicketEntity>();
    public DbSet<GameEntity> Games => Set<GameEntity>();
    public DbSet<GamePlayerEntity> GamePlayers => Set<GamePlayerEntity>();
    public DbSet<MoveEntity> Moves => Set<MoveEntity>();
    public DbSet<MoveCommandReceiptEntity> MoveCommandReceipts => Set<MoveCommandReceiptEntity>();
    public DbSet<DrawOfferEntity> DrawOffers => Set<DrawOfferEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureGuestSessions(modelBuilder);
        ConfigureRooms(modelBuilder);
        ConfigureRoomPlayers(modelBuilder);
        ConfigureMatchmakingTickets(modelBuilder);
        ConfigureGames(modelBuilder);
        ConfigureGamePlayers(modelBuilder);
        ConfigureMoves(modelBuilder);
        ConfigureMoveCommandReceipts(modelBuilder);
        ConfigureDrawOffers(modelBuilder);
    }

    private static void ConfigureGuestSessions(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<GuestSessionEntity>();
        entity.ToTable("guest_sessions", table => table.HasCheckConstraint("ck_guest_sessions_expiry", "expires_at > created_at"));
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        entity.Property(value => value.DisplayName).HasColumnName("display_name").HasMaxLength(40).IsRequired();
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        entity.HasIndex(value => value.TokenHash).IsUnique().HasDatabaseName("ux_guest_sessions_token_hash");
        entity.HasIndex(value => value.ExpiresAt).HasDatabaseName("ix_guest_sessions_expires_at");
    }

    private static void ConfigureRooms(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RoomEntity>();
        entity.ToTable("rooms", table =>
        {
            table.HasCheckConstraint("ck_rooms_status", "status IN ('WaitingForOpponent', 'WaitingForReady', 'Playing', 'Finished')");
            table.HasCheckConstraint("ck_rooms_game", "(status IN ('Playing', 'Finished') AND game_id IS NOT NULL) OR (status IN ('WaitingForOpponent', 'WaitingForReady') AND game_id IS NULL)");
        });
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.Code).HasColumnName("code").HasMaxLength(8).IsRequired();
        entity.Property(value => value.CreatorPlayerId).HasColumnName("creator_player_id");
        entity.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        entity.Property(value => value.RuleVersion).HasColumnName("rule_version").HasMaxLength(64).IsRequired();
        entity.Property(value => value.TimeControl).HasColumnName("time_control").HasMaxLength(64);
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.HasIndex(value => value.Code).IsUnique().HasDatabaseName("ux_rooms_code");
        entity.HasIndex(value => value.GameId).IsUnique().HasFilter("\"game_id\" IS NOT NULL").HasDatabaseName("ux_rooms_game_id");
        entity.HasOne(value => value.CreatorPlayer).WithMany().HasForeignKey(value => value.CreatorPlayerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(value => value.Game).WithOne().HasForeignKey<RoomEntity>(value => value.GameId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRoomPlayers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RoomPlayerEntity>();
        entity.ToTable("room_players", table => table.HasCheckConstraint("ck_room_players_seat", "seat BETWEEN 0 AND 1"));
        entity.HasKey(value => new { value.RoomId, value.PlayerId });
        entity.Property(value => value.RoomId).HasColumnName("room_id");
        entity.Property(value => value.PlayerId).HasColumnName("player_id");
        entity.Property(value => value.Seat).HasColumnName("seat");
        entity.Property(value => value.Side).HasColumnName("side").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.IsReady).HasColumnName("is_ready");
        entity.Property(value => value.JoinedAt).HasColumnName("joined_at");
        entity.HasIndex(value => new { value.RoomId, value.Seat }).IsUnique().HasDatabaseName("ux_room_players_seat");
        entity.HasIndex(value => new { value.RoomId, value.Side }).IsUnique().HasFilter("\"side\" IS NOT NULL").HasDatabaseName("ux_room_players_side");
        entity.HasOne(value => value.Room).WithMany(value => value.Players).HasForeignKey(value => value.RoomId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.Player).WithMany().HasForeignKey(value => value.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMatchmakingTickets(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<MatchmakingTicketEntity>();
        entity.ToTable("matchmaking_tickets", table =>
        {
            table.HasCheckConstraint("ck_matchmaking_tickets_expiry", "expires_at > created_at AND expires_at >= last_heartbeat_at");
            table.HasCheckConstraint("ck_matchmaking_tickets_status", "status IN ('Searching', 'Matched', 'Cancelled', 'Expired')");
            table.HasCheckConstraint("ck_matchmaking_tickets_game", "(status = 'Matched' AND game_id IS NOT NULL) OR (status <> 'Matched' AND game_id IS NULL)");
        });
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.PlayerId).HasColumnName("player_id");
        entity.Property(value => value.RuleVersion).HasColumnName("rule_version").HasMaxLength(64).IsRequired();
        entity.Property(value => value.TimeControl).HasColumnName("time_control").HasMaxLength(64);
        entity.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.LastHeartbeatAt).HasColumnName("last_heartbeat_at");
        entity.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        entity.Property(value => value.ClientRequestId).HasColumnName("client_request_id").HasMaxLength(64).IsRequired();
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.Property(value => value.ConcurrencyStamp).HasColumnName("concurrency_stamp").IsConcurrencyToken();
        entity.HasIndex(value => value.PlayerId).IsUnique().HasFilter("\"status\" = 'Searching'").HasDatabaseName("ux_matchmaking_tickets_active_player");
        entity.HasIndex(value => new { value.PlayerId, value.ClientRequestId }).IsUnique().HasDatabaseName("ux_matchmaking_tickets_request");
        entity.HasIndex(value => new { value.Status, value.RuleVersion, value.TimeControl, value.CreatedAt, value.Id }).HasDatabaseName("ix_matchmaking_tickets_pool_fifo");
        entity.HasIndex(value => value.ExpiresAt).HasFilter("\"status\" = 'Searching'").HasDatabaseName("ix_matchmaking_tickets_expiry");
        entity.HasOne(value => value.Player).WithMany().HasForeignKey(value => value.PlayerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(value => value.Game).WithMany().HasForeignKey(value => value.GameId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureGames(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<GameEntity>();
        entity.ToTable("games", table =>
        {
            table.HasCheckConstraint("ck_games_distinct_players", "red_player_id <> black_player_id");
            table.HasCheckConstraint("ck_games_status", "status IN ('Playing', 'Finished')");
            table.HasCheckConstraint("ck_games_result", "(status = 'Finished' AND result_reason IS NOT NULL AND finished_at IS NOT NULL) OR (status = 'Playing' AND result_reason IS NULL AND winner IS NULL AND finished_at IS NULL)");
            table.HasCheckConstraint("ck_games_clock", "(time_control IS NULL AND red_milliseconds IS NULL AND black_milliseconds IS NULL AND turn_started_at IS NULL) OR (time_control IS NOT NULL AND red_milliseconds >= 0 AND black_milliseconds >= 0)");
        });
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.RedPlayerId).HasColumnName("red_player_id");
        entity.Property(value => value.BlackPlayerId).HasColumnName("black_player_id");
        entity.Property(value => value.InitialStateJson).HasColumnName("initial_state").HasColumnType("jsonb").IsRequired();
        entity.Property(value => value.StateJson).HasColumnName("state").HasColumnType("jsonb").IsRequired();
        entity.Property(value => value.SideToMove).HasColumnName("side_to_move").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        entity.Property(value => value.Winner).HasColumnName("winner").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.ResultReason).HasColumnName("result_reason").HasMaxLength(32);
        entity.Property(value => value.RuleVersion).HasColumnName("rule_version").HasMaxLength(64).IsRequired();
        entity.Property(value => value.TimeControl).HasColumnName("time_control").HasMaxLength(64);
        entity.Property(value => value.RedMilliseconds).HasColumnName("red_milliseconds");
        entity.Property(value => value.BlackMilliseconds).HasColumnName("black_milliseconds");
        entity.Property(value => value.TurnStartedAt).HasColumnName("turn_started_at");
        entity.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        entity.Property(value => value.FinishedAt).HasColumnName("finished_at");
        entity.HasIndex(value => value.RedPlayerId).HasFilter("\"status\" <> 'Finished'").HasDatabaseName("ix_games_active_red_player");
        entity.HasIndex(value => value.BlackPlayerId).HasFilter("\"status\" <> 'Finished'").HasDatabaseName("ix_games_active_black_player");
        entity.HasIndex(value => new { value.Status, value.TurnStartedAt }).HasDatabaseName("ix_games_active_clock");
        entity.HasOne(value => value.RedPlayer).WithMany().HasForeignKey(value => value.RedPlayerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(value => value.BlackPlayer).WithMany().HasForeignKey(value => value.BlackPlayerId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureGamePlayers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<GamePlayerEntity>();
        entity.ToTable("game_players", table => table.HasCheckConstraint("ck_game_players_side", "side IN ('Red', 'Black')"));
        entity.HasKey(value => new { value.GameId, value.PlayerId });
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.Property(value => value.PlayerId).HasColumnName("player_id");
        entity.Property(value => value.Side).HasColumnName("side").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.IsActive).HasColumnName("is_active");
        entity.HasIndex(value => new { value.GameId, value.Side }).IsUnique().HasDatabaseName("ux_game_players_side");
        entity.HasIndex(value => value.PlayerId).IsUnique().HasFilter("\"is_active\"").HasDatabaseName("ux_game_players_active_player");
        entity.HasOne(value => value.Game).WithMany(value => value.Players).HasForeignKey(value => value.GameId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.Player).WithMany().HasForeignKey(value => value.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMoves(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<MoveEntity>();
        entity.ToTable("moves", table =>
        {
            table.HasCheckConstraint("ck_moves_from", "from_file BETWEEN 0 AND 8 AND from_rank BETWEEN 0 AND 9");
            table.HasCheckConstraint("ck_moves_to", "to_file BETWEEN 0 AND 8 AND to_rank BETWEEN 0 AND 9");
            table.HasCheckConstraint("ck_moves_ply", "ply > 0");
        });
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.Property(value => value.Ply).HasColumnName("ply");
        entity.Property(value => value.FromFile).HasColumnName("from_file");
        entity.Property(value => value.FromRank).HasColumnName("from_rank");
        entity.Property(value => value.ToFile).HasColumnName("to_file");
        entity.Property(value => value.ToRank).HasColumnName("to_rank");
        entity.Property(value => value.Side).HasColumnName("side").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.MovingPieceType).HasColumnName("moving_piece_type").HasConversion<string>().HasMaxLength(16);
        entity.Property(value => value.CapturedPieceType).HasColumnName("captured_piece_type").HasConversion<string>().HasMaxLength(16);
        entity.Property(value => value.ElapsedMilliseconds).HasColumnName("elapsed_milliseconds");
        entity.Property(value => value.ClientMoveId).HasColumnName("client_move_id").HasMaxLength(64).IsRequired();
        entity.Property(value => value.PositionKey).HasColumnName("position_key").HasMaxLength(256).IsRequired();
        entity.Property(value => value.StateAfterJson).HasColumnName("state_after").HasColumnType("jsonb").IsRequired();
        entity.Property(value => value.GameVersion).HasColumnName("game_version");
        entity.Property(value => value.WinnerAfter).HasColumnName("winner_after").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.ResultReasonAfter).HasColumnName("result_reason_after").HasMaxLength(32);
        entity.Property(value => value.RedMillisecondsAfter).HasColumnName("red_milliseconds_after");
        entity.Property(value => value.BlackMillisecondsAfter).HasColumnName("black_milliseconds_after");
        entity.Property(value => value.TurnStartedAtAfter).HasColumnName("turn_started_at_after");
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => new { value.GameId, value.Ply }).IsUnique().HasDatabaseName("ux_moves_game_ply");
        entity.HasIndex(value => new { value.GameId, value.ClientMoveId }).IsUnique().HasDatabaseName("ux_moves_game_client_move");
        entity.HasOne(value => value.Game).WithMany(value => value.Moves).HasForeignKey(value => value.GameId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMoveCommandReceipts(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<MoveCommandReceiptEntity>();
        entity.ToTable("move_command_receipts");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.Property(value => value.PlayerId).HasColumnName("player_id");
        entity.Property(value => value.ClientMoveId).HasColumnName("client_move_id").HasMaxLength(64).IsRequired();
        entity.Property(value => value.StateAfterJson).HasColumnName("state_after").HasColumnType("jsonb").IsRequired();
        entity.Property(value => value.GameVersion).HasColumnName("game_version");
        entity.Property(value => value.WinnerAfter).HasColumnName("winner_after").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.ResultReasonAfter).HasColumnName("result_reason_after").HasMaxLength(32);
        entity.Property(value => value.RedMillisecondsAfter).HasColumnName("red_milliseconds_after");
        entity.Property(value => value.BlackMillisecondsAfter).HasColumnName("black_milliseconds_after");
        entity.Property(value => value.TurnStartedAtAfter).HasColumnName("turn_started_at_after");
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => new { value.GameId, value.PlayerId, value.ClientMoveId })
            .IsUnique()
            .HasDatabaseName("ux_move_command_receipts_game_player_client_move");
        entity.HasOne(value => value.Game)
            .WithMany(value => value.MoveCommandReceipts)
            .HasForeignKey(value => value.GameId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.Player)
            .WithMany()
            .HasForeignKey(value => value.PlayerId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDrawOffers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DrawOfferEntity>();
        entity.ToTable("draw_offers", table => table.HasCheckConstraint("ck_draw_offers_status", "status IN ('Pending', 'Accepted', 'Rejected', 'Withdrawn')"));
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.Property(value => value.OfferedByPlayerId).HasColumnName("offered_by_player_id");
        entity.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        entity.HasIndex(value => value.GameId).IsUnique().HasFilter("\"status\" = 'Pending'").HasDatabaseName("ux_draw_offers_pending_game");
        entity.HasOne(value => value.Game).WithMany(value => value.DrawOffers).HasForeignKey(value => value.GameId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.OfferedByPlayer).WithMany().HasForeignKey(value => value.OfferedByPlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MistChessDesignTimeDbContextFactory : IDesignTimeDbContextFactory<MistChessDbContext>
{
    public MistChessDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MistChess");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__MistChess is required for design-time database operations.");
        }

        var options = new DbContextOptionsBuilder<MistChessDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(MistChessDbContext).Assembly.GetName().Name!))
            .Options;
        return new MistChessDbContext(options);
    }
}
