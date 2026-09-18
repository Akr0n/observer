using System.Security.Cryptography;
using Observer.App.Services;
using Observer.Core.Security;

namespace Observer.App.Tests;

/// <summary>
/// L'elenco delle macchine, e cio' che NON ci entra.
/// </summary>
/// <remarks>
/// La regola che questa classe difende: una voce configurata male non sparisce in silenzio. Una
/// macchina che semplicemente non compare e' indistinguibile da una che non e' stata aggiunta,
/// e chi la cerca non ha modo di sapere che cosa correggere.
/// <para>
/// Da oggi ne difende una seconda: <b>il token non sta piu' nel file</b>. Sta nel deposito del
/// sistema, e una voce che se lo porta ancora dietro viene rifiutata anche se quel token e'
/// giusto — accettarlo "per compatibilita'" vorrebbe dire che il segreto puo' restare li' per
/// sempre.
/// </para>
/// </remarks>
public class MachineDirectoryTests
{
    private static readonly string Fingerprint =
        CertificateFingerprint.From(SHA256.HashData("una macchina"u8.ToArray()));

    private static ClientConfigurationResult NoOtherConfiguration() =>
        new(ObserverEndpoint.LocalChannel(), null);

    private static MachineListResult Read(string json, ISecretStore? store = null) =>
        MachineDirectory.Resolve(json, NoOtherConfiguration(), store ?? FakeSecretStore.With("laptop", "il-token"));

    /// <summary>Una voce del file. Il token si passa solo per provare che viene rifiutato.</summary>
    private static string Entry(
        string? address, string? fingerprint, string name = "laptop", string? tokenInFile = null) =>
        $$"""
          { "machines": [ { "name": {{JsonValue(name)}}, "baseAddress": {{JsonValue(address)}},
            {{(tokenInFile is null ? string.Empty : "\"apiToken\": " + JsonValue(tokenInFile) + ",")}}
            "fingerprint": {{JsonValue(fingerprint)}} } ] }
          """;

    private static string JsonValue(string? value) =>
        value is null ? "null" : "\"" + value + "\"";

    [Fact]
    public void ThisMachineIsAlwaysThereAndComesFirst()
    {
        // Non si elenca e non si puo' togliere: non ha bisogno di niente per funzionare,
        // quindi non c'e' modo di sbagliarne la configurazione.
        MachineListResult result = MachineDirectory.Resolve(null, NoOtherConfiguration(), FakeSecretStore.Empty());

        ObserverEndpoint first = Assert.Single(result.Machines);

        Assert.Equal(EndpointKind.Local, first.Kind);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ACompleteEntryJoinsTheList()
    {
        MachineListResult result = Read(Entry("https://laptop:5058", Fingerprint));

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Machines.Count);

        ObserverEndpoint remoteMachine = result.Machines[1];

        Assert.Equal(EndpointKind.Remote, remoteMachine.Kind);
        Assert.Equal("laptop", remoteMachine.DisplayName);
        Assert.True(remoteMachine.IsFingerprintPinned);
        Assert.EndsWith("/", remoteMachine.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATokenWrittenInTheFileIsRefused()
    {
        // Il cuore della modifica. Il token qui sotto e' quello giusto, e non basta: se una
        // voce col token nel file continuasse a funzionare, nessuno lo toglierebbe mai da li'.
        MachineListResult result = Read(
            Entry("https://laptop:5058", Fingerprint, tokenInFile: "il-token"),
            FakeSecretStore.Empty());

        Assert.Single(result.Machines);

        string problem = Assert.Single(result.Problems);

        Assert.Contains("observer token set laptop", problem, StringComparison.Ordinal);
        Assert.Contains("ending processes", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutATokenInTheStoreItStaysOutAndSaysHowToAddOne()
    {
        MachineListResult result = Read(Entry("https://laptop:5058", Fingerprint), FakeSecretStore.Empty());

        Assert.Single(result.Machines);
        Assert.Contains(
            "observer token set laptop", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutANameThereIsNowhereToLookForTheToken()
    {
        // Prima il nome era facoltativo e la macchina si chiamava col proprio indirizzo. Ora e'
        // la chiave con cui il token si cerca nel deposito, quindi senza non si va da nessuna
        // parte — e va detto, invece di far sparire la voce.
        MachineListResult result = Read(
            $$"""
              { "machines": [ { "baseAddress": "https://laptop:5058",
                "fingerprint": "{{Fingerprint}}" } ] }
              """);

        Assert.Single(result.Machines);
        Assert.Contains("\"name\"", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntrustedStoreDropsOnlyThatEntry()
    {
        // Un file di segreti leggibile da altri non deve far cadere l'intero elenco: le altre
        // macchine non c'entrano, e la finestra deve restare utilizzabile.
        MachineListResult result = Read(
            Entry("https://laptop:5058", Fingerprint),
            FakeSecretStore.ThatFails("chmod 600 e riprova"));

        Assert.Single(result.Machines);
        Assert.Contains("chmod 600", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ACleartextAddressStaysOutAndSaysWhy()
    {
        // Il caso di gran lunga piu' probabile: una configurazione che era giusta ieri. Il
        // servizio non risponde piu' in chiaro sulla rete, e va detto perche'.
        MachineListResult result = Read(Entry("http://laptop:5057", Fingerprint));

        Assert.Single(result.Machines);

        string problem = Assert.Single(result.Problems);

        Assert.Contains("https://", problem, StringComparison.Ordinal);
        Assert.Contains("packet capture", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAFingerprintItStaysOut()
    {
        // Cifrato non basta. Senza impronta, chi si mette in mezzo presenta il proprio
        // certificato e il collegamento riesce lo stesso.
        MachineListResult result = Read(Entry("https://laptop:5058", null));

        Assert.Single(result.Machines);
        Assert.Contains("fingerprint", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedFingerprintIsRejected()
    {
        // Un'impronta con dentro un errore di battitura non va aggiustata: verrebbe confrontata
        // con successo contro nessun certificato al mondo, e il messaggio parlerebbe di un
        // attacco.
        MachineListResult result = Read(Entry("https://laptop:5058", "sha256:non-sono-esadecimale"));

        Assert.Single(result.Machines);
        Assert.Contains("hex digits", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenFileDoesNotHideThisMachine()
    {
        // La finestra deve restare utilizzabile: un elenco malscritto non puo' impedire di
        // guardare la macchina su cui si e' seduti.
        MachineListResult result = Read("{ non sono json");

        Assert.Single(result.Machines);
        Assert.Equal(EndpointKind.Local, result.Machines[0].Kind);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void WithNoListTheOldSingleMachineConfigurationStillApplies()
    {
        // Chi aveva gia' configurato una macchina non deve rifare niente solo perche' adesso
        // se ne possono elencare tante.
        ObserverEndpoint previous = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "dal vecchio client.json", Fingerprint);

        MachineListResult result = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(previous, null), FakeSecretStore.Empty());

        Assert.Equal(2, result.Machines.Count);
        Assert.Equal(previous, result.Machines[1]);
    }

    [Fact]
    public void TheFingerprintMismatchExplanationNamesExpectedAndReceived()
    {
        // Un messaggio che si limita a "non corrisponde" lascia l'utente senza il valore nuovo,
        // cioe' senza il modo di distinguere una reinstallazione da un attacco e senza il dato
        // da incollare per rimettere le cose a posto.
        CertificatePinning pinning = new(Fingerprint);

        string explanation = pinning.DescribeMismatch("laptop");

        Assert.Contains("Expected:", explanation, StringComparison.Ordinal);
        Assert.Contains("Received:", explanation, StringComparison.Ordinal);
        Assert.Contains("reinstalled", explanation, StringComparison.Ordinal);

        // Nessun certificato e' ancora arrivato: dirlo e' meglio che lasciare la riga vuota.
        Assert.Contains("none", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExplanationSaysTheTokenNeverLeftThisMachine()
    {
        // E' la prima domanda che si fa chi vede quel messaggio, e la risposta e' buona: il
        // collegamento viene rifiutato durante l'handshake, prima di spedire qualsiasi cosa.
        CertificatePinning pinning = new(Fingerprint);

        Assert.Contains("never left this machine", pinning.DescribeMismatch("laptop"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldClientJsonCannotReopenTheCleartextRoute()
    {
        // La porta di servizio piu' facile da lasciare aperta: l'elenco rifiuta http://, ma il
        // ripiego a macchina singola entrava senza passare da alcun controllo. Il risultato
        // sarebbe stato il token spedito in chiaro una volta al secondo, cioe' esattamente cio'
        // che la chiusura della porta doveva impedire.
        ObserverEndpoint cleartext = ObserverEndpoint.Remote(
            new Uri("http://vecchia:5057/"), "token", "dal vecchio client.json", Fingerprint);

        MachineListResult result = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(cleartext, null), FakeSecretStore.Empty());

        Assert.Single(result.Machines);
        Assert.Contains("https", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldClientJsonWithoutAFingerprintStaysOut()
    {
        // Stesso buco, altra meta': cifrato ma verso nessuno in particolare.
        ObserverEndpoint withoutFingerprint = ObserverEndpoint.Remote(
            new Uri("https://vecchia:5058/"), "token", "dal vecchio client.json");

        MachineListResult result = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(withoutFingerprint, null), FakeSecretStore.Empty());

        Assert.Single(result.Machines);
        Assert.Contains("fingerprint", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutTheListDoesNotDropTheOldConfiguration()
    {
        // JSON valido ma senza "machines": non e' un file vuoto che va bene, e' un file che
        // qualcuno credeva di aver scritto. Azzerare tutto in silenzio farebbe sparire anche la
        // configurazione precedente, e chi guarda vedrebbe una macchina sparire senza motivo.
        ObserverEndpoint previous = ObserverEndpoint.Remote(
            new Uri("https://altra:5058/"), "token", "dal vecchio client.json", Fingerprint);

        MachineListResult result = MachineDirectory.Resolve(
            """{ "altro": 1 }""", new ClientConfigurationResult(previous, null), FakeSecretStore.Empty());

        Assert.Equal(2, result.Machines.Count);
        Assert.Contains("machines", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
        private readonly string? failure;

        private FakeSecretStore(string? failure) => this.failure = failure;

        public string Description => "the pretend store";

        public static FakeSecretStore Empty() => new(failure: null);

        public static FakeSecretStore With(string name, string secret)
        {
            FakeSecretStore store = new(failure: null);
            store.secrets[name] = secret;

            return store;
        }

        public static FakeSecretStore ThatFails(string reason) => new(reason);

        public bool TryRead(string name, out string secret)
        {
            if (failure is not null)
            {
                throw new SecretStoreException(failure);
            }

            if (secrets.TryGetValue(name, out string? found))
            {
                secret = found;

                return true;
            }

            secret = string.Empty;

            return false;
        }

        public void Write(string name, string secret) => secrets[name] = secret;

        public bool Delete(string name) => secrets.Remove(name);
    }
}
