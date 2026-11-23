using System.Diagnostics.CodeAnalysis;

namespace InventoryAggregation.Api.Inventory;

/// <summary>
/// The outcome of asking for inventory data: either the value, or the reason it could not be obtained.
/// An upstream failure is an expected outcome here rather than an exceptional one, so it is part of the
/// return type and every caller has to deal with it. Exceptions are left for genuine bugs.
/// </summary>
public sealed class FetchResult<T>
{
    private FetchResult(T? value, FetchFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public FetchFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Failure is null;

    public static FetchResult<T> Success(T value) => new(value, null);

    public static implicit operator FetchResult<T>(FetchFailure failure) => new(default, failure);
}

/// <param name="Kind">What went wrong, in terms a caller can act on.</param>
/// <param name="Detail">Human-readable and safe to show to a caller: no secrets, no stack traces.</param>
/// <param name="RetryAfter">How long the upstream asked us to wait, when it said so.</param>
public sealed record FetchFailure(FetchFailureKind Kind, string Detail, TimeSpan? RetryAfter = null);

public enum FetchFailureKind
{
    /// <summary>Could not be reached, or kept answering with server errors.</summary>
    Unavailable,

    /// <summary>Did not answer within the time we were prepared to wait.</summary>
    TimedOut,

    /// <summary>Told us to slow down.</summary>
    RateLimited,

    /// <summary>Refused the request in a way a retry cannot fix, such as bad credentials or a bad request.</summary>
    Rejected,

    /// <summary>Answered, but with data that is malformed, incomplete or inconsistent.</summary>
    InvalidData,
}
