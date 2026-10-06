using System.Text;
using System.Text.Json;
using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Cloud;

public class CloudClient
{
    private readonly HttpClient _http;

    // Working credentials from AlexxIT/SonoffLAN (V2 API)
    private const string AppId = "R8Oq3y0eSZSYdKccHlrQzT1ACCOUT9Gv";
    private const string SignKey = "1ve5Qk9GXfUhKAn1svnKwpAlxXkMarru";

    public string Region { get; set; } = "cn";

    private string BaseUrl => Region == "cn"
        ? "https://cn-apia.coolkit.cn"
        : $"https://{Region}-apia.coolkit.cc";

    public CloudClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<(AuthTokens Tokens, string Region)> LoginAsync(string account, string password, string countryCode = "+86")
    {
        // Build request body using System.Text.Json to prevent JSON injection
        // while maintaining exact field order required for HMAC signing
        using var bodyDoc = System.Text.Json.JsonDocument.Parse(
            SerializeLoginBody(account, password, countryCode));
        var bodyJson = bodyDoc.RootElement.GetRawText();
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

        // Sign with HMAC-SHA256
        var sign = AuthSigner.Sign(bodyJson, SignKey);

        var url = $"{BaseUrl}/v2/user/login";

        // Send as raw bytes (like Python's data=data)
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("Authorization", $"Sign {sign}");
        request.Headers.Add("X-CK-Appid", AppId);

        // Use HttpCompletionOption.ResponseHeadersRead to avoid exception on non-200 status
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var json = await response.Content.ReadAsStringAsync();

        // Check for errors before throwing on status code (to allow region auto-detection)
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("error", out var error) && error.GetInt32() != 0)
        {
            var errorMsg = doc.RootElement.TryGetProperty("msg", out var msg) ? msg.GetString() : "Unknown error";

            // Error 10004 means wrong region, retry with correct one
            if (error.GetInt32() == 10004 && doc.RootElement.TryGetProperty("data", out var data))
            {
                var correctRegion = data.TryGetProperty("region", out var r) ? r.GetString() ?? "cn" : "cn";
                throw new WrongRegionException(correctRegion);
            }

            // Throw with status code info if response was also non-success
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Login failed (HTTP {(int)response.StatusCode}): {errorMsg}");
            }

            throw new Exception($"Login failed: {errorMsg} (error={error.GetInt32()})");
        }

        // Only throw for non-200 if no structured error was returned
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Login failed with HTTP status {(int)response.StatusCode}");
        }

        if (!doc.RootElement.TryGetProperty("data", out var dataElement))
            throw new Exception("Response missing 'data' property");
        var tokens = new AuthTokens
        {
            AccessToken = dataElement.TryGetProperty("at", out var at) ? at.GetString() ?? "" : "",
            RefreshToken = dataElement.TryGetProperty("rt", out var rt) ? rt.GetString() ?? "" : "",
            UserApiKey = dataElement.TryGetProperty("user", out var user) && user.TryGetProperty("apikey", out var apikey)
                ? apikey.GetString() ?? "" : ""
        };

        return (tokens, Region);
    }

    public async Task<AuthTokens> RefreshTokenAsync(string refreshToken)
    {
        // Use System.Text.Json serialization to prevent JSON injection in refreshToken
        var bodyJson = JsonSerializer.Serialize(new { rt = refreshToken });
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        var sign = AuthSigner.Sign(bodyJson, SignKey);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/user/refresh")
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("Authorization", $"Sign {sign}");
        request.Headers.Add("X-CK-Appid", AppId);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("error", out var error) && error.GetInt32() != 0)
        {
            var errorMsg = doc.RootElement.TryGetProperty("msg", out var msg) ? msg.GetString() : "Unknown";
            throw new Exception($"Token refresh failed: {errorMsg} (error={error.GetInt32()})");
        }

        if (!doc.RootElement.TryGetProperty("data", out var dataElement))
        {
            throw new Exception("Response missing 'data' property");
        }

        return new AuthTokens
        {
            AccessToken = dataElement.TryGetProperty("at", out var at) ? at.GetString() ?? "" : "",
            RefreshToken = dataElement.TryGetProperty("rt", out var rt) ? rt.GetString() ?? "" : "",
            UserApiKey = dataElement.TryGetProperty("user", out var user) && user.TryGetProperty("apikey", out var apikey)
                ? apikey.GetString() ?? "" : ""
        };
    }

    /// <summary>
    /// Serialize login body with exact field order required for HMAC signing.
    /// countryCode must come first, then identifier (email/phoneNumber), then password.
    /// </summary>
    private static string SerializeLoginBody(string account, string password, string countryCode)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        if (account.Contains('@'))
        {
            writer.WriteString("countryCode", countryCode);
            writer.WriteString("email", account);
        }
        else
        {
            // Phone number: auto-prepend country code
            var cleanNumber = account.TrimStart('+');
            var countryCodeDigits = countryCode.TrimStart('+');
            if (cleanNumber.StartsWith(countryCodeDigits))
            {
                cleanNumber = cleanNumber.Substring(countryCodeDigits.Length);
            }
            var phoneNumber = $"+{countryCodeDigits}{cleanNumber}";
            writer.WriteString("countryCode", countryCode);
            writer.WriteString("phoneNumber", phoneNumber);
        }

        writer.WriteString("password", password);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(string accessToken, int maxDevices = 200)
    {
        // size 参数用于分页，避免设备较多时只返回第一页
        var url = $"{BaseUrl}/v2/device/thing?num=0&size={maxDevices}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");

        using var response = await _http.SendAsync(request);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            // body 只进日志不进异常消息：异常消息会原样弹到界面上
            SimpleLogger.Log($"[Cloud] GetDevices failed: HTTP {status}, {Truncate(errorBody, 200)}");
            throw new CloudApiException($"云端返回 HTTP {status}", status, null, null, status is 401 or 403);
        }

        var json = await response.Content.ReadAsStringAsync();

        using var doc = ParseBodyOrThrow(json, status, response.Content.Headers.ContentType?.ToString());

        // 云端把业务错误装在 HTTP 200 里回。实测凭证失效时是
        // {"error":401,"msg":"cannot found access token info","data":{}} —— data 在但没有 thingList。
        // 不先看 error，"登录过期"就会被报成"返回结构异常"，谁也看不出该重新登录。
        ThrowIfCloudError(doc.RootElement, status);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("thingList", out var thingList) ||
            thingList.ValueKind != JsonValueKind.Array)
        {
            // 结构异常不是"0 个设备"。返回空表会让调用方把设备列表覆盖成空，
            // 连带把规则里的 DeviceId 写成 null，所以要明确失败。
            // 只记顶层键名，不记 body——正常响应里含 deviceKey。
            var rootKeys = string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name));
            SimpleLogger.Log($"[Cloud] GetDevices: unexpected response structure, HTTP {status}, root=[{rootKeys}]");
            throw new CloudApiException("云端设备列表返回结构异常，已中止（设备与规则未做任何改动）", status, null, null, false);
        }

        var devices = new List<DeviceInfo>();
        foreach (var item in thingList.EnumerateArray())
        {
            if (!item.TryGetProperty("itemData", out var deviceData))
                continue;

            var powerState = "off";
            if (deviceData.TryGetProperty("params", out var paramsObj) &&
                paramsObj.TryGetProperty("switches", out var switches) &&
                switches.ValueKind == JsonValueKind.Array &&
                switches.GetArrayLength() > 0)
            {
                var firstSwitch = switches[0];
                if (firstSwitch.TryGetProperty("switch", out var switchState))
                    powerState = switchState.GetString() ?? "off";
            }

            var macAddress = string.Empty;
            if (deviceData.TryGetProperty("extra", out var extra) &&
                extra.TryGetProperty("mac", out var mac))
                macAddress = mac.GetString() ?? "";

            var channelCount = 1;
            var channelStates = new List<string> { powerState };
            if (deviceData.TryGetProperty("params", out var devParams) &&
                devParams.TryGetProperty("switches", out var swArray) &&
                swArray.ValueKind == JsonValueKind.Array)
            {
                channelCount = Math.Max(1, swArray.GetArrayLength());
                channelStates = new List<string>();
                foreach (var sw in swArray.EnumerateArray())
                    channelStates.Add(sw.TryGetProperty("switch", out var swState) ? swState.GetString() ?? "off" : "off");
            }

            devices.Add(new DeviceInfo
            {
                DeviceId = deviceData.TryGetProperty("deviceid", out var id) ? id.GetString() ?? "" : "",
                Name = deviceData.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
                IpAddress = deviceData.TryGetProperty("ip", out var ip) ? ip.GetString() ?? "" : "",
                DeviceKey = deviceData.TryGetProperty("devicekey", out var key) ? key.GetString() ?? "" : "",
                MacAddress = macAddress,
                IsOnline = deviceData.TryGetProperty("online", out var online) && online.GetBoolean(),
                ChannelCount = channelCount,
                ChannelStates = channelStates
            });
        }

        SimpleLogger.Log($"[Cloud] GetDevices: {devices.Count} devices");
        return devices;
    }

    private static JsonDocument ParseBodyOrThrow(string json, int status, string? contentType)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 不记 body：它可能是整页网关 HTML，也可能含设备密钥
            SimpleLogger.Log($"[Cloud] GetDevices: body 不是 JSON, HTTP {status}, type={contentType}, len={json.Length}");
            throw new CloudApiException("云端返回了无法解析的内容，已中止（设备与规则未做任何改动）", status, null, null, false);
        }
    }

    /// <summary>
    /// 顶层 error 非 0 就是云端明确报了错，只是它披着 HTTP 200。
    /// 401/403 或 msg 提到 access token 的，归成"凭证失效"——界面据此才能说"该重新登录"而不是"结构异常"。
    /// </summary>
    private static void ThrowIfCloudError(JsonElement root, int status)
    {
        // TryGetInt32 只对 Number 生效，遇到 "error":"401" 这种字符串会直接抛 InvalidOperationException
        if (!root.TryGetProperty("error", out var errorEl) ||
            errorEl.ValueKind != JsonValueKind.Number ||
            !errorEl.TryGetInt32(out var code) || code == 0)
            return;

        var cloudMsg = root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString()
            : null;
        var isAuthFailure = code is 401 or 403 ||
            cloudMsg?.Contains("access token", StringComparison.OrdinalIgnoreCase) == true ||
            cloudMsg?.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) == true;

        SimpleLogger.Log($"[Cloud] GetDevices: HTTP {status}, error={code}, msg={cloudMsg}");
        throw new CloudApiException(
            isAuthFailure ? "云端登录已过期或凭证无效" : $"云端返回错误 {code}：{cloudMsg ?? "无说明"}",
            status, code, cloudMsg, isAuthFailure);
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max
            ? value ?? string.Empty
            : value[..max] + "…";
}

/// <summary>
/// 云端调用失败。带上 HTTP 状态、云端自己的 error/msg，以及 IsAuthFailure——
/// 让调用方能区分"凭证要重新登录"和"接口/网络出问题了"，而不是把所有失败糊成一句"返回结构异常"。
/// Message 是给人看的，不含响应体原文。
/// </summary>
public class CloudApiException(string message, int? httpStatus, int? errorCode, string? cloudMessage, bool isAuthFailure)
    : Exception(message)
{
    public int? HttpStatus { get; } = httpStatus;
    public int? ErrorCode { get; } = errorCode;
    public string? CloudMessage { get; } = cloudMessage;
    public bool IsAuthFailure { get; } = isAuthFailure;
}

public class WrongRegionException : Exception
{
    public string CorrectRegion { get; }

    public WrongRegionException(string correctRegion)
        : base($"Wrong region. Correct region: {correctRegion}")
    {
        CorrectRegion = correctRegion;
    }
}
