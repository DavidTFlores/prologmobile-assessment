using InventoryAggregation.Api.Upstream;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryAggregation.Tests.Support;

/// <summary>
/// The real application, started in memory, with only the network replaced. The DI wiring, the typed
/// HttpClient configuration, the retry loop, validation, aggregation and error mapping all run for real.
/// </summary>
internal sealed class InventoryApp(
    HttpMessageHandler upstream,
    Action<InventoryApiOptions>? configureOptions = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => upstream));

            // PostConfigure runs after configuration binding, so these values win regardless of what
            // appsettings, environment variables or a developer's own user secrets contain.
            services.PostConfigure<InventoryApiOptions>(options =>
            {
                options.BaseUrl = "http://inventory.test/v1";
                options.ApiKey = "test-key";
                options.ApiKeyHeaderName = "X-Api-Key";
                options.PageSize = 2;
                options.MaxAttemptsPerPage = 3;
                options.BaseRetryDelay = TimeSpan.Zero;
                options.MaxRetryDelay = TimeSpan.Zero;
                configureOptions?.Invoke(options);
            });

            configureServices?.Invoke(services);
        });
    }
}
