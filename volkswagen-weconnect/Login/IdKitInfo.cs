namespace volkswagen_weconnect.Login;

/// <summary>
/// Accumulates the fields carried across the IDKit login pages (identifier -&gt; authenticate -&gt;
/// code confirmation). Some fields (client_id, user_code, user_id) are set once and stay valid
/// for the rest of the flow; others (csrf token, relay state, hmac) are refreshed on every page.
/// </summary>
internal class IdKitInfo
{
    public string? ClientId { get; private set; }
    public string? UserCode { get; private set; }
    public string? UserId { get; private set; }
    public string? RelayState { get; set; }
    public string? Hmac { get; set; }
    public string? CsrfToken { get; set; }
    public string? ClientIdentityName { get; set; }

    public void SetClientId(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ClientId = value;
        }
    }

    public void SetUserCode(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            UserCode = value;
        }
    }

    public void SetUserId(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            UserId = value;
        }
    }

    public string RequireClientId() => ClientId ?? throw MissingField("client_id");
    public string RequireUserCode() => UserCode ?? throw MissingField("user_code");
    public string RequireUserId() => UserId ?? throw MissingField("user_id");
    public string RequireRelayState() => RelayState ?? throw MissingField("relayState");
    public string RequireHmac() => Hmac ?? throw MissingField("hmac");
    public string RequireCsrfToken() => CsrfToken ?? throw MissingField("csrf_token");
    public string RequireClientIdentityName() => ClientIdentityName ?? throw MissingField("client_identity_name");

    private static InvalidOperationException MissingField(string name) =>
        new($"Login flow is missing required field '{name}'. VW may have changed its login page.");
}
