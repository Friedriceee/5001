using System.ComponentModel.DataAnnotations;
using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Fulfilment.Api.Features.Delivery;

public sealed class CreateRiderProfileRequest
{
    [Required, StringLength(80)] public string Name { get; set; } = string.Empty;
    [Required, RegularExpression(@"^\d{8,11}$")] public string PhoneNumber { get; set; } = string.Empty;
    [Required, StringLength(40)] public string Zone { get; set; } = string.Empty;
    [Range(1, 20)] public int Capacity { get; set; } = 3;
}

public sealed record RiderProfileCreatedResponse(int Id, string Name, string Zone);

[ApiController, Route("internal/fulfilment/riders")]
public sealed class RiderRegistrationController(FulfilmentDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResult<RiderProfileCreatedResponse>>> Create(
        CreateRiderProfileRequest request, CancellationToken cancellationToken)
    {
        // Seeded demo riders use low ids; generated registration ids stay in a separate range.
        var highest = await db.Riders.Select(item => (int?)item.Id).MaxAsync(cancellationToken) ?? 0;
        var rider = new Rider
        {
            Id = Math.Max(1000, highest + 1),
            Name = request.Name.Trim(),
            Zone = request.Zone.Trim(),
            Capacity = request.Capacity,
            IsActive = true,
            IsAvailable = true,
        };
        db.Riders.Add(rider);
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            ApiResult<RiderProfileCreatedResponse>.Ok(new(rider.Id, rider.Name, rider.Zone)));
    }

    [HttpDelete("{riderId:int}")]
    public async Task<IActionResult> Delete(int riderId, CancellationToken cancellationToken)
    {
        await db.Riders.Where(item => item.Id == riderId && !item.Deliveries.Any())
            .ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }
}
