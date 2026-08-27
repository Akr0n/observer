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
/// </remarks>
public class MachineDirectoryTests
{
    private static readonly string Impronta =
        CertificateFingerprint.Da(SHA256.HashData("una macchina"u8.ToArray()));

    private static ClientConfigurationResult NienteAltro() =>
        new(ObserverEndpoint.CanaleLocale(), null);

    private static MachineListResult Leggi(string json) =>
        MachineDirectory.Resolve(json, NienteAltro());

    private static string Voce(string? indirizzo, string? token, string? impronta, string nome = "laptop") =>
        $$"""
          { "machines": [ { "name": "{{nome}}", "baseAddress": {{Testo(indirizzo)}},
            "apiToken": {{Testo(token)}}, "fingerprint": {{Testo(impronta)}} } ] }
          """;

    private static string Testo(string? valore) =>
        valore is null ? "null" : "\"" + valore + "\"";

    [Fact]
    public void QuestaMacchinaCEsempreEStaPerPrima()
    {
        // Non si elenca e non si puo' togliere: non ha bisogno di niente per funzionare,
        // quindi non c'e' modo di sbagliarne la configurazione.
        MachineListResult elenco = MachineDirectory.Resolve(null, NienteAltro());

        ObserverEndpoint prima = Assert.Single(elenco.Machines);

        Assert.Equal(EndpointKind.Locale, prima.Kind);
        Assert.Empty(elenco.Problems);
    }

    [Fact]
    public void UnaVoceCompletaEntraNellElenco()
    {
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", "il-token", Impronta));

        Assert.Empty(elenco.Problems);
        Assert.Equal(2, elenco.Machines.Count);

        ObserverEndpoint remota = elenco.Machines[1];

        Assert.Equal(EndpointKind.Remoto, remota.Kind);
        Assert.Equal("laptop", remota.NomeVisibile);
        Assert.True(remota.ImprontaFissata);
        Assert.EndsWith("/", remota.BaseAddress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnIndirizzoInChiaroNonEntraELoSpiega()
    {
        // Il caso di gran lunga piu' probabile: una configurazione che era giusta ieri. Il
        // servizio non risponde piu' in chiaro sulla rete, e va detto perche'.
        MachineListResult elenco = Leggi(Voce("http://laptop:5057", "il-token", Impronta));

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
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", "il-token", null));

        Assert.Single(elenco.Machines);
        Assert.Contains("fingerprint", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void UnImprontaMalScrittaNonPassaPerBuona()
    {
        // Un'impronta con dentro un errore di battitura non va aggiustata: verrebbe confrontata
        // con successo contro nessun certificato al mondo, e il messaggio parlerebbe di un
        // attacco.
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", "il-token", "sha256:non-sono-esadecimale"));

        Assert.Single(elenco.Machines);
        Assert.Contains("hex digits", Assert.Single(elenco.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaTokenNonEntra()
    {
        MachineListResult elenco = Leggi(Voce("https://laptop:5058", null, Impronta));

        Assert.Single(elenco.Machines);
        Assert.Contains("token", Assert.Single(elenco.Problems), StringComparison.Ordinal);
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

        MachineListResult elenco = MachineDirectory.Resolve(null, new ClientConfigurationResult(vecchia, null));

        Assert.Equal(2, elenco.Machines.Count);
        Assert.Equal(vecchia, elenco.Machines[1]);
    }

    [Fact]
    public void SenzaNomeLaMacchinaSiChiamaColProprioIndirizzo()
    {
        MachineListResult elenco = Leggi(
            $$"""
              { "machines": [ { "baseAddress": "https://laptop:5058",
                "apiToken": "t", "fingerprint": "{{Impronta}}" } ] }
              """);

        Assert.Contains("laptop:5058", elenco.Machines[1].NomeVisibile, StringComparison.Ordinal);
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
}