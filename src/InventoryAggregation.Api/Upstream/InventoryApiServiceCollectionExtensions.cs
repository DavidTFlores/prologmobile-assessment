using System.Net.Http.Headers;
using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

public static class InventoryApiServiceCollectionExtensions
{
    /// <summary>Registers everything needed to read the Inventory API, exposed to the app as <see cref="IInventorySource"/>.</summary>
    public static IServiceCollection AddInventoryApi(this IServiceCollection services)
    {
        // Validated when the host starts, so a missing key or URL stops the app with a clear message
        // instead of surfacing as a failed request later.
        services.AddOptions<InventoryApiOptions>()
            .BindConfiguration(InventoryApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // A typed client: IHttpClientFactory owns and recycles the underlying handler, which avoids both
        // socket exhaustion (a new HttpClient per call) and stale DNS (one static HttpClient forever).
        services.AddHttpClient<IInventoryPageClient, InventoryPageClient>((provider, http) =>
            {
                var options = provider.GetRequiredService<IOptions<InventoryApiOptions>>().Value;

                // The trailing slash matters: without it, a base URL with a path ("https://host/v1")
                // would lose its last segment when the relative "api/records" is resolved against it.
                http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                http.DefaultRequestHeaders.Add(options.ApiKeyHeaderName, options.ApiKey);
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // InventoryPageClient enforces its own per-attempt timeout. Leaving HttpClient's default
                // 100 s timeout in place as well would only make it unclear which one had fired.
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            // HttpClient logs headers at Trace level. Redact them all so the key cannot reach a log.
            .RedactLoggedHeaders(_ => true);

        // Transient, like the typed client it depends on. Held by a singleton, the typed client would be
        // captured for the life of the app and the factory could never recycle its handler.
        services.AddTransient<IInventorySource, PaginatedInventorySource>();

        return services;
    }
}
