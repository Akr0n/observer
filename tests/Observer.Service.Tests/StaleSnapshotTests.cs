using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Observer.Core.Metrics;

namespace Observer.Service.Tests;

/// <summary>
/// What <c>/metrics/latest</c> does when the sampler has stopped but the service has not.
/// </summary>
/// <remarks>
/// The endpoint reads only from the cache, on purpose: two simultaneous requests must not make
/// the collectors run twice. The cost of that choice is this defect — if the sampling loop dies,
/// and the loop's own remarks say a hung P/Invoke can do exactly that without observing the
/// cancellation token, the cache keeps handing out the SAME snapshot for ever and the endpoint
/// serves it with a 200. A dashboard then draws those numbers once a second under a green dot:
/// a machine that stopped measuring looks healthier than one that is switched off.
/// <para>
/// The age is the SERVICE's own, taken from a monotonic counter between one publish and the
/// read. Not from <c>CapturedAt</c> — that is a wall clock, and a wall clock steps. The test
/// below nails that down by publishing a snapshot stamped 1970 and demanding a 200: anything
/// that measured the age from <c>CapturedAt</c> would call it fifty-six years old.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class StaleSnapshotTests
{
    private readonly InMemoryService service;

    public StaleSnapshotTests(InMemoryService service)
    {
        this.service = service;
    }

    [Fact]
    public async Task ASnapshotThatStoppedAdvancingIsNotServedAsCurrent()
    {
        ControlledTime time = new();

        using WebApplicationFactory<Program> host = WithoutTheSampler(time);
        using HttpClient client = Authorized(host);

        MetricSnapshotCache cache = host.Services.GetRequiredService<MetricSnapshotCache>();

        // Stamped at the epoch, deliberately. The age must come from the service's own counter,
        // and a reading whose CapturedAt says 1970 is still CURRENT if it was published a
        // moment ago - which is exactly what a machine whose clock is wrong looks like.
        cache.Publish(new MachineSnapshot(MachineSnapshot.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, []));

        // At EXACTLY the threshold it is still served - the check is "older than", not "as old
        // as" - and taking this reading after advancing the clock is the point: asserted at age
        // zero it would agree with any threshold at all, a millisecond included, and a service
        // built that way answers 503 to every request it ever gets.
        time.Advance(MetricSnapshotCache.StaleAfter);

        using (HttpResponseMessage fresh = await client.GetAsync(new Uri("/metrics/latest", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        time.Advance(TimeSpan.FromSeconds(1));

        using HttpResponseMessage stale = await client.GetAsync(new Uri("/metrics/latest", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, stale.StatusCode);

        // And it says WHICH of the two silences it is, because they send you to different
        // places: one is a service that is still starting, the other one is a service whose
        // measuring has died while the rest of it goes on answering.
        string body = await stale.Content.ReadAsStringAsync();
        Assert.Contains("stopped", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AServiceThatHasNeverSampledSaysSomethingElse()
    {
        ControlledTime time = new();

        using WebApplicationFactory<Program> host = WithoutTheSampler(time);
        using HttpClient client = Authorized(host);

        // Nothing published at all: this is the machine that has just started.
        using HttpResponseMessage response = await client.GetAsync(new Uri("/metrics/latest", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("first reading", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stopped", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheThresholdIsWellAboveOneRoundAndWellBelowAnyonesPatience()
    {
        // The number itself, because every other assertion in this file is written in terms of
        // it and would agree with any value whatsoever. Below about ten seconds it would start
        // firing on a round that overran its second, which is ordinary under load and which the
        // service logs separately; above about half a minute a machine that has stopped
        // measuring goes on looking healthy for longer than anybody watches one screen.
        Assert.InRange(
            MetricSnapshotCache.StaleAfter,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AServiceWhoseSamplerIsRunningServesItsReadings()
    {
        // The whole real wiring, sampler included. It is the only place that asks this endpoint
        // of a service that is genuinely measuring, and without it the refusal has nothing
        // holding it down from above: an endpoint that answered 503 to everything would leave
        // every other test in this repository green.
        using HttpClient client = service.CreateAuthorizedClient();

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));

        HttpStatusCode? answered = null;

        // A budget, not a wait: the first sample lands within a second of the host starting, and
        // the loop leaves as soon as it does. The fifteen are for a busy runner.
        while (!stop.IsCancellationRequested && answered != HttpStatusCode.OK)
        {
            using (HttpResponseMessage response =
                await client.GetAsync(new Uri("/metrics/latest", UriKind.Relative)))
            {
                answered = response.StatusCode;
            }

            if (answered != HttpStatusCode.OK)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }

        Assert.Equal(HttpStatusCode.OK, answered);
    }

    /// <summary>The real service, on a clock the test moves, and with nobody publishing.</summary>
    /// <remarks>
    /// The sampler is removed and that is the whole point of this fixture: left in, it
    /// republishes once a second and no snapshot is ever old enough to test. What stays is the
    /// real wiring from the route to the cache, which is what could be got wrong.
    /// </remarks>
    private WebApplicationFactory<Program> WithoutTheSampler(TimeProvider time) =>
        service.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(time);

            ServiceDescriptor? sampler = services.FirstOrDefault(
                registration => registration.ImplementationType == typeof(MetricSamplingService));

            Assert.NotNull(sampler);
            services.Remove(sampler);
        }));

    private static HttpClient Authorized(WebApplicationFactory<Program> host)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", InMemoryService.Token);

        return client;
    }

    /// <summary>A monotonic counter the test advances by hand.</summary>
    /// <remarks>
    /// Ten lines instead of a package: what is needed here is a timestamp that moves only when
    /// the test says so, and <see cref="TimeProvider"/> already has the two members that takes.
    /// </remarks>
    private sealed class ControlledTime : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}