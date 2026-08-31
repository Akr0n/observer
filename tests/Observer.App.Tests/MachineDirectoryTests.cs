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
    private static readonly string Impronta =
        CertificateFingerprint.Da(SHA256.HashData("una macchina"u8.ToArray()));

    private static ClientConfigurationResult NienteAltro() =>
        new(ObserverEndpoint.CanaleLocale(), null);

    private static MachineListResult Leggi(string json, ISecretStore? deposito = null) =>
        MachineDirectory.Resolve(json, NienteAltro(), deposito ?? DepositoFinto.Con("laptop", "il-token"));

    /// <summary>Una voce del file. Il token si passa solo per provare che viene rifiutato.</summary>
    private static string Voce(
        string? indirizzo, string? impronta, string nome = "laptop", string? tokenNelFile = null) =>
        $$"""
          { "machines": [ { "name": {{Testo(nome)}}, "baseAddress": {{Testo(indirizzo)}},
            {{(tokenNelFile is null ? string.Empty : "\"apiToken\": " + Testo(tokenNelFile) + ",")}}
            "fingerprint": {{Testo(impronta)}} } ] }
          """;

    private static string Testo(string? valore) =>
        valore is null ? "null" : "\"" + valore + "\"";

    [Fact]
    public void QuestaMacchinaCEsempreEStaPerPrima()
    {
        // Non si elenca e non si puo' togliere: non ha bisogno di niente per funzionare,
        // quindi non c'e' modo di sbagliarne la configurazione.
        MachineListResult elenco = MachineDirectory.Resolve(null, NienteAltro(), DepositoFinto.Vuoto());

        ObserverEndpoint prima = Assert.Single(elenco.Machines);

        Assert.Equal(EndpointKind.Locale, prima.Kind);
        Assert.Empty(elenco.Problems);
    }

    [Fact]
    public void UnaVoceCompletaEntraNellElenco()
    {
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", Impronta));

        Assert.Empty(elenco.Problems);
        Assert.Equal(2, elenco.Machines.Count);

        ObserverEndpoint remota = elenco.Machines[1];

        Assert.Equal(EndpointKind.Remoto, remota.Kind);
        Assert.Equal("laptop", remota.NomeVisibile);
        Assert.True(remota.ImprontaFissata);
        Assert.EndsWith("/", remota.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnTokenScrittoNelFileNonVieneUsato()
    {
        // Il cuore della modifica. Il token qui sotto e' quello giusto, e non basta: se una
        // voce col token nel file continuasse a funzionare, nessuno lo toglierebbe mai da li'.
        MachineListResult elenco = Leggi(
            Voce("https://laptop:5058", Impronta, tokenNelFile: "il-token"),
            DepositoFinto.Vuoto());

        Assert.Single(elenco.Machines);

        string problema = Assert.Single(elenco.Problems);

        Assert.Contains("observer token set laptop", problema, StringComparison.Ordinal);
        Assert.Contains("ending processes", problema, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaTokenNelDepositoNonEntraEDiceComeMetterceLo()
    {
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", Impronta), DepositoFinto.Vuoto());

        Assert.Single(elenco.Machines);
        Assert.Contains(
            "observer token set laptop", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaNomeNonSiSaDoveCercareIlToken()
    {
        // Prima il nome era facoltativo e la macchina si chiamava col proprio indirizzo. Ora e'
        // la chiave con cui il token si cerca nel deposito, quindi senza non si va da nessuna
        // parte — e va detto, invece di far sparire la voce.
        MachineListResult elenco = Leggi(
            $$"""
              { "machines": [ { "baseAddress": "https://laptop:5058",
                "fingerprint": "{{Impronta}}" } ] }
              """);

        Assert.Single(elenco.Machines);
        Assert.Contains("\"name\"", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void UnDepositoDiCuiNonFidarsiFaSaltareSoloQuellaVoce()
    {
        // Un file di segreti leggibile da altri non deve far cadere l'intero elenco: le altre
        // macchine non c'entrano, e la finestra deve restare utilizzabile.
        MachineListResult elenco = Leggi(
            Voce("https://laptop:5058", Impronta),
            DepositoFinto.CheProtesta("chmod 600 e riprova"));

        Assert.Single(elenco.Machines);
        Assert.Contains("chmod 600", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void UnIndirizzoInChiaroNonEntraELoSpiega()
    {
        // Il caso di gran lunga piu' probabile: una configurazione che era giusta ieri. Il
        // servizio non risponde piu' in chiaro sulla rete, e va detto perche'.
        MachineListResult elenco = Leggi(Voce("http://laptop:5057", Impronta));

        Assert.Single(elenco.Machines);

        string problema = Assert.Single(elenco.Problems);

        Assert.Contains("https://", problema, StringComparison.Ordinal);
        Assert.Contains("packet capture", problema, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaImprontaNonEntra()
    {
        // Cifrato non basta. Senza impronta, chi si mette in mezzo presenta il proprio
        // certificato e il collegamento riesce lo stesso.
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", null));

        Assert.Single(elenco.Machines);
        Assert.Contains("fingerprint", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void UnImprontaMalScrittaNonPassaPerBuona()
    {
        // Un'impronta con dentro un errore di battitura non va aggiustata: verrebbe confrontata
        // con successo contro nessun certificato al mondo, e il messaggio parlerebbe di un
        // attacco.
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", "sha256:non-sono-esadecimale"));

        Assert.Single(elenco.Machines);
        Assert.Contains("hex digits", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void UnFileRottoNonFaSparireQuestaMacchina()
    {
        // La finestra deve restare utilizzabile: un elenco malscritto non puo' impedire di
        // guardare la macchina su cui si e' seduti.
        MachineListResult elenco = Leggi("{ non sono json");

        Assert.Single(elenco.Machines);
        Assert.Equal(EndpointKind.Locale, elenco.Machines[0].Kind);
        Assert.Single(elenco.Problems);
    }

    [Fact]
    public void SenzaElencoValeAncoraLaVecchiaConfigurazioneAMacchinaSingola()
    {
        // Chi aveva gia' configurato una macchina non deve rifare niente solo perche' adesso
        // se ne possono elencare tante.
        ObserverEndpoint vecchia = ObserverEndpoint.Remoto(
            new Uri("https://altra:5058/"), "token", "dal vecchio client.json", Impronta);

        MachineListResult elenco = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(vecchia, null), DepositoFinto.Vuoto());

        Assert.Equal(2, elenco.Machines.Count);
        Assert.Equal(vecchia, elenco.Machines[1]);
    }

    [Fact]
    public void LaSpiegazioneDellImprontaSbagliataDiceEntrambeLeImpronte()
    {
        // Un messaggio che si limita a "non corrisponde" lascia l'utente senza il valore nuovo,
        // cioe' senza il modo di distinguere una reinstallazione da un attacco e senza il dato
        // da incollare per rimettere le cose a posto.
        CertificatePinning fissaggio = new(Impronta);

        string spiegazione = fissaggio.Spiegazione("laptop");

        Assert.Contains("Expected:", spiegazione, StringComparison.Ordinal);
        Assert.Contains("Received:", spiegazione, StringComparison.Ordinal);
        Assert.Contains("reinstalled", spiegazione, StringComparison.Ordinal);

        // Nessun certificato e' ancora arrivato: dirlo e' meglio che lasciare la riga vuota.
        Assert.Contains("none", spiegazione, StringComparison.Ordinal);
    }

    [Fact]
    public void LaSpiegazioneDiceCheIlTokenNonEUscito()
    {
        // E' la prima domanda che si fa chi vede quel messaggio, e la risposta e' buona: il
        // collegamento viene rifiutato durante l'handshake, prima di spedire qualsiasi cosa.
        CertificatePinning fissaggio = new(Impronta);

        Assert.Contains("never left this machine", fissaggio.Spiegazione("laptop"), StringComparison.Ordinal);
    }

    [Fact]
    public void IlVecchioClientJsonNonPuoRiaprireLaStradaInChiaro()
    {
        // La porta di servizio piu' facile da lasciare aperta: l'elenco rifiuta http://, ma il
        // ripiego a macchina singola entrava senza passare da alcun controllo. Il risultato
        // sarebbe stato il token spedito in chiaro una volta al secondo, cioe' esattamente cio'
        // che la chiusura della porta doveva impedire.
        ObserverEndpoint inChiaro = ObserverEndpoint.Remoto(
            new Uri("http://vecchia:5057/"), "token", "dal vecchio client.json", Impronta);

        MachineListResult elenco = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(inChiaro, null), DepositoFinto.Vuoto());

        Assert.Single(elenco.Machines);
        Assert.Contains("https", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void IlVecchioClientJsonSenzaImprontaNonEntra()
    {
        // Stesso buco, altra meta': cifrato ma verso nessuno in particolare.
        ObserverEndpoint senzaImpronta = ObserverEndpoint.Remoto(
            new Uri("https://vecchia:5058/"), "token", "dal vecchio client.json");

        MachineListResult elenco = MachineDirectory.Resolve(
            null, new ClientConfigurationResult(senzaImpronta, null), DepositoFinto.Vuoto());

        Assert.Single(elenco.Machines);
        Assert.Contains("fingerprint", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void UnFileSenzaLElencoNonFaSparireLaVecchiaConfigurazione()
    {
        // JSON valido ma senza "machines": non e' un file vuoto che va bene, e' un file che
        // qualcuno credeva di aver scritto. Azzerare tutto in silenzio farebbe sparire anche la
        // configurazione precedente, e chi guarda vedrebbe una macchina sparire senza motivo.
        ObserverEndpoint vecchia = ObserverEndpoint.Remoto(
            new Uri("https://altra:5058/"), "token", "dal vecchio client.json", Impronta);

        MachineListResult elenco = MachineDirectory.Resolve(
            """{ "altro": 1 }""", new ClientConfigurationResult(vecchia, null), DepositoFinto.Vuoto());

        Assert.Equal(2, elenco.Machines.Count);
        Assert.Contains("machines", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    private sealed class DepositoFinto : ISecretStore
    {
        private readonly Dictionary<string, string> segreti = new(StringComparer.Ordinal);
        private readonly string? protesta;

        private DepositoFinto(string? protesta) => this.protesta = protesta;

        public string Descrizione => "the pretend store";

        public static DepositoFinto Vuoto() => new(protesta: null);

        public static DepositoFinto Con(string nome, string segreto)
        {
            DepositoFinto deposito = new(protesta: null);
            deposito.segreti[nome] = segreto;

            return deposito;
        }

        public static DepositoFinto CheProtesta(string motivo) => new(motivo);

        public bool TryRead(string nome, out string segreto)
        {
            if (protesta is not null)
            {
                throw new SecretStoreException(protesta);
            }

            if (segreti.TryGetValue(nome, out string? trovato))
            {
                segreto = trovato;

                return true;
            }

            segreto = string.Empty;

            return false;
        }

        public void Write(string nome, string segreto) => segreti[nome] = segreto;

        public bool Delete(string nome) => segreti.Remove(nome);
    }
}
