using System.Buffers.Text;
using System.Security.Cryptography;

namespace Observer.Service.Credentials;

/// <summary>Genera il token di macchina.</summary>
public static class TokenGenerator
{
    /// <summary>Byte di entropia. 256 bit: non e' indovinabile e non e' scomodo da copiare.</summary>
    private const int Byte = 32;

    /// <summary>Un token nuovo.</summary>
    /// <returns>Il token, in Base64Url.</returns>
    /// <remarks>
    /// Base64Url e non Base64 normale: finisce in un header "Authorization: Bearer ...", e i
    /// caratteri + / = andrebbero codificati. Chi copia e incolla il token da un terminale a
    /// un file di configurazione non deve doverci pensare.
    /// </remarks>
    public static string Genera() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Byte));
}