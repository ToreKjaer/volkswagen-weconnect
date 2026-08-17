using System.Net;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using volkswagen_weconnect.Dtos;

namespace volkswagen_weconnect.Login;

/// <summary>
/// Logs in to VW using the OAuth "device authorization" flow that identity.vwgroup.io moved to
/// after the Auth0 migration (the old openid-configuration + redirect flow no longer works).
///
/// The flow:
///  1. POST /oidc/v1/device_authorization to get a device_code + a verification URL.
///  2. "Open" the verification URL in a cookie-aware browser session and walk through the
///     IDKit pages (identifier -&gt; authenticate -&gt; code confirmation), submitting username,
///     password and the confirmation "allow" step along the way.
///  3. Poll /oidc/v1/token with the device_code until VW hands back access/refresh tokens.
///
/// Ported from the reference implementation: https://github.com/robinostlund/volkswagencarnet
/// (used by https://github.com/robinostlund/homeassistant-volkswagencarnet).
/// </summary>
internal class VwLoginFlow : IDisposable
{
    private static readonly IdKitStage[] FullRoute =
    [
        IdKitStage.Identifier,
        IdKitStage.Authenticate,
        IdKitStage.CodeConfirmation,
        IdKitStage.VerificationSuccess
    ];

    private static readonly IdKitStage[] QuickRoute =
    [
        IdKitStage.CodeConfirmation,
        IdKitStage.VerificationSuccess
    ];

    private static readonly HashSet<int> TransientPollStatusCodes = [429, 500, 502, 503, 504];
    private static readonly HashSet<string> TransientPollErrors = ["temporarily_unavailable", "server_error"];

    private static readonly Dictionary<string, string> AuthErrorMessages = new()
    {
        ["login.errors.password_invalid"] = "Incorrect password.",
        ["login.error.throttled"] = "Too many failed login attempts - please wait before trying again.",
        ["login.error.locked"] = "Account has been locked due to too many failed attempts.",
        ["login.error.blocked"] = "Login blocked by VW identity service."
    };

    private readonly VwAuth _vwAuth;
    private readonly HttpClient _client;
    private readonly ILogger _logger;

    public VwLoginFlow(VwAuth vwAuth, ILogger logger)
    {
        _vwAuth = vwAuth;
        _logger = logger;

        HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false };
        _client = new HttpClient(handler);
    }

    public Dictionary<string, string> Login()
    {
        Dictionary<string, string> device = StartDeviceFlow();
        string deviceCode = RequireValue(device, "device_code");
        string verificationUri = device.TryGetValue("verification_uri_complete", out string? completeUri) && !string.IsNullOrEmpty(completeUri)
            ? completeUri
            : RequireValue(device, "verification_uri");

        int interval = ParseIntOrDefault(device, "interval", 5);
        int expiresIn = ParseIntOrDefault(device, "expires_in", 330);
        int maxWaitSeconds = Math.Max(330, Math.Max(expiresIn - 5, 30));

        _logger.LogInformation("Walking VW device-flow login pages");
        RunBrowserRoute(verificationUri);

        _logger.LogInformation("Polling for device-flow token");
        return PollToken(deviceCode, interval, maxWaitSeconds);
    }

    private Dictionary<string, string> StartDeviceFlow()
    {
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, AppConstants.DeviceFlowAuthorizationUrl)
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("client_id", AppConstants.DeviceFlowClientId),
                new("scope", AppConstants.DeviceFlowScope)
            })
        };

        HttpResponseMessage response = _client.Send(requestMessage);
        string json = response.Content.ReadAsStringAsync().Result;

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to start device authorization flow, status code: {response.StatusCode}, body: {json}");
        }

        return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
               ?? throw new InvalidOperationException("Failed to deserialize device authorization response");
    }

    private void RunBrowserRoute(string verificationUri)
    {
        CookieContainer cookieContainer = new CookieContainer();
        HttpClientHandler handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true
        };

        using HttpClient browser = new HttpClient(handler);
        browser.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppConstants.DeviceFlowUserAgent);
        browser.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        browser.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");

        _logger.LogDebug("GET {url}", verificationUri);
        HttpResponseMessage landingResponse = browser.Send(new HttpRequestMessage(HttpMethod.Get, verificationUri));
        string currentUrl = landingResponse.RequestMessage?.RequestUri?.ToString() ?? verificationUri;
        string html = landingResponse.Content.ReadAsStringAsync().Result;

        IdKitPageObject page = IdKitPageObjectExtractor.FromHtml(html);
        IdKitStage[] route = page.Stage switch
        {
            IdKitStage.Identifier => FullRoute,
            IdKitStage.CodeConfirmation => QuickRoute,
            _ => throw new InvalidOperationException($"Login flow changed: expected the identifier or code-confirmation stage first, got '{page.Stage}'")
        };

        IdKitInfo info = new IdKitInfo();

        for (int index = 0; index < route.Length - 1; index++)
        {
            IdKitStage expectedStage = route[index];

            if (page.Stage != expectedStage)
            {
                throw new InvalidOperationException($"Login flow changed: expected stage '{expectedStage}', got '{page.Stage}'");
            }

            UpdateInfo(info, page, expectedStage);
            (string url, List<KeyValuePair<string, string>> payload) = BuildRequest(expectedStage, info);

            _logger.LogDebug("POST {url} (stage={stage})", url, expectedStage);
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(payload)
            };
            HttpResponseMessage response = browser.Send(requestMessage);
            currentUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            html = response.Content.ReadAsStringAsync().Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Login failed at stage '{expectedStage}' with status code: {response.StatusCode}");
            }

            CheckUrlForCredentialsError(currentUrl);

            page = IdKitPageObjectExtractor.FromHtml(html);
            IdKitStage expectedNext = route[index + 1];
            if (page.Stage != expectedNext)
            {
                throw new InvalidOperationException($"Login flow changed after stage '{expectedStage}': expected next stage '{expectedNext}', got '{page.Stage}'");
            }
        }
    }

    private void UpdateInfo(IdKitInfo info, IdKitPageObject page, IdKitStage stage)
    {
        info.CsrfToken = page.CsrfToken ?? throw new InvalidOperationException("Missing csrf_token on login page");

        switch (stage)
        {
            case IdKitStage.Identifier:
                info.SetClientId(page.ClientId);
                info.RelayState = page.RelayState ?? throw new InvalidOperationException("Missing relayState on identifier page");
                info.Hmac = page.Hmac ?? throw new InvalidOperationException("Missing hmac on identifier page");
                break;
            case IdKitStage.Authenticate:
                info.RelayState = page.RelayState ?? throw new InvalidOperationException("Missing relayState on password page");
                info.Hmac = page.Hmac ?? throw new InvalidOperationException("Missing hmac on password page");
                break;
            case IdKitStage.CodeConfirmation:
                info.SetClientId(page.ClientId);
                info.RelayState = page.RelayState ?? throw new InvalidOperationException("Missing relayState on confirmation page");
                info.Hmac = page.Hmac ?? throw new InvalidOperationException("Missing hmac on confirmation page");
                info.SetUserCode(page.UserCode);
                info.SetUserId(page.UserId);
                info.ClientIdentityName = page.ClientIdentityName ?? throw new InvalidOperationException("Missing clientIdentityName on confirmation page");
                break;
        }
    }

    private (string Url, List<KeyValuePair<string, string>> Payload) BuildRequest(IdKitStage stage, IdKitInfo info)
    {
        switch (stage)
        {
            case IdKitStage.Identifier:
            {
                string clientId = info.ClientId ?? AppConstants.DeviceFlowClientId;
                string url = string.Format(AppConstants.DeviceFlowLoginIdentifierUrl, clientId);
                List<KeyValuePair<string, string>> payload =
                [
                    new("_csrf", info.RequireCsrfToken()),
                    new("relayState", info.RequireRelayState()),
                    new("hmac", info.RequireHmac()),
                    new("email", _vwAuth.Username)
                ];
                return (url, payload);
            }
            case IdKitStage.Authenticate:
            {
                string url = string.Format(AppConstants.DeviceFlowLoginAuthenticateUrl, info.RequireClientId());
                List<KeyValuePair<string, string>> payload =
                [
                    new("_csrf", info.RequireCsrfToken()),
                    new("relayState", info.RequireRelayState()),
                    new("hmac", info.RequireHmac()),
                    new("email", _vwAuth.Username),
                    new("password", _vwAuth.Password)
                ];
                return (url, payload);
            }
            case IdKitStage.CodeConfirmation:
            {
                string query = $"relayState={Uri.EscapeDataString(info.RequireRelayState())}"
                               + $"&user_id={Uri.EscapeDataString(info.RequireUserId())}"
                               + $"&hmac={Uri.EscapeDataString(info.RequireHmac())}";
                string url = string.Format(AppConstants.DeviceFlowCodeConfirmationUrl, info.RequireClientId(), info.RequireUserCode()) + "?" + query;
                List<KeyValuePair<string, string>> payload =
                [
                    new("_csrf", info.RequireCsrfToken()),
                    new("client_identity_name", info.RequireClientIdentityName()),
                    new("allow", "")
                ];
                return (url, payload);
            }
            default:
                throw new InvalidOperationException($"Login flow changed: no request known for stage '{stage}'");
        }
    }

    private static void CheckUrlForCredentialsError(string url)
    {
        string? error = QueryHelper.GetQueryParam(url, "error");
        if (string.IsNullOrEmpty(error))
        {
            return;
        }

        string message = AuthErrorMessages.TryGetValue(error, out string? knownMessage)
            ? knownMessage
            : $"Authentication rejected by VW: '{error}'";
        throw new InvalidOperationException(message);
    }

    private Dictionary<string, string> PollToken(string deviceCode, int interval, int maxWaitSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        int pollInterval = Math.Max(interval, 1);

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(TimeSpan.FromSeconds(pollInterval));

            _logger.LogDebug("POST {url} (polling, interval={interval}s)", AppConstants.DeviceFlowTokenUrl, pollInterval);
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, AppConstants.DeviceFlowTokenUrl)
            {
                Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new("device_code", deviceCode),
                    new("client_id", AppConstants.DeviceFlowClientId)
                })
            };

            HttpResponseMessage response = _client.Send(requestMessage);
            string json = response.Content.ReadAsStringAsync().Result;

            if (response.IsSuccessStatusCode)
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                       ?? throw new InvalidOperationException("Failed to deserialize token response");
            }

            if (TransientPollStatusCodes.Contains((int)response.StatusCode))
            {
                _logger.LogDebug("Transient token poll HTTP {status}", response.StatusCode);
                continue;
            }

            string errorCode = TryGetErrorCode(json);

            if (errorCode == "authorization_pending")
            {
                continue;
            }

            if (errorCode == "slow_down")
            {
                pollInterval += 5;
                continue;
            }

            if (TransientPollErrors.Contains(errorCode))
            {
                _logger.LogDebug("Transient token poll OAuth error={error}", errorCode);
                continue;
            }

            throw new InvalidOperationException($"Token polling failed with OAuth error: '{errorCode}'");
        }

        throw new InvalidOperationException("Token polling timed out");
    }

    private static string TryGetErrorCode(string json)
    {
        try
        {
            Dictionary<string, object>? body = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            return body != null && body.TryGetValue("error", out object? value) ? value?.ToString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string RequireValue(Dictionary<string, string> dictionary, string key) =>
        dictionary.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value)
            ? value
            : throw new InvalidOperationException($"Device authorization response missing '{key}'");

    private static int ParseIntOrDefault(Dictionary<string, string> dictionary, string key, int defaultValue) =>
        dictionary.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : defaultValue;

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
