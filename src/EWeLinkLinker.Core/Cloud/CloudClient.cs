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

    public async Task<List<DeviceInfo>> GetDevicesAsync(string accessToken)
    {
        var url = $"{BaseUrl}/v2/device/thing?num=0";
        SimpleLogger.Log($"GetDevices: URL={url}");

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");

        using var response = await _http.SendAsync(request);
        SimpleLogger.Log($"GetDevices: HTTP Status={(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            SimpleLogger.Log($"GetDevices: Error response={errorBody}");
            response.EnsureSuccessStatusCode(); // Will throw
        }

        var json = await response.Content.ReadAsStringAsync();
        SimpleLogger.Log($"GetDevices: Response length={json.Length}, preview={json[..Math.Min(200, json.Length)]}");

        using var doc = JsonDocument.Parse(json);

        // Check response structure
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            SimpleLogger.Log("GetDevices: No 'data' property in response");
            SimpleLogger.Log($"GetDevices: Root properties: {string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))}");
            return new List<DeviceInfo>();
        }

        if (!data.TryGetProperty("thingList", out var thingList))
        {
            SimpleLogger.Log("GetDevices: No 'thingList' in data");
            SimpleLogger.Log($"GetDevices: data properties: {string.Join(", ", data.EnumerateObject().Select(p => p.Name))}");
            return new List<DeviceInfo>();
        }

        if (thingList.ValueKind != JsonValueKind.Array)
        {
            SimpleLogger.Log($"GetDevices: thingList is not an array (kind={thingList.ValueKind})");
            return new List<DeviceInfo>();
        }

        var devices = new List<DeviceInfo>();
        SimpleLogger.Log($"GetDevices: thingList has {thingList.GetArrayLength()} items");

        foreach (var item in thingList.EnumerateArray())
        {
            // V2 API wraps device data in "itemData" property
            if (item.TryGetProperty("itemData", out var deviceData))
            {
                // Parse switch state from params.switches[0].switch
                var powerState = "off";
                if (deviceData.TryGetProperty("params", out var paramsObj) &&
                    paramsObj.TryGetProperty("switches", out var switches) &&
                    switches.ValueKind == JsonValueKind.Array &&
                    switches.GetArrayLength() > 0)
                {
                    var firstSwitch = switches[0];
                    if (firstSwitch.TryGetProperty("switch", out var switchState))
                    {
                        powerState = switchState.GetString() ?? "off";
                    }
                }

                // Get MAC address from extra.mac
                var macAddress = string.Empty;
                if (deviceData.TryGetProperty("extra", out var extra) &&
                    extra.TryGetProperty("mac", out var mac))
                {
                    macAddress = mac.GetString() ?? "";
                }

                // Determine channel count and per-channel states from switches array
                var channelCount = 1;
                var channelStates = new List<string> { powerState };
                if (deviceData.TryGetProperty("params", out var devParams) &&
                    devParams.TryGetProperty("switches", out var swArray) &&
                    swArray.ValueKind == JsonValueKind.Array)
                {
                    channelCount = Math.Max(1, swArray.GetArrayLength());
                    channelStates = new List<string>();
                    foreach (var sw in swArray.EnumerateArray())
                    {
                        channelStates.Add(sw.TryGetProperty("switch", out var swState) ? swState.GetString() ?? "off" : "off");
                    }
                }

                var device = new DeviceInfo
                {
                    DeviceId = deviceData.TryGetProperty("deviceid", out var id) ? id.GetString() ?? "" : "",
                    Name = deviceData.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
                    IpAddress = deviceData.TryGetProperty("ip", out var ip) ? ip.GetString() ?? "" : "",
                    DeviceKey = deviceData.TryGetProperty("devicekey", out var key) ? key.GetString() ?? "" : "",
                    DeviceApiKey = deviceData.TryGetProperty("apikey", out var apikey) ? apikey.GetString() ?? "" : "",
                    MacAddress = macAddress,
                    Uuid = deviceData.TryGetProperty("uuid", out var uuid) ? uuid.GetInt32() : 0,
                    IsOnline = deviceData.TryGetProperty("online", out var online) && online.GetBoolean(),
                    ChannelCount = channelCount,
                    ChannelStates = channelStates
                };
                devices.Add(device);
                SimpleLogger.Log($"Cloud device: {device.Name} ({device.DeviceId}) Channels={device.ChannelCount} States=[{string.Join(",", device.ChannelStates)}]");
            }
            else
            {
                SimpleLogger.Log($"GetDevices: item has no 'itemData'. Properties: {string.Join(", ", item.EnumerateObject().Select(p => p.Name))}");
            }
        }

        SimpleLogger.Log($"Total devices found: {devices.Count}");
        return devices;
    }
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
