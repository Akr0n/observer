using System.Text.Json;
using System.Text.Json.Serialization;
using Observer.Core.Security;

namespace Observer.App.Services;

/// <summary>A machine as it is written in the file.</summary>
/// <param name="Name">What to call it on screen. Optional.</param>
/// <param name="BaseAddress">The service's address, over HTTPS.</param>
/// <param name="ApiToken">THAT machine's token.</param>
/// <param name="Fingerprint">THAT machine's certificate fingerprint.</param>
public sealed record MachineEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("baseAddress")] string? BaseAddress,
    [property: JsonPropertyName("apiToken")] string? ApiToken,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint);

/// <summary>The contents of <c>machines.json</c>.</summary>
/// <param name="Machines">The remote machines. The local one is not listed: it is always there.</param>
public sealed record MachinesFile(
    [property: JsonPropertyName("machines")] IReadOnlyList<MachineEntry>? Machines);

/// <summary>The resolved list, with the reasons for whatever did not make it in.</summary>
/// <param name="Machines">The usable machines. The first one is always this machine.</param>
/// <param name="Problems">One sentence per rejected entry, ready to put on screen.</param>
public sealed record MachineListResult(
    IReadOnlyList<ObserverEndpoint> Machines,
    IReadOnlyList<string> Problems);

/// <summary>
/// Where the list of machines to watch comes from.
/// </summary>
/// <remarks>
/// The file is the single source of truth and the dashboard <b>only reads it</b>. There is no
/// window for adding machines, and that is not a gap: it means no field validation to maintain,
/// no edit window, and above all no program with a graphical interface writing a file full of
/// other machines' credentials.
/// <para>
/// The local machine <b>is not listed</b> and cannot be removed: it is always the first entry,
/// and it has no address, no token and no fingerprint, because it goes through the local channel.
/// </para>
/// </remarks>
public static class MachineDirectory
{
    /// <summary>The file name, next to the older <c>client.json</c>.</summary>
    public const string FileName = "machines.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Where the list lives: next to the single-machine configuration.</summary>
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(ClientConfiguration.FilePath) ?? ".",
        FileName);

    /// <summary>Actually reads the disk and the environment.</summary>
    /// <returns>The list and the problems.</returns>
    public static MachineListResult Read() =>
        Resolve(ReadFile(FilePath), ClientConfiguration.Read(), SecretStores.ForThisMachine());

    /// <summary>Builds the list without touching the disk.</summary>
    /// <param name="content">The raw contents of <c>machines.json</c>, if it exists.</param>
    /// <param name="fallback">
    /// What the single-machine configuration says, used when the list is not there.
    /// </param>
    /// <returns>The list and the problems.</returns>
    /// <remarks>
    /// If <c>machines.json</c> does not exist the older <c>client.json</c> still applies, with its
    /// environment variable. Anyone who had already configured one machine has nothing to redo
    /// just because several can be listed now.
    /// </remarks>
    public static MachineListResult Resolve(
        string? content, ClientConfigurationResult fallback, ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(store);

        // The machine you are sitting at is ALWAYS there, and it comes first. It needs nothing
        // to work, so there is no way to get its configuration wrong.
        List<ObserverEndpoint> machines = [ObserverEndpoint.LocalChannel()];
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(content))
        {
            AddFallback(fallback, machines, problems);

            return new MachineListResult(machines, problems);
        }

        MachinesFile? file;

        try
        {
            file = JsonSerializer.Deserialize<MachinesFile>(content, JsonOptions);
        }
        catch (JsonException error)
        {
            problems.Add(
                $"{FilePath} isn't valid JSON ({error.Message}). Until it is fixed, only this " +
                "machine is listed. " + DescribeExample());

            return new MachineListResult(machines, problems);
        }

        if (file?.Machines is null)
        {
            // Valid JSON but with no list: this is not an "empty and that's fine" file, it is a
            // file someone thought they had written. Silently emptying the list would also make
            // the older single-machine configuration disappear, and whoever is watching would
            // just see a machine vanish without knowing why.
            problems.Add(
                $"{FilePath} has no \"machines\" list, so nothing in it can be used. " + DescribeExample());

            AddFallback(fallback, machines, problems);

            return new MachineListResult(machines, problems);
        }

        foreach (MachineEntry entry in file.Machines)
        {
            try
            {
                if (ToEndpoint(entry, store) is { } endpoint)
                {
                    machines.Add(endpoint);
                }
                else
                {
                    problems.Add(ExplainRejection(entry, store));
                }
            }
            catch (SecretStoreException error)
            {
                // A store that cannot be trusted drops THAT entry and says so, instead of
                // bringing down the whole list: the other machines have nothing to do with it.
                problems.Add(error.Message);
            }
        }

        return new MachineListResult(machines, problems);
    }

    /// <summary>
    /// Adds the older single-machine configuration, if it is usable.
    /// </summary>
    /// <remarks>
    /// It goes through the SAME requirements as the listed entries, and that is not redundancy:
    /// without this check the older <c>client.json</c> would be a back door letting
    /// <c>http://</c> and endpoints with no fingerprint back in, which is exactly what the list
    /// refuses.
    /// </remarks>
    private static void AddFallback(
        ClientConfigurationResult fallback,
        List<ObserverEndpoint> machines,
        List<string> problems)
    {
        if (fallback.Endpoint is not { Kind: EndpointKind.Remote } singleMachine)
        {
            if (fallback.Problem is { Length: > 0 } problem)
            {
                problems.Add(problem);
            }

            return;
        }

        if (singleMachine.BaseAddress.Scheme != Uri.UriSchemeHttps || !singleMachine.IsFingerprintPinned)
        {
            problems.Add(
                $"{singleMachine.Description} comes from the older single-machine configuration and " +
                "can't be used as it stands: a remote machine needs an https address and a " +
                "certificate fingerprint. " + DescribeExample());

            return;
        }

        machines.Add(singleMachine);
    }

    /// <summary>An example of a correct file, for use in the problem messages.</summary>
    /// <returns>The example text.</returns>
    public static string DescribeExample() =>
        "A machine looks like this: { \"name\": \"laptop\", \"baseAddress\": " +
        "\"https://laptop:5058/\", \"fingerprint\": \"sha256:...\" }. Run \"observer share\" " +
        "on that machine to get the fingerprint, and \"observer token set laptop\" here to keep " +
        "its token out of this file, then reopen this window.";

    private static ObserverEndpoint? ToEndpoint(MachineEntry entry, ISecretStore store)
    {
        if (string.IsNullOrWhiteSpace(entry.BaseAddress)
            || !Uri.TryCreate(WithTrailingSlash(entry.BaseAddress.Trim()), UriKind.Absolute, out Uri? address)
            || address.Scheme != Uri.UriSchemeHttps
            || CertificateFingerprint.Normalize(entry.Fingerprint) is null
            || string.IsNullOrWhiteSpace(entry.Name))
        {
            return null;
        }

        // A token written in the file is NOT used, not even when it is the right one. Accepting
        // it "just for compatibility" would mean the secret can stay there for ever, and that
        // this change took nothing away from anyone.
        if (!string.IsNullOrWhiteSpace(entry.ApiToken))
        {
            return null;
        }

        return store.TryRead(entry.Name.Trim(), out string token)
            ? ObserverEndpoint.Remote(
                address,
                token,
                "from the secret store",
                entry.Fingerprint,
                entry.Name)
            : null;
    }

    /// <summary>Why an entry was rejected, phrased so that it can be fixed.</summary>
    /// <remarks>
    /// By far the most likely case is <c>http://</c> instead of <c>https://</c>, and it deserves
    /// a sentence of its own: since the service has a machine certificate it no longer answers
    /// in the clear over the network, so an old address is not a typo but a configuration that
    /// was right yesterday.
    /// </remarks>
    private static string ExplainRejection(MachineEntry entry, ISecretStore store)
    {
        string label = string.IsNullOrWhiteSpace(entry.Name)
            ? (string.IsNullOrWhiteSpace(entry.BaseAddress) ? "an entry with no address" : entry.BaseAddress.Trim())
            : entry.Name.Trim();

        if (!string.IsNullOrWhiteSpace(entry.BaseAddress)
            && entry.BaseAddress.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return
                label + " is listed with an http:// address. Observer no longer answers in the " +
                "clear over the network: the token used to cross it once a second, and a single " +
                "packet capture handed over a permanent credential. Change it to https:// and " +
                "add that machine's fingerprint.";
        }

        if (CertificateFingerprint.Normalize(entry.Fingerprint) is null)
        {
            return string.IsNullOrWhiteSpace(entry.Fingerprint)
                ? label + " has no fingerprint, so there is no way to tell that machine apart from " +
                  "anyone able to stand in the middle of the connection. " + DescribeExample()
                : label + " has a fingerprint that isn't a SHA-256 value of " +
                  CertificateFingerprint.DigitCount() + " hex digits. " + DescribeExample();
        }

        if (!string.IsNullOrWhiteSpace(entry.ApiToken))
        {
            return
                label + " still carries its token inside " + FileName + ", and Observer will not " +
                "use it from there. That token now also authorises ending processes on that " +
                "machine, so a file meant to be read, copied and shared is the wrong place for " +
                "it. Run \"" + TokenSetCommand(label) + "\" to hand it over, then delete the " +
                "\"apiToken\" line from " + FilePath + ".";
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            return
                "An entry with address " + (entry.BaseAddress ?? "(none)").Trim() + " has no " +
                "\"name\", and the name is how its token is looked up in " +
                store.Description + ". " + DescribeExample();
        }

        // The address is checked BEFORE the missing token: with a malformed address that
        // machine is unreachable anyway, and sending someone off to store a token would be
        // sending them to do the right thing in the wrong order.
        if (string.IsNullOrWhiteSpace(entry.BaseAddress)
            || !Uri.TryCreate(WithTrailingSlash(entry.BaseAddress.Trim()), UriKind.Absolute, out Uri? address)
            || address.Scheme != Uri.UriSchemeHttps)
        {
            return label + " can't be used: the address must be a full https:// address. " + DescribeExample();
        }

        return
            label + " has no token in " + store.Description + ", and another machine's Observer " +
            "rejects every request that isn't authenticated. Run \"" + TokenSetCommand(label) +
            "\" to store it.";
    }

    // A name with a space in it has to be quoted: unquoted, the shell splits it in two, and
    // "observer token set" refuses the extra word rather than keep the token under the first.
    private static string TokenSetCommand(string label) =>
        "observer token set " +
        (label.Contains(' ', StringComparison.Ordinal) ? "\"" + label + "\"" : label);

    private static string WithTrailingSlash(string address) =>
        address.EndsWith('/') ? address : address + "/";

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}