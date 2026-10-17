using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapGet("/", () => Results.Ok(new { service = "fulfilment", status = "foundation-ready" }));
app.Run();
