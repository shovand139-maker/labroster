using System.Data;
using System.Data.Common;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Data;

public sealed class EventStore(ITenantConnectionFactory connections)
{
    public async Task<PagedResult<EventDto>> ListAsync(
        CurrentUser user, string? search, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Event_List")
            .With("@Search", SqlDbType.NVarChar, search, 100)
            .With("@Status", SqlDbType.VarChar, status, 12)
            .With("@Page", SqlDbType.Int, page)
            .With("@PageSize", SqlDbType.Int, pageSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var total = await reader.ReadAsync(cancellationToken) ? reader.GetInt32("TotalCount") : 0;
        await reader.NextResultAsync(cancellationToken);

        var items = new List<EventDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Map(reader));
        }

        return new PagedResult<EventDto>(items, page, pageSize, total);
    }

    public async Task<EventDto> GetAsync(CurrentUser user, Guid eventId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Event_Get")
            .With("@EventId", SqlDbType.UniqueIdentifier, eventId);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<EventDto> CreateAsync(CurrentUser user, EventRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Event_Create")
            .With("@TemplateId", SqlDbType.UniqueIdentifier, request.TemplateId)
            .With("@Title", SqlDbType.NVarChar, request.Title?.Trim(), 160)
            .With("@StartsAt", SqlDbType.DateTime2, request.StartsAt?.ToSqlUtc())
            .With("@EndsAt", SqlDbType.DateTime2, request.EndsAt?.ToSqlUtc())
            .With("@Capacity", SqlDbType.Int, request.Capacity);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<EventDto> UpdateAsync(
        CurrentUser user, Guid eventId, EventRequest request, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Event_Update")
            .With("@EventId", SqlDbType.UniqueIdentifier, eventId)
            .With("@Title", SqlDbType.NVarChar, request.Title?.Trim(), 160)
            .With("@StartsAt", SqlDbType.DateTime2, request.StartsAt?.ToSqlUtc())
            .With("@EndsAt", SqlDbType.DateTime2, request.EndsAt?.ToSqlUtc())
            .With("@Capacity", SqlDbType.Int, request.Capacity)
            .With("@ExpectedRowVer", SqlDbType.Binary, expectedRowVersion, 8);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<EventDto> SetStatusAsync(
        CurrentUser user, Guid eventId, string status, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Event_SetStatus")
            .With("@EventId", SqlDbType.UniqueIdentifier, eventId)
            .With("@Status", SqlDbType.VarChar, status, 12)
            .With("@ExpectedRowVer", SqlDbType.Binary, expectedRowVersion, 8);
        return await ReadSingleAsync(command, cancellationToken);
    }

    private static async Task<EventDto> ReadSingleAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"{command.CommandText} returned no event.");
        }

        return Map(reader);
    }

    internal static EventDto Map(DbDataReader reader)
    {
        var capacity = reader.GetInt32("Capacity");
        var enrolled = reader.GetInt32("Enrolled");

        return new EventDto(
            Id: reader.GetGuid("EventId"),
            Title: reader.GetString("Title"),
            TemplateId: reader.GetGuid("TemplateId"),
            TemplateTitle: reader.GetString("TemplateTitle"),
            Cloud: reader.GetString("Cloud"),
            CreditsPerSeat: reader.GetInt32("CreditsPerSeat"),
            StartsAt: reader.GetUtc("StartsAt"),
            EndsAt: reader.GetUtc("EndsAt"),
            Capacity: capacity,
            Enrolled: enrolled,
            Waitlisted: reader.GetInt32("Waitlisted"),
            SeatsLeft: Math.Max(0, capacity - enrolled),
            Status: reader.GetString("Status"),
            CreatedBy: reader.GetString("CreatedBy"),
            CreatedAt: reader.GetUtc("CreatedAt"),
            UpdatedAt: reader.GetUtc("UpdatedAt"),
            ETag: ETags.Format(reader.GetRowVersion("RowVer")));
    }
}
