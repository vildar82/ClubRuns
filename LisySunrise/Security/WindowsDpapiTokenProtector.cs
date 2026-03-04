using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace LisySunrise.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiTokenProtector : ITokenProtector
{
    private const string Prefix = "dpapi:";

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return plainText;
        }

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return protectedText;
        }

        // Backward-compatible mode: if value has no prefix, treat it as legacy plain text.
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedText;
        }

        var base64 = protectedText[Prefix.Length..];
        var protectedBytes = Convert.FromBase64String(base64);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
