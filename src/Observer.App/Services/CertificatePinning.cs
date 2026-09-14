using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;

namespace Observer.App.Services;

/// <summary>
/// Decides whether the certificate arriving from the network is the right machine's.
/// </summary>
/// <remarks>
/// Observer's certificate is <b>self-signed</b>: no authority vouches for it, and ordinary TLS
/// validation would always reject it. In its place there is a comparison against the
/// fingerprint taken by hand from the machine itself, with <c>observer share</c>.
/// <para>
/// Chain errors are ignored <b>deliberately</b>, and it is not a shortcut: a chain that leads
/// to no authority is exactly what is expected here. What is NOT ignored is the identity, and
/// that is the only thing that matters: without this comparison, whoever manages to sit in the
/// middle presents their own certificate, the connection succeeds, and the token lands
/// straight in their hands.
/// </para>
/// <para>
/// The last fingerprint seen is kept so that it can be <b>shown</b>. After the service is
/// reinstalled the fingerprint changes for a legitimate reason, and without seeing the new one
/// the user has no way to update their own configuration.
/// </para>
/// </remarks>
public sealed class CertificatePinning
{
    private string? lastSeenFingerprint;
    private int rejected;

    /// <summary>Builds the comparison against an expected fingerprint.</summary>
    /// <param name="fingerprint">The fingerprint that machine must present.</param>
    public CertificatePinning(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        ExpectedFingerprint = fingerprint;
    }

    /// <summary>The fingerprint that is expected.</summary>
    public string ExpectedFingerprint { get; }

    /// <summary>The last fingerprint that arrived from the network, or null if none has.</summary>
    public string? LastSeenFingerprint => Volatile.Read(ref lastSeenFingerprint);

    /// <summary>True if the last time a certificate was examined it was rejected.</summary>
    /// <remarks>
    /// It is there so that faults which are not the pinning's do not get blamed on it. A TLS
    /// connection can fail for many reasons - incompatible protocols, an intermediary that
    /// closes it, a server that does not speak TLS at all - and they all arrive as the same
    /// exception. Without this, any fault whatsoever would be reported to the user as "someone is
    /// standing in the middle", which is a serious accusation to make without evidence.
    /// <para>
    /// The callback is NOT invoked when the connection is reused, so
    /// <see cref="LastSeenFingerprint"/> on its own could be stale: it is this flag, cleared on every
    /// successful examination, that says whether the rejection is the current one.
    /// </para>
    /// </remarks>
    public bool HasRejected => Volatile.Read(ref rejected) != 0;

    /// <summary>A handler that accepts only that machine.</summary>
    /// <returns>The handler, already configured.</returns>
    public SocketsHttpHandler Handler()
    {
        // This is the NETWORK path, so this is where the bytes cost: without this line the
        // service would compress nothing, because compression is negotiated per request and a
        // client that does not send Accept-Encoding gets it in the clear. The two halves go
        // together or not at all. The local channel does NOT set it, deliberately: there the
        // bytes cross nothing, and compressing them would be CPU spent by the very machine
        // this program measures.
        SocketsHttpHandler handler = new() { AutomaticDecompression = DecompressionMethods.All };

        handler.SslOptions.RemoteCertificateValidationCallback = (_, presentedCertificate, _, _) =>
        {
            if (presentedCertificate is not X509Certificate2 certificate)
            {
                Volatile.Write(ref lastSeenFingerprint, null);
                Volatile.Write(ref rejected, 1);

                return false;
            }

            string seenFingerprint = CertificateFingerprint.From(certificate.RawDataMemory.Span);
            bool matches = CertificateFingerprint.Match(ExpectedFingerprint, seenFingerprint);

            Volatile.Write(ref lastSeenFingerprint, seenFingerprint);
            Volatile.Write(ref rejected, matches ? 0 : 1);

            return matches;
        };

        return handler;
    }

    /// <summary>The sentence to show when the certificate is not the expected one.</summary>
    /// <param name="description">What the machine being queried is called.</param>
    /// <returns>The text for the status bar.</returns>
    /// <remarks>
    /// It gives both fingerprints. A message that goes no further than "does not match" leaves
    /// the user without the new value, that is, without any way to tell a reinstall from an
    /// attack and without the value to paste to put things right.
    /// </remarks>
    public string DescribeMismatch(string description)
    {
        string seenFingerprint = LastSeenFingerprint is { } received
            ? CertificateFingerprint.ForHumans(received)
            : "none - the machine presented no certificate at all";

        return
            $"{description} presented a certificate that is not the one pinned for it, so the " +
            "connection was refused before anything was sent. Nothing was disclosed: the token " +
            "never left this machine." + Environment.NewLine +
            "Expected: " + CertificateFingerprint.ForHumans(ExpectedFingerprint) + Environment.NewLine +
            "Received: " + seenFingerprint + Environment.NewLine +
            "If Observer was reinstalled on that machine this is expected, and the fix is to run " +
            "\"observer share\" there and copy the new fingerprint into this machine's " +
            "machines.json. If it was not reinstalled, do NOT copy the new value: this is what a " +
            "machine standing in the middle of the connection looks like.";
    }
}
