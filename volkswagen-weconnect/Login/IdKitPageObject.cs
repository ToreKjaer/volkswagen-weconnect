using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace volkswagen_weconnect.Login;

/// <summary>
/// A parsed snapshot of the `window._IDK = {...}` blob that VW's login pages embed in a
/// &lt;script&gt; tag. The blob is not strict JSON (keys/values may be unquoted or single-quoted),
/// so fields are pulled out with targeted regexes rather than a full JSON parse - this mirrors
/// what the reference Python implementation (volkswagencarnet) needs from the page.
/// </summary>
internal class IdKitPageObject
{
    public IdKitStage Stage { get; }
    public string? CsrfToken { get; }
    public string? ClientId { get; }
    public string? RelayState { get; }
    public string? Hmac { get; }
    public string? UserCode { get; }
    public string? UserId { get; }
    public string? ClientIdentityName { get; }

    public IdKitPageObject(string rawIdkBlob)
    {
        Stage = IdKitStageExtensions.ParseTemplateValue(ExtractField(rawIdkBlob, "template"));
        CsrfToken = ExtractField(rawIdkBlob, "csrf_token");
        ClientIdentityName = ExtractField(rawIdkBlob, "clientIdentityName");

        string? deviceUrl = ExtractDeviceUrl(rawIdkBlob);
        DeviceUrlParts deviceUrlParts = ParseDeviceUrl(deviceUrl);

        ClientId = ExtractField(rawIdkBlob, "clientId") ?? deviceUrlParts.ClientId;
        RelayState = ExtractField(rawIdkBlob, "relayState") ?? deviceUrlParts.RelayState;
        Hmac = ExtractField(rawIdkBlob, "hmac") ?? deviceUrlParts.Hmac;
        UserCode = deviceUrlParts.UserCode;
        UserId = deviceUrlParts.UserId;
    }

    private static string? ExtractField(string text, string key)
    {
        // Matches: "key": "value"   key: 'value'   'key':"value" ...
        // The look-around boundaries stop "template" from matching inside "templateModel" etc.
        string pattern = $@"(?<![A-Za-z0-9_])[""']?{Regex.Escape(key)}(?![A-Za-z0-9_])[""']?\s*:\s*[""']([^""']*)[""']";
        Match match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractDeviceUrl(string text)
    {
        // There can be several "url" fields on a page; the one we need points at
        // /signin-service/v1/device/{client_id}/{user_code}.
        string pattern = @"(?<![A-Za-z0-9_])[""']?url(?![A-Za-z0-9_])[""']?\s*:\s*[""']([^""']*)[""']";
        foreach (Match match in Regex.Matches(text, pattern))
        {
            string candidate = match.Groups[1].Value;
            if (candidate.Contains("/device/"))
            {
                return candidate;
            }
        }

        return null;
    }

    private static DeviceUrlParts ParseDeviceUrl(string? deviceUrl)
    {
        if (string.IsNullOrEmpty(deviceUrl))
        {
            return new DeviceUrlParts(null, null, null, null, null);
        }

        int queryIndex = deviceUrl.IndexOf('?');
        string path = queryIndex >= 0 ? deviceUrl[..queryIndex] : deviceUrl;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? clientId = null;
        string? userCode = null;
        int deviceIndex = Array.IndexOf(segments, "device");
        if (deviceIndex >= 0 && segments.Length >= deviceIndex + 3)
        {
            clientId = segments[deviceIndex + 1];
            userCode = segments[deviceIndex + 2];
        }

        string? relayState = QueryHelper.GetQueryParam(deviceUrl, "relayState");
        string? userId = QueryHelper.GetQueryParam(deviceUrl, "user_id");
        string? hmac = QueryHelper.GetQueryParam(deviceUrl, "hmac");

        return new DeviceUrlParts(clientId, userCode, relayState, userId, hmac);
    }

    private sealed record DeviceUrlParts(string? ClientId, string? UserCode, string? RelayState, string? UserId, string? Hmac);
}

/// <summary>
/// Extracts the `window._IDK` object literal out of a VW login page's HTML.
/// </summary>
internal static class IdKitPageObjectExtractor
{
    private const string Assignment = "window._IDK";

    public static IdKitPageObject FromHtml(string html)
    {
        HtmlDocument document = new HtmlDocument();
        document.LoadHtml(html);

        HtmlNodeCollection? scripts = document.DocumentNode.SelectNodes("//script");
        if (scripts != null)
        {
            foreach (HtmlNode script in scripts)
            {
                string text = script.InnerHtml;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                int assignmentIndex = text.IndexOf(Assignment, StringComparison.Ordinal);
                if (assignmentIndex < 0)
                {
                    continue;
                }

                int equalsIndex = text.IndexOf('=', assignmentIndex + Assignment.Length);
                if (equalsIndex < 0)
                {
                    throw new InvalidOperationException($"Found {Assignment} but no '=' assignment");
                }

                string rawBlob = text[(equalsIndex + 1)..].Trim();
                if (!rawBlob.Contains('{'))
                {
                    throw new InvalidOperationException($"Found {Assignment} but no object literal");
                }

                return new IdKitPageObject(rawBlob);
            }
        }

        throw new InvalidOperationException($"{Assignment} not found in any script tag. VW may have changed its login page.");
    }
}

internal static class QueryHelper
{
    public static string? GetQueryParam(string url, string name)
    {
        int queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        string query = url[(queryIndex + 1)..];
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = pair.IndexOf('=');
            string key = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
            if (Uri.UnescapeDataString(key) != name)
            {
                continue;
            }

            return equalsIndex >= 0 ? Uri.UnescapeDataString(pair[(equalsIndex + 1)..]) : string.Empty;
        }

        return null;
    }
}
