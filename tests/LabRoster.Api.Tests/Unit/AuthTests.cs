using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabRoster.Api.Auth;

namespace LabRoster.Api.Tests.Unit;

public sealed class AuthTests
{
    private static readonly DevTokenOptions Options = new()
    {
        Enabled = true,
        SigningKey = "unit-test-signing-key-0123456789abcdef",
    };

    [Fact]
    public void Dev_tokens_carry_the_claims_entra_would_issue()
    {
        var tenant = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        var token = DevTokenIssuer.Issue(Options, "arjun", tenant, "Arjun Bhatt", "arjun@contoso.example", Roles.Instructor, now);

        var parts = token.AccessToken.Split('.');
        Assert.Equal(3, parts.Length);

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        var root = payload.RootElement;
        Assert.Equal(tenant.ToString(), root.GetProperty("tid").GetString());
        Assert.Equal("arjun", root.GetProperty("oid").GetString());
        Assert.Equal(Roles.Instructor, root.GetProperty("roles")[0].GetString());
        Assert.Equal(now.AddMinutes(Options.LifetimeMinutes).ToUnixTimeSeconds(), root.GetProperty("exp").GetInt64());
        Assert.Equal(now.AddMinutes(Options.LifetimeMinutes), token.ExpiresAt);
    }

    [Fact]
    public void Dev_token_signatures_are_hmac_sha256_over_header_and_payload()
    {
        var token = DevTokenIssuer.Issue(Options, "priya", Guid.NewGuid(), "Priya", null, Roles.Admin, DateTimeOffset.UtcNow);
        var parts = token.AccessToken.Split('.');

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Options.SigningKey), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));

        Assert.Equal(expected, FromBase64Url(parts[2]));
    }

    [Fact]
    public void A_short_signing_key_is_refused()
    {
        var weak = new DevTokenOptions { Enabled = true, SigningKey = "too-short" };

        Assert.Throws<InvalidOperationException>(() =>
            DevTokenIssuer.Issue(weak, "x", Guid.NewGuid(), "X", null, Roles.Viewer, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_strongest_role_wins_when_a_token_has_several()
    {
        var principal = Principal(Guid.NewGuid().ToString(), "user-1", Roles.Viewer, Roles.Admin, Roles.Instructor);

        Assert.True(CurrentUser.TryFrom(principal, out var user));
        Assert.Equal(Roles.Admin, user.Role);
        Assert.True(user.CanReadAudit);
    }

    [Fact]
    public void A_token_without_roles_is_read_only()
    {
        Assert.True(CurrentUser.TryFrom(Principal(Guid.NewGuid().ToString(), "user-1"), out var user));
        Assert.Equal(Roles.Viewer, user.Role);
        Assert.False(user.CanManageEvents);
    }

    [Theory]
    [InlineData(null, "user-1")]
    [InlineData("not-a-guid", "user-1")]
    [InlineData("6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01", null)]
    public void A_token_without_a_tenant_or_user_is_rejected(string? tenant, string? userId)
    {
        Assert.False(CurrentUser.TryFrom(Principal(tenant, userId), out _));
        Assert.Throws<MissingTenantException>(() => CurrentUser.From(Principal(tenant, userId)));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01/v2.0", "6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01", true)]
    [InlineData("https://login.microsoftonline.com/9a2e4c71-5d8b-4f3e-8a20-7b6c1d9e3f02/v2.0", "6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01", false)]
    [InlineData("https://evil.example/6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01/v2.0", "6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01", false)]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0", "organizations", false)]
    [InlineData("https://login.microsoftonline.com/6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01/v2.0", null, false)]
    public void Entra_issuers_must_match_the_token_tenant(string issuer, string? tenant, bool valid)
    {
        Assert.Equal(valid, EntraIssuer.IsValid("https://login.microsoftonline.com/", issuer, tenant));
    }

    private static ClaimsPrincipal Principal(string? tenant, string? userId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (tenant is not null)
        {
            claims.Add(new Claim(CurrentUser.TenantClaim, tenant));
        }

        if (userId is not null)
        {
            claims.Add(new Claim(CurrentUser.ObjectIdClaim, userId));
        }

        claims.AddRange(roles.Select(r => new Claim(CurrentUser.RoleClaim, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", CurrentUser.NameClaim, CurrentUser.RoleClaim));
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
