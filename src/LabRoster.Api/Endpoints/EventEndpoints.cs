using System.Security.Claims;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Data;
using LabRoster.Api.Infrastructure;

namespace LabRoster.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events").WithTags("Events");

        events.MapGet("/", ListAsync)
            .WithName("ListEvents")
            .WithSummary("List events in your organization, soonest first.")
            .Produces<PagedResult<EventDto>>();

        events.MapGet("/{id:guid}", GetAsync)
            .WithName("GetEvent")
            .WithSummary("Get one event. The ETag header is required to change it.")
            .Produces<EventDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        events.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.ManageEvents)
            .WithName("CreateEvent")
            .WithSummary("Create a draft event from a lab template.")
            .Produces<EventDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        events.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Policies.ManageEvents)
            .WithName("UpdateEvent")
            .WithSummary("Change title, schedule or capacity. Send the event's ETag in If-Match.")
            .Produces<EventDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        events.MapPost("/{id:guid}/status", SetStatusAsync)
            .RequireAuthorization(Policies.ManageEvents)
            .WithName("SetEventStatus")
            .WithSummary("Open, close or cancel an event. Cancelling refunds every seat.")
            .Produces<EventDto>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        return app;
    }

    private static async Task<IResult> ListAsync(
        string? search, string? status, int? page, int? pageSize,
        ClaimsPrincipal principal, EventStore store, CancellationToken cancellationToken)
    {
        var result = await store.ListAsync(CurrentUser.From(principal), search, status, page ?? 1, pageSize ?? 20, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal principal, EventStore store, HttpResponse response, CancellationToken cancellationToken)
    {
        var item = await store.GetAsync(CurrentUser.From(principal), id, cancellationToken);
        response.Headers.ETag = item.ETag;
        return TypedResults.Ok(item);
    }

    private static async Task<IResult> CreateAsync(
        EventRequest request, ClaimsPrincipal principal, EventStore store, TimeProvider clock,
        HttpResponse response, CancellationToken cancellationToken)
    {
        var errors = request.Validate(clock.GetUtcNow(), isCreate: true);
        if (errors.Count > 0)
        {
            return ApiProblem.Validation(errors);
        }

        var created = await store.CreateAsync(CurrentUser.From(principal), request, cancellationToken);
        response.Headers.ETag = created.ETag;
        return TypedResults.Created($"/api/events/{created.Id}", created);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, EventRequest request, ClaimsPrincipal principal, EventStore store, TimeProvider clock,
        HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(http, out var rowVersion, out var problem))
        {
            return problem;
        }

        var errors = request.Validate(clock.GetUtcNow(), isCreate: false);
        if (errors.Count > 0)
        {
            return ApiProblem.Validation(errors);
        }

        var updated = await store.UpdateAsync(CurrentUser.From(principal), id, request, rowVersion, cancellationToken);
        response.Headers.ETag = updated.ETag;
        return TypedResults.Ok(updated);
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, StatusRequest request, ClaimsPrincipal principal, EventStore store,
        HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(http, out var rowVersion, out var problem))
        {
            return problem;
        }

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return ApiProblem.Validation(errors);
        }

        var updated = await store.SetStatusAsync(CurrentUser.From(principal), id, request.Status!, rowVersion, cancellationToken);
        response.Headers.ETag = updated.ETag;
        return TypedResults.Ok(updated);
    }

    /// <summary>Optimistic concurrency: writes must prove which version they are changing.</summary>
    internal static bool TryReadIfMatch(HttpRequest request, out byte[] rowVersion, out IResult problem)
    {
        rowVersion = [];
        problem = TypedResults.Empty;
        var header = request.Headers.IfMatch.ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            problem = ApiProblem.Create(
                StatusCodes.Status428PreconditionRequired,
                "if_match_required",
                "Send the event's current ETag in the If-Match header.");
            return false;
        }

        if (!ETags.TryParse(header, out rowVersion))
        {
            problem = ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "invalid_etag",
                "If-Match must be a strong ETag returned by this API.");
            return false;
        }

        return true;
    }
}
