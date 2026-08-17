using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using volkswagen_weconnect.Dtos;
using volkswagen_weconnect.Login;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace volkswagen_weconnect;

public class VwConnection : IDisposable
{
    private readonly VwAuth _vwAuth;
    private readonly HttpClient _client;
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
        _logger.LogInformation("Logging in to VW using username and password (device authorization flow)");

        using VwLoginFlow loginFlow = new VwLoginFlow(_vwAuth, _logger);
        return loginFlow.Login();
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
