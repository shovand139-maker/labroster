using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using LabRoster.Api.Data;
using LabRoster.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace LabRoster.Api.Endpoints;

public static class EnrollmentEndpoints
{
    public const string RateLimitPolicy = "enrollments";
    private const int MaxBodyBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events/{eventId:guid}").WithTags("Enrollments");

        events.MapGet("/roster", RosterAsync)
            .WithName("GetRoster")
            .WithSummary("Enrolled learners first, then the waitlist in order.")
            .Produces<PagedResult<EnrollmentDto>>();

        events.MapPost("/enrollments", EnrollAsync)
            .RequireAuthorization(Policies.ManageEvents)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("EnrollLearner")
            .WithSummary("Enroll one learner, or add them to the waitlist when the event is full. Supports Idempotency-Key.")
            .Produces<EnrollmentDto>(StatusCodes.Status201Created)
            .Produces<EnrollmentDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict);

        events.MapPost("/enrollments/bulk", BulkEnrollAsync)
            .RequireAuthorization(Policies.ManageEvents)
            .RequireRateLimiting(RateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .Accepts<BulkEnrollmentRequest>("application/json", "text/csv")
            .WithName("BulkEnroll")
            .WithSummary("Enroll up to 500 learners from JSON or a CSV export in one transaction.")
            .Produces<BulkEnrollmentResult>()
            .ProducesValidationProblem();

        app.MapPost("/api/enrollments/{id:guid}/cancel", CancelAsync)
            .WithTags("Enrollments")
            .RequireAuthorization(Policies.ManageEvents)
            .WithName("CancelEnrollment")
            .WithSummary("Cancel an enrollment, refund its credits and fill the seat from the waitlist.")
            .Produces<CancelResult>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> RosterAsync(
        Guid eventId, string? search, string? status, int? page, int? pageSize,
        ClaimsPrincipal principal, EnrollmentStore store, CancellationToken cancellationToken)
    {
        var roster = await store.RosterAsync(
            CurrentUser.From(principal), eventId, search, status, page ?? 1, pageSize ?? 50, cancellationToken);
        return TypedResults.Ok(roster);
    }

    private static async Task<IResult> EnrollAsync(
        Guid eventId, EnrollmentRequest request, ClaimsPrincipal principal, EnrollmentStore store,
        HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        var key = http.Headers["Idempotency-Key"].ToString();
        if (key.Length > 0 && !IdempotencyKeys.IsValid(key))
        {
            return ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "Idempotency-Key must be 8 to 80 letters, digits or - _ : . characters.");
        }

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return ApiProblem.Validation(errors);
        }

        var outcome = await store.CreateAsync(
            CurrentUser.From(principal), eventId, request, key.Length > 0 ? key : null, cancellationToken);

        if (outcome.Replayed)
        {
            response.Headers["Idempotent-Replay"] = "true";
            return TypedResults.Ok(outcome.Enrollment);
        }

        return TypedResults.Created($"/api/events/{eventId}/roster", outcome.Enrollment);
    }

    private static async Task<IResult> BulkEnrollAsync(
        Guid eventId, ClaimsPrincipal principal, EnrollmentStore store, HttpRequest http, CancellationToken cancellationToken)
    {
        IReadOnlyList<EnrollmentRequest> learners;

        if (http.ContentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var reader = new StreamReader(http.Body, Encoding.UTF8);
            var parsed = CsvRoster.Parse(await reader.ReadToEndAsync(cancellationToken));
            if (parsed.Errors.Count > 0)
            {
                return ApiProblem.Validation(new Dictionary<string, string[]> { ["file"] = [.. parsed.Errors] });
            }

            learners = parsed.Learners;
        }
        else
        {
            BulkEnrollmentRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync<BulkEnrollmentRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return ApiProblem.Create(StatusCodes.Status400BadRequest, "invalid_json", "The request body is not valid JSON.");
            }

            learners = body?.Learners ?? [];
        }

        if (learners.Count == 0)
        {
            return ApiProblem.Validation(new Dictionary<string, string[]> { ["learners"] = ["Add at least one learner."] });
        }

        if (learners.Count > BulkEnrollmentRequest.MaxLearners)
        {
            return ApiProblem.Validation(new Dictionary<string, string[]>
            {
                ["learners"] = [$"A roster upload is limited to {BulkEnrollmentRequest.MaxLearners} learners."],
            });
        }

        // Row-level problems (bad email, duplicates) are reported per row rather than
        // failing the whole upload, so the stored procedure sees every row.
        var result = await store.BulkCreateAsync(CurrentUser.From(principal), eventId, learners, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> CancelAsync(
        Guid id, ClaimsPrincipal principal, EnrollmentStore store, CancellationToken cancellationToken)
    {
        var result = await store.CancelAsync(CurrentUser.From(principal), id, cancellationToken);
        return TypedResults.Ok(result);
    }
}
