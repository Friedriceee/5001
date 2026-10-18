using System.Security.Claims;
using System.Text;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Cart;
using JiKeDa.Order.Api.Features.Checkout;
using JiKeDa.Order.Api.Features.Outbox;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddSingleton<OutboxWorker>();
if (builder.Configuration.GetValue("Outbox:Enabled", true))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxWorker>());

var app = builder.Build();

// Apply migrations at startup when a real database is configured.
if (!string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Default")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.Migrate();
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

app.Run();

// Exposed for integration tests (WebApplicationFactory).
public partial class Program { }
