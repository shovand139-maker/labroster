using System.Data;
using LabRoster.Migrator;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Tests.Support;

/// <summary>
/// Creates a throwaway database on a real SQL Server, applies the real migrations and
/// creates a least-privilege app login, exactly as a deployment does. Point
/// LABROSTER_TEST_SQL at a server (for example the docker compose one) to run these
/// tests; without it they are skipped.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "LABROSTER_TEST_SQL";

    private readonly string _appLogin = $"labroster_test_{Guid.NewGuid():N}";
    private readonly string _appPassword = $"Lr!{Guid.NewGuid():N}Aa9";

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(ConnectionVariable);

    public string DatabaseName { get; } = $"LabRosterTests_{Guid.NewGuid():N}";

    /// <summary>Connects as the admin (dbo) to the test database.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Connects as the app login, which can only execute api.* procedures.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerConnectionString))
        {
            return;
        }

        AdminConnectionString = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = DatabaseName }.ConnectionString;

        await new DatabaseMigrator(AdminConnectionString).RunAsync(new MigrationOptions(
            CreateDatabase: true,
            App: new AppPrincipal(_appLogin, _appPassword, External: false)));

        AppConnectionString = new SqlConnectionStringBuilder(AdminConnectionString)
        {
            UserID = _appLogin,
            Password = _appPassword,
            IntegratedSecurity = false,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerConnectionString))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            IF SUSER_ID(N'{_appLogin}') IS NOT NULL DROP LOGIN [{_appLogin}];
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Provisions a fresh organization with one lab template, through the admin procedures.</summary>
    public async Task<TestTenant> CreateTenantAsync(int credits = 1000, int creditsPerSeat = 10)
    {
        var tenant = new TestTenant(Guid.NewGuid(), Guid.NewGuid(), credits, creditsPerSeat);

        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        await using (var provision = Procedure(connection, "admin.usp_Tenant_Provision"))
        {
            provision.Parameters.Add("@TenantId", SqlDbType.UniqueIdentifier).Value = tenant.Id;
            provision.Parameters.Add("@Name", SqlDbType.NVarChar).Value = $"Test organization {tenant.Id:N}";
            provision.Parameters.Add("@Slug", SqlDbType.VarChar).Value = $"t{tenant.Id:N}";
            provision.Parameters.Add("@InitialCredits", SqlDbType.Int).Value = credits;
            await provision.ExecuteNonQueryAsync();
        }

        await using (var template = Procedure(connection, "admin.usp_Template_Upsert"))
        {
            template.Parameters.Add("@TenantId", SqlDbType.UniqueIdentifier).Value = tenant.Id;
            template.Parameters.Add("@TemplateId", SqlDbType.UniqueIdentifier).Value = tenant.TemplateId;
            template.Parameters.Add("@Title", SqlDbType.NVarChar).Value = "Integration test lab";
            template.Parameters.Add("@Cloud", SqlDbType.VarChar).Value = "azure";
            template.Parameters.Add("@DurationMinutes", SqlDbType.Int).Value = 60;
            template.Parameters.Add("@CreditsPerSeat", SqlDbType.Int).Value = creditsPerSeat;
            await template.ExecuteNonQueryAsync();
        }

        return tenant;
    }

    /// <summary>Runs a scalar query as the admin with SESSION_CONTEXT set to one tenant.</summary>
    public async Task<T> ScalarAsTenantAsync<T>(Guid tenantId, string sql)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXEC sys.sp_set_session_context @key = N'TenantId', @value = @tenant; {sql}";
        command.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = tenantId;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs a query as the admin and returns the first column of every row.</summary>
    public async Task<IReadOnlyList<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };
}

public sealed record TestTenant(Guid Id, Guid TemplateId, int InitialCredits, int CreditsPerSeat);

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

/// <summary>A fact that needs SQL Server. Skipped, with the reason, when none is configured.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlServerFixture.ServerConnectionString))
        {
            Skip = $"Set {SqlServerFixture.ConnectionVariable} to a SQL Server connection string to run integration tests.";
        }
    }
}
