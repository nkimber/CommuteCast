using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CommuteCast.Web;

public static class WebAuthentication
{
    public static IServiceCollection AddCommuteCastAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication("CommuteCast").AddPolicyScheme("CommuteCast", "CommuteCast access", options =>
        {
            options.ForwardDefaultSelector = context => string.IsNullOrEmpty(context.RequestServices.GetRequiredService<IConfiguration>()["Cloudflare:TeamDomain"])
                ? "LocalSetup" : JwtBearerDefaults.AuthenticationScheme;
        }).AddScheme<AuthenticationSchemeOptions, LocalSetupAuthentication>("LocalSetup", _ => { }).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IConfiguration>((options, config) =>
        {
            var team = config["Cloudflare:TeamDomain"] ?? "";
            if (team.Length == 0) return;
            if (!Regex.IsMatch(team, "^[a-z0-9-]+\\.cloudflareaccess\\.com$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Cloudflare:TeamDomain must be your team.cloudflareaccess.com hostname.");
            var audience = config["Cloudflare:Audience"];
            var email = config["Cloudflare:AllowedEmail"];
            if (string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Configure the Cloudflare Access application audience and your allowed email before enabling remote access.");
            var issuer = "https://" + team;
                options.MapInboundClaims = false;
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(issuer + "/cdn-cgi/access/certs", new CloudflareKeys(issuer));
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
                    ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "email"
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context => { context.Token = context.Request.Headers["Cf-Access-Jwt-Assertion"].FirstOrDefault(); return Task.CompletedTask; },
                    OnTokenValidated = context =>
                    {
                        if (!string.Equals(context.Principal?.FindFirstValue("email"), email, StringComparison.OrdinalIgnoreCase)) context.Fail("This identity is not authorized for CommuteCast.");
                        return Task.CompletedTask;
                    }
                };
        });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
    private sealed class CloudflareKeys(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            var json = await retriever.GetDocumentAsync(address, cancel);
            var config = new OpenIdConnectConfiguration { Issuer = issuer };
            foreach (var key in new JsonWebKeySet(json).GetSigningKeys()) config.SigningKeys.Add(key);
            return config;
        }
    }
}

public sealed class LocalSetupAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var remote = Context.Connection.RemoteIpAddress;
        var local = remote is not null && IPAddress.IsLoopback(remote) && Request.Host.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        if (!local) return Task.FromResult(AuthenticateResult.Fail("Remote access requires Cloudflare Access configuration."));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "local-owner"), new Claim(ClaimTypes.Name, "Local setup")], Scheme.Name)), Scheme.Name)));
    }
}
