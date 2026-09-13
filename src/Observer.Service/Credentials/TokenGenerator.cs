using System.Buffers.Text;
using System.Security.Cryptography;

namespace Observer.Service.Credentials;

/// <summary>Generates the machine token.</summary>
public static class TokenGenerator
{
    /// <summary>Bytes of entropy. 256 bits: not guessable and not awkward to copy.</summary>
    private const int EntropyBytes = 32;

    /// <summary>A new token.</summary>
    /// <returns>The token, in Base64Url.</returns>
    /// <remarks>
    /// Base64Url and not plain Base64: it ends up in an "Authorization: Bearer ..." header, and
    /// the characters + / = would have to be encoded. Whoever copies and pastes the token from a
    /// terminal into a configuration file must not have to think about it.
    /// </remarks>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyBytes));
}