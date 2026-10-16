using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace LabRoster.Api.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Instructor = "Instructor";
    public const string Viewer = "Viewer";
}

public static class Policies
{
    public const string ManageEvents = "events:manage";
    public const string ReadAudit = "audit:read";
}

/// <summary>
/// The caller, as described by their access token. Tenant comes from the Entra
/// <c>tid</c> claim, so a customer organization is identified by its own directory.
/// </summary>
public sealed record CurrentUser(Guid TenantId, string UserId, string Name, string Role)
{
    public const string TenantClaim = "tid";
    public const string ObjectIdClaim = "oid";
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string RoleClaim = "roles";

    public bool CanManageEvents => Role is Roles.Admin or Roles.Instructor;

    public bool CanReadAudit => Role is Roles.Admin;

    public static CurrentUser From(ClaimsPrincipal principal) =>
        TryFrom(principal, out var user) ? user : throw new MissingTenantException();

    public static bool TryFrom(ClaimsPrincipal principal, [NotNullWhen(true)] out CurrentUser? user)
    {
        var tenant = principal.FindFirst(TenantClaim)?.Value;
        var userId = principal.FindFirst(ObjectIdClaim)?.Value ?? principal.FindFirst(SubjectClaim)?.Value;

        if (!Guid.TryParse(tenant, out var tenantId) || string.IsNullOrWhiteSpace(userId))
        {
            user = null;
            return false;
        }

        var roles = principal.FindAll(RoleClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var role = roles.Contains(Roles.Admin) ? Roles.Admin
            : roles.Contains(Roles.Instructor) ? Roles.Instructor
            : Roles.Viewer;

        user = new CurrentUser(
            tenantId,
            userId.Length > 64 ? userId[..64] : userId,
            principal.FindFirst(NameClaim)?.Value ?? userId,
            role);
        return true;
    }
}

public sealed class MissingTenantException()
    : Exception("The access token has no tenant (tid) or user (oid or sub) claim.");
