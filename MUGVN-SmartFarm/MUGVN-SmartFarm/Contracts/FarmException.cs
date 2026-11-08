namespace SmartFarm.Contracts;

public enum FarmErrorCode { InvalidInput, NotFound, Conflict }

public sealed class FarmException(FarmErrorCode code, string message) : Exception(message)
{
    public FarmErrorCode Code { get; } = code;
}
