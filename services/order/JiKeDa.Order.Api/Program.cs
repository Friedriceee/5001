using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Cart;
using JiKeDa.Order.Api.Features.Admin;
using JiKeDa.Order.Api.Features.Checkout;
using JiKeDa.Order.Api.Features.Outbox;
using JiKeDa.Order.Api.Features.Pricing;
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

// Validates tokens issued by Identity; also signs short-lived service tokens for Catalog (shared key).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
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

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddSingleton<IDiscountStrategy, MerchantDiscountStrategy>();
builder.Services.AddSingleton<IDiscountStrategy, PlatformCouponDiscountStrategy>();
builder.Services.AddSingleton<ServiceTokenProvider>();
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:CatalogBaseUrl"] ?? "http://catalog:8080/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<IFulfilmentStatusClient, FulfilmentStatusClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:FulfilmentBaseUrl"] ?? "http://fulfilment:8080/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddSingleton<OutboxWorker>();
if (builder.Configuration.GetValue("Outbox:Enabled", true))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxWorker>());


var messagingNamespace = builder.Configuration["Messaging:FullyQualifiedNamespace"];
if (!string.IsNullOrWhiteSpace(messagingNamespace))
{
    var credentials = new DefaultAzureCredential();
    builder.Services.AddSingleton(new ServiceBusClient(messagingNamespace, credentials,
        new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets }));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(builder.Configuration["Messaging:QueueName"] ?? "order-confirmed"));
    builder.Services.AddSingleton(new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(messagingNamespace, credentials));
    builder.Services.AddSingleton<QueueObservation>();
    builder.Services.AddHostedService<ServiceBusPublisher>();
}

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
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.Migrate();
}

if (app.Configuration.GetValue("MigrateOnly", false))
{
    await app.DisposeAsync();
    return;
}

if (app.Configuration.GetValue<int>("QueueReplayCount") is > 0 and var replayCount)
{
    await QueueReplayCommand.RunAsync(app, replayCount);
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
app.MapGet("/", () => Results.Ok(new { service = "order", status = "ready" }));
app.MapControllers();
if (!string.IsNullOrWhiteSpace(messagingNamespace))
    app.MapGet("/internal/messaging/status", (QueueObservation state) => Results.Ok(state));


app.Run();

// Exposed for integration tests (WebApplicationFactory).
public partial class Program { }
