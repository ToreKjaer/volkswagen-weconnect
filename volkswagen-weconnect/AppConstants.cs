namespace volkswagen_weconnect;

public class AppConstants
{
    public static string BaseApi = "https://emea.bff.cariad.digital";

    public static string ClientId = "a24fba63-34b3-4d43-b181-942111e6bda8@apps_vw-dilab_com";
    public static string AndroidPackageName = "com.volkswagen.weconnect";

    // VW migrated login to an Auth0-backed OAuth "device authorization" flow served from
    // identity.vwgroup.io. It no longer uses the old openid-configuration + redirect flow.
    // See: https://github.com/robinostlund/homeassistant-volkswagencarnet
    public static string DeviceFlowClientId = "650d46ca-2475-4384-85c2-6af3bf3d52f1@apps_vw-dilab_com";
    public static string DeviceFlowScope = "openid profile badge cars dealers vin offline_access";
    public static string DeviceFlowBaseUrl = "https://identity.vwgroup.io";
    public static string DeviceFlowAuthorizationUrl = $"{DeviceFlowBaseUrl}/oidc/v1/device_authorization";
    public static string DeviceFlowTokenUrl = $"{DeviceFlowBaseUrl}/oidc/v1/token";
    public static string DeviceFlowLoginIdentifierUrl = $"{DeviceFlowBaseUrl}/signin-service/v1/{{0}}/login/identifier";
    public static string DeviceFlowLoginAuthenticateUrl = $"{DeviceFlowBaseUrl}/signin-service/v1/{{0}}/login/authenticate";
    public static string DeviceFlowCodeConfirmationUrl = $"{DeviceFlowBaseUrl}/signin-service/v1/device/{{0}}/{{1}}";
    public static string DeviceFlowUserAgent = "volkswagencarnet-auth/1.0 (+https://github.com/robinostlund/volkswagencarnet)";

    public static void SetSessionHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Connection", "keep-alive");
        request.Headers.Add("Accept-charset", "UTF-8");
        request.Headers.Add("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Volkswagen/3.51.1-android/14");
        request.Headers.Add("tokentype", "IDK_TECHNICAL");
        request.Headers.Add("x-android-package-name", AndroidPackageName);
    }
}
