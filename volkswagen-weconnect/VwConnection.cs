using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
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

    private string? _token;

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
        if (_token != null)
        {
            return _token;
        }

        _token = Login();
        _logger.LogInformation("Succesfully logged in to VW");
        return _token;
    }

    private string Login()
    {
        // Get OpenID configuration
        _logger.LogInformation("Get OpenID configuration");
        OpenIdConfig openIdConfig = GetOpenIdConfig();

        // Get authorization page
        _logger.LogInformation("Get authorization page");
        string authorizationPage = GetAuthorizationPage(openIdConfig);

        // Extract form data
        _logger.LogInformation("Extract form data (email)");
        List<KeyValuePair<string, string>> formValues = GetFormContent(authorizationPage, "emailPasswordForm", out string actionUrl);
        // Get "email" key value pair and set its value
        _logger.LogInformation("Setting email form value to {mail}", _vwAuth.Username);
        KeyValuePair<string, string> mail = formValues.FirstOrDefault(pair => pair.Key == "email");
        formValues[formValues.IndexOf(mail)] = new KeyValuePair<string, string>(mail.Key, _vwAuth.Username);

        // POST email
        // https://identity.vwgroup.io/signin-service/v1/{CLIENT_ID}/login/identifier
        _logger.LogInformation("POST email form");
        string passwordLoginPage = PostEmailForm(openIdConfig, formValues, actionUrl, out string refererUrl);
        formValues = GetPasswordFormContent(passwordLoginPage, out string clientId, out actionUrl);
        formValues.Add(new KeyValuePair<string, string>("password", _vwAuth.Password));

        // POST password
        _logger.LogInformation("POST password form");
        string codeQueryString = PostPasswordForm(openIdConfig, formValues, clientId, actionUrl, refererUrl);
        if (!codeQueryString.Contains("code="))
        {
            throw new InvalidOperationException("Failed to get code");
        }

        // Extract code and get JWT token
        _logger.LogInformation("Extract code and get JWT token from query string: {codeQueryString}", codeQueryString);
        return ExtractCodeAndGetToken(openIdConfig, codeQueryString, clientId);
    }

    private string GetAuthorizationPage(OpenIdConfig openIdConfig)
    {
        // https://identity.vwgroup.io/oidc/v1/authorize?client_id={CLIENT_ID}&scope={SCOPE}&response_type={TOKEN_TYPES}&redirect_uri={APP_URI}
        string url = $"{openIdConfig.AuthorizationEndpoint}?client_id={AppConstants.ClientId}&redirect_uri={AppConstants.AppUri}&response_type={AppConstants.TokenTypes}&scope={AppConstants.Scope}";
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        requestMessage.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>());
        AppConstants.SetAuthHeaders(requestMessage);
        return FollowRedirects(requestMessage);
    }

    private List<KeyValuePair<string, string>> GetFormContent(string html, string formId, out string actionUrl)
    {
        HtmlDocument htmlDocument = new HtmlDocument();
        htmlDocument.LoadHtml(html);
        HtmlNode formNode = htmlDocument.GetElementbyId(formId);

        if (formNode == null)
        {
            throw new InvalidOperationException($"Failed to get form with id '{formId}'");
        }

        actionUrl = formNode.GetAttributeValue("action", string.Empty);

        if (actionUrl == string.Empty)
        {
            throw new InvalidOperationException("Failed to get action URL");
        }

        List<KeyValuePair<string, string>> formValues = new List<KeyValuePair<string, string>>();
        foreach (HtmlNode inputNode in formNode.SelectNodes("//input"))
        {
            string name = inputNode.GetAttributeValue("name", string.Empty);
            string value = inputNode.GetAttributeValue("value", string.Empty);
            formValues.Add(new KeyValuePair<string, string>(name, value));
        }

        return formValues;
    }

    private string PostEmailForm(OpenIdConfig openIdConfig, List<KeyValuePair<string, string>> formValues, string actionUrl, out string refererUrl)
    {
        refererUrl = $"{openIdConfig.Issuer}{actionUrl}";
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, refererUrl);
        requestMessage.Content = new FormUrlEncodedContent(formValues);
        AppConstants.SetAuthHeaders(requestMessage);
        requestMessage.Headers.Add("Referer", openIdConfig.AuthorizationEndpoint);
        requestMessage.Headers.Add("Origin", openIdConfig.Issuer);
        return FollowRedirects(requestMessage);
    }

    private string FollowRedirects(HttpRequestMessage request)
    {
        HttpResponseMessage response = _client.Send(request);
        if (response.IsSuccessStatusCode)
        {
            return response.Content.ReadAsStringAsync().Result;
        }

        var result = response.Content.ReadAsStringAsync().Result;
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

        if (!redirectUrl.Contains("http")) // Relative URL
        {
            redirectUrl = request.RequestUri!.Scheme + "://" + request.RequestUri!.Host + redirectUrl;
        }

        // Build a clone of the original request
        HttpRequestMessage newRequest = new HttpRequestMessage(HttpMethod.Get, redirectUrl)
        {
            Content = request.Content
        };
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            newRequest.Headers.Add(header.Key, header.Value);
        }

        return FollowRedirects(newRequest);
    }

    private List<KeyValuePair<string, string>> GetPasswordFormContent(string passwordLoginPage, out string clientId, out string actionUrl)
    {
        HtmlDocument htmlDocument = new HtmlDocument();
        htmlDocument.LoadHtml(passwordLoginPage);
        HtmlNode scriptNode = htmlDocument.DocumentNode.SelectSingleNode("//script[contains(., 'window._ID')]");
        if (scriptNode == null)
        {
            throw new InvalidOperationException("Failed to find script node containing 'window._ID'");
        }

        string script = scriptNode.InnerText;
        List<KeyValuePair<string, string>> formValues = new List<KeyValuePair<string, string>>();

        formValues.Add(new KeyValuePair<string, string>("relayState", Regex.Match(script, "\"relayState\":\"([^\"]*)\"").Groups[1].Value));
        formValues.Add(new KeyValuePair<string, string>("hmac", Regex.Match(script, "\"hmac\":\"([^\"]*)\"").Groups[1].Value));
        formValues.Add(new KeyValuePair<string, string>("email", Regex.Match(script, "\"email\":\"([^\"]*)\"").Groups[1].Value));
        formValues.Add(new KeyValuePair<string, string>("_csrf", Regex.Match(script, "csrf_token:\\s*'([^\"']*)'").Groups[1].Value));

        clientId = Regex.Match(script, "\"clientId\":\\s*\"([^\"']*)\"").Groups[1].Value;
        actionUrl = Regex.Match(script, "\"postAction\":\\s*\"([^\"']*)\"").Groups[1].Value;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            // Log form values
            foreach (KeyValuePair<string, string> formValue in formValues)
            {
                _logger.LogDebug("{key} = {value}", formValue.Key, formValue.Value);
            }
            _logger.LogDebug("clientId = {clientId}", clientId);
            _logger.LogDebug("actionUrl = {actionUrl}", actionUrl);
        }
        
        return formValues;
    }

    private string PostPasswordForm(OpenIdConfig openIdConfig, List<KeyValuePair<string, string>> formValues, string clientId, string action, string refererUrl)
    {
        // https://identity.vwgroup.io/signin-service/v1/{CLIENT_ID}/login/authenticate
        string url = $"{openIdConfig.Issuer}/signin-service/v1/{clientId}/{action}";
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
        requestMessage.Content = new FormUrlEncodedContent(formValues);
        AppConstants.SetAuthHeaders(requestMessage);
        requestMessage.Headers.Add("Referer", refererUrl);
        return FollowRedirects(requestMessage);
    }

    private string ExtractCodeAndGetToken(OpenIdConfig openIdConfig, string codeQueryString, string clientId)
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
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)!["access_token"];
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

        _token = null;
        _client.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion
}