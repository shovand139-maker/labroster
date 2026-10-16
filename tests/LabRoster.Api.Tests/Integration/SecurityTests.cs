using System.Net;
using System.Net.Http.Json;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Tests.Support;
using LabRoster.Migrator;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Tests.Integration;

[Collection(SqlCollection.Name)]
public sealed class SecurityTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiFactory _api = new(sql);

    public void Dispose() => _api.Dispose();

    [SqlFact]
    public async Task Requests_without_a_valid_token_are_rejected()
    {
        var tenant = await sql.CreateTenantAsync();
        var forged = ApiFactory.TokenFor(tenant.Id, Roles.Admin, options: new DevTokenOptions
        {
            Enabled = true,
            SigningKey = "someone-elses-signing-key-0123456789abcdef",
        });

        var anonymous = await _api.ClientWithToken(null).GetAsync("/api/events");
        var wrongKey = await _api.ClientWithToken(forged).GetAsync("/api/events");

        await anonymous.ShouldHaveStatusAsync(HttpStatusCode.Unauthorized);
        await wrongKey.ShouldHaveStatusAsync(HttpStatusCode.Unauthorized);
    }

    [SqlFact]
    public async Task Viewers_can_read_but_not_change_events()
    {
        var tenant = await sql.CreateTenantAsync();
        var item = await _api.ClientFor(tenant, Roles.Instructor).CreateOpenEventAsync(tenant);
        var viewer = _api.ClientFor(tenant, Roles.Viewer);

        var read = await viewer.GetAsync($"/api/events/{item.Id}");
        var enroll = await viewer.EnrollAsync(item.Id, "asha@example.com");
        var create = await viewer.PostAsJsonAsync("/api/events", new EventRequest(
            "Not allowed", tenant.TemplateId, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(1).AddHours(2), 5));

        await read.ShouldHaveStatusAsync(HttpStatusCode.OK);
        await enroll.ShouldHaveStatusAsync(HttpStatusCode.Forbidden);
        await create.ShouldHaveStatusAsync(HttpStatusCode.Forbidden);
    }

    [SqlFact]
    public async Task Only_admins_read_the_audit_log()
    {
        var tenant = await sql.CreateTenantAsync();

        var instructor = await _api.ClientFor(tenant, Roles.Instructor).GetAsync("/api/audit");
        var admin = await _api.ClientFor(tenant, Roles.Admin).GetAsync("/api/audit");

        await instructor.ShouldHaveStatusAsync(HttpStatusCode.Forbidden);
        await admin.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [SqlFact]
    public async Task The_app_login_can_only_execute_procedures()
    {
        await using var connection = new SqlConnection(sql.AppConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.Events;";

        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());

        Assert.Equal(229, error.Number);
    }

    [SqlFact]
    public async Task The_app_login_cannot_run_admin_procedures()
    {
        await using var connection = new SqlConnection(sql.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("admin.usp_Credits_Allocate", connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@TenantId", Guid.NewGuid());
        command.Parameters.AddWithValue("@Credits", 1_000_000);

        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(229, error.Number);
    }

    [SqlFact]
    public async Task Every_table_with_a_tenant_id_is_covered_by_row_level_security()
    {
        var unprotected = await sql.QueryStringsAsync("""
            SELECT t.name
            FROM sys.tables AS t
            JOIN sys.columns AS c ON c.object_id = t.object_id AND c.name = N'TenantId'
            WHERE NOT EXISTS (SELECT 1 FROM sys.security_predicates AS p
                              WHERE p.target_object_id = t.object_id AND p.predicate_type_desc = N'FILTER')
               OR (t.name <> N'Tenants'
                   AND NOT EXISTS (SELECT 1 FROM sys.security_predicates AS p
                                   WHERE p.target_object_id = t.object_id
                                     AND p.predicate_type_desc = N'BLOCK'
                                     AND p.operation_desc = N'AFTER INSERT'))
            ORDER BY t.name;
            """);

        Assert.Empty(unprotected);
    }

    [SqlFact]
    public async Task Api_procedures_never_accept_a_tenant_or_user_from_the_caller()
    {
        var offenders = await sql.QueryStringsAsync("""
            SELECT CONCAT(OBJECT_NAME(p.object_id), N' ', p.name)
            FROM sys.parameters AS p
            JOIN sys.procedures AS pr ON pr.object_id = p.object_id
            WHERE SCHEMA_NAME(pr.schema_id) = N'api' AND p.name IN (N'@TenantId', N'@UserId', N'@ActorId');
            """);

        Assert.Empty(offenders);
    }

    [SqlFact]
    public async Task Migrations_are_idempotent()
    {
        var result = await new DatabaseMigrator(sql.AdminConnectionString).RunAsync(new MigrationOptions());

        Assert.Empty(result.Applied);
        Assert.NotEmpty(result.Unchanged);
    }

    [SqlFact]
    public async Task Unknown_api_routes_return_a_problem_not_the_spa()
    {
        var tenant = await sql.CreateTenantAsync();

        var response = await _api.ClientFor(tenant, Roles.Viewer).GetAsync("/api/does-not-exist");

        await response.ShouldHaveStatusAsync(HttpStatusCode.NotFound);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SqlFact]
    public async Task Health_endpoints_are_public_and_check_the_database()
    {
        var client = _api.ClientWithToken(null);

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        await live.ShouldHaveStatusAsync(HttpStatusCode.OK);
        await ready.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }
}
