namespace ClubRuns.App.Security;

public interface ITokenProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}
