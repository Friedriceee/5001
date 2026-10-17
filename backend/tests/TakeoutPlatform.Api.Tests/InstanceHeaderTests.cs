using NUnit.Framework;

namespace TakeoutPlatform.Api.Tests;

/// <summary>
/// The X-Instance header names the replica that answered. It is how the scale-out test shows
/// that traffic really is spread over several API containers. It must be off unless asked for.
/// </summary>
public class InstanceHeaderTests
{
    [Test]
    public async Task Header_is_absent_by_default()
    {
        using var factory = new ApiTestFactory();
        factory.EnsureDatabase();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.That(response.Headers.Contains("X-Instance"), Is.False);
    }

    [Test]
    public async Task Header_names_the_host_when_enabled()
    {
        using var factory = new ApiTestFactory();
        using var configured = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Observability:InstanceHeader", "true"));
        factory.EnsureDatabase();
        using var client = configured.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.That(response.Headers.TryGetValues("X-Instance", out var values), Is.True);
        Assert.That(values!.Single(), Is.EqualTo(Environment.MachineName));
    }
}
