using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Observer.Service.Tests;

/// <summary>
/// Speaks HTTP/2 the way a caller reaches it on a CLEARTEXT endpoint: by prior knowledge.
/// </summary>
/// <remarks>
/// It exists because the local channel - a named pipe on Windows, a unix socket on Linux - has no
/// TLS handshake, so there is no ALPN outcome to read and the test that covers the HTTPS endpoint
/// cannot cover this one. The only way to ask a cleartext endpoint which protocols it really
/// accepts is to start speaking one and look at the answer.
/// <para>
/// The answer is NOT text, and that was measured rather than assumed. A Kestrel restricted to
/// HTTP/1.1 does not read the preface as a request line it dislikes and reply <c>400</c>: it
/// recognises the preface, and refuses inside HTTP/2 itself with a GOAWAY frame carrying the
/// error code HTTP_1_1_REQUIRED. So the discriminator is the FRAME TYPE - SETTINGS means the
/// endpoint took the connection, GOAWAY means it declined it - and this class names it rather
/// than leaving a test to compare bytes.
/// </para>
/// <para>
/// It lives in its own file rather than in either platform's test class because both need it and
/// neither may reference the other's platform-annotated types.
/// </para>
/// </remarks>
internal static class Http2Preface
{
    /// <summary>What an endpoint that takes the connection answers.</summary>
    public const string Accepted = "HTTP/2 ACCEPTED (SETTINGS)";

    /// <summary>What an endpoint restricted to HTTP/1.1 answers.</summary>
    public const string RefusedAsHttp1Required = "HTTP/2 REFUSED (GOAWAY, HTTP_1_1_REQUIRED)";

    /// <summary>The 24-byte connection preface, from RFC 9113 section 3.4.</summary>
    private const string Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";

    private const byte Settings = 0x04;
    private const byte GoAway = 0x07;
    private const uint Http11Required = 0x0d;

    /// <summary>Writes the preface and names what came back.</summary>
    /// <param name="connection">An already-open connection to the endpoint under test.</param>
    /// <returns>
    /// <see cref="Accepted"/>, <see cref="RefusedAsHttp1Required"/>, or a sentence describing
    /// whatever else arrived - never an exception, so a surprise is reported and not thrown.
    /// </returns>
    public static async Task<string> AskWhatItSpeaks(Stream connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

        await connection.WriteAsync(Encoding.ASCII.GetBytes(Preface), deadline.Token).ConfigureAwait(false);
        await connection.FlushAsync(deadline.Token).ConfigureAwait(false);

        byte[] answer = new byte[64];
        int held = 0;

        try
        {
            // A frame header is nine bytes and the GOAWAY payload another eight. Reading until
            // there are enough, rather than once, because a stream may hand them over in pieces.
            while (held < 17)
            {
                int read = await connection.ReadAsync(answer.AsMemory(held), deadline.Token).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                held += read;
            }
        }
        catch (IOException error)
        {
            return "the endpoint tore the connection down: " + error.GetType().Name;
        }
        catch (OperationCanceledException)
        {
            return "the endpoint said nothing within the deadline";
        }

        if (held == 0)
        {
            return "the endpoint closed the connection without answering";
        }

        // An HTTP/1.1 endpoint that did NOT recognise the preface would answer in text, so this
        // stays as a named outcome instead of falling into "unknown".
        string asText = Encoding.ASCII.GetString(answer, 0, held);

        if (asText.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return "an HTTP/1.1 answer: " + asText.Split('\r')[0];
        }

        if (held < 9)
        {
            return FormattableString.Invariant($"{held} bytes, too few to be a frame header");
        }

        byte type = answer[3];

        if (type == Settings)
        {
            return Accepted;
        }

        if (type != GoAway)
        {
            return "an HTTP/2 frame of type 0x" + type.ToString("x2", CultureInfo.InvariantCulture);
        }

        if (held < 17)
        {
            return "a GOAWAY frame with no error code read";
        }

        uint code = BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(13, 4));

        return code == Http11Required
            ? RefusedAsHttp1Required
            : "a GOAWAY frame with error code 0x" + code.ToString("x", CultureInfo.InvariantCulture);
    }
}
