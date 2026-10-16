using System.Text.RegularExpressions;

namespace LabRoster.Api.Contracts;

/// <summary>
/// Request validation runs in the API so callers get every field error at once.
/// The stored procedures and table constraints repeat the critical checks, so a
/// caller that skips the API (or a bug here) still cannot store bad data.
/// </summary>
public sealed record EventRequest(string? Title, Guid? TemplateId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? Capacity)
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(14);

    public Dictionary<string, string[]> Validate(DateTimeOffset now, bool isCreate)
    {
        var errors = new Dictionary<string, string[]>();
        var title = Title?.Trim() ?? string.Empty;

        if (title.Length is < 3 or > 160)
        {
            errors["title"] = ["Title must be between 3 and 160 characters."];
        }

        if (isCreate && (TemplateId is null || TemplateId == Guid.Empty))
        {
            errors["templateId"] = ["Choose a lab template."];
        }

        if (StartsAt is null)
        {
            errors["startsAt"] = ["Start time is required."];
        }
        else if (isCreate && StartsAt < now.AddMinutes(-5))
        {
            errors["startsAt"] = ["A new event must start in the future."];
        }

        if (EndsAt is null)
        {
            errors["endsAt"] = ["End time is required."];
        }
        else if (StartsAt is not null && EndsAt <= StartsAt)
        {
            errors["endsAt"] = ["The event must end after it starts."];
        }
        else if (StartsAt is not null && EndsAt - StartsAt > MaxDuration)
        {
            errors["endsAt"] = ["An event can run for at most 14 days."];
        }

        if (Capacity is null or < 1 or > 1000)
        {
            errors["capacity"] = ["Capacity must be between 1 and 1000 seats."];
        }

        return errors;
    }
}

public sealed record StatusRequest(string? Status)
{
    public static readonly string[] Allowed = ["open", "closed", "cancelled"];

    public Dictionary<string, string[]> Validate() =>
        Status is not null && Allowed.Contains(Status)
            ? new Dictionary<string, string[]>()
            : new Dictionary<string, string[]> { ["status"] = ["Status must be open, closed or cancelled."] };
}

public sealed partial record EnrollmentRequest(string? Email, string? FullName)
{
    public string NormalizedEmail => (Email ?? string.Empty).Trim().ToLowerInvariant();

    public string NormalizedName => (FullName ?? string.Empty).Trim();

    public Dictionary<string, string[]> Validate(string prefix = "")
    {
        var errors = new Dictionary<string, string[]>();

        if (!IsValidEmail(NormalizedEmail))
        {
            errors[prefix + "email"] = ["Enter a valid email address."];
        }

        if (NormalizedName.Length is < 2 or > 120)
        {
            errors[prefix + "fullName"] = ["Full name must be between 2 and 120 characters."];
        }

        return errors;
    }

    public static bool IsValidEmail(string email) => email.Length <= 254 && EmailPattern().IsMatch(email);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}

public sealed record BulkEnrollmentRequest(List<EnrollmentRequest>? Learners)
{
    public const int MaxLearners = 500;
}

public static partial class IdempotencyKeys
{
    /// <summary>Clients send a unique key per logical request so a retry never enrolls twice.</summary>
    public static bool IsValid(string key) => KeyPattern().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{8,80}$")]
    private static partial Regex KeyPattern();
}
