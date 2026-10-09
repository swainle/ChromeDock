using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChromeDock.Repositories;

namespace ChromeDock;

internal sealed class BrowserLauncher
{
    private static readonly LogRepository Log = LogRepository.For<BrowserLauncher>();
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(1)
    };

    public static int GetPort(int seed)
    {
        if (seed < 1 || seed > 35534)
        {
            throw new InvalidOperationException("指纹种子超出调试端口范围，无法启动环境。端口规则为 30000 + 指纹种子。");
        }

        return 30000 + seed;
    }

    public static string GetProfilePath(string root, int seed)
    {
        return Path.Combine(root, seed.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<bool> IsRunningAsync(string root, int seed)
    {
        if (seed < 1 || seed > 35534)
        {
            return false;
        }

        return await GetBrowserEndpointAsync(root, seed) is not null;
    }

    public async Task OpenAsync(BrowserEnvironment environment, string root, int localProxyPort)
    {
        var port = GetPort(environment.FingerprintSeed);
        var profile = GetProfilePath(root, environment.FingerprintSeed);
        var executable = Path.Combine(AppContext.BaseDirectory, "Chrome-bin", "chrome.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("找不到浏览器，请将 chrome.exe 放在启动器目录下的 Chrome-bin 文件夹中。", executable);
        }

        if (await IsRunningAsync(root, environment.FingerprintSeed))
        {
            throw new InvalidOperationException("环境已经打开。");
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException($"调试端口 {port} 已被占用，无法确认它属于当前环境。", ex);
        }
        finally
        {
            listener.Stop();
        }

        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add("--restore-last-session");
        start.ArgumentList.Add($"--remote-debugging-port={port}");
        start.ArgumentList.Add($"--fingerprint={environment.FingerprintSeed}");

        if (environment.Platform != "Windows")
        {
            start.ArgumentList.Add($"--fingerprint-platform={environment.Platform.ToLowerInvariant()}");
        }
        if (environment.Browser != "默认 Chromium")
        {
            start.ArgumentList.Add($"--fingerprint-brand={environment.Browser}");
        }
        if (environment.Language != "跟随系统")
        {
            var languages = environment.Language.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (languages.Length > 0)
            {
                start.ArgumentList.Add($"--lang={languages[0]}");
                start.ArgumentList.Add($"--accept-lang={string.Join(',', languages)}");
            }
        }
        if (environment.Timezone != "跟随系统")
        {
            start.ArgumentList.Add($"--timezone={environment.Timezone}");
        }
        if (localProxyPort > 0)
        {
            start.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{localProxyPort}");
        }
        else if (environment.ProxyType != "不使用代理")
        {
            var scheme = environment.ProxyType.ToLowerInvariant();
            start.ArgumentList.Add($"--proxy-server={scheme}://{environment.ProxyAddress}:{environment.ProxyPort}");
        }

        Log.Debug("启动参数：{0}", string.Join(' ', start.ArgumentList));
        Log.Info("启动环境 {0}：种子 {1}，端口 {2}，用户数据 {3}，可执行文件 {4}，代理类型 {5}",
            environment.Name, environment.FingerprintSeed, port, profile, executable, environment.ProxyType);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("浏览器进程未能启动。");
        Log.Debug("浏览器启动请求已提交：PID {0}", process.Id);

        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            var endpoint = await GetRawEndpointAsync(port);
            if (endpoint is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(profile, "ChromeDockDebugEndpoint"), endpoint.AbsolutePath);
                Log.Info("环境 {0} 已打开，调试端口 {1}", environment.Name, port);
                return;
            }
            await Task.Delay(250);
        }
        throw new InvalidOperationException($"浏览器未在 10 秒内开放调试端口 {port}，请检查浏览器版本和日志。");
    }

    public async Task CloseAsync(string root, int seed)
    {
        var endpoint = await GetBrowserEndpointAsync(root, seed);
        if (endpoint is null)
        {
            throw new InvalidOperationException("环境未运行，或无法确认调试端口属于该环境。");
        }

        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(endpoint, timeout.Token);
        var command = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}");
        await socket.SendAsync(command, WebSocketMessageType.Text, true, timeout.Token);
        Log.Info("已向环境种子 {0} 的调试端口 {1} 发送关闭请求", seed, GetPort(seed));

        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (!await IsRunningAsync(root, seed))
            {
                Log.Info("环境种子 {0} 已关闭", seed);
                return;
            }
            await Task.Delay(250);
        }

        throw new InvalidOperationException("浏览器收到关闭请求后仍在运行，请手动检查浏览器窗口。");
    }

    private static async Task<Uri?> GetBrowserEndpointAsync(string root, int seed)
    {
        var port = GetPort(seed);
        var markerFile = Path.Combine(GetProfilePath(root, seed), "ChromeDockDebugEndpoint");
        if (!File.Exists(markerFile))
        {
            return null;
        }

        try
        {
            var expectedPath = (await File.ReadAllTextAsync(markerFile)).Trim();
            var endpoint = await GetRawEndpointAsync(port);
            if (endpoint is null || endpoint.AbsolutePath != expectedPath)
            {
                return null;
            }

            return endpoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "无法读取环境种子 {0} 的调试端点标识", seed);
            return null;
        }
    }

    private static async Task<Uri?> GetRawEndpointAsync(int port)
    {
        try
        {
            using var response = await Client.GetAsync($"http://127.0.0.1:{port}/json/version");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var address = document.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "ws" ||
                endpoint.Host != "127.0.0.1" && endpoint.Host != "localhost" && endpoint.Host != "[::1]" ||
                endpoint.Port != port ||
                !endpoint.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
            {
                return null;
            }

            return endpoint;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }
}
