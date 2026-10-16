using LabRoster.Api.Auth;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;

namespace LabRoster.Api.Infrastructure;

public sealed record ApiError(int Status, string Code, string Detail);

/// <summary>
/// Translates SQL Server errors into HTTP problems. Procedures raise business errors
/// with THROW 50xxx, N'code|message'; 50400-50499 map to the matching 4xx status.
/// </summary>
public static class SqlErrors
{
    public static ApiError? Map(int number, string message)
    {
        if (number is >= 50400 and <= 50499)
        {
            var separator = message.IndexOf('|', StringComparison.Ordinal);
            return separator > 0
                ? new ApiError(number - 50000, message[..separator], message[(separator + 1)..])
                : new ApiError(number - 50000, "error", message);
        }

        return number switch
        {
            2601 or 2627 => new ApiError(409, "duplicate", "The request conflicts with an existing record."),
            1205 => new ApiError(503, "deadlock_victim", "The request collided with another change. Retry it."),
            33504 => new ApiError(403, "tenant_violation", "The change would write data outside your organization."),
            _ => null,
        };
    }
}

public static class ApiProblem
{
    public static ProblemHttpResult Create(int status, string code, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: status,
            title: ReasonPhrases.GetReasonPhrase(status),
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "One or more fields are invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
}

/// <summary>Writes known failures as RFC 9457 problem details; unknown ones fall through to a 500.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var error = exception switch
        {
            SqlException sql => SqlErrors.Map(sql.Number, sql.Message),
            MissingTenantException missing => new ApiError(403, "missing_tenant", missing.Message),
            BadHttpRequestException bad => new ApiError(bad.StatusCode, "bad_request", bad.Message),
            _ => null,
        };

        if (error is null)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
            return false;
        }

        if (error.Status >= 500)
        {
            logger.LogWarning(exception, "Transient failure: {Code}", error.Code);
        }

        httpContext.Response.StatusCode = error.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = error.Status,
                Title = ReasonPhrases.GetReasonPhrase(error.Status),
                Detail = error.Detail,
                Extensions = { ["code"] = error.Code },
            },
        });
    }
}
