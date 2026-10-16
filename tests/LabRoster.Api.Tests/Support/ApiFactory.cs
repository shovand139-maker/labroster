using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LabRoster.Api.Auth;
using LabRoster.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LabRoster.Api.Tests.Support;

/// <summary>The real API in memory, connected to the fixture database as the app login.</summary>
public sealed class ApiFactory(SqlServerFixture sql) : WebApplicationFactory<Program>
{
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";

    private static readonly DevTokenOptions TokenOptions = new() { Enabled = true, SigningKey = SigningKey };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:LabRoster"] = sql.AppConnectionString,
            ["Auth:DevTokens:Enabled"] = "true",
            ["Auth:DevTokens:SigningKey"] = SigningKey,
            ["RateLimits:EnrollmentPermitsPerMinute"] = "100000",
        }));
    }

    public HttpClient ClientFor(TestTenant tenant, string role, string? userId = null) =>
        ClientWithToken(TokenFor(tenant.Id, role, userId));

    public HttpClient ClientWithToken(string? token)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public static string TokenFor(Guid tenantId, string role, string? userId = null, DevTokenOptions? options = null) =>
        DevTokenIssuer.Issue(
            options ?? TokenOptions,
            userId ?? $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}",
            tenantId,
            $"Test {role}",
            email: null,
            role,
            DateTimeOffset.UtcNow).AccessToken;
}

public sealed record Problem(int Status, string? Title, string? Detail, string? Code, Dictionary<string, string[]>? Errors);

public static class ApiClientExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<EventDto> CreateEventAsync(this HttpClient client, TestTenant tenant, int capacity = 10, string? title = null)
    {
        var response = await client.PostAsJsonAsync("/api/events", new EventRequest(
            title ?? $"Event {Guid.NewGuid():N}",
            tenant.TemplateId,
            DateTimeOffset.UtcNow.AddDays(7),
            DateTimeOffset.UtcNow.AddDays(7).AddHours(6),
            capacity));
        await response.ShouldHaveStatusAsync(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<EventDto>(Json))!;
    }

    public static async Task<EventDto> CreateOpenEventAsync(this HttpClient client, TestTenant tenant, int capacity = 10)
    {
        var created = await client.CreateEventAsync(tenant, capacity);
        var response = await client.SetStatusAsync(created.Id, "open", created.ETag);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<EventDto>(Json))!;
    }

    public static Task<HttpResponseMessage> SetStatusAsync(this HttpClient client, Guid eventId, string status, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{eventId}/status")
        {
            Content = JsonContent.Create(new StatusRequest(status)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> EnrollAsync(
        this HttpClient client, Guid eventId, string email, string fullName = "Test Learner", string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{eventId}/enrollments")
        {
            Content = JsonContent.Create(new EnrollmentRequest(email, fullName)),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static async Task<PagedResult<EnrollmentDto>> RosterAsync(this HttpClient client, Guid eventId)
    {
        var roster = await client.GetFromJsonAsync<PagedResult<EnrollmentDto>>($"/api/events/{eventId}/roster?pageSize=200", Json);
        return roster!;
    }

    public static async Task<int> CreditBalanceAsync(this HttpClient client)
    {
        var credits = await client.GetFromJsonAsync<CreditsDto>("/api/tenant/credits", Json);
        return credits!.Balance;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public static async Task<Problem> ReadProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>(Json))!;

    /// <summary>Asserts the status and, on failure, shows the response body to explain why.</summary>
    public static async Task ShouldHaveStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode}: {body}");
        }
    }
}
