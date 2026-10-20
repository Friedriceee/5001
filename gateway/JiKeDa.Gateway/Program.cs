using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddHealthChecks();

// JWT validation at the gateway (defence in depth). The signing key is shared with
// the Identity service; downstream services still enforce their own [Authorize] checks,
// so the gateway is not the only authorization point.
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtEnabled = !string.IsNullOrWhiteSpace(jwtKey) && Encoding.UTF8.GetByteCount(jwtKey) >= 32;
if (jwtEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "JiKeDa",
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "JiKeDa.Client",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!)),
                ValidateLifetime = true,
                RoleClaimType = "role",
                NameClaimType = ClaimTypes.Name,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });
    builder.Services.AddAuthorization();
}

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Service-Instance"] = Environment.MachineName;
        context.Response.Headers["X-Trace-ID"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        return Task.CompletedTask;
    });
    using var logScope = app.Logger.BeginScope(new Dictionary<string, object?>
    {
        ["TraceId"] = Activity.Current?.TraceId.ToString(), ["Instance"] = Environment.MachineName,
    });
    await next();
});

app.Use(async (context, next) =>
{
    const string headerName = "X-Correlation-ID";
    var correlationId = context.Request.Headers[headerName].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
        correlationId = Guid.NewGuid().ToString("N");

    context.TraceIdentifier = correlationId;
    context.Request.Headers[headerName] = correlationId;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers[headerName] = correlationId;
        return Task.CompletedTask;
    });

    await next();
});

if (jwtEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapHealthChecks("/health", new HealthCheckOptions());
// The browser API client uses /api as its base URL, so expose the same liveness
// check there as well. This keeps the landing-page probe on the proxied origin.
app.MapHealthChecks("/api/health", new HealthCheckOptions());
app.MapReverseProxy();

app.Run();
