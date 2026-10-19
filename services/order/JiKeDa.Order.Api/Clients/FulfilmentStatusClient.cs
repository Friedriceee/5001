using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace JiKeDa.Order.Api.Clients;

public sealed record FulfilmentOrderStatus(int OrderId, string Status);

public interface IFulfilmentStatusClient
{
    Task<IReadOnlyDictionary<int, string>> GetStatusesAsync(
        IReadOnlyCollection<int> orderIds,
        CancellationToken cancellationToken);
}

public sealed class FulfilmentStatusClient(HttpClient http, ServiceTokenProvider tokens) : IFulfilmentStatusClient
{
    private sealed record Envelope<T>(bool Success, T? Data, string? Message, string? Code);

    public async Task<IReadOnlyDictionary<int, string>> GetStatusesAsync(
        IReadOnlyCollection<int> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0) return new Dictionary<int, string>();

        var query = string.Join("&", orderIds.Distinct().Select(id => $"orderIds={id}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"internal/fulfilment/order-statuses?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create());
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<List<FulfilmentOrderStatus>>>(cancellationToken);
        return (envelope?.Data ?? []).ToDictionary(item => item.OrderId, item => item.Status);
    }
}
