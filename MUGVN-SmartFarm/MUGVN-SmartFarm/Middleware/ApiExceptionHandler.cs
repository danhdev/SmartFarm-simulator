using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace MUGVN_SmartFarm.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            FarmException { Code: FarmErrorCode.InvalidInput } => 400,
            FarmException { Code: FarmErrorCode.NotFound } => 404,
            FarmException { Code: FarmErrorCode.Conflict } => 409,
            MongoException or TimeoutException => 503, _ => 500
        };
        logger.LogWarning("API failure {StatusCode} {Failure}", status, exception.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = status,
                Title = status switch
                {
                    400 => "Invalid request", 404 => "Not found", 409 => "Conflict", 503 => "Storage unavailable",
                    _ => "Request failed"
                },
                Detail = exception is FarmException ? exception.Message : "The request could not be completed",
                Instance = context.Request.Path
            }, options: JsonDefaults.Options, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }
}