using System.Globalization;
using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api.Endpoints;

public static class InventorySummaryEndpoint
{
    public const string Route = "/api/inventory/summary";

    public static IEndpointRouteBuilder MapInventorySummary(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, GetSummaryAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSummaryAsync(
        IInventorySource source,
        InventoryAggregator aggregator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await source.GetAllRecordsAsync(cancellationToken);

        return result.IsSuccess
            ? Results.Ok(aggregator.Aggregate(result.Value))
            : ToProblem(result.Failure, httpContext.Response);
    }

    /// <summary>
    /// This service is a gateway to the Inventory API, so its failures are reported with the gateway
    /// status codes: 504 when the upstream was too slow, 503 when it asked us to back off, and 502 for
    /// everything else that stopped us getting a complete, valid dataset. The body is an RFC 9457
    /// problem document; <c>failureKind</c> gives a caller something stable to branch on.
    /// </summary>
    private static IResult ToProblem(FetchFailure failure, HttpResponse response)
    {
        var (status, title) = failure.Kind switch
        {
            FetchFailureKind.TimedOut =>
                (StatusCodes.Status504GatewayTimeout, "The inventory API did not respond in time."),
            FetchFailureKind.RateLimited =>
                (StatusCodes.Status503ServiceUnavailable, "The inventory API is rate limiting requests."),
            FetchFailureKind.Rejected =>
                (StatusCodes.Status502BadGateway, "The inventory API rejected the request."),
            FetchFailureKind.InvalidData =>
                (StatusCodes.Status502BadGateway, "The inventory API returned data that could not be trusted."),
            _ =>
                (StatusCodes.Status502BadGateway, "The inventory API is unavailable."),
        };

        if (failure.RetryAfter is { } retryAfter)
        {
            // Pass the upstream's own advice on, so a well-behaved caller backs off for long enough.
            response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return Results.Problem(
            statusCode: status,
            title: title,
            detail: failure.Detail,
            extensions: new Dictionary<string, object?> { ["failureKind"] = failure.Kind.ToString() });
    }
}
