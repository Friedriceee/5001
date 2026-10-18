using System.Net.Http.Json;
using JiKeDa.Identity.Api.Common;

namespace JiKeDa.Identity.Api.Features.Auth;

public interface IMerchantProfileClient
{
    Task<int> CreateAsync(RegisterMerchantRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}

public interface IRiderProfileClient
{
    Task<int> CreateAsync(RegisterRiderRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}

public sealed class MerchantProfileClient(HttpClient http) : IMerchantProfileClient
{
    public async Task<int> CreateAsync(RegisterMerchantRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("internal/catalog/merchants", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResult<ProfileCreatedResponse>>(cancellationToken);
        return body?.Data?.Id ?? throw new HttpRequestException("Catalog did not return a merchant profile id.");
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken) =>
        _ = await http.DeleteAsync($"internal/catalog/merchants/{id}", cancellationToken);
}

public sealed class RiderProfileClient(HttpClient http) : IRiderProfileClient
{
    public async Task<int> CreateAsync(RegisterRiderRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("internal/fulfilment/riders", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResult<ProfileCreatedResponse>>(cancellationToken);
        return body?.Data?.Id ?? throw new HttpRequestException("Fulfilment did not return a rider profile id.");
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken) =>
        _ = await http.DeleteAsync($"internal/fulfilment/riders/{id}", cancellationToken);
}

public sealed record ProfileCreatedResponse(int Id);
