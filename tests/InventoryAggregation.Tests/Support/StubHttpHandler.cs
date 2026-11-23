using System.Net;
using System.Text;

namespace InventoryAggregation.Tests.Support;

/// <summary>
/// Stands in for the network. Responses are scripted in order; when the script runs out the
/// <see cref="ThenAlways(Responder)"/> responder answers, and with neither the test fails loudly because
/// the code under test made a request the test did not expect.
/// </summary>
internal sealed class StubHttpHandler(TimeProvider? clock = null) : HttpMessageHandler
{
    public delegate Task<HttpResponseMessage> Responder(HttpRequestMessage request, CancellationToken cancellationToken);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Queue<Responder> _script = new();
    private readonly SemaphoreSlim _arrivals = new(0);
    private Responder? _fallback;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>When each request arrived, by the supplied clock.</summary>
    public List<DateTimeOffset> RequestTimes { get; } = [];

    /// <summary>
    /// Completes once one more request has reached the stub. Tests that drive a fake clock use this as
    /// their sync point: once the request is in flight, its timeout timer is known to be running.
    /// </summary>
    public async Task NextRequestAsync()
    {
        if (!await _arrivals.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("No further request reached the stub.");
        }
    }

    public StubHttpHandler Then(Responder responder)
    {
        _script.Enqueue(responder);
        return this;
    }

    public StubHttpHandler Then(HttpStatusCode status, Action<HttpResponseMessage>? configure = null)
        => Then(Status(status, configure));

    public StubHttpHandler ThenJson(string json) => Then(Json(json));

    public StubHttpHandler ThenThrow(Exception exception)
        => Then((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers; ends only when the request is cancelled.</summary>
    public StubHttpHandler ThenHang() => Then(Hang);

    public StubHttpHandler ThenAlways(Responder responder)
    {
        _fallback = responder;
        return this;
    }

    public StubHttpHandler ThenAlways(HttpStatusCode status, Action<HttpResponseMessage>? configure = null)
        => ThenAlways(Status(status, configure));

    public static Responder Json(string json) => (_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public static Responder Status(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) => (_, _) =>
    {
        var response = new HttpResponseMessage(status);
        configure?.Invoke(response);
        return Task.FromResult(response);
    };

    public static async Task<HttpResponseMessage> Hang(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable: an infinite delay only ends by cancellation.");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestTimes.Add(_clock.GetUtcNow());
        _arrivals.Release();

        var responder = _script.Count > 0
            ? _script.Dequeue()
            : _fallback ?? throw new InvalidOperationException(
                $"Unexpected request #{Requests.Count} to {request.RequestUri}: the stub has no response left.");

        return responder(request, cancellationToken);
    }
}
