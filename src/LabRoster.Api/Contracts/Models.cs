using System.Text.Json;

namespace LabRoster.Api.Contracts;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record EventDto(
    Guid Id,
    string Title,
    Guid TemplateId,
    string TemplateTitle,
    string Cloud,
    int CreditsPerSeat,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int Capacity,
    int Enrolled,
    int Waitlisted,
    int SeatsLeft,
    string Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ETag);

public sealed record EnrollmentDto(
    Guid Id,
    Guid EventId,
    string Email,
    string FullName,
    string Status,
    int CreditsCharged,
    int? WaitlistPosition,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt);

public sealed record BulkEnrollmentRow(int Row, string Email, string Outcome, Guid? EnrollmentId);

public sealed record BulkEnrollmentResult(int Enrolled, int Waitlisted, int Skipped, IReadOnlyList<BulkEnrollmentRow> Rows);

public sealed record CancelResult(Guid EnrollmentId, Guid EventId, string PreviousStatus, int CreditsRefunded, int PromotedFromWaitlist);

public sealed record TemplateDto(Guid Id, string Title, string Cloud, int DurationMinutes, int CreditsPerSeat);

public sealed record LedgerEntryDto(long Id, int Delta, string Reason, Guid? EventId, string? EventTitle, DateTimeOffset CreatedAt);

public sealed record CreditsDto(int Balance, int Allocated, int Spent30Days, int Refunded30Days, IReadOnlyList<LedgerEntryDto> Recent);

public sealed record AuditEntryDto(long Id, string Actor, string Action, Guid? EntityId, JsonElement? Details, DateTimeOffset CreatedAt);

public sealed record PermissionsDto(bool ManageEvents, bool ReadAudit);

public sealed record MeDto(
    string UserId,
    string Name,
    string Role,
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    int CreditBalance,
    PermissionsDto Permissions);

public sealed record DevUserDto(string Id, string Name, string Email, string Role, string TenantName);

public sealed record DevTokenRequest(string? UserId);

public sealed record DevTokenResponse(string AccessToken, DateTimeOffset ExpiresAt, DevUserDto User);
