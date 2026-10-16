using System.Data;
using Microsoft.Data.SqlClient;

namespace LabRoster.Migrator;

/// <summary>The database principal the web API connects as.</summary>
/// <param name="Name">SQL login name, or the display name of an Entra managed identity.</param>
/// <param name="Password">Password for a SQL login. Null for an Entra principal.</param>
/// <param name="External">True to create a user FROM EXTERNAL PROVIDER (Azure SQL with Entra).</param>
public sealed record AppPrincipal(string Name, string? Password, bool External);

public sealed record MigrationOptions(bool CreateDatabase = false, bool Seed = false, AppPrincipal? App = null);

public sealed record MigrationResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Unchanged);

/// <summary>
/// Applies embedded SQL scripts and records them in dbo.SchemaVersions.
/// Each script runs in its own transaction, so a failing script leaves no partial changes.
/// </summary>
public sealed class DatabaseMigrator(string connectionString, TextWriter? log = null, IReadOnlyList<MigrationScript>? scripts = null)
{
    private readonly IReadOnlyList<MigrationScript> _scripts = scripts ?? ScriptCatalog.LoadEmbedded();
    private readonly TextWriter _log = log ?? TextWriter.Null;

    public async Task<MigrationResult> RunAsync(MigrationOptions options, CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException("The connection string must name a database (Initial Catalog).");
        }

        if (options.CreateDatabase)
        {
            await EnsureDatabaseAsync(builder, cancellationToken);
        }

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureHistoryTableAsync(connection, cancellationToken);

        var history = await LoadHistoryAsync(connection, cancellationToken);
        var applied = new List<string>();
        var unchanged = new List<string>();

        foreach (var script in _scripts)
        {
            if (script.Kind == ScriptKind.Seed && !options.Seed)
            {
                continue;
            }

            if (history.TryGetValue(script.Name, out var checksum))
            {
                if (string.Equals(checksum, script.Checksum, StringComparison.Ordinal))
                {
                    unchanged.Add(script.Name);
                    continue;
                }

                if (script.Kind != ScriptKind.Repeatable)
                {
                    throw new InvalidOperationException(
                        $"{script.Name} changed after it was applied. Applied migrations are immutable; add a new versioned script instead.");
                }
            }

            _log.WriteLine($"Applying {script.Name}");
            await ApplyAsync(connection, script, cancellationToken);
            applied.Add(script.Name);
        }

        if (options.App is not null)
        {
            await EnsureAppPrincipalAsync(connection, options.App, cancellationToken);
        }

        _log.WriteLine($"Done. {applied.Count} applied, {unchanged.Count} unchanged.");
        return new MigrationResult(applied, unchanged);
    }

    private static async Task EnsureDatabaseAsync(SqlConnectionStringBuilder target, CancellationToken cancellationToken)
    {
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """;
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = target.InitialCatalog });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureHistoryTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
            CREATE TABLE dbo.SchemaVersions
            (
                ScriptName nvarchar(200) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
                Kind       varchar(12)   NOT NULL,
                Checksum   char(64)      NOT NULL,
                AppliedAt  datetime2(3)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME(),
                AppliedBy  nvarchar(128) NOT NULL CONSTRAINT DF_SchemaVersions_AppliedBy DEFAULT SUSER_SNAME()
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> LoadHistoryAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var history = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ScriptName, Checksum FROM dbo.SchemaVersions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history[reader.GetString(0)] = reader.GetString(1);
        }

        return history;
    }

    private static async Task ApplyAsync(SqlConnection connection, MigrationScript script, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var batch in SqlBatchSplitter.Split(script.Sql))
            {
                await using var command = new SqlCommand(batch, connection, transaction) { CommandTimeout = 300 };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = new SqlCommand(
                """
                DELETE FROM dbo.SchemaVersions WHERE ScriptName = @name;
                INSERT dbo.SchemaVersions (ScriptName, Kind, Checksum) VALUES (@name, @kind, @checksum);
                """,
                connection,
                transaction))
            {
                record.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = script.Name });
                record.Parameters.Add(new SqlParameter("@kind", SqlDbType.VarChar, 12) { Value = script.Kind.ToString().ToLowerInvariant() });
                record.Parameters.Add(new SqlParameter("@checksum", SqlDbType.Char, 64) { Value = script.Checksum });
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException($"{script.Name} failed: {ex.Message}", ex);
        }
    }

    private async Task EnsureAppPrincipalAsync(SqlConnection connection, AppPrincipal app, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = app.Name });

        if (app.External)
        {
            command.CommandText = """
                IF DATABASE_PRINCIPAL_ID(@name) IS NULL
                BEGIN
                    DECLARE @sql nvarchar(max) = N'CREATE USER ' + QUOTENAME(@name) + N' FROM EXTERNAL PROVIDER;';
                    EXEC (@sql);
                END
                """;
        }
        else
        {
            if (string.IsNullOrEmpty(app.Password))
            {
                throw new InvalidOperationException("A password is required for a SQL login.");
            }

            command.Parameters.Add(new SqlParameter("@password", SqlDbType.NVarChar, 128) { Value = app.Password });
            command.CommandText = """
                DECLARE @sql nvarchar(max);
                IF SUSER_ID(@name) IS NULL
                    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@name) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N';';
                ELSE
                    SET @sql = N'ALTER LOGIN ' + QUOTENAME(@name) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N';';
                EXEC (@sql);

                IF DATABASE_PRINCIPAL_ID(@name) IS NULL
                BEGIN
                    SET @sql = N'CREATE USER ' + QUOTENAME(@name) + N' FOR LOGIN ' + QUOTENAME(@name) + N';';
                    EXEC (@sql);
                END
                """;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var membership = connection.CreateCommand();
        membership.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = app.Name });
        membership.CommandText = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.database_role_members AS rm
                JOIN sys.database_principals AS r ON r.principal_id = rm.role_principal_id
                JOIN sys.database_principals AS m ON m.principal_id = rm.member_principal_id
                WHERE r.name = N'app_executor' AND m.name = @name
            )
            BEGIN
                DECLARE @sql nvarchar(max) = N'ALTER ROLE app_executor ADD MEMBER ' + QUOTENAME(@name) + N';';
                EXEC (@sql);
            END
            """;
        await membership.ExecuteNonQueryAsync(cancellationToken);

        _log.WriteLine($"App principal {app.Name} can execute the api schema.");
    }
}
