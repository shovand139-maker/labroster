using System.Data;
using System.Data.Common;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Data;

public sealed record EnrollmentOutcome(EnrollmentDto Enrollment, bool Replayed);

public sealed class EnrollmentStore(ITenantConnectionFactory connections)
{
    public async Task<EnrollmentOutcome> CreateAsync(
        CurrentUser user, Guid eventId, EnrollmentRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Enrollment_Create")
            .With("@EventId", SqlDbType.UniqueIdentifier, eventId)
            .With("@Email", SqlDbType.NVarChar, request.NormalizedEmail, 254)
            .With("@FullName", SqlDbType.NVarChar, request.NormalizedName, 120)
            .With("@IdempotencyKey", SqlDbType.VarChar, idempotencyKey, 80);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("api.usp_Enrollment_Create returned no enrollment.");
        }

        return new EnrollmentOutcome(Map(reader), reader.GetBoolean("Replayed"));
    }

    public async Task<BulkEnrollmentResult> BulkCreateAsync(
        CurrentUser user, Guid eventId, IReadOnlyList<EnrollmentRequest> learners, CancellationToken cancellationToken)
    {
        using var table = new DataTable();
        table.Columns.Add("RowNumber", typeof(int));
        table.Columns.Add("Email", typeof(string));
        table.Columns.Add("FullName", typeof(string));
        for (var i = 0; i < learners.Count; i++)
        {
            table.Rows.Add(i + 1, Truncate(learners[i].NormalizedEmail, 254), Truncate(learners[i].NormalizedName, 120));
        }

        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Enrollment_BulkCreate")
            .With("@EventId", SqlDbType.UniqueIdentifier, eventId);
        command.Parameters.Add(new SqlParameter("@Learners", SqlDbType.Structured)
        {
            TypeName = "api.LearnerList",
            Value = table,
        });

        var rows = new List<BulkEnrollmentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BulkEnrollmentRow(
                reader.GetInt32("RowNumber"),
                reader.GetString("Email"),
                reader.GetString("Outcome"),
                reader.GetNullableGuid("EnrollmentId")));
        }

        var enrolled = rows.Count(r => r.Outcome == "enrolled");
        var waitlisted = rows.Count(r => r.Outcome == "waitlisted");
        return new BulkEnrollmentResult(enrolled, waitlisted, rows.Count - enrolled - waitlisted, rows);
    }

    public async Task<CancelResult> CancelAsync(CurrentUser user, Guid enrollmentId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Enrollment_Cancel")
            .With("@EnrollmentId", SqlDbType.UniqueIdentifier, enrollmentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("api.usp_Enrollment_Cancel returned no result.");
        }

        return new CancelResult(
            reader.GetGuid("EnrollmentId"),
            reader.GetGuid("EventId"),
            reader.GetString("PreviousStatus"),
            reader.GetInt32("CreditsRefunded"),
            reader.GetInt32("PromotedCount"));
    }

    public async Task<PagedResult<EnrollmentDto>> RosterAsync(
        CurrentUser user, Guid eventId, string? search, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Roster_Get")
            .With("@EventId", SqlDbType.UniqueIdentifier, eventId)
            .With("@Search", SqlDbType.NVarChar, search, 100)
            .With("@Status", SqlDbType.VarChar, status, 12)
            .With("@Page", SqlDbType.Int, page)
            .With("@PageSize", SqlDbType.Int, pageSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var total = await reader.ReadAsync(cancellationToken) ? reader.GetInt32("TotalCount") : 0;
        await reader.NextResultAsync(cancellationToken);

        var items = new List<EnrollmentDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Map(reader));
        }

        return new PagedResult<EnrollmentDto>(items, page, pageSize, total);
    }

    internal static EnrollmentDto Map(DbDataReader reader) => new(
        Id: reader.GetGuid("EnrollmentId"),
        EventId: reader.GetGuid("EventId"),
        Email: reader.GetString("Email"),
        FullName: reader.GetString("FullName"),
        Status: reader.GetString("Status"),
        CreditsCharged: reader.GetInt32("CreditsCharged"),
        WaitlistPosition: reader.GetNullableInt32("WaitlistPosition"),
        CreatedAt: reader.GetUtc("CreatedAt"),
        StatusChangedAt: reader.GetUtc("StatusChangedAt"));

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
