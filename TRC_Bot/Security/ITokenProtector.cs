namespace TRC_Bot;

public interface ITokenProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}
