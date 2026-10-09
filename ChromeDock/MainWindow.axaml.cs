using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;


using ChromeDock.Repositories;

namespace ChromeDock;

public sealed class EnvironmentListItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string Status { get; private set; }
    public string ActionText { get; private set; } = "打开";
    public bool IsBusy { get; private set; }
    public bool IsRunning => this.Status == "运行中";
    public bool IsOpening => this.Status == "启动中";
    public string Proxy { get; }
    public int FingerprintSeed { get; }
    public string Ip { get; private set; } = "检测 IP";
    public string Location { get; private set; } = "未检测";
    public string NetworkColor { get; private set; } = "#98A2B3";
    public string? NetworkError { get; private set; }
    public string RemoteColor { get; private set; } = "#98A2B3";
    public string LocalColor { get; private set; } = "#98A2B3";
    public string DebugColor { get; private set; } = "#98A2B3";
    public string RemoteTip { get; private set; } = "远端代理未检测";
    public string LocalTip { get; private set; } = "本地代理未检测";
    public string DebugTip { get; private set; } = "调试端口未检测";
    public bool IsChecking { get; private set; }
    public double SpinnerAngle { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EnvironmentListItem(string name, string status, string proxy, int fingerprintSeed)
    {
        this.Name = name;
        this.Status = status;
        this.Proxy = proxy;
        this.FingerprintSeed = fingerprintSeed;
    }

    public void SetStatus(string status, bool busy = false)
    {
        if (this.Status == status && this.IsBusy == busy)
        {
            return;
        }

        this.Status = status;
        this.IsBusy = busy;
        this.ActionText = "打开";
        if (status == "运行中" || status == "关闭中")
        {
            this.ActionText = "关闭";
        }
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Status)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsBusy)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsRunning)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsOpening)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ActionText)));
    }

    public void BeginCheck()
    {
        this.IsChecking = true;
        this.SpinnerAngle = 0;
        this.NetworkColor = "#98A2B3";
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsChecking)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.SpinnerAngle)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.NetworkColor)));
    }

    public void AdvanceSpinner()
    {
        this.SpinnerAngle = (this.SpinnerAngle + 18) % 360;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.SpinnerAngle)));
    }

    public void SetNetwork(string ip, string location, string color, string? error = null)
    {
        this.Ip = ip;
        this.Location = location;
        this.NetworkColor = color;
        this.NetworkError = error;
        this.IsChecking = false;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetDiagnostics(bool running, bool hasRemoteProxy, bool? remoteReachable, bool hasLocalProxy, bool localListening)
    {
        this.DebugColor = "#98A2B3";
        this.DebugTip = "调试端口未连接";
        if (running)
        {
            this.DebugColor = "#12B76A";
            this.DebugTip = "调试端口已连接";
        }
        if (!hasRemoteProxy)
        {
            this.RemoteColor = "#98A2B3";
            this.RemoteTip = "未使用远端代理";
        }
        else if (remoteReachable.HasValue)
        {
            SetRemote(remoteReachable.Value);
        }
        else if (this.RemoteTip == "未使用远端代理")
        {
            this.RemoteTip = "远端代理未检测";
        }
        this.LocalColor = "#98A2B3";
        this.LocalTip = "未使用本地代理";
        if (hasLocalProxy)
        {
            this.LocalColor = "#D92D20";
            this.LocalTip = "本地代理端口无法连接";
            if (localListening)
            {
                this.LocalColor = "#12B76A";
                this.LocalTip = "本地代理端口可连接";
            }
        }
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetRemote(bool reachable)
    {
        this.RemoteColor = "#D92D20";
        this.RemoteTip = "远端代理端口无法连接";
        if (reachable)
        {
            this.RemoteColor = "#12B76A";
            this.RemoteTip = "远端代理端口可连接";
        }
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.RemoteColor)));
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.RemoteTip)));
    }
}

public partial class MainWindow : Window
{
    private static readonly LogRepository Log = LogRepository.For<MainWindow>();
    private readonly DataRepository _data;
    private readonly BrowserFacade _browser;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _spinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _refreshingStatuses;
    private DateTimeOffset _nextRemoteCheck = DateTimeOffset.MinValue;
    private int _page = 1;
    private int _pageSize = 20;
    private bool _updatingPageBox;
    private string _ipInfoToken = string.Empty;
    private string _userDataDirectory = string.Empty;
    private EnvironmentListItem? _selectedEnvironment;

    public ObservableCollection<EnvironmentListItem> Environments { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        this.ProductTitle.Text = $"ChromeDock 指纹浏览器 [v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知"}]";
        this._data = new DataRepository();
        this._ipInfoToken = this._data.LoadIpInfoToken();
        this._userDataDirectory = this._data.LoadUserDataDirectory();
        this._browser = new BrowserFacade(this._data, this._userDataDirectory);
        this.StatusFilterBox.ItemsSource = this._data.GetOptions("status_filter");
        this.StatusFilterBox.SelectedIndex = 0;
        this.PageSizeBox.ItemsSource = this._data.GetOptions("page_size");
        this.PageSizeBox.SelectedIndex = 0;
        if (this.PageSizeBox.SelectedValue is string initialPageSize &&
            int.TryParse(initialPageSize, out var parsedPageSize) && parsedPageSize > 0)
        {
            this._pageSize = parsedPageSize;
        }
        Debug.Assert(GetPageCount(0, 20) == 1 && GetPageCount(41, 20) == 3);
        var filterCheck = new EnvironmentListItem("测试环境", "运行中", "不使用代理", 1001);
        Debug.Assert(filterCheck.IsRunning && !filterCheck.IsOpening);
        filterCheck.SetStatus("启动中", true);
        Debug.Assert(!filterCheck.IsRunning && filterCheck.IsOpening);
        filterCheck.SetStatus("运行中");
        Debug.Assert(MatchesFilter(filterCheck, "测试", "running") &&
            MatchesFilter(filterCheck, "1001", "all") &&
            !MatchesFilter(filterCheck, "其他", "all") &&
            !MatchesFilter(filterCheck, "", "stopped"));
        filterCheck.SetDiagnostics(true, true, true, true, true);
        Debug.Assert(filterCheck.RemoteColor == "#12B76A" &&
            filterCheck.LocalColor == "#12B76A" && filterCheck.DebugColor == "#12B76A");
        foreach (var environment in this._data.LoadEnvironments())
        {
            this.Environments.Add(ToListItem(environment));
        }
        this.Environments.CollectionChanged += (_, _) =>
        {
            this._nextRemoteCheck = DateTimeOffset.MinValue;
            RefreshPage();
        };
        RefreshPage();
        this._spinnerTimer.Tick += (_, _) =>
        {
            var checking = false;
            foreach (var environment in this.Environments)
            {
                if (!environment.IsChecking && !environment.IsOpening)
                {
                    continue;
                }
                environment.AdvanceSpinner();
                checking = true;
            }
            if (!checking)
            {
                this._spinnerTimer.Stop();
            }
        };
        this.Loaded += async (_, _) =>
        {
            this._statusTimer.Tick += async (_, _) => await RefreshStatusesAsync();
            await StartResidentProxiesAsync();
            this._statusTimer.Start();
            await RefreshStatusesAsync();
        };
        this.Closed += (_, _) =>
        {
            this._statusTimer.Stop();
            this._spinnerTimer.Stop();
        };
    }

    private static EnvironmentListItem ToListItem(BrowserEnvironment environment)
    {
        var proxy = "不使用代理";
        if (environment.ProxyType != "不使用代理")
        {
            var scheme = environment.ProxyType.ToLowerInvariant();
            proxy = $"{scheme}://{environment.ProxyAddress}:{environment.ProxyPort}";
        }
        return new EnvironmentListItem(environment.Name, "未启动", proxy, environment.FingerprintSeed);
    }

    private static int GetPageCount(int count, int pageSize)
    {
        return Math.Max(1, (count - 1) / pageSize + 1);
    }

    private async Task StartResidentProxiesAsync()
    {
        foreach (var environment in this._data.LoadEnvironments())
        {
            try
            {
                await this._browser.EnsureProxyAsync(environment);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动环境 {0} 的本地代理失败", environment.Name);
            }
        }
    }

    private static bool MatchesFilter(EnvironmentListItem environment, string query, string statusFilter)
    {
        if (query.Length > 0 &&
            !environment.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !environment.FingerprintSeed.ToString().Contains(query, StringComparison.Ordinal))
        {
            return false;
        }
        if ((statusFilter == "running" && environment.Status != "运行中") ||
            (statusFilter == "stopped" && environment.Status != "未启动"))
        {
            return false;
        }
        return true;
    }

    private void RefreshPage()
    {
        var query = this.SearchBox.Text?.Trim() ?? string.Empty;
        var statusFilter = this.StatusFilterBox.SelectedValue as string ?? "all";
        var filtered = new List<EnvironmentListItem>();
        foreach (var environment in this.Environments)
        {
            if (!MatchesFilter(environment, query, statusFilter))
            {
                continue;
            }
            filtered.Add(environment);
        }

        var count = filtered.Count;
        var pageCount = GetPageCount(count, this._pageSize);
        this._page = Math.Clamp(this._page, 1, pageCount);

        var start = (this._page - 1) * this._pageSize;
        var end = Math.Min(start + this._pageSize, count);
        var rows = new List<EnvironmentListItem>();

        for (var i = start; i < end; i++)
        {
            rows.Add(filtered[i]);
        }

        this.EnvironmentRows.ItemsSource = rows;
        this.EnvironmentRows.IsVisible = true;
        this.EmptyState.IsVisible = false;
        var first = start + 1;
        if (count == 0)
        {
            this.EnvironmentRows.IsVisible = false;
            this.EmptyState.IsVisible = true;
            this.EmptyStateText.Text = "暂无浏览器环境";
            this.EmptyStateHint.Text = "创建环境后，可在这里启动和管理浏览器";
            if (query.Length > 0 || statusFilter != "all")
            {
                this.EmptyStateText.Text = "没有匹配的环境";
                this.EmptyStateHint.Text = "请调整搜索内容或状态筛选";
            }
            first = 0;
        }

        this.RangeText.Text = $"共 {count} 个环境 · 显示 {first}–{end} 个";
        this.PreviousButton.IsEnabled = this._page > 1;
        this.NextButton.IsEnabled = this._page < pageCount;
        this.PageCountText.Text = $"/ {pageCount} 页";

        this._updatingPageBox = true;
        this.PageBox.Items.Clear();
        for (var page = 1; page <= pageCount; page++)
        {
            this.PageBox.Items.Add(page);
        }
        this.PageBox.SelectedIndex = this._page - 1;
        this.PageBox.IsHitTestVisible = this.PageBox.Focusable = pageCount > 1;
        this._updatingPageBox = false;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!this.IsInitialized)
        {
            return;
        }

        this._page = 1;
        RefreshPage();
    }

    private void StatusFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this.IsInitialized)
        {
            return;
        }

        this._page = 1;
        RefreshPage();
    }

    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this.IsInitialized)
        {
            return;
        }

        if (this.PageSizeBox.SelectedValue is not string value ||
            !int.TryParse(value, out var pageSize) || pageSize < 1)
        {
            return;
        }

        this._pageSize = pageSize;
        this._page = 1;
        RefreshPage();
    }

    private void PageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._updatingPageBox || this.PageBox.SelectedItem is not int page)
        {
            return;
        }

        this._page = page;
        RefreshPage();
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        this._page--;
        RefreshPage();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        this._page++;
        RefreshPage();
    }

    private async Task RefreshStatusesAsync()
    {
        if (this._refreshingStatuses)
        {
            return;
        }

        this._refreshingStatuses = true;
        try
        {
            var checkRemote = DateTimeOffset.UtcNow >= this._nextRemoteCheck;
            if (checkRemote)
            {
                this._nextRemoteCheck = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            var remoteChecks = new List<(EnvironmentListItem Row, Task<bool> Check)>();
            foreach (var environment in this.Environments.ToArray())
            {
                if (environment.IsBusy)
                {
                    continue;
                }

                var running = await this._browser.IsRunningAsync(environment.FingerprintSeed);
                if (environment.IsBusy || !this.Environments.Contains(environment))
                {
                    continue;
                }
                if (running)
                {
                    environment.SetStatus("运行中");
                }
                else
                {
                    environment.SetStatus("未启动");
                }

                var source = this._data.GetEnvironment(environment.Name);
                var hasRemoteProxy = source is not null && source.ProxyType != "不使用代理";
                var hasLocalProxy = hasRemoteProxy;
                var localListening = false;
                if (hasLocalProxy)
                {
                    localListening = await this._browser.GetLocalProxyPortAsync(environment.FingerprintSeed) is not null;
                }
                if (hasRemoteProxy && source is not null && checkRemote)
                {
                    remoteChecks.Add((environment, BrowserFacade.IsRemoteProxyReachableAsync(source)));
                }
                environment.SetDiagnostics(running, hasRemoteProxy, null,
                    hasLocalProxy, localListening);
            }
            foreach (var check in remoteChecks)
            {
                var reachable = await check.Check;
                if (this.Environments.Contains(check.Row))
                {
                    check.Row.SetRemote(reachable);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新浏览器环境状态失败");
        }
        finally
        {
            this._refreshingStatuses = false;
            RefreshPage();
        }
    }

    private async void OpenCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not EnvironmentListItem environment ||
            environment.IsBusy || environment.IsChecking)
        {
            return;
        }

        environment.SetStatus("检查中", true);
        try
        {
            var running = await this._browser.IsRunningAsync(environment.FingerprintSeed);
            if (running)
            {
                environment.SetStatus("关闭中", true);
                await this._browser.CloseAsync(environment.FingerprintSeed);
                environment.SetStatus("未启动");
            }
            else
            {
                await StartEnvironmentAsync(environment);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "切换环境运行状态失败：{0}，种子 {1}", environment.Name, environment.FingerprintSeed);
            var running = false;
            try
            {
                running = await this._browser.IsRunningAsync(environment.FingerprintSeed);
            }
            catch (Exception probeError)
            {
                Log.Warn(probeError, "无法检查环境 {0} 的调试端口", environment.Name);
            }
            if (running)
            {
                environment.SetStatus("运行中");
            }
            else
            {
                environment.SetStatus("未启动");
            }
            StyledDialog.ShowMessage(this, "无法切换环境", ex.Message);
        }
        finally
        {
            RefreshPage();
        }
    }

    private async Task StartEnvironmentAsync(EnvironmentListItem environment)
    {
        environment.SetStatus("启动中", true);
        this._spinnerTimer.Start();
        try
        {
            var info = await this._browser.OpenAsync(environment.Name);
            environment.SetNetwork(info.Ip, info.Location, "#12B76A");
            environment.SetStatus("运行中");
            this._nextRemoteCheck = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开环境失败：{0}，种子 {1}", environment.Name, environment.FingerprintSeed);
            var running = await this._browser.IsRunningAsync(environment.FingerprintSeed);
            if (running)
            {
                environment.SetStatus("运行中");
            }
            else
            {
                environment.SetStatus("未启动");
            }
            StyledDialog.ShowMessage(this, "无法打开环境", ex.Message);
        }
        finally
        {
            RefreshPage();
        }
    }

    private async void NewEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!this._data.CanCreateEnvironment())
        {
            StyledDialog.ShowMessage(this, "无法新建环境", "最多支持 8999 个环境，ID 范围为 1001–9999。");
            return;
        }

        Log.Debug("打开新建环境窗口");
        var window = new NewEnvironmentWindow(this._data);
        
        if (await window.ShowDialog<bool>(this) && window.SavedEnvironment is not null)
        {
            var row = ToListItem(window.SavedEnvironment);
            this.Environments.Add(row);
            Log.Info("环境已加入列表：{0}", window.SavedEnvironment.Name);
            try
            {
                await this._browser.EnsureProxyAsync(window.SavedEnvironment);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动新环境 {0} 的本地代理失败", window.SavedEnvironment.Name);
                StyledDialog.ShowMessage(this, "代理启动失败", ex.Message);
                return;
            }
            if (window.StartAfterSave)
            {
                await StartEnvironmentAsync(row);
            }
        }
    }

    private void ModifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not EnvironmentListItem environment)
        {
            return;
        }
        this._selectedEnvironment = environment;
        this.ActionPopup.PlacementTarget = button;
        this.ActionPopup.IsOpen = true;
    }

    private async void EditEnvironmentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        this.ActionPopup.IsOpen = false;
        if (sender is not Button item || (item.CommandParameter as EnvironmentListItem ?? this._selectedEnvironment) is not { } environment)
        {
            return;
        }

        try
        {
            if (environment.IsBusy || await this._browser.IsRunningAsync(environment.FingerprintSeed))
            {
                StyledDialog.ShowMessage(this, "无法编辑", "请先关闭环境，再修改启动参数。");
                return;
            }

            var source = this._data.GetEnvironment(environment.Name);
            if (source is null)
            {
                StyledDialog.ShowMessage(this, "无法编辑", "环境记录已不存在。");
                return;
            }

            var window = new NewEnvironmentWindow(this._data, source);
            
            if (await window.ShowDialog<bool>(this) && window.SavedEnvironment is not null)
            {
                var index = this.Environments.IndexOf(environment);
                if (index >= 0)
                {
                    var row = ToListItem(window.SavedEnvironment);
                    this.Environments[index] = row;
                    try
                    {
                        await this._browser.ApplyProxyConfigurationAsync(window.SavedEnvironment);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "更新环境 {0} 的本地代理失败", window.SavedEnvironment.Name);
                        StyledDialog.ShowMessage(this, "代理启动失败", ex.Message);
                        return;
                    }
                    if (window.StartAfterSave)
                    {
                        await StartEnvironmentAsync(row);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开环境编辑失败：{0}", environment.Name);
            StyledDialog.ShowMessage(this, "编辑失败", "无法编辑环境，请查看日志。");
        }
    }

    private async void CopyEnvironmentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        this.ActionPopup.IsOpen = false;
        if (sender is not Button item || (item.CommandParameter as EnvironmentListItem ?? this._selectedEnvironment) is not { } environment)
        {
            return;
        }

        try
        {
            var copied = this._data.CopyEnvironment(environment.Name);
            this.Environments.Add(ToListItem(copied));
            try
            {
                await this._browser.EnsureProxyAsync(copied);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动复制环境 {0} 的本地代理失败", copied.Name);
                StyledDialog.ShowMessage(this, "代理启动失败", ex.Message);
            }
        }
        catch (OverflowException ex)
        {
            StyledDialog.ShowMessage(this, "无法复制", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "复制环境失败：{0}", environment.Name);
            StyledDialog.ShowMessage(this, "无法复制", "复制失败，请查看日志。");
        }
    }

    private async void DeleteEnvironmentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        this.ActionPopup.IsOpen = false;
        if (sender is not Button item || (item.CommandParameter as EnvironmentListItem ?? this._selectedEnvironment) is not { } environment)
        {
            return;
        }

        try
        {
            if (environment.IsBusy || await this._browser.IsRunningAsync(environment.FingerprintSeed))
            {
                StyledDialog.ShowMessage(this, "无法删除", "请先关闭环境，再删除记录。");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除前检查环境运行状态失败：{0}", environment.Name);
            StyledDialog.ShowMessage(this, "无法删除", "无法确认环境是否正在运行，请查看日志。");
            return;
        }

        if (!await StyledDialog.ConfirmDelete(this,
            $"确定删除环境“{environment.Name}”？\n配置副本和用户数据目录会保留，下次启动将自动恢复该环境。"))
        {
            return;
        }

        try
        {
            this._browser.StopProxy(environment.FingerprintSeed);
            if (!this._data.DeleteEnvironment(environment.Name))
            {
                Log.Warn("环境记录已不存在：{0}", environment.Name);
            }
            this.Environments.Remove(environment);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除环境失败：{0}", environment.Name);
            StyledDialog.ShowMessage(this, "无法删除", "删除失败，请查看日志。");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        this.TokenBox.Text = this._ipInfoToken;
        this.UserDataDirectoryBox.Text = this._userDataDirectory;
        this.SettingsPopup.PlacementTarget = this.SettingsButton;
        this.SettingsPopup.IsOpen = true;
        this.TokenBox.Focus();
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = (this.TokenBox.Text ?? string.Empty).Trim();
            var directory = (this.UserDataDirectoryBox.Text ?? string.Empty).Trim();
            if (Path.IsPathFullyQualified(directory) &&
                !Path.GetFullPath(directory).Equals(Path.GetFullPath(this._userDataDirectory), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var environment in this.Environments)
                {
                    if (environment.IsBusy || await this._browser.IsRunningAsync(environment.FingerprintSeed))
                    {
                        StyledDialog.ShowMessage(this, "无法修改目录", "请先关闭所有环境，再修改用户数据目录。");
                        return;
                    }
                }
            }

            this._data.SaveSettings(token, directory);
            this._ipInfoToken = token;
            this._userDataDirectory = Path.GetFullPath(directory);
            this._browser.SetUserDataDirectory(this._userDataDirectory);
            await StartResidentProxiesAsync();
            this.SettingsPopup.IsOpen = false;
        }
        catch (ArgumentException ex)
        {
            StyledDialog.ShowMessage(this, "无法保存设置", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置失败");
            StyledDialog.ShowMessage(this, "无法保存", "设置保存失败，请查看日志。");
        }
    }

    private async void IpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: EnvironmentListItem environment })
        {
            return;
        }

        // 同一环境检测未完成时不重复请求。
        if (environment.IsChecking || environment.IsBusy)
        {
            return;
        }

        environment.BeginCheck();
        this._spinnerTimer.Start();
        Log.Info("开始检测 IP：环境 {0}，代理 {1}", environment.Name, environment.Proxy);
        try
        {
            var info = await this._browser.CheckIpAsync(environment.Name, this._ipInfoToken);
            environment.SetNetwork(info.Ip, info.Location, "#12B76A");
            Log.Info("IP 检测成功：环境 {0}，IP {1}，位置 {2}", environment.Name, info.Ip, info.Location);
        }
        catch (Exception ex)
        {
            environment.SetNetwork("检测 IP", "检测失败", "#F04438", ex.Message);
            Log.Error(ex, "IP 检测失败：环境 {0}", environment.Name);
        }
    }

}

