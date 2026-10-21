using System.Data.Common;
using Npgsql;
using Stellaegis.Operations.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Operations.Tests;

/// <summary>
/// A database of its own for one test class, installed by <see cref="DatabaseLifecycle.InstallAsync"/> from the
/// scripts of the package, and the ordinary role the tests connect under.
/// </summary>
/// <remarks>
/// The administration connection comes from <c>STELLAEGIS_TEST_POSTGRESQL</c>; without it, the tests that need
/// the database are skipped. xunit builds one fixture per test class, and the classes run in parallel on the same
/// server: each class gets a database named after it, so that resetting one touches no other.
/// </remarks>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string Variable = "STELLAEGIS_TEST_POSTGRESQL";
    private const string AppRole = "stellaegis_operations_app";

    public static readonly ApplicationFrame Frame = ApplicationFrame.ManyCompartments().WithSharedSpace();

    private string? _admin;
    private string? _database;

    /// <summary>Why the database cannot be used, or <see langword="null"/> when it can.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>The connection string of the test database under the ordinary role.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            Unavailable = $"{Variable} is not set.";
            return;
        }

        var database = $"stellaegis_operations_{Guid.NewGuid():N}"[..40];
        var server = new NpgsqlConnectionStringBuilder(configured);
        await using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            await Execute(connection, $"""
                do $$ begin
                  create role {AppRole} login password '{AppRole}';
                exception when duplicate_object or unique_violation then null;
                end $$;
                """);
            await Execute(connection, $"create database \"{database}\"");
        }

        _database = database;
        _admin = new NpgsqlConnectionStringBuilder(configured) { Database = database }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Username = AppRole, Password = AppRole }.ConnectionString;
        await DatabaseLifecycle.InstallAsync([OperationsDatabase.Value], Frame, OpenAdminAsync, AppRole);
    }

    /// <summary>Empties the tables of the module, between two tests of the class.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await Execute(connection, "truncate ops.jobs, ops.schedule_runs, ops.schedules");
    }

    /// <summary>Opens the test database under the administration role.</summary>
    public async Task<DbConnection> OpenAdminAsync(ModuleDatabase _, CancellationToken ct) => await OpenAsync(_admin!, ct);

    /// <summary>Opens the test database under the ordinary role.</summary>
    public async Task<DbConnection> OpenAppAsync(ModuleDatabase _, CancellationToken ct) => await OpenAsync(AppConnectionString, ct);

    public async ValueTask DisposeAsync()
    {
        if (_database is null)
            return;
        NpgsqlConnection.ClearAllPools();
        var server = new NpgsqlConnectionStringBuilder(_admin) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await Execute(connection, $"drop database if exists \"{_database}\" with (force)");
    }

    private static async Task<DbConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
