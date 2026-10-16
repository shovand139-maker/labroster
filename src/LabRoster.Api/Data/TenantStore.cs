using System.Data;
using System.Text.Json;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;

namespace LabRoster.Api.Data;

public sealed class TenantStore(ITenantConnectionFactory connections)
{
    public async Task<MeDto> MeAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Tenant_Get");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("api.usp_Tenant_Get returned no tenant.");
        }

        return new MeDto(
            UserId: user.UserId,
            Name: user.Name,
            Role: user.Role,
            TenantId: reader.GetGuid("TenantId"),
            TenantName: reader.GetString("Name"),
            TenantSlug: reader.GetString("Slug"),
            CreditBalance: reader.GetInt32("CreditBalance"),
            Permissions: new PermissionsDto(user.CanManageEvents, user.CanReadAudit));
    }

    public async Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Template_List");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var templates = new List<TemplateDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            templates.Add(new TemplateDto(
                reader.GetGuid("TemplateId"),
                reader.GetString("Title"),
                reader.GetString("Cloud"),
                reader.GetInt32("DurationMinutes"),
                reader.GetInt32("CreditsPerSeat")));
        }

        return templates;
    }

    public async Task<CreditsDto> CreditsAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Credits_Get");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("api.usp_Credits_Get returned no summary.");
        }

        var balance = reader.GetInt32("Balance");
        var allocated = reader.GetInt32("Allocated");
        var spent = reader.GetInt32("Spent30Days");
        var refunded = reader.GetInt32("Refunded30Days");

        await reader.NextResultAsync(cancellationToken);
        var recent = new List<LedgerEntryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            recent.Add(new LedgerEntryDto(
                reader.GetInt64("LedgerId"),
                reader.GetInt32("Delta"),
                reader.GetString("Reason"),
                reader.GetNullableGuid("EventId"),
                reader.GetNullableString("EventTitle"),
                reader.GetUtc("CreatedAt")));
        }

        return new CreditsDto(balance, allocated, spent, refunded, recent);
    }

    public async Task<PagedResult<AuditEntryDto>> AuditAsync(CurrentUser user, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(user, cancellationToken);
        await using var command = Sql.Procedure(connection, "api.usp_Audit_List")
            .With("@Page", SqlDbType.Int, page)
            .With("@PageSize", SqlDbType.Int, pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var total = await reader.ReadAsync(cancellationToken) ? reader.GetInt32("TotalCount") : 0;
        await reader.NextResultAsync(cancellationToken);

        var items = new List<AuditEntryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var details = reader.GetNullableString("Details");
            items.Add(new AuditEntryDto(
                reader.GetInt64("AuditId"),
                reader.GetString("ActorId"),
                reader.GetString("Action"),
                reader.GetNullableGuid("EntityId"),
                details is null ? null : JsonSerializer.Deserialize<JsonElement>(details),
                reader.GetUtc("CreatedAt")));
        }

        return new PagedResult<AuditEntryDto>(items, page, pageSize, total);
    }
}
