using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Observer.Service.Credentials;

/// <summary>Da dove arriva il certificato in uso.</summary>
public enum CertificateOrigin
{
    /// <summary>Generato in memoria e mai depositato: l'impronta cambia a ogni avvio.</summary>
    Effimero = 0,

    /// <summary>Riletto dal deposito.</summary>
    Deposito,

    /// <summary>Generato adesso e depositato.</summary>
    GeneratoEDepositato,
}

/// <summary>Il certificato in uso, con la sua provenienza.</summary>
/// <param name="Certificate">Il certificato, con chiave privata.</param>
/// <param name="Fingerprint">L'impronta da consegnare ai client.</param>
/// <param name="Origin">Da dove arriva.</param>
/// <param name="Percorso">Il file usato, oppure null se non e' stato depositato.</param>
public sealed record ProvisionedCertificate(
    X509Certificate2 Certificate,
    string Fingerprint,
    CertificateOrigin Origin,
    string? Percorso);

/// <summary>
/// Procura al servizio il certificato con cui si presenta alle altre macchine.
/// </summary>
/// <remarks>
/// Stessa forma di <see cref="CredentialProvisioning"/>, e per la stessa ragione: e' l'installer
/// a non dover conoscere niente. Un certificato generato dall'installer sarebbe un certificato
/// che l'installer ha visto, con la chiave privata passata da qualche parte.
/// <para>
/// Anche il perimetro e' lo stesso, e non per comodita': la chiave privata vale quanto il
/// token — chi ce l'ha puo' impersonare questa macchina davanti a ogni dashboard che ne ha
/// fissato l'impronta.
/// </para>
/// </remarks>
public static class CertificateProvisioning
{
    /// <summary>Procura il certificato.</summary>
    /// <param name="percorsoDeposito">Il percorso di <c>credentials.json</c>.</param>
    /// <param name="nomeMacchina">Il nome da mettere nel certificato.</param>
    /// <param name="adesso">L'istante da cui contare la validita'.</param>
    /// <param name="giraComeServizio">Se il processo e' registrato come servizio di sistema.</param>
    /// <returns>Il certificato e la sua provenienza.</returns>
    /// <exception cref="InvalidOperationException">
    /// Quando gira come servizio e il certificato non puo' essere depositato al sicuro.
    /// </exception>
    public static ProvisionedCertificate Provvedi(
        string percorsoDeposito,
        string nomeMacchina,
        DateTimeOffset adesso,
        bool giraComeServizio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoDeposito);

        string percorso = MachineCertificate.PercorsoAccantoA(percorsoDeposito);

        try
        {
            CredentialDirectory.Prepara(percorsoDeposito);

            if (Rileggi(percorso) is { } depositato)
            {
                return new ProvisionedCertificate(
                    depositato,
                    MachineCertificate.Impronta(depositato),
                    CertificateOrigin.Deposito,
                    percorso);
            }

            using X509Certificate2 generato = MachineCertificate.Genera(nomeMacchina, adesso);

            byte[] dati = MachineCertificate.Esporta(generato);

            Deposita(percorso, dati);

            // Cio' che va a Kestrel e' il certificato RILETTO, mai quello appena generato, e la
            // differenza e' misurata: l'oggetto che esce da CreateSelfSigned porta la chiave
            // privata solo in memoria, e su Windows SChannel non la sa servire - l'handshake
            // muore con "Received an unexpected EOF or 0 bytes from the transport stream".
            //
            // Il primo avvio sarebbe stato l'unico rotto, e il sintomo peggiore del guasto:
            // lato client quell'errore arriva come IOException e non come AuthenticationException,
            // quindi la dashboard avrebbe detto "controlla che la macchina sia accesa"; e dal
            // secondo avvio in poi si passa da Rileggi, quindi al primo riavvio del servizio
            // sarebbe sparito tutto. Un guasto che sembra un problema di rete e si ripara da solo.
            return new ProvisionedCertificate(
                Servibile(dati),
                MachineCertificate.Impronta(generato),
                CertificateOrigin.GeneratoEDepositato,
                percorso);
        }
        catch (Exception errore) when (errore is IOException or UnauthorizedAccessException)
        {
            if (giraComeServizio)
            {
                throw new InvalidOperationException(TestoRifiuto(percorso), errore);
            }

            return Effimero(nomeMacchina, adesso);
        }
        catch (InvalidOperationException errore)
            when (!giraComeServizio && errore.InnerException is not CryptographicException)
        {
            // Il ripiego effimero vale per un deposito che non si riesce a METTERE IN SICUREZZA,
            // non per un certificato che c'e' ed e' illeggibile. Quel caso va detto anche a chi
            // lancia il servizio a mano: ripiegare in silenzio gli farebbe vedere un servizio
            // che parte, un'impronta nuova a ogni avvio, e nessun indizio sul file rotto che ha
            // sul disco.
            return Effimero(nomeMacchina, adesso);
        }
    }

    /// <summary>Un certificato che vale per questa esecuzione e basta.</summary>
    /// <remarks>
    /// Come per il token: mai un ripiego su disco fuori dal perimetro. Qui il prezzo e'
    /// visibile — l'impronta cambia a ogni avvio, quindi le dashboard remote non si
    /// collegheranno — ed e' giusto che si veda, invece di lasciar credere che sia tutto a
    /// posto mentre la chiave privata sta dove nessuno la protegge.
    /// </remarks>
    private static ProvisionedCertificate Effimero(string nomeMacchina, DateTimeOffset adesso)
    {
        using X509Certificate2 generato = MachineCertificate.Genera(nomeMacchina, adesso);

        // Stesso giro anche qui, anche se non tocca il disco: senza, il certificato effimero
        // non reggerebbe alcun handshake su Windows, e il messaggio d'avvio prometterebbe
        // un'impronta che cambia a ogni riavvio su una porta che non funziona mai.
        return new ProvisionedCertificate(
            Servibile(MachineCertificate.Esporta(generato)),
            MachineCertificate.Impronta(generato),
            CertificateOrigin.Effimero,
            null);
    }

    /// <summary>Il certificato in una forma che un server TLS sa davvero servire.</summary>
    /// <param name="pkcs12">Il certificato impacchettato con la sua chiave.</param>
    /// <returns>Il certificato ricaricato.</returns>
    private static X509Certificate2 Servibile(byte[] pkcs12) =>
        MachineCertificate.Carica(pkcs12);

    /// <summary>Rilegge il deposito, distinguendo "non c'e'" da "non riesco a leggerlo".</summary>
    /// <remarks>
    /// La distinzione e' la stessa di <see cref="CredentialStore.Leggi"/>, e qui il motivo e'
    /// ancora piu' forte: rigenerare il certificato perche' non lo si e' saputo leggere ne
    /// cambierebbe l'impronta, cioe' taglierebbe fuori ogni dashboard remota in un colpo solo.
    /// Meglio non partire.
    /// </remarks>
    private static X509Certificate2? Rileggi(string percorso)
    {
        byte[] contenuto;

        try
        {
            contenuto = File.ReadAllBytes(percorso);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return MachineCertificate.Carica(contenuto);
        }
        catch (CryptographicException errore)
        {
            throw new InvalidOperationException(
                $"The machine certificate '{percorso}' exists but can't be read ({errore.Message}). " +
                "Observer will not replace it on its own: a new certificate has a new fingerprint, " +
                "and every dashboard that pinned the old one would stop connecting at once. " +
                "Delete the file deliberately if you mean to issue a new one.",
                errore);
        }
    }

    /// <summary>Deposita il certificato con la stessa ricetta del token.</summary>
    /// <remarks>
    /// Temporaneo nella stessa cartella, creato GIA' protetto, sostituzione atomica,
    /// cancellazione in un <c>finally</c>. Le ragioni di ogni passo stanno in
    /// <see cref="CredentialStore"/> e valgono identiche qui, perche' qui invece di un token
    /// c'e' una chiave privata.
    /// </remarks>
    private static void Deposita(string percorso, byte[] dati)
    {
        string temporaneo = percorso + ".nuovo";

        try
        {
            if (File.Exists(temporaneo))
            {
                File.Delete(temporaneo);
            }

            using (Stream flusso = CredentialFile.CreaProtetto(temporaneo))
            {
                flusso.Write(dati, 0, dati.Length);
            }

            File.Move(temporaneo, percorso, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaneo))
            {
                File.Delete(temporaneo);
            }
        }
    }

    private static string TestoRifiuto(string percorso) =>
        $"Observer runs as a system service and can't secure its machine certificate at '{percorso}'. " +
        "It will not start: the private key of that certificate is what proves this machine's " +
        "identity to every dashboard that pinned its fingerprint, so leaving it where other " +
        "accounts can read it would be worse than not starting at all.";
}