using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

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

app.MapHealthChecks("/health", new HealthCheckOptions());
app.MapReverseProxy();

app.Run();
