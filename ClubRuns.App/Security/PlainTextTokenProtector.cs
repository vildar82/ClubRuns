namespace ClubRuns.App.Security;

public sealed class PlainTextTokenProtector : ITokenProtector
{
    // No encryption: tokens are stored as plain text for portability between users/machines.
    public string Protect(string plainText) => plainText;

    // No decryption required in plain-text mode.
    public string Unprotect(string protectedText) => protectedText;
}
