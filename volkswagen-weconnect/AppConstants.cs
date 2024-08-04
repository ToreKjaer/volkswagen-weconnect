namespace volkswagen_weconnect;

public class AppConstants
{
    public static string BaseApi = "https://emea.bff.cariad.digital";
    public static string AppUri = "weconnect://authenticated";

    public static string ClientId = "a24fba63-34b3-4d43-b181-942111e6bda8@apps_vw-dilab_com"; // From https://github.com/robinostlund/volkswagencarnet/blob/master/volkswagencarnet/vw_const.py#L12C23-L12C77
    public static string Scope = "openid profile badge cars dealers vin";
    public static string TokenTypes = "code";

    public static void SetSessionHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Connection", "keep-alive");
        request.Headers.Add("Accept-charset", "UTF-8");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", "Volkswagen/2.20.0 iOS/17.1.1");
        request.Headers.Add("tokentype", "IDK_TECHNICAL");
    }
    
    public static void SetAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Connection", "keep-alive");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
        request.Headers.Add("Accept-Encoding", "gzip, deflate");
    }
}