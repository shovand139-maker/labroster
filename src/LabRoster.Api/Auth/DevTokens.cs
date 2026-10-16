using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace LabRoster.Api.Auth;

/// <summary>
/// Local development and test sign-in. Tokens carry the same claims Microsoft Entra
/// issues (tid, oid, name, roles), so everything downstream runs unchanged.
/// Never enable this in a deployed environment.
/// </summary>
public sealed class DevTokenOptions
{
    public const string Section = "Auth:DevTokens";

    public bool Enabled { get; set; }

    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "labroster-dev";

    public string Audience { get; set; } = "labroster-api";

    public int LifetimeMinutes { get; set; } = 480;

    public List<DevUser> Users { get; set; } = [];
}

public sealed class DevUser
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Role { get; set; } = Roles.Viewer;

    public Guid TenantId { get; set; }

    public string TenantName { get; set; } = string.Empty;
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public static class DevTokenIssuer
{
    public const int MinimumKeyLength = 32;

    public static IssuedToken Issue(DevTokenOptions options, DevUser user, DateTimeOffset now) =>
        Issue(options, user.Id, user.TenantId, user.Name, user.Email, user.Role, now);

    public static IssuedToken Issue(
        DevTokenOptions options, string userId, Guid tenantId, string name, string? email, string role, DateTimeOffset now)
    {
        if (options.SigningKey.Length < MinimumKeyLength)
        {
            throw new InvalidOperationException($"Auth:DevTokens:SigningKey must be at least {MinimumKeyLength} characters.");
        }

        var expires = now.AddMinutes(options.LifetimeMinutes);
        var header = new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = options.Issuer,
            ["aud"] = options.Audience,
            ["sub"] = userId,
            ["oid"] = userId,
            ["tid"] = tenantId.ToString(),
            ["name"] = name,
            ["email"] = email,
            ["roles"] = new[] { role },
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
        };

        var unsigned = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.SigningKey), Encoding.ASCII.GetBytes(unsigned));
        return new IssuedToken($"{unsigned}.{Base64Url(signature)}", expires);
    }

    public static SymmetricSecurityKey SigningKeyFor(DevTokenOptions options) =>
        new(Encoding.UTF8.GetBytes(options.SigningKey));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
