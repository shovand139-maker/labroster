using LabRoster.Api.Contracts;

namespace LabRoster.Api.Tests.Unit;

public sealed class RequestValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static EventRequest ValidEvent() => new(
        Title: "AZ-104 Bootcamp",
        TemplateId: Guid.NewGuid(),
        StartsAt: Now.AddDays(3),
        EndsAt: Now.AddDays(3).AddHours(8),
        Capacity: 20);

    [Fact]
    public void A_complete_event_is_valid()
    {
        Assert.Empty(ValidEvent().Validate(Now, isCreate: true));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var errors = new EventRequest(" x ", null, null, null, 0).Validate(Now, isCreate: true);

        Assert.Equal(new[] { "capacity", "endsAt", "startsAt", "templateId", "title" }, errors.Keys.Order().ToArray());
    }

    [Fact]
    public void An_event_must_end_after_it_starts()
    {
        var request = ValidEvent() with { EndsAt = Now.AddDays(3) };

        Assert.Contains("endsAt", request.Validate(Now, isCreate: true).Keys);
    }

    [Fact]
    public void An_event_cannot_run_longer_than_fourteen_days()
    {
        var request = ValidEvent() with { EndsAt = Now.AddDays(3) + EventRequest.MaxDuration + TimeSpan.FromMinutes(1) };

        Assert.Contains("endsAt", request.Validate(Now, isCreate: true).Keys);
    }

    [Fact]
    public void A_new_event_cannot_start_in_the_past_but_an_existing_one_can_be_edited()
    {
        var request = ValidEvent() with { StartsAt = Now.AddDays(-1), EndsAt = Now.AddDays(-1).AddHours(4) };

        Assert.Contains("startsAt", request.Validate(Now, isCreate: true).Keys);
        Assert.Empty(request.Validate(Now, isCreate: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Capacity_must_be_between_one_and_a_thousand(int capacity)
    {
        Assert.Contains("capacity", (ValidEvent() with { Capacity = capacity }).Validate(Now, isCreate: true).Keys);
    }

    [Theory]
    [InlineData("asha@example.com", true)]
    [InlineData("  Asha.Rao@Example.COM ", true)]
    [InlineData("asha@example", false)]
    [InlineData("asha example.com", false)]
    [InlineData("asha@exa mple.com", false)]
    [InlineData("", false)]
    public void Emails_are_checked_after_trimming(string email, bool valid)
    {
        var errors = new EnrollmentRequest(email, "Asha Rao").Validate();

        Assert.Equal(valid, !errors.ContainsKey("email"));
    }

    [Fact]
    public void Emails_are_normalised_to_lower_case()
    {
        Assert.Equal("asha.rao@example.com", new EnrollmentRequest(" Asha.Rao@Example.COM ", "Asha").NormalizedEmail);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("cancelled", true)]
    [InlineData("draft", false)]
    [InlineData("OPEN", false)]
    [InlineData(null, false)]
    public void Only_reachable_statuses_are_accepted(string? status, bool valid)
    {
        Assert.Equal(valid, new StatusRequest(status).Validate().Count == 0);
    }

    [Theory]
    [InlineData("3f1c7d2a-retry-1", true)]
    [InlineData("short", false)]
    [InlineData("has spaces in it", false)]
    [InlineData("quote\"inside-key", false)]
    public void Idempotency_keys_are_restricted_to_safe_characters(string key, bool valid)
    {
        Assert.Equal(valid, IdempotencyKeys.IsValid(key));
    }
}
