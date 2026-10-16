using System.Net;
using System.Net.Http.Json;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Tests.Support;

namespace LabRoster.Api.Tests.Integration;

[Collection(SqlCollection.Name)]
public sealed class EventLifecycleTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiFactory _api = new(sql);

    public void Dispose() => _api.Dispose();

    [SqlFact]
    public async Task Creating_an_event_returns_its_location_and_etag()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);

        var created = await client.CreateEventAsync(tenant, capacity: 12, title: "AZ-104 Bootcamp");
        var fetched = await client.GetAsync($"/api/events/{created.Id}");

        Assert.Equal("draft", created.Status);
        Assert.Equal(12, created.SeatsLeft);
        await fetched.ShouldHaveStatusAsync(HttpStatusCode.OK);
        Assert.Equal(created.ETag, fetched.Headers.ETag?.ToString());
    }

    [SqlFact]
    public async Task Invalid_input_lists_every_field_problem()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);

        var response = await client.PostAsJsonAsync("/api/events", new EventRequest("x", null, null, null, 5000));

        await response.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("validation_failed", problem.Code);
        Assert.Equal(new[] { "capacity", "endsAt", "startsAt", "templateId", "title" }, problem.Errors!.Keys.Order());
    }

    [SqlFact]
    public async Task A_template_from_another_organization_is_rejected()
    {
        var tenant = await sql.CreateTenantAsync();
        var other = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);

        var response = await client.PostAsJsonAsync("/api/events", new EventRequest(
            "Borrowed template", other.TemplateId, DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(2).AddHours(3), 10));

        await response.ShouldHaveStatusAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("unknown_template", (await response.ReadProblemAsync()).Code);
    }

    [SqlFact]
    public async Task Updates_require_if_match()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateEventAsync(tenant);

        var response = await client.PutAsJsonAsync($"/api/events/{item.Id}", Edit(item, capacity: 20));

        await response.ShouldHaveStatusAsync((HttpStatusCode)428);
    }

    [SqlFact]
    public async Task A_stale_etag_cannot_overwrite_someone_elses_change()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var original = await client.CreateEventAsync(tenant);

        var first = await PutAsync(client, original, Edit(original, capacity: 20), original.ETag);
        var second = await PutAsync(client, original, Edit(original, capacity: 30), original.ETag);

        await first.ShouldHaveStatusAsync(HttpStatusCode.OK);
        await second.ShouldHaveStatusAsync(HttpStatusCode.PreconditionFailed);
        Assert.Equal("stale_etag", (await second.ReadProblemAsync()).Code);
        Assert.Equal(20, (await client.GetFromJsonAsync<EventDto>($"/api/events/{original.Id}"))!.Capacity);
    }

    [SqlFact]
    public async Task Capacity_cannot_drop_below_the_learners_already_enrolled()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant, capacity: 5);
        await client.EnrollAsync(item.Id, "a@example.com");
        await client.EnrollAsync(item.Id, "b@example.com");
        await client.EnrollAsync(item.Id, "c@example.com");
        var current = (await client.GetFromJsonAsync<EventDto>($"/api/events/{item.Id}"))!;

        var response = await PutAsync(client, current, Edit(current, capacity: 2), current.ETag);

        await response.ShouldHaveStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("capacity_below_enrolled", (await response.ReadProblemAsync()).Code);
    }

    [SqlFact]
    public async Task Adding_seats_fills_them_from_the_waitlist()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant, capacity: 1);
        await client.EnrollAsync(item.Id, "a@example.com");
        await client.EnrollAsync(item.Id, "b@example.com");
        await client.EnrollAsync(item.Id, "c@example.com");
        var current = (await client.GetFromJsonAsync<EventDto>($"/api/events/{item.Id}"))!;

        var response = await PutAsync(client, current, Edit(current, capacity: 3), current.ETag);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var updated = await response.ReadAsync<EventDto>();
        Assert.Equal(3, updated.Enrolled);
        Assert.Equal(0, updated.Waitlisted);
    }

    [SqlFact]
    public async Task Cancelling_an_event_releases_every_seat_and_refunds_every_credit()
    {
        var tenant = await sql.CreateTenantAsync(credits: 500, creditsPerSeat: 25);
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant, capacity: 3);
        foreach (var email in new[] { "a@example.com", "b@example.com", "c@example.com", "d@example.com" })
        {
            await client.EnrollAsync(item.Id, email);
        }

        Assert.Equal(500 - 75, await client.CreditBalanceAsync());
        var current = (await client.GetFromJsonAsync<EventDto>($"/api/events/{item.Id}"))!;

        var response = await client.SetStatusAsync(item.Id, "cancelled", current.ETag);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        Assert.Equal("cancelled", (await response.ReadAsync<EventDto>()).Status);
        Assert.All((await client.RosterAsync(item.Id)).Items, e => Assert.Equal("cancelled", e.Status));
        Assert.Equal(500, await client.CreditBalanceAsync());
    }

    [SqlFact]
    public async Task A_draft_cannot_jump_straight_to_closed()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var draft = await client.CreateEventAsync(tenant);

        var response = await client.SetStatusAsync(draft.Id, "closed", draft.ETag);

        await response.ShouldHaveStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("invalid_transition", (await response.ReadProblemAsync()).Code);
    }

    [SqlFact]
    public async Task Every_change_is_written_to_the_audit_log()
    {
        var tenant = await sql.CreateTenantAsync();
        var instructor = _api.ClientFor(tenant, Roles.Instructor, userId: "arjun");
        var admin = _api.ClientFor(tenant, Roles.Admin);
        var item = await instructor.CreateOpenEventAsync(tenant);
        await instructor.EnrollAsync(item.Id, "asha@example.com");

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/audit");

        var actions = audit!.Items.Select(a => a.Action).ToList();
        Assert.Contains("event.created", actions);
        Assert.Contains("event.status_changed", actions);
        Assert.Contains("enrollment.created", actions);
        Assert.All(audit.Items.Where(a => a.Action.StartsWith("event.", StringComparison.Ordinal)), a => Assert.Equal("arjun", a.Actor));
    }

    private static EventRequest Edit(EventDto item, int capacity) =>
        new(item.Title, null, item.StartsAt, item.EndsAt, capacity);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, EventDto item, EventRequest body, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/events/{item.Id}") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }
}
