using LabRoster.Api.Infrastructure;

namespace LabRoster.Api.Tests.Unit;

public sealed class SqlErrorsTests
{
    [Theory]
    [InlineData(50404, "event_not_found|Event not found.", 404, "event_not_found", "Event not found.")]
    [InlineData(50409, "already_enrolled|This learner is already on the roster.", 409, "already_enrolled", "This learner is already on the roster.")]
    [InlineData(50402, "insufficient_credits|Not enough credits.", 402, "insufficient_credits", "Not enough credits.")]
    [InlineData(50412, "stale_etag|Reload and try again.", 412, "stale_etag", "Reload and try again.")]
    public void Business_errors_map_to_their_http_status_and_code(int number, string message, int status, string code, string detail)
    {
        var error = SqlErrors.Map(number, message);

        Assert.NotNull(error);
        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal(detail, error.Detail);
    }

    [Fact]
    public void A_message_without_a_code_still_maps()
    {
        var error = SqlErrors.Map(50400, "Something was wrong.");

        Assert.NotNull(error);
        Assert.Equal(400, error.Status);
        Assert.Equal("error", error.Code);
        Assert.Equal("Something was wrong.", error.Detail);
    }

    [Theory]
    [InlineData(2627, 409, "duplicate")]
    [InlineData(2601, 409, "duplicate")]
    [InlineData(1205, 503, "deadlock_victim")]
    [InlineData(33504, 403, "tenant_violation")]
    public void Engine_errors_with_a_safe_meaning_are_mapped(int number, int status, string code)
    {
        var error = SqlErrors.Map(number, "engine message that must not leak");

        Assert.NotNull(error);
        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("engine message", error.Detail);
    }

    [Theory]
    [InlineData(51001)]
    [InlineData(50500)]
    [InlineData(208)]
    [InlineData(547)]
    public void Everything_else_is_left_to_the_500_handler(int number)
    {
        Assert.Null(SqlErrors.Map(number, "internal|details"));
    }
}
