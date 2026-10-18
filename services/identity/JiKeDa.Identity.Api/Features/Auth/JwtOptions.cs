namespace JiKeDa.Identity.Api.Features.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "JiKeDa";
    public string Audience { get; set; } = "JiKeDa.Client";
    public string Key { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 60;
}
