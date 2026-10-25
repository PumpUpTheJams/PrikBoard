using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Tests;

/// <summary>
/// The test databases on the PostgreSQL server from <c>compose.yaml</c> (or the one in <c>PRIKBOARD_TEST_POSTGRES</c>).
/// They are a pool that outlives test runs: a factory leases one (an advisory lock, so parallel runs from other worktrees
/// get others), empties it with one TRUNCATE and hands it back on dispose. Creating and dropping a database for every
/// test class cost far more, since every DROP DATABASE waits for a checkpoint of the whole server. A database that is new
/// to the pool is created empty, and the API's startup migrations bring it up to date. The pool's names carry a hash of
/// the migration script, so a branch with other migrations never gets a database migrated by another one, and databases
/// left by other migrations are dropped as soon as nobody uses them.
/// </summary>
internal static class TestDatabases
{
    // 127.0.0.1 rather than localhost: on Windows each new connection to localhost first tries ::1, which takes about two
    // seconds when Docker only listens on IPv4.
    private const string DefaultServer = "Host=127.0.0.1;Port=5432;Username=prikboard;Password=prikboard";

    private const string PoolPrefix = "prikboard_tests_pool_";

    private const string MissingPrefix = "prikboard_tests_first_start_";

    private static readonly string Server = Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? DefaultServer;

    private static readonly Lazy<string> Pool = new(() => $"{PoolPrefix}{MigrationsHash()}_");

    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Server) { Database = database }.ConnectionString;

    /// <summary>Leases an empty database at the current migrations for as long as the lease isn't disposed.</summary>
    public static Lease LeaseDatabase()
    {
        var server = Open();
        try
        {
            for (var number = 0; ; number++)
            {
                var database = Pool.Value + number;
                if (!Scalar<bool>(server, "SELECT pg_try_advisory_lock(hashtext(@name))", database)) continue;
                if (Scalar<bool>(server, "SELECT EXISTS (SELECT FROM pg_database WHERE datname = @name)", database))
                {
                    Empty(database);
                }
                else
                {
                    Execute(server, $"""CREATE DATABASE "{database}" """);
                    DropUnusedFromOtherMigrations(server);
                }
                return new Lease(database, server);
            }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Leases the name of a database that does not exist yet, for a test of a first start, which creates it. Nothing
    /// waits for it to be dropped: every DROP DATABASE waits for a checkpoint of the whole server, which takes seconds
    /// while tests are writing. Instead each lease drops those of earlier runs in the background.
    /// </summary>
    public static Lease LeaseMissingDatabase()
    {
        var server = Open();
        var database = MissingPrefix + Guid.NewGuid().ToString("N");
        // Held while the test uses it, so another run's cleanup leaves it alone.
        Scalar<bool>(server, "SELECT pg_try_advisory_lock(hashtext(@name))", database);
        _ = Task.Run(DropUnusedMissing);
        return new Lease(database, server);
    }

    private static void DropUnusedMissing()
    {
        try
        {
            using var server = Open();
            DropUnused(server, Databases(server, MissingPrefix));
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
        {
            // Left for the next run.
        }
    }

    /// <summary>A database leased from the pool; disposing hands it back.</summary>
    public sealed class Lease(string database, NpgsqlConnection holder) : IDisposable
    {
        public string Database { get; } = database;

        // Closing the connection releases the advisory lock.
        public void Dispose() => holder.Dispose();
    }

    /// <summary>Deletes every row but the migration history, so the database is as the migrations left it.</summary>
    private static void Empty(string database)
    {
        using var connection = new NpgsqlConnection(ConnectionString(database));
        connection.Open();
        // Nothing to empty before the first start has migrated it.
        Execute(connection, """
            DO $$
            DECLARE tables text := (SELECT string_agg(format('%I.%I', schemaname, tablename), ', ') FROM pg_tables
                WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory');
            BEGIN
                IF tables IS NOT NULL THEN EXECUTE 'TRUNCATE ' || tables || ' RESTART IDENTITY CASCADE'; END IF;
            END $$
            """);
    }

    /// <summary>Drops the pool's databases for other migrations that no run is using, after a migration changed.</summary>
    private static void DropUnusedFromOtherMigrations(NpgsqlConnection server) =>
        DropUnused(server, Databases(server, PoolPrefix).Where(d => !d.StartsWith(Pool.Value, StringComparison.Ordinal)));

    /// <summary>Drops those of <paramref name="databases"/> that no run holds the lock of.</summary>
    private static void DropUnused(NpgsqlConnection server, IEnumerable<string> databases)
    {
        foreach (var database in databases)
        {
            if (!Scalar<bool>(server, "SELECT pg_try_advisory_lock(hashtext(@name))", database)) continue;
            Execute(server, $"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
            Scalar<bool>(server, "SELECT pg_advisory_unlock(hashtext(@name))", database);
        }
    }

    private static List<string> Databases(NpgsqlConnection server, string prefix)
    {
        using var command = new NpgsqlCommand("SELECT datname FROM pg_database WHERE starts_with(datname, @prefix)", server);
        command.Parameters.AddWithValue("prefix", prefix);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static string MigrationsHash()
    {
        using var context = new PrikBoardDbContext(
            new DbContextOptionsBuilder<PrikBoardDbContext>().UseNpgsql(ConnectionString("postgres")).Options);
        var script = context.GetService<IMigrator>().GenerateScript();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..16];
    }

    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString("postgres"));
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(NpgsqlConnection connection, string sql, string name)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", name);
        return (T)command.ExecuteScalar()!;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
