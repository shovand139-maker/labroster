using System.Net;
using System.Text;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Tests.Support;

namespace LabRoster.Api.Tests.Integration;

[Collection(SqlCollection.Name)]
public sealed class EnrollmentTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiFactory _api = new(sql);

    public void Dispose() => _api.Dispose();

    [SqlFact]
    public async Task Parallel_enrollments_never_oversell_seats_or_credits()
    {
        var tenant = await sql.CreateTenantAsync(credits: 1000, creditsPerSeat: 15);
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant, capacity: 5);

        var responses = await Task.WhenAll(Enumerable.Range(1, 25)
            .Select(i => client.EnrollAsync(item.Id, $"learner{i}@example.com", $"Learner {i}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var roster = await client.RosterAsync(item.Id);
        Assert.Equal(5, roster.Items.Count(e => e.Status == "enrolled"));
        Assert.Equal(20, roster.Items.Count(e => e.Status == "waitlisted"));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (int?)i), roster.Items.Where(e => e.Status == "waitlisted").Select(e => e.WaitlistPosition));
        Assert.Equal(1000 - (5 * 15), await client.CreditBalanceAsync());
        Assert.Equal(5, await sql.ScalarAsTenantAsync<int>(tenant.Id, $"SELECT COUNT(*) FROM dbo.CreditLedger WHERE EventId = '{item.Id}' AND Reason = 'enrollment';"));
    }

    [SqlFact]
    public async Task A_retried_request_with_the_same_idempotency_key_enrolls_once()
    {
        var tenant = await sql.CreateTenantAsync(credits: 100, creditsPerSeat: 10);
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant);

        var first = await client.EnrollAsync(item.Id, "asha@example.com", idempotencyKey: "enroll-asha-0001");
        var retry = await client.EnrollAsync(item.Id, "asha@example.com", idempotencyKey: "enroll-asha-0001");

        await first.ShouldHaveStatusAsync(HttpStatusCode.Created);
        await retry.ShouldHaveStatusAsync(HttpStatusCode.OK);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal((await first.ReadAsync<EnrollmentDto>()).Id, (await retry.ReadAsync<EnrollmentDto>()).Id);
        Assert.Equal(90, await client.CreditBalanceAsync());
    }

    [SqlFact]
    public async Task The_same_learner_cannot_be_enrolled_twice()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant);
        await client.EnrollAsync(item.Id, "asha@example.com");

        var duplicate = await client.EnrollAsync(item.Id, "  ASHA@example.com ");

        await duplicate.ShouldHaveStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("already_enrolled", (await duplicate.ReadProblemAsync()).Code);
    }

    [SqlFact]
    public async Task Cancelling_an_enrollment_refunds_it_and_promotes_the_first_waitlisted_learner()
    {
        var tenant = await sql.CreateTenantAsync(credits: 100, creditsPerSeat: 20);
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant, capacity: 1);
        var seated = await (await client.EnrollAsync(item.Id, "first@example.com")).ReadAsync<EnrollmentDto>();
        await client.EnrollAsync(item.Id, "second@example.com");
        await client.EnrollAsync(item.Id, "third@example.com");

        var response = await client.PostAsync($"/api/enrollments/{seated.Id}/cancel", null);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var result = await response.ReadAsync<CancelResult>();
        Assert.Equal(20, result.CreditsRefunded);
        Assert.Equal(1, result.PromotedFromWaitlist);

        var roster = await client.RosterAsync(item.Id);
        Assert.Equal("enrolled", roster.Items.Single(e => e.Email == "second@example.com").Status);
        var third = roster.Items.Single(e => e.Email == "third@example.com");
        Assert.Equal("waitlisted", third.Status);
        Assert.Equal(1, third.WaitlistPosition);
        Assert.Equal(80, await client.CreditBalanceAsync());
    }

    [SqlFact]
    public async Task Cancelling_twice_is_a_conflict()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant);
        var enrollment = await (await client.EnrollAsync(item.Id, "asha@example.com")).ReadAsync<EnrollmentDto>();
        await client.PostAsync($"/api/enrollments/{enrollment.Id}/cancel", null);

        var again = await client.PostAsync($"/api/enrollments/{enrollment.Id}/cancel", null);

        await again.ShouldHaveStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("already_cancelled", (await again.ReadProblemAsync()).Code);
    }

    [SqlFact]
    public async Task A_seat_needs_credits()
    {
        var tenant = await sql.CreateTenantAsync(credits: 9, creditsPerSeat: 10);
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant);

        var response = await client.EnrollAsync(item.Id, "asha@example.com");

        await response.ShouldHaveStatusAsync(HttpStatusCode.PaymentRequired);
        Assert.Equal("insufficient_credits", (await response.ReadProblemAsync()).Code);
        Assert.Equal(0, (await client.RosterAsync(item.Id)).Total);
    }

    [SqlFact]
    public async Task Learners_cannot_be_enrolled_before_the_event_opens()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var draft = await client.CreateEventAsync(tenant);

        var response = await client.EnrollAsync(draft.Id, "asha@example.com");

        await response.ShouldHaveStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("event_not_open", (await response.ReadProblemAsync()).Code);
    }

    [SqlFact]
    public async Task A_csv_roster_reports_an_outcome_for_every_row()
    {
        var tenant = await sql.CreateTenantAsync(credits: 1000, creditsPerSeat: 10);
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant, capacity: 2);
        const string csv = """
            email,fullName
            one@example.com,Learner One
            two@example.com,Learner Two
            ONE@example.com,Learner One Again
            not-an-email,Broken Row
            three@example.com,Learner Three
            four@example.com,Learner Four
            """;

        var response = await client.PostAsync(
            $"/api/events/{item.Id}/enrollments/bulk",
            new StringContent(csv, Encoding.UTF8, "text/csv"));

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var result = await response.ReadAsync<BulkEnrollmentResult>();
        Assert.Equal(2, result.Enrolled);
        Assert.Equal(2, result.Waitlisted);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(
            new[] { "enrolled", "enrolled", "duplicate_in_file", "invalid_email", "waitlisted", "waitlisted" },
            result.Rows.OrderBy(r => r.Row).Select(r => r.Outcome));
        Assert.Equal(1000 - 20, await client.CreditBalanceAsync());
    }

    [SqlFact]
    public async Task A_malformed_csv_is_rejected_before_touching_the_database()
    {
        var tenant = await sql.CreateTenantAsync();
        var client = _api.ClientFor(tenant, Roles.Instructor);
        var item = await client.CreateOpenEventAsync(tenant);

        var response = await client.PostAsync(
            $"/api/events/{item.Id}/enrollments/bulk",
            new StringContent("one@example.com,Learner One", Encoding.UTF8, "text/csv"));

        await response.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
        Assert.NotNull((await response.ReadProblemAsync()).Errors?["file"]);
    }
}
