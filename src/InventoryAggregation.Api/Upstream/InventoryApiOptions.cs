using System.ComponentModel.DataAnnotations;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Settings for talking to the Inventory API, bound from the "InventoryApi" configuration section and
/// validated at startup so a missing key fails fast instead of on the first request.
/// </summary>
public sealed class InventoryApiOptions : IValidatableObject
{
    public const string SectionName = "InventoryApi";

    /// <summary>Root of the Inventory API, for example https://host/. Supplied outside source control.</summary>
    [Required]
    public string BaseUrl { get; set; } = "";

    /// <summary>Never committed: comes from user secrets or an environment variable.</summary>
    [Required]
    public string ApiKey { get; set; } = "";

    /// <summary>The request header the key is sent in.</summary>
    [Required]
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>The API accepts 1-25. The maximum means the fewest requests, so the fewest chances to fail.</summary>
    [Range(1, 25)]
    public int PageSize { get; set; } = 25;

    /// <summary>Total tries for one page, including the first.</summary>
    [Range(1, 10)]
    public int MaxAttemptsPerPage { get; set; } = 4;

    /// <summary>How long a single request may take before it is abandoned and retried.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Backoff before the first retry. It doubles on each further retry, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest server-requested Retry-After we will wait out. Beyond this we give up and say so.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound on collecting the whole dataset, so a caller is never left hanging.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult("BaseUrl must be an absolute http(s) URL.", [nameof(BaseUrl)]);
        }

        if (AttemptTimeout <= TimeSpan.Zero || TotalTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "AttemptTimeout and TotalTimeout must be greater than zero.",
                [nameof(AttemptTimeout), nameof(TotalTimeout)]);
        }

        if (BaseRetryDelay < TimeSpan.Zero || MaxRetryDelay < BaseRetryDelay || MaxRetryAfter < TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "Retry delays cannot be negative, and MaxRetryDelay cannot be below BaseRetryDelay.",
                [nameof(BaseRetryDelay), nameof(MaxRetryDelay), nameof(MaxRetryAfter)]);
        }
    }
}
