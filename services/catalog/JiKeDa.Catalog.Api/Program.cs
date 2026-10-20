using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using JiKeDa.Catalog.Api.Common;
using JiKeDa.Catalog.Api.Data;
using JiKeDa.Catalog.Api.Features.Dishes;
using JiKeDa.Catalog.Api.Features.Inventory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
});

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = context.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid request" : e.ErrorMessage)
            .FirstOrDefault() ?? "Invalid request";
        return new BadRequestObjectResult(ApiResult<object>.Fail(message));
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// Catalog only validates tokens issued by the Identity service (shared signing key).
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT configuration is missing.");
if (Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes and should come from an environment secret.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ValidateLifetime = true,
            RoleClaimType = "role",
            NameClaimType = ClaimTypes.Name,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DishService>();
builder.Services.AddScoped<InventoryService>();

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

// Apply migrations at startup when a real database is configured.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true) && !string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Default")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    db.Database.Migrate();
}

if (app.Configuration.GetValue("MigrateOnly", false))
{
    await app.DisposeAsync();
    return;
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapGet("/", () => Results.Ok(new { service = "catalog", status = "ready" }));
app.MapControllers();

app.Run();

// Exposed for integration tests (WebApplicationFactory).
public partial class Program { }
