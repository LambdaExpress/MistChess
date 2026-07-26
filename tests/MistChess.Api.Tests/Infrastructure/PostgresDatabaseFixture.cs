using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MistChess.Infrastructure.Persistence;
using Npgsql;
using Respawn;

namespace MistChess.Api.Tests.Infrastructure;

public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    private const string EnvironmentVariable = "MISTCHESS_TEST_ADMIN_CONNECTION_STRING";
    private string? _adminConnectionString;
    private string? _databaseName;
    private Respawner? _respawner;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var configuredAdminConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredAdminConnectionString))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is required for PostgreSQL integration tests.");
        }

        var admin = new NpgsqlConnectionStringBuilder(configuredAdminConnectionString);
        if (!IsLocalHost(admin.Host) || !StringComparer.OrdinalIgnoreCase.Equals(admin.Database, "postgres"))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must target the local PostgreSQL maintenance database 'postgres'.");
        }

        admin.Pooling = false;
        var adminConnectionString = admin.ConnectionString;
        _adminConnectionString = adminConnectionString;
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var versionCommand = new NpgsqlCommand("SHOW server_version_num", connection);
            var versionText = (string?)await versionCommand.ExecuteScalarAsync();
            if (!int.TryParse(versionText, out var version) || version < 180000 || version >= 190000)
            {
                throw new InvalidOperationException("PostgreSQL 18 is required for integration tests.");
            }

            var databaseName = $"mistchess_test_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
            _databaseName = databaseName;
            await using var createCommand = new NpgsqlCommand(
                $"CREATE DATABASE {QuoteIdentifier(databaseName)}",
                connection);
            await createCommand.ExecuteNonQueryAsync();
        }

        try
        {
            var application = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
            {
                Database = _databaseName!,
                Pooling = true
            };
            ConnectionString = application.ConnectionString;
            var options = new DbContextOptionsBuilder<MistChessDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;
            await using (var db = new MistChessDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await using var resetConnection = new NpgsqlConnection(ConnectionString);
            await resetConnection.OpenAsync();
            _respawner = await Respawner.CreateAsync(resetConnection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = ["public"],
                TablesToIgnore = ["__EFMigrationsHistory"]
            });
        }
        catch
        {
            await DropDatabaseAsync();
            throw;
        }
    }

    public async Task ResetAsync()
    {
        var respawner = _respawner
            ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await DropDatabaseAsync();
    }

    private async Task DropDatabaseAsync()
    {
        var adminConnectionString = _adminConnectionString;
        var databaseName = _databaseName;
        if (adminConnectionString is null || databaseName is null)
        {
            return;
        }

        if (!databaseName.StartsWith("mistchess_test_", StringComparison.Ordinal) ||
            databaseName.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new InvalidOperationException("Refusing to drop a database with an unsafe name.");
        }

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE)",
            connection);
        await command.ExecuteNonQueryAsync();
        _databaseName = null;
    }

    private static bool IsLocalHost(string? host) =>
        StringComparer.OrdinalIgnoreCase.Equals(host, "localhost") || StringComparer.Ordinal.Equals(host, "127.0.0.1");

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "PostgreSQL integration";
}
