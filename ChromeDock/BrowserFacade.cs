using ChromeDock.Repositories;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace ChromeDock;

public sealed class BrowserFacade
{
    private readonly DataRepository _data;
    private readonly BrowserLauncher _launcher = new();
    private readonly ProxyBridge _proxyBridge = new();
    private string _userDataDirectory;

    public BrowserFacade(DataRepository data, string userDataDirectory)
    {
        this._data = data;
        this._userDataDirectory = userDataDirectory;
        Debug.Assert(ParseIpInfo("{\"ip\":\"38.70.78.35\",\"country\":\"US\",\"region\":\"California\"}") ==
            ("38.70.78.35", "US/California"));
        Debug.Assert(ParseIpify("{\"ip\":\"38.70.78.35\"}") == ("38.70.78.35", "位置未知"));
        Debug.Assert(ParseIpWhoIs("{\"success\":true,\"ip\":\"38.70.78.35\",\"country_code\":\"US\",\"region\":\"California\"}") ==
            ("38.70.78.35", "US/California"));
    }

    public async Task<bool> IsRunningAsync(int seed)
    {
        return await this._launcher.IsRunningAsync(this._userDataDirectory, seed);
    }

    public async Task<int> EnsureProxyAsync(BrowserEnvironment environment)
    {
        if (environment.ProxyType == "不使用代理")
        {
            StopProxy(environment.FingerprintSeed);
            return 0;
        }
        var profile = BrowserLauncher.GetProfilePath(this._userDataDirectory, environment.FingerprintSeed);
        var port = await this._proxyBridge.GetListeningPortAsync(profile);
        if (port.HasValue)
        {
            return port.Value;
        }
        return await this._proxyBridge.StartAsync(environment, profile);
    }

    public async Task ApplyProxyConfigurationAsync(BrowserEnvironment environment)
    {
        StopProxy(environment.FingerprintSeed);
        await EnsureProxyAsync(environment);
    }

    public void StopProxy(int seed)
    {
        this._proxyBridge.Stop(BrowserLauncher.GetProfilePath(this._userDataDirectory, seed));
    }

    public Task<int?> GetLocalProxyPortAsync(int seed)
    {
        return this._proxyBridge.GetListeningPortAsync(BrowserLauncher.GetProfilePath(this._userDataDirectory, seed));
    }

    public static async Task<bool> IsRemoteProxyReachableAsync(BrowserEnvironment environment)
    {
        if (environment.ProxyPort is not int port || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(environment.ProxyAddress))
        {
            return false;
        }
        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(environment.ProxyAddress, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return false;
        }
    }

    public async Task<(string Ip, string Location)> CheckIpAsync(string name, string token)
    {
        var environment = this._data.GetEnvironment(name) ??
            throw new InvalidOperationException("环境记录已不存在。");
        var localPort = 0;
        if (environment.ProxyType == "不使用代理")
        {
            return await FetchIpInfoAsync(localPort, token);
        }

        localPort = await EnsureProxyAsync(environment);
        return await FetchIpInfoAsync(localPort, token);
    }

    public async Task<(string Ip, string Location)> OpenAsync(string name)
    {
        var environment = this._data.GetEnvironment(name) ??
            throw new InvalidOperationException("环境记录已不存在。");
        if (await IsRunningAsync(environment.FingerprintSeed))
        {
            throw new InvalidOperationException("环境已经打开。");
        }

        var localProxyPort = await EnsureProxyAsync(environment);
        if (localProxyPort > 0 && await GetLocalProxyPortAsync(environment.FingerprintSeed) != localProxyPort)
        {
            throw new InvalidOperationException("本地代理端口无法连接，浏览器未启动。请检查 sing-box 进程和代理配置。");
        }
        (string Ip, string Location) info;
        try
        {
            info = await FetchIpInfoAsync(localProxyPort, this._data.LoadIpInfoToken());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException($"IP 检测失败，浏览器未启动：{ex.Message}", ex);
        }
        await this._launcher.OpenAsync(environment, this._userDataDirectory, localProxyPort);
        return info;
    }

    public async Task CloseAsync(int seed)
    {
        await this._launcher.CloseAsync(this._userDataDirectory, seed);
    }

    public void SetUserDataDirectory(string path)
    {
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(this._userDataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        foreach (var environment in this._data.LoadEnvironments())
        {
            StopProxy(environment.FingerprintSeed);
        }
        this._userDataDirectory = path;
    }

    private static async Task<(string Ip, string Location)> FetchIpInfoAsync(int localProxyPort, string token)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        if (localProxyPort > 0)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{localProxyPort}");
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var response = await client.GetAsync("https://ipinfo.io/json");
            if (!response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(token))
            {
                response.Dispose();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://ipinfo.io/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await client.SendAsync(request);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                return ParseIpInfo(await response.Content.ReadAsStringAsync());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // IPinfo 可能拒绝代理出口；备用查询仍通过同一个本地代理。
        }

        try
        {
            using var locationResponse = await client.GetAsync("https://ipapi.co/json/");
            locationResponse.EnsureSuccessStatusCode();
            return ParseIpInfo(await locationResponse.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // 位置服务不可用时，仍可通过同一代理确认出口 IP。
        }

        try
        {
            using var locationResponse = await client.GetAsync("https://ipwho.is/");
            locationResponse.EnsureSuccessStatusCode();
            return ParseIpWhoIs(await locationResponse.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // 第三个位置服务也不可用时，只确认出口 IP。
        }

        using var fallback = await client.GetAsync("https://api64.ipify.org?format=json");
        fallback.EnsureSuccessStatusCode();
        return ParseIpify(await fallback.Content.ReadAsStringAsync());
    }

    private static (string Ip, string Location) ParseIpInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ip = root.GetProperty("ip").GetString();
        var country = root.GetProperty("country").GetString();
        var region = root.GetProperty("region").GetString();
        if (!IPAddress.TryParse(ip, out _) || string.IsNullOrWhiteSpace(country) || string.IsNullOrWhiteSpace(region))
        {
            throw new JsonException("IPinfo 响应缺少 IP 或位置信息");
        }
        return (ip, $"{country}/{region}");
    }

    private static (string Ip, string Location) ParseIpify(string json)
    {
        using var document = JsonDocument.Parse(json);
        var ip = document.RootElement.GetProperty("ip").GetString();
        if (!IPAddress.TryParse(ip, out _))
        {
            throw new JsonException("备用 IP 检测响应缺少有效 IP");
        }
        return (ip, "位置未知");
    }

    private static (string Ip, string Location) ParseIpWhoIs(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("success").GetBoolean())
        {
            throw new JsonException("备用位置服务未返回有效位置");
        }
        var ip = root.GetProperty("ip").GetString();
        var country = root.GetProperty("country_code").GetString();
        var region = root.GetProperty("region").GetString();
        if (!IPAddress.TryParse(ip, out _) || string.IsNullOrWhiteSpace(country) || string.IsNullOrWhiteSpace(region))
        {
            throw new JsonException("备用位置服务响应缺少 IP 或位置信息");
        }
        return (ip, $"{country}/{region}");
    }
}
