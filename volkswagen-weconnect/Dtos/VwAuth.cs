namespace volkswagen_weconnect.Dtos;

public class VwAuth
{
    public string Username { get; private set; }
    public string Password { get; private set; }

    public VwAuth(string username, string password)
    {
        Username = username;
        Password = password;
    }
}