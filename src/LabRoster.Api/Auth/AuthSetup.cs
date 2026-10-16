using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LabRoster.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Bearer tokens from Microsoft Entra ID (multi-tenant), or locally issued dev tokens
    /// when Auth:DevTokens:Enabled is true. Every endpoint requires an authenticated caller
    /// with a tenant claim unless it opts out explicitly.
    /// </summary>
    public static IServiceCollection AddLabRosterAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevTokenOptions>(configuration.GetSection(DevTokenOptions.Section));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured lazily so test hosts can override settings after Program.cs runs.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<DevTokenOptions>, IConfiguration>((jwt, dev, config) =>
                Configure(jwt, dev.Value, config.GetSection("Auth:Entra")));

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(CurrentUser.TenantClaim)
                .Build())
            .AddPolicy(Policies.ManageEvents, policy => policy.RequireRole(Roles.Admin, Roles.Instructor))
            .AddPolicy(Policies.ReadAudit, policy => policy.RequireRole(Roles.Admin));

        return services;
    }

    internal static void Configure(JwtBearerOptions jwt, DevTokenOptions dev, IConfigurationSection entra)
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters.NameClaimType = CurrentUser.NameClaim;
        jwt.TokenValidationParameters.RoleClaimType = CurrentUser.RoleClaim;

        if (dev.Enabled)
        {
            jwt.TokenValidationParameters.ValidIssuer = dev.Issuer;
            jwt.TokenValidationParameters.ValidAudience = dev.Audience;
            jwt.TokenValidationParameters.IssuerSigningKey = DevTokenIssuer.SigningKeyFor(dev);
            jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            return;
        }

        // Multi-tenant Entra app: any organization's directory may sign in, and the
        // issuer must belong to the same directory as the token's tid claim. Directories
        // that were never onboarded see no data, because row-level security finds no tenant.
        var instance = (entra["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');
        jwt.Authority = $"{instance}/{entra["TenantId"] ?? "organizations"}/v2.0";
        jwt.Audience = entra["Audience"];
        jwt.TokenValidationParameters.IssuerValidator = (issuer, token, _) => EntraIssuer.Validate(instance, issuer, token);
    }
}

public static class EntraIssuer
{
    public static string Validate(string instance, string issuer, SecurityToken token)
    {
        var tenant = token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>(CurrentUser.TenantClaim, out var tid) ? tid : null;
        if (IsValid(instance, issuer, tenant))
        {
            return issuer;
        }

        throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' does not match the token's tenant.");
    }

    public static bool IsValid(string instance, string issuer, string? tenantId) =>
        Guid.TryParse(tenantId, out _)
        && string.Equals(issuer, $"{instance.TrimEnd('/')}/{tenantId}/v2.0", StringComparison.OrdinalIgnoreCase);
}
