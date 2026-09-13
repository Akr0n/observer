using System.Buffers.Text;
using System.Security.Cryptography;

namespace Observer.Service.Credentials;

/// <summary>Generate il token di macchina.</summary>
public static class TokenGenerator
{
    /// <summary>EntropyBytes di entropia. 256 bit: non e' indovinabile e non e' scomodo da copiare.</summary>
    private const int EntropyBytes = 32;

    /// <summary>Un token nuovo.</summary>
    /// <returns>Il token, in Base64Url.</returns>
    /// <remarks>
    /// Base64Url e non Base64 normale: finisce in un header "Authorization: Bearer ...", e i
    /// caratteri + / = andrebbero codificati. Chi copia e incolla il token da un terminale a
    /// un file di configurazione non deve doverci pensare.
    /// </remarks>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyBytes));
}