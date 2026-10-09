using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ChromeDock.Repositories;

namespace ChromeDock;

internal sealed class ProxyBridge
{
    private const string ConfigName = "ChromeDockSingBox.json";
    private const string MarkerName = "ChromeDockSingBox.pid";

    public async Task<int?> GetListeningPortAsync(string profile)
    {
        var markerPath = Path.Combine(profile, MarkerName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var parts = (await File.ReadAllTextAsync(markerPath)).Split('|');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], out var pid) ||
                !long.TryParse(parts[1], out var startedAt) ||
                !int.TryParse(parts[2], out var port))
            {
                return null;
            }
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedAt)
            {
                return null;
            }
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(500));
            return port;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or SocketException or TimeoutException)
        {
            return null;
        }
    }

    public async Task<int> StartAsync(BrowserEnvironment environment, string profile)
    {
        var system = string.Empty;
        var extension = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            system = "windows";
            extension = ".exe";
        }
        else if (OperatingSystem.IsLinux())
        {
            system = "linux";
        }
        else if (OperatingSystem.IsMacOS())
        {
            system = "darwin";
        }
        else
        {
            throw new PlatformNotSupportedException("当前系统没有对应的 sing-box 可执行文件。");
        }

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException("当前系统架构没有对应的 sing-box 可执行文件。")
        };
        var executable = Path.Combine(AppContext.BaseDirectory, "Proxy", $"sing-box_{system}-{architecture}{extension}");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("找不到当前系统及架构对应的 sing-box，请检查启动器目录下的 Proxy 文件夹。", executable);
        }

        if (environment.ProxyPort is not int remotePort || remotePort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(environment.ProxyAddress))
        {
            throw new InvalidOperationException("代理地址或端口无效。");
        }

        Stop(profile);
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = Random.Shared.Next(10000, 30000);
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                listener.Start();
                port = candidate;
                break;
            }
            catch (SocketException)
            {
                // 换一个未占用的本地端口。
            }
            finally
            {
                listener.Stop();
            }
        }
        if (port == 0)
        {
            throw new InvalidOperationException("找不到可用的本地代理端口。");
        }

        var outbound = new JsonObject
        {
            ["tag"] = "remote",
            ["server"] = environment.ProxyAddress,
            ["server_port"] = remotePort,
            ["username"] = environment.ProxyUsername,
            ["password"] = environment.ProxyPassword
        };
        if (environment.ProxyType == "SOCKS5")
        {
            outbound["type"] = "socks";
            outbound["version"] = "5";
        }
        else if (environment.ProxyType is "HTTP" or "HTTPS")
        {
            outbound["type"] = "http";
            if (environment.ProxyType == "HTTPS")
            {
                outbound["tls"] = new JsonObject { ["enabled"] = true };
            }
        }
        else
        {
            throw new InvalidOperationException("不支持的代理类型。");
        }

        var inbounds = new JsonArray();
        inbounds.Add((JsonNode)new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "local",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = port
        });
        var outbounds = new JsonArray();
        outbounds.Add((JsonNode)outbound);
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject { ["final"] = "remote" }
        };
        Directory.CreateDirectory(profile);
        var configPath = Path.Combine(profile, ConfigName);
        var markerPath = Path.Combine(profile, MarkerName);
        File.WriteAllText(configPath, config.ToJsonString());

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(configPath);
            process = Process.Start(start) ?? throw new InvalidOperationException("sing-box 进程未能启动。");
            var startedAt = process.StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(markerPath, $"{process.Id}|{startedAt}|{port}");

            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"sing-box 已退出，退出码 {process.ExitCode}。请检查代理配置和 sing-box 版本。");
                }
                try
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(250));
                    return port;
                }
                catch (SocketException)
                {
                    await Task.Delay(100);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(100);
                }
            }
            throw new InvalidOperationException("sing-box 未在 5 秒内开放本地代理端口。");
        }
        catch
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
            File.Delete(markerPath);
            File.Delete(configPath);
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public void Stop(string profile)
    {
        var markerPath = Path.Combine(profile, MarkerName);
        if (!File.Exists(markerPath))
        {
            return;
        }

        var parts = File.ReadAllText(markerPath).Split('|');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var startedAt))
        {
            throw new InvalidOperationException("sing-box 进程标记无效，不能安全停止进程。");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks == startedAt && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    throw new TimeoutException("sing-box 进程未能停止。");
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 进程已退出。
        }

        File.Delete(markerPath);
        File.Delete(Path.Combine(profile, ConfigName));
    }
}
