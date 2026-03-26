using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using volkswagen_weconnect.Dtos;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace volkswagen_weconnect;

public class VwConnection : IDisposable
{
    private readonly VwAuth _vwAuth;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly JsonSerializerOptions _camelCaseJsonSerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ILogger<VwConnection> _logger;
    private static readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public VwConnection(VwAuth vwAuth, ILoggerFactory loggerFactory)
    {
        _vwAuth = vwAuth;
        HttpClientHandler noRedirectClientHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        _client = new HttpClient(noRedirectClientHandler);
        _logger = loggerFactory.CreateLogger<VwConnection>();
    }

    private OpenIdConfig GetOpenIdConfig()
    {
        string url = $"{AppConstants.BaseApi}/login/v1/idk/openid-configuration";
        HttpResponseMessage response = _client.GetAsync(url).Result;

        if (response.IsSuccessStatusCode)
        {
            string json = response.Content.ReadAsStringAsync().Result;
            return JsonSerializer.Deserialize<OpenIdConfig>(json, _jsonSerializerOptions) ?? throw new InvalidOperationException("Failed to deserialize OpenIdConfig");
        }

        throw new InvalidOperationException("Failed to get OpenIdConfig");
    }

    public T RequestVwBackend<T>(string path)
    {
        string url = $"{AppConstants.BaseApi}{path}";
        _logger.LogInformation("Requesting data from VW backend: {url}", url);

        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken());
        AppConstants.SetSessionHeaders(requestMessage);
        HttpResponseMessage response = _client.Send(requestMessage);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to get data from VW backend, status code: {response.StatusCode}");
        }

        string json = response.Content.ReadAsStringAsync().Result;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Response: {json}", json);
        }

        return JsonSerializer.Deserialize<T>(json, _camelCaseJsonSerializerOptions)!;
    }

    private string GetToken()
    {
        string cacheKey = CreateCacheKey();

        if (_cache.TryGetValue($"{cacheKey}Token", out string? token))
        {
            // return token!;
        }

        Dictionary<string, string> tokenResponse;
        if (_cache.TryGetValue($"{cacheKey}RefreshToken", out string? refreshToken) && TryLoginUsingRefreshToken(refreshToken!, out Dictionary<string, string>? refreshResponse))
        {
            tokenResponse = refreshResponse!;
        }
        else
        {
            tokenResponse = Login();
        }

        _logger.LogInformation("Succesfully logged in to VW");
        token = tokenResponse["access_token"];
        _cache.Set($"{cacheKey}Token", token, TimeSpan.FromSeconds(int.Parse(tokenResponse["expires_in"])));
        _cache.Set($"{cacheKey}RefreshToken", tokenResponse["refresh_token"], TimeSpan.FromHours(24));

        return token;
    }

    private string CreateCacheKey()
    {
        // Create a cache key based on the username and password hash
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(_vwAuth.Username + _vwAuth.Password));
        return Convert.ToBase64String(hash);
    }

    private bool TryLoginUsingRefreshToken(string refreshToken, out Dictionary<string, string>? tokenResponse)
    {
        _logger.LogInformation("Logging in to VW using refresh token");
        string url = $"{AppConstants.BaseApi}/login/v1/idk/token";
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
        requestMessage.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", AppConstants.ClientId)
        });
        AppConstants.SetSessionHeaders(requestMessage);
        HttpResponseMessage response = _client.Send(requestMessage);
        if (response.IsSuccessStatusCode)
        {
            string json = response.Content.ReadAsStringAsync().Result;
            tokenResponse = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)!;
            return true;
        }

        tokenResponse = null;
        return false;
    }

    private Dictionary<string, string> Login()
    {
        _logger.LogInformation("Logging in to VW using username and password");

        // Get OpenID configuration
        _logger.LogInformation("Get OpenID configuration");
        OpenIdConfig openIdConfig = GetOpenIdConfig();

        // Get authorization page (login page)
        _logger.LogInformation("Get authorization page");
        string authorizationPage = GetAuthorizationPage(openIdConfig);

        // Extract state token from login page
        _logger.LogInformation("Extract state token");
        string stateToken = ExtractStateToken(authorizationPage);

        // POST username + password + state in a single request
        _logger.LogInformation("POST login form");
        string loginUrl = $"{openIdConfig.Issuer}/u/login?state={stateToken}";
        HttpRequestMessage loginRequest = new HttpRequestMessage(HttpMethod.Post, loginUrl);
        loginRequest.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("username", _vwAuth.Username),
            new("password", _vwAuth.Password),
            new("state", stateToken)
        });
        AppConstants.SetAuthHeaders(loginRequest);

        // Send login request (expect a 302 redirect)
        HttpResponseMessage loginResponse = _client.Send(loginRequest);

        if (loginResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException("Login failed: wrong username or password");
        }

        if ((int)loginResponse.StatusCode is not (301 or 302 or 303))
        {
            throw new InvalidOperationException($"Login failed with status code: {loginResponse.StatusCode}");
        }

        if (!loginResponse.Headers.Contains("Location"))
        {
            throw new InvalidOperationException("Login response missing Location header");
        }

        // Follow redirects to get the authorization code
        string redirectUrl = loginResponse.Headers.GetValues("Location").First();
        if (!redirectUrl.Contains("http"))
        {
            redirectUrl = $"{openIdConfig.Issuer}{redirectUrl}";
        }

        string codeQueryString = FollowRedirects(new HttpRequestMessage(HttpMethod.Get, redirectUrl));
        if (!codeQueryString.Contains("code="))
        {
            throw new InvalidOperationException("Failed to get authorization code");
        }

        // Extract code and get JWT token
        _logger.LogInformation("Extract code and get JWT token from query");
        return ExtractCodeAndGetToken(openIdConfig, codeQueryString, AppConstants.ClientId);
    }

    private string GetAuthorizationPage(OpenIdConfig openIdConfig)
    {
        string url = $"{openIdConfig.AuthorizationEndpoint}?client_id={AppConstants.ClientId}&redirect_uri={AppConstants.AppUri}&response_type={AppConstants.TokenTypes}&scope={AppConstants.Scope}";
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        AppConstants.SetAuthHeaders(requestMessage);

        // The authorize endpoint returns a 302 redirect to the login page
        HttpResponseMessage response = _client.Send(requestMessage);

        if ((int)response.StatusCode is not (301 or 302 or 303))
        {
            throw new InvalidOperationException($"Expected redirect from authorization endpoint, got: {response.StatusCode}");
        }

        if (!response.Headers.Contains("Location"))
        {
            throw new InvalidOperationException("Authorization response missing Location header");
        }

        string redirectUrl = response.Headers.GetValues("Location").First();
        if (!redirectUrl.Contains("http"))
        {
            redirectUrl = $"{requestMessage.RequestUri!.Scheme}://{requestMessage.RequestUri!.Host}{redirectUrl}";
        }

        // Fetch the actual login page
        HttpRequestMessage loginPageRequest = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        AppConstants.SetAuthHeaders(loginPageRequest);
        HttpResponseMessage loginPageResponse = _client.Send(loginPageRequest);

        if (!loginPageResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to fetch login page, status: {loginPageResponse.StatusCode}");
        }

        return loginPageResponse.Content.ReadAsStringAsync().Result;
    }

    private string ExtractStateToken(string loginPageHtml)
    {
        HtmlDocument htmlDocument = new HtmlDocument();
        htmlDocument.LoadHtml(loginPageHtml);
        HtmlNode stateInput = htmlDocument.DocumentNode.SelectSingleNode("//input[@name='state']");

        if (stateInput == null)
        {
            throw new InvalidOperationException("Failed to find state token in login page");
        }

        string stateToken = stateInput.GetAttributeValue("value", string.Empty);

        if (string.IsNullOrEmpty(stateToken))
        {
            throw new InvalidOperationException("State token value is empty");
        }

        _logger.LogDebug("Extracted state token: {stateToken}", stateToken);
        return stateToken;
    }

    private string FollowRedirects(HttpRequestMessage request)
    {
        HttpResponseMessage response = _client.Send(request);
        if (response.IsSuccessStatusCode)
        {
            return response.Content.ReadAsStringAsync().Result;
        }

        if ((int)response.StatusCode is not (301 or 302 or 303 or 304))
        {
            throw new InvalidOperationException($"Failed to follow redirect, status code: {response.StatusCode}");
        }

        if (!response.Headers.Contains("Location"))
        {
            throw new InvalidOperationException($"Missing 'Location' header, payload returned: {response.Content.ReadAsStringAsync().Result}");
        }

        string redirectUrl = response.Headers.FirstOrDefault(header => header.Key == "Location").Value.First().ToString();
        if (redirectUrl.StartsWith(AppConstants.AppUri))
        {
            return redirectUrl;
        }

        if (!redirectUrl.Contains("http"))
        {
            redirectUrl = request.RequestUri!.Scheme + "://" + request.RequestUri!.Host + redirectUrl;
        }

        HttpRequestMessage newRequest = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            newRequest.Headers.Add(header.Key, header.Value);
        }

        return FollowRedirects(newRequest);
    }

    private Dictionary<string, string> ExtractCodeAndGetToken(OpenIdConfig openIdConfig, string codeQueryString, string clientId)
    {
        string code = Regex.Match(codeQueryString, "[?&]code=([^&]*)").Groups[1].Value;
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, openIdConfig.TokenEndpoint);
        requestMessage.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", AppConstants.AppUri),
        });
        AppConstants.SetSessionHeaders(requestMessage);
        string json = FollowRedirects(requestMessage);
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)!;
    }

    private class OpenIdConfig
    {
        public string? AuthorizationEndpoint { get; set; }
        public string? TokenEndpoint { get; set; }
        public string? Issuer { get; set; }
    }

    #region IDisposable Support

    private bool _disposed = false;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion
}
