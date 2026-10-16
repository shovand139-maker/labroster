using System.Security.Claims;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Data;
using LabRoster.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace LabRoster.Api.Endpoints;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Organization");

        api.MapGet("/me", async (ClaimsPrincipal principal, TenantStore store, CancellationToken ct) =>
                TypedResults.Ok(await store.MeAsync(CurrentUser.From(principal), ct)))
            .WithName("GetMe")
            .WithSummary("The signed-in user, their organization, role and credit balance.")
            .Produces<MeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/templates", async (ClaimsPrincipal principal, TenantStore store, CancellationToken ct) =>
                TypedResults.Ok(await store.TemplatesAsync(CurrentUser.From(principal), ct)))
            .WithName("ListTemplates")
            .WithSummary("Lab templates events can be created from.")
            .Produces<IReadOnlyList<TemplateDto>>();

        api.MapGet("/tenant/credits", async (ClaimsPrincipal principal, TenantStore store, CancellationToken ct) =>
                TypedResults.Ok(await store.CreditsAsync(CurrentUser.From(principal), ct)))
            .WithName("GetCredits")
            .WithSummary("Credit balance, 30-day usage and the latest ledger entries.")
            .Produces<CreditsDto>();

        api.MapGet("/audit", async (int? page, int? pageSize, ClaimsPrincipal principal, TenantStore store, CancellationToken ct) =>
                TypedResults.Ok(await store.AuditAsync(CurrentUser.From(principal), page ?? 1, pageSize ?? 50, ct)))
            .RequireAuthorization(Policies.ReadAudit)
            .WithName("ListAudit")
            .WithSummary("Who changed what, newest first. Admins only.")
            .Produces<PagedResult<AuditEntryDto>>();

        return app;
    }

    /// <summary>
    /// Sign-in for local development and end-to-end tests. Returns 404 unless
    /// Auth:DevTokens:Enabled is true, which is never the case in a deployed environment.
    /// </summary>
    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        var dev = app.MapGroup("/api/dev").WithTags("Development").AllowAnonymous();

        dev.MapGet("/users", (IOptions<DevTokenOptions> options) =>
                options.Value.Enabled
                    ? Results.Ok(options.Value.Users.Select(ToDto).ToList())
                    : DisabledResult())
            .WithName("ListDevUsers")
            .WithSummary("Demo accounts available for local sign-in.");

        dev.MapPost("/token", (DevTokenRequest request, IOptions<DevTokenOptions> options, TimeProvider clock) =>
            {
                var settings = options.Value;
                if (!settings.Enabled)
                {
                    return DisabledResult();
                }

                var user = settings.Users.FirstOrDefault(u => string.Equals(u.Id, request.UserId, StringComparison.OrdinalIgnoreCase));
                if (user is null)
                {
                    return ApiProblem.Create(StatusCodes.Status404NotFound, "unknown_user", "No demo user has that id.");
                }

                var token = DevTokenIssuer.Issue(settings, user, clock.GetUtcNow());
                return Results.Ok(new DevTokenResponse(token.AccessToken, token.ExpiresAt, ToDto(user)));
            })
            .WithName("IssueDevToken")
            .WithSummary("Issue a signed token for a demo user.");

        return app;
    }

    private static DevUserDto ToDto(DevUser user) => new(user.Id, user.Name, user.Email, user.Role, user.TenantName);

    private static IResult DisabledResult() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "dev_tokens_disabled", "Development sign-in is turned off.");
}
