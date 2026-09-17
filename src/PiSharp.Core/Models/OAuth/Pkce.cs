using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// PKCE utilities (pinned pi-ai: auth/oauth/pkce.ts). 32 random bytes base64url-encoded
/// as the verifier; SHA-256 digest as the S256 challenge.
/// </summary>
public static class Pkce
{
    /// <summary>Result of PKCE generation.</summary>
    public sealed record PkcePair(string Verifier, string Challenge);

    /// <summary>Generates a code verifier and S256 challenge pair.</summary>
    public static PkcePair Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return new PkcePair(verifier, Base64UrlEncode(hash));
    }

    /// <summary>Encodes bytes as a base64url string (no padding).</summary>
    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a base64url string to bytes (tolerating missing padding).</summary>
    public static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
        }

        return Convert.FromBase64String(base64);
    }
}

/// <summary>
/// Generates a random state/nonce string (16 bytes base64url).
/// </summary>
public static class OAuthState
{
    /// <summary>Generates a random state string.</summary>
    public static string Generate()
        => Pkce.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
}
