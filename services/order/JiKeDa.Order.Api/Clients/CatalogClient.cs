using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using JiKeDa.Order.Api.Common;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JiKeDa.Order.Api.Clients;

public sealed record CatalogDish(int Id, int MerchantId, string Name, decimal Price, string? ImageUrl, int Inventory, bool IsAvailable);
public sealed record CatalogMerchant(int Id, string Name);
public sealed record CatalogOffer(int Id, int MerchantId, decimal MinPrice, decimal AmountRemission);
public sealed record ReservationLine(int DishId, int Quantity);
public sealed record ReservedLine(int DishId, int MerchantId, string DishName, decimal UnitPrice, int Quantity, decimal LineTotal);
public sealed record Reservation(string ReservationId, IReadOnlyList<ReservedLine> Items, decimal TotalAmount, bool Replayed);

public enum ReserveStatus { Reserved, DishNotFound, InsufficientStock, Unavailable, Conflict, Invalid }
public sealed record ReserveResult(ReserveStatus Status, Reservation? Reservation = null, string? Message = null);

/// <summary>Thrown when Catalog cannot be reached or answers with a server error.</summary>
public sealed class CatalogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The Catalog service as seen by Order. Public reads plus the internal reservation API.</summary>
public interface ICatalogClient
{
    Task<CatalogDish?> GetDishAsync(int dishId, CancellationToken cancellationToken);
    Task<CatalogMerchant?> GetMerchantAsync(int merchantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogOffer>> GetOffersAsync(int merchantId, CancellationToken cancellationToken);
    Task<ReserveResult> ReserveAsync(string reservationId, IReadOnlyList<ReservationLine> lines, CancellationToken cancellationToken);

    /// <summary>True when released or already released; false when Catalog has no such reservation.</summary>
    Task<bool> ReleaseAsync(string reservationId, CancellationToken cancellationToken);
}

/// <summary>
/// Signs a short-lived token with role "Service" for calls to /internal endpoints. Order cannot
/// forward the customer's token from the background worker, so every internal call uses this.
/// </summary>
public sealed class ServiceTokenProvider(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    private readonly JwtOptions _jwt = options.Value;

    public string Create()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(
            _jwt.Issuer,
            _jwt.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "order-service"), new Claim("role", "Service")],
            notBefore: now,
            expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class CatalogClient(HttpClient http, ServiceTokenProvider tokens) : ICatalogClient
{
    private sealed record Envelope<T>(bool Success, T? Data, string? Message, string? Code);

    public Task<CatalogDish?> GetDishAsync(int dishId, CancellationToken cancellationToken) =>
        GetAsync<CatalogDish>($"api/catalog/dishes/{dishId}", cancellationToken);

    public Task<CatalogMerchant?> GetMerchantAsync(int merchantId, CancellationToken cancellationToken) =>
        GetAsync<CatalogMerchant>($"api/merchants/{merchantId}", cancellationToken);

    public async Task<IReadOnlyList<CatalogOffer>> GetOffersAsync(int merchantId, CancellationToken cancellationToken) =>
        await GetAsync<List<CatalogOffer>>($"api/merchants/{merchantId}/special-offers", cancellationToken) ?? [];

    public async Task<ReserveResult> ReserveAsync(
        string reservationId,
        IReadOnlyList<ReservationLine> lines,
        CancellationToken cancellationToken)
    {
        using var request = Internal(HttpMethod.Post, "internal/catalog/reservations");
        request.Content = JsonContent.Create(new { reservationId, items = lines });
        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<Envelope<Reservation>>(cancellationToken);
        if (response.IsSuccessStatusCode && body?.Data is not null)
            return new(ReserveStatus.Reserved, body.Data);

        var status = body?.Code switch
        {
            "DISH_NOT_FOUND" => ReserveStatus.DishNotFound,
            "INSUFFICIENT_STOCK" => ReserveStatus.InsufficientStock,
            "UNAVAILABLE" => ReserveStatus.Unavailable,
            "RESERVATION_CONFLICT" => ReserveStatus.Conflict,
            _ => ReserveStatus.Invalid,
        };
        return new(status, Message: body?.Message);
    }

    public async Task<bool> ReleaseAsync(string reservationId, CancellationToken cancellationToken)
    {
        using var request = Internal(HttpMethod.Post, $"internal/catalog/reservations/{Uri.EscapeDataString(reservationId)}/release");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode)
            throw new CatalogUnavailableException($"Catalog release returned HTTP {(int)response.StatusCode}.");
        return true;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new CatalogUnavailableException($"Catalog GET {path} returned HTTP {(int)response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<Envelope<T>>(cancellationToken))?.Data;
    }

    private HttpRequestMessage Internal(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create());
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new CatalogUnavailableException("Catalog service is unreachable.", error);
        }
        if ((int)response.StatusCode >= 500)
        {
            response.Dispose();
            throw new CatalogUnavailableException($"Catalog returned HTTP {(int)response.StatusCode}.");
        }
        return response;
    }
}
