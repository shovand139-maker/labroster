using System.Net;
using System.Net.Http.Json;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Tests.Support;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Tests.Integration;

[Collection(SqlCollection.Name)]
public sealed class TenantIsolationTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiFactory _api = new(sql);

    public void Dispose() => _api.Dispose();

    [SqlFact]
    public async Task Another_organization_cannot_see_or_touch_an_event()
    {
        var contoso = await sql.CreateTenantAsync();
        var fabrikam = await sql.CreateTenantAsync();
        var contosoClient = _api.ClientFor(contoso, Roles.Instructor);
        var fabrikamClient = _api.ClientFor(fabrikam, Roles.Admin);
        var item = await contosoClient.CreateOpenEventAsync(contoso);

        var read = await fabrikamClient.GetAsync($"/api/events/{item.Id}");
        var roster = await fabrikamClient.GetAsync($"/api/events/{item.Id}/roster");
        var enroll = await fabrikamClient.EnrollAsync(item.Id, "intruder@fabrikam.example");
        var list = await fabrikamClient.GetFromJsonAsync<PagedResult<EventDto>>("/api/events?pageSize=100");

        await read.ShouldHaveStatusAsync(HttpStatusCode.NotFound);
        await roster.ShouldHaveStatusAsync(HttpStatusCode.NotFound);
        await enroll.ShouldHaveStatusAsync(HttpStatusCode.NotFound);
        Assert.DoesNotContain(list!.Items, e => e.Id == item.Id);
        Assert.Equal(0, (await contosoClient.RosterAsync(item.Id)).Total);
    }

    [SqlFact]
    public async Task Pooled_connections_never_carry_one_tenants_context_into_another_request()
    {
        var first = await sql.CreateTenantAsync();
        var second = await sql.CreateTenantAsync();
        var firstClient = _api.ClientFor(first, Roles.Viewer);
        var secondClient = _api.ClientFor(second, Roles.Viewer);

        for (var i = 0; i < 15; i++)
        {
            var a = await firstClient.GetFromJsonAsync<MeDto>("/api/me");
            var b = await secondClient.GetFromJsonAsync<MeDto>("/api/me");
            Assert.Equal(first.Id, a!.TenantId);
            Assert.Equal(second.Id, b!.TenantId);
        }
    }

    [SqlFact]
    public async Task Row_level_security_blocks_writes_into_another_tenant_even_for_the_database_owner()
    {
        var owner = await sql.CreateTenantAsync();
        var other = await sql.CreateTenantAsync();

        await using var connection = new SqlConnection(sql.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_set_session_context @key = N'TenantId', @value = @other;
            INSERT dbo.Events (TenantId, TemplateId, Title, StartsAt, EndsAt, Capacity, CreatedBy)
            VALUES (@owner, @template, N'Smuggled event', '2030-01-01', '2030-01-02', 5, N'attacker');
            """;
        command.Parameters.AddWithValue("@other", other.Id);
        command.Parameters.AddWithValue("@owner", owner.Id);
        command.Parameters.AddWithValue("@template", owner.TemplateId);

        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(33504, error.Number);
    }

    [SqlFact]
    public async Task An_unknown_organization_is_told_it_is_not_onboarded()
    {
        var client = _api.ClientWithToken(ApiFactory.TokenFor(Guid.NewGuid(), Roles.Admin));

        var response = await client.GetAsync("/api/me");

        await response.ShouldHaveStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal("tenant_not_onboarded", (await response.ReadProblemAsync()).Code);
    }
}
