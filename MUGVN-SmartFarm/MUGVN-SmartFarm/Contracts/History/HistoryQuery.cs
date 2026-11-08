
using SmartFarm.Contracts;
namespace SmartFarm.Contracts.History;

public sealed record HistoryQuery(
    string DeviceId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit = 100,
    string? Cursor = null)
{
    public void Validate()
    {
        if (From >= To || To - From > TimeSpan.FromDays(366))
            throw new FarmException(FarmErrorCode.InvalidInput, "A nonempty time range of at most 366 days is required");
        if (Limit is < 1 or > 1000) throw new FarmException(FarmErrorCode.InvalidInput, "limit must be between 1 and 1000");
    }
}

