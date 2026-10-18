using JiKeDa.Catalog.Api.Features.Dishes;

namespace JiKeDa.Catalog.Api.Features.Merchants;

/// <summary>
/// Merchant business profile. Login credentials live in the Identity service;
/// a Merchant account's profile_id is this Id.
/// </summary>
public class Merchant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Contact { get; set; }
    public string? DishType { get; set; }
    public int? TimeForOpenBusiness { get; set; }
    public int? TimeForCloseBusiness { get; set; }

    public ICollection<Dish> Dishes { get; set; } = new List<Dish>();
    public ICollection<SpecialOffer> SpecialOffers { get; set; } = new List<SpecialOffer>();
}

/// <summary>Spend-threshold discount published by a merchant. Read-only in Catalog for now.</summary>
public class SpecialOffer
{
    public int Id { get; set; }
    public int MerchantId { get; set; }
    public decimal MinPrice { get; set; }
    public decimal AmountRemission { get; set; }

    public Merchant Merchant { get; set; } = null!;
}

public sealed record MerchantResponse(
    int Id,
    string Name,
    string? Address,
    string? Contact,
    string? DishType,
    int? TimeForOpenBusiness,
    int? TimeForCloseBusiness);

public sealed record SpecialOfferResponse(
    int Id,
    int MerchantId,
    decimal MinPrice,
    decimal AmountRemission);
