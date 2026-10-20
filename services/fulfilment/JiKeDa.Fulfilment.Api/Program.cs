using System.Security.Claims;
using System.Text;
using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using JiKeDa.Fulfilment.Api.Events;
using JiKeDa.Fulfilment.Api.Features.Delivery;
using JiKeDa.Fulfilment.Api.Features.Merchant;
using JiKeDa.Fulfilment.Api.Features.Tracking;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers(options => options.Filters.Add<ApiExceptionFilter>());
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
        ApiResult<object>.Fail(context.ModelState.Values.SelectMany(v => v.Errors)
            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid request." : e.ErrorMessage)
            .FirstOrDefault() ?? "Invalid request."));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT configuration is missing.");
if (Encoding.UTF8.GetByteCount(jwt.Key) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ValidateLifetime = true, RoleClaimType = "role", NameClaimType = ClaimTypes.Name, ClockSkew = TimeSpan.FromSeconds(30),
    };
});
builder.Services.AddAuthorization();
builder.Services.AddDbContext<FulfilmentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServiceTokenProvider>();
builder.Services.AddHttpClient<IOrderEventClient, OrderEventClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:OrderBaseUrl"] ?? "http://order:8080/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<OrderEventConsumer>();
builder.Services.AddScoped<MerchantFulfilmentService>();
builder.Services.AddScoped<DeliveryService>();
builder.Services.AddScoped<CustomerTrackingService>();
if (builder.Configuration.GetValue("EventConsumer:Enabled", true))
    builder.Services.AddHostedService<OrderEventPollingWorker>();

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Default")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FulfilmentDbContext>();
    db.Database.Migrate();
    // Migrations deliberately contain schema only. This demo profile must match Identity's rider1
    // profile_id and is inserted here so the migration remains provider-self-contained.
    if (!db.Riders.Any(rider => rider.Id == 1))
    {
        db.Riders.Add(new JiKeDa.Fulfilment.Api.Features.Rider
        {
            Id = 1, Name = "Rider One", PhoneNumber = "13800000000", Zone = "ZoneA", IsActive = true, IsAvailable = true, Capacity = 3,
        });
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapGet("/", () => Results.Ok(new { service = "fulfilment", status = "ready" }));
app.MapControllers();
app.Run();

public partial class Program { }
