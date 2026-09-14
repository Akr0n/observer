using System.Net;
using System.Text.Json;

namespace Observer.Service.Tests;

/// <summary>
/// The process endpoints, against the real service started in memory.
/// </summary>
/// <remarks>
/// Here is the one thing this service does that is not a read, and those are exactly the
/// checks that matter: that <c>/processes</c> cannot be reached without a token, and that
/// <c>kill</c> on a PID that does not exist answers "not there" instead of bringing something
/// else down.
/// <para>
/// The path where a process is REALLY terminated is not covered, and that is a choice: a test
/// that kills a process on a development machine or on a CI runner can hit something that is
/// needed, and the only part of that path that is ours — finding the process from the PID and
/// asking the system to stop it — is two calls into the standard library. The real risk is not
/// that Kill does not work: it is that the wrong process gets stopped, and that depends on the
/// PID arriving in the request.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class ProcessEndpointsTests
{
    private readonly InMemoryService service;

    public ProcessEndpointsTests(InMemoryService service)
    {
        this.service = service;
    }

    [Theory]
    [InlineData("/processes")]
    [InlineData("/processes?by=memory")]
    public async Task TheProcessListIsRefusedWithoutAToken(string path)
    {
        // The process list says far more than a CPU percentage: it says which programs the
        // person at that machine uses. An endpoint added outside the middleware would hand it
        // to anyone on the network.
        using HttpClient anonymous = service.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NothingCanBeKilledWithoutAToken()
    {
        using HttpClient anonymous = service.CreateClient();

        using HttpResponseMessage response = await anonymous.PostAsync(
            new Uri("/processes/999999/kill", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheListIncludesAtLeastTheProcessServingTheRequest()
    {
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/processes", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement processes = document.RootElement.GetProperty("processes");

        Assert.True(
            processes.GetArrayLength() > 0,
            "the process list is empty on the very machine serving it");

        JsonElement first = processes[0];
        Assert.True(first.GetProperty("pid").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task OrderingByMemoryPutsTheBiggestFirst()
    {
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/processes?by=memory&top=5", UriKind.Relative));

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement processes = document.RootElement.GetProperty("processes");

        Assert.True(processes.GetArrayLength() <= 5);

        long previous = long.MaxValue;

        foreach (JsonElement process in processes.EnumerateArray())
        {
            long current = process.GetProperty("workingSetBytes").GetInt64();
            Assert.True(current <= previous, "the list by memory is not in descending order");
            previous = current;
        }
    }

    [Fact]
    public async Task KillingAPidThatDoesNotExistAnswersNotFound()
    {
        using HttpClient client = service.CreateAuthorizedClient();

        // A PID this high cannot be assigned on either system: the case is "not there", and
        // the right answer is to say so, not a server error.
        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/processes/2147483646/kill", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/processes", "cpu")]
    [InlineData("/processes?by=memory", "memory")]
    [InlineData("/processes?by=io", "io")]
    [InlineData("/processes?by=IO", "io")]
    [InlineData("/processes?by=nonsense", "cpu")]
    public async Task TheResponseEchoesTheCriterionItApplied(string path, string expected)
    {
        // The client uses it to notice a service that does not know "io" yet: without it, the
        // client would receive the CPU list and show it under the I/O title.
        using HttpClient client = service.CreateAuthorizedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("by").GetString());
    }

    [Fact]
    public async Task OrderingByIoPutsTheBusiestFirstAndTheUnknownRatesLast()
    {
        using HttpClient client = service.CreateAuthorizedClient();
        Uri path = new("/processes?by=io&top=100", UriKind.Relative);

        // Two reads: on the first there is no previous sample and every rate is unknown.
        (await client.GetAsync(path)).Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using HttpResponseMessage response = await client.GetAsync(path);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        double previous = double.MaxValue;
        bool unknownRatesStarted = false;

        foreach (JsonElement process in document.RootElement.GetProperty("processes").EnumerateArray())
        {
            JsonElement rate = process.GetProperty("ioBytesPerSecond");

            if (rate.ValueKind == JsonValueKind.Null)
            {
                unknownRatesStarted = true;

                continue;
            }

            Assert.False(unknownRatesStarted, "a known rate after an unknown one: the ordering is wrong");

            double current = rate.GetDouble();
            Assert.True(current <= previous, "the list by I/O is not in descending order");
            previous = current;
        }

        // At least one rate must be KNOWN. Without this line the test would pass vacuously
        // with every rate null - that is, with the I/O reader never wired up in Program.cs -
        // and a mutation proved it: new SystemProcessLister(ioReader: null), suite green.
        // This is the only test that crosses the real wiring, from the service to the system.
        Assert.Contains(
            document.RootElement.GetProperty("processes").EnumerateArray(),
            process => process.GetProperty("ioBytesPerSecond").ValueKind != JsonValueKind.Null);
    }
}
