using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// The endpoint that can revoke this machine's key, and the two guards in front of it.
/// </summary>
/// <remarks>
/// There are two refusals and they are not the same one. The access control refuses a caller
/// that is not local, before the endpoint exists for it at all - that is the local-only marker,
/// and the platform test files cover it on a real pipe and a real socket. This file covers the
/// SECOND one: the endpoint's own rule, which it has to ask itself.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class CredentialEndpointsTests
{
    [Fact]
    public async Task TheEndpointAsksItsOwnRuleAndDoesNotRelyOnTheMiddlewareToHaveDoneIt()
    {
        // THE ACCESS CONTROL IS DELIBERATELY NOT MOUNTED HERE, and that is the whole design of
        // the test. With it, a caller from the network gets 404 from the local-only marker and
        // the endpoint is never entered - so deleting the endpoint's own gate would change
        // nothing observable, and the guard would be untested while looking covered. Without the
        // middleware the request reaches the handler, the caller classifies as FromNetwork, and
        // the only thing that can still refuse it is the line under test.
        //
        // It is also host-independent, which the elevated branch could never be: the rule is
        // false for FromNetwork at every elevation, so this reads the same on an elevated CI
        // runner and on an ordinary desktop. That distinction is what #95 had to learn twice.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.Listen(IPAddress.Loopback, 0),
            app => app.MapCredentialEndpoints(),
            services: services => services.AddSingleton(Holding("k", null)));

        using HttpClient client = new() { BaseAddress = new Uri(bench.Addresses.Single()) };
        using HttpResponseMessage answer =
            await client.PostAsync("credentials/reload", content: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task ARefusalSaysNothingAboutWhatTheServiceIsHolding()
    {
        // The refusal body is read by a person during an incident, so it has to name the remedy -
        // and it must not become a place where the state of the credentials leaks out to a
        // caller who has just been told they may not touch them.
        await using RealKestrelBench bench = await RealKestrelBench.StartAsync(
            options => options.Listen(IPAddress.Loopback, 0),
            app => app.MapCredentialEndpoints(),
            services: services => services.AddSingleton(Holding("secret-key", "older-secret")));

        using HttpClient client = new() { BaseAddress = new Uri(bench.Addresses.Single()) };
        using HttpResponseMessage answer =
            await client.PostAsync("credentials/reload", content: null, CancellationToken.None);

        string body = await answer.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.DoesNotContain("secret-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("older-secret", body, StringComparison.Ordinal);
        Assert.Contains("administrative rights", body, StringComparison.Ordinal);
    }

    /// <summary>A source with no store behind it, so a reload can touch no file.</summary>
    private static CredentialSource Holding(string current, string? previous) =>
        new(new ProvisionedCredentials(
            new MachineCredentials(current, previous, previous is null ? null : DateTimeOffset.UtcNow.AddHours(1)),
            CredentialOrigin.Configuration,
            null));
}
