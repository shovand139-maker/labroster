using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using LabRoster.Api.Auth;
using LabRoster.Api.Data;
using LabRoster.Api.Endpoints;
using LabRoster.Api.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddLabRosterAuth(builder.Configuration);

builder.Services.AddSingleton<ITenantConnectionFactory, TenantConnectionFactory>();
builder.Services.AddScoped<EventStore>();
builder.Services.AddScoped<EnrollmentStore>();
builder.Services.AddScoped<TenantStore>();

builder.Services.AddHealthChecks().AddCheck<SqlHealthCheck>("sql", tags: ["ready"]);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // One bucket per user per organization, so one busy instructor cannot starve another.
    options.AddPolicy(EnrollmentEndpoints.RateLimitPolicy, context =>
    {
        var permits = context.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("RateLimits:EnrollmentPermitsPerMinute", 120);
        var partition = $"{context.User.FindFirst(CurrentUser.TenantClaim)?.Value}:" +
                        $"{context.User.FindFirst(CurrentUser.ObjectIdClaim)?.Value ?? context.User.FindFirst(CurrentUser.SubjectClaim)?.Value}";

        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
                Detail = "Too many enrollment requests. Wait a minute and try again.",
                Extensions = { ["code"] = "rate_limited" },
            },
        });
    };
});

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Traces, metrics and logs to Application Insights, including SQL dependency calls.
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next(context);
});

// The Angular build is copied into wwwroot for deployment; static files need no token.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

app.MapTenantEndpoints();
app.MapEventEndpoints();
app.MapEnrollmentEndpoints();
app.MapDevEndpoints();

// Unknown API routes are a 404 problem; every other path belongs to the Angular router.
app.MapFallback("/api/{**path}", () =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "route_not_found", "No API route matches this path."))
    .AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
