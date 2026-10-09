using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Diagnostics;

using Microsoft.Data.Sqlite;
using ChromeDock.Repositories;

namespace ChromeDock;

public partial class NewEnvironmentWindow : Window
{
    private static readonly LogRepository Log = LogRepository.For<NewEnvironmentWindow>();
    private readonly DataRepository _data;
    private readonly string? _originalName;
    private readonly int _originalSeed;
    private readonly string _suggestedName = string.Empty;
    private readonly HashSet<string> _selectedLanguages = [];
    private IReadOnlyList<SelectionOption> _languageOptions = [];
    private IReadOnlyList<SelectionOption> _timezoneOptions = [];
    private string _selectedTimezone = string.Empty;
    private bool _updatingLanguages;
    private bool _updatingTimezone;
    private TextBox? _proxyPasteBox;
    public BrowserEnvironment? SavedEnvironment { get; private set; }
    public bool StartAfterSave { get; private set; }

    public NewEnvironmentWindow() : this(new DataRepository()) { }

    public NewEnvironmentWindow(DataRepository data, BrowserEnvironment? source = null)
    {
        this._data = data;
        InitializeComponent();
        this.LanguagePopup.PlacementTarget = this.LanguageButton;
        this.TimezonePopup.PlacementTarget = this.TimezoneButton;
        Debug.Assert(TryParseProxy("BushyNeville_1:123@proxysg.rola.vip:2000",
            out var fullAddress, out var fullPort, out var fullUser, out var fullPassword) &&
            fullAddress == "proxysg.rola.vip" && fullPort == "2000" &&
            fullUser == "BushyNeville_1" && fullPassword == "123");
        Debug.Assert(TryParseProxy("proxysg.rola.vip:2000",
            out var simpleAddress, out var simplePort, out var simpleUser, out var simplePassword) &&
            simpleAddress == "proxysg.rola.vip" && simplePort == "2000" &&
            simpleUser.Length == 0 && simplePassword.Length == 0);
        Debug.Assert(!TryParseProxy("proxysg.rola.vip:70000", out _, out _, out _, out _));
        this.AddHandler(InputElement.PointerWheelChangedEvent,
            OptionList_PointerWheelChanged, RoutingStrategies.Tunnel, true);
        foreach (var box in new[] { this.ProxyAddressBox, this.ProxyPortBox, this.ProxyUsernameBox, this.ProxyPasswordBox })
        {
            box.PastingFromClipboard += ProxyField_PastingFromClipboard;
            box.TextChanged += ProxyField_TextChanged;
        }
        SetOptions(this.ProxyTypeBox, "proxy_type");
        this._languageOptions = this._data.GetOptions("language");
        this.LanguageList.ItemsSource = this._languageOptions;
        if (this._languageOptions.Count > 0)
        {
            this._selectedLanguages.Add(this._languageOptions[0].Value);
        }
        SyncLanguageSelection();
        this._timezoneOptions = this._data.GetOptions("timezone");
        this.TimezoneList.ItemsSource = this._timezoneOptions;
        if (this._timezoneOptions.Count > 0)
        {
            this._selectedTimezone = this._timezoneOptions[0].Value;
            this.TimezoneSelectionText.Text = this._selectedTimezone;
            this.TimezoneList.SelectedIndex = 0;
        }
        SetOptions(this.PlatformBox, "platform");
        SetOptions(this.BrowserBox, "browser");

        if (source is not null)
        {
            this._originalName = source.Name;
            this._originalSeed = source.FingerprintSeed;
            this.Title = "编辑环境";
            this.EditorTitle.Text = "编辑浏览器环境";
            this.NameBox.Text = source.Name;
            this.ProxyTypeBox.SelectedValue = source.ProxyType;
            this.ProxyAddressBox.Text = source.ProxyAddress;
            this.ProxyPortBox.Text = source.ProxyPort?.ToString() ?? string.Empty;
            this.ProxyUsernameBox.Text = source.ProxyUsername;
            this.ProxyPasswordBox.Text = source.ProxyPassword;
            this._selectedLanguages.Clear();
            foreach (var language in source.Language.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                this._selectedLanguages.Add(language);
            }
            SyncLanguageSelection();
            this._selectedTimezone = source.Timezone;
            this.TimezoneSelectionText.Text = source.Timezone;
            foreach (var option in this._timezoneOptions)
            {
                if (option.Value == source.Timezone)
                {
                    this.TimezoneList.SelectedItem = option;
                    break;
                }
            }
            this.PlatformBox.SelectedValue = source.Platform;
            this.BrowserBox.SelectedValue = source.Browser;
        }
        else
        {
            this._suggestedName = this._data.GetNextEnvironmentName();
            this.NameBox.Text = this._suggestedName;
        }
    }

    private void SetOptions(ComboBox box, string group)
    {
        box.ItemsSource = this._data.GetOptions(group);
        box.SelectedIndex = 0;
    }

    private void ProxyField_PastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        this._proxyPasteBox = sender as TextBox;
    }

    private async void ProxyField_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || !ReferenceEquals(box, this._proxyPasteBox))
        {
            return;
        }
        this._proxyPasteBox = null;

        try
        {
            var text = await this.Clipboard!.TryGetTextAsync();
            if (!TryParseProxy(text, out var address, out var port, out var username, out var password))
            {
                return;
            }

            this.ProxyAddressBox.Text = address;
            this.ProxyPortBox.Text = port;
            this.ProxyUsernameBox.Text = username;
            this.ProxyPasswordBox.Text = password;
            if (SelectedValue(this.ProxyTypeBox) == "不使用代理")
            {
                this.ProxyTypeBox.SelectedValue = "HTTP";
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "无法读取代理粘贴内容");
        }
    }

    private static bool TryParseProxy(string? text, out string address, out string port, out string username, out string password)
    {
        address = string.Empty;
        port = string.Empty;
        username = string.Empty;
        password = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var at = value.LastIndexOf('@');
        var endpoint = value;
        if (at >= 0)
        {
            var credentials = value[..at];
            var separator = credentials.IndexOf(':');
            if (separator < 1 || separator == credentials.Length - 1)
            {
                return false;
            }
            username = credentials[..separator];
            password = credentials[(separator + 1)..];
            endpoint = value[(at + 1)..];
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon < 1 || !int.TryParse(endpoint[(colon + 1)..], out var number) || number is < 1 or > 65535)
        {
            return false;
        }
        address = endpoint[..colon];
        if (address.IndexOfAny([' ', '\t', '\r', '\n', '/', '\\', '@', ':']) >= 0)
        {
            return false;
        }
        port = number.ToString();
        return true;
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        this.LanguagePopup.IsOpen = !this.LanguagePopup.IsOpen;
    }

    private void LanguagePopup_Opened(object sender, EventArgs e)
    {
        this.LanguageSearchBox.Text = string.Empty;
        this.LanguageSearchBox.Focus();
    }

    private void LanguageSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (this._languageOptions.Count == 0)
        {
            return;
        }

        var query = (this.LanguageSearchBox.Text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            this._updatingLanguages = true;
            this.LanguageList.ItemsSource = this._languageOptions;
            this._updatingLanguages = false;
        }
        else
        {
            var matches = new List<SelectionOption>();
            foreach (var option in this._languageOptions)
            {
                if (option.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(option);
                }
            }
            this._updatingLanguages = true;
            this.LanguageList.ItemsSource = matches;
            this._updatingLanguages = false;
        }
        SyncLanguageSelection();
    }

    private void LanguageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._updatingLanguages)
        {
            return;
        }

        foreach (SelectionOption option in e.RemovedItems)
        {
            this._selectedLanguages.Remove(option.Value);
        }
        foreach (SelectionOption option in e.AddedItems)
        {
            if (option.Value == "跟随系统")
            {
                this._selectedLanguages.Clear();
            }
            else
            {
                this._selectedLanguages.Remove("跟随系统");
            }
            this._selectedLanguages.Add(option.Value);
        }
        if (this._selectedLanguages.Count == 0)
        {
            this._selectedLanguages.Add("跟随系统");
        }
        SyncLanguageSelection();
    }

    private void OptionList_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var list = this.LanguagePopup.IsOpen ? this.LanguageList :
            this.TimezonePopup.IsOpen ? this.TimezoneList : null;
        if (list is null ||
            list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller)
        {
            return;
        }

        var maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X,
            Math.Clamp(scroller.Offset.Y - e.Delta.Y * 48, 0, maxOffset));
        e.Handled = true;
    }

    private void SyncLanguageSelection()
    {
        this._updatingLanguages = true;
        this.LanguageList.SelectedItems?.Clear();
        foreach (var option in this.LanguageList.Items.OfType<SelectionOption>())
        {
            if (this._selectedLanguages.Contains(option.Value))
            {
                this.LanguageList.SelectedItems?.Add(option);
            }
        }
        this._updatingLanguages = false;

        var values = new List<string>();
        foreach (var option in this._languageOptions)
        {
            if (this._selectedLanguages.Contains(option.Value))
            {
                values.Add(option.Value);
            }
        }
        this.LanguageSelectionText.Text = string.Join(",", values);
        ToolTip.SetTip(this.LanguageSelectionText, this.LanguageSelectionText.Text);
    }

    private void TimezoneButton_Click(object sender, RoutedEventArgs e)
    {
        this.TimezonePopup.IsOpen = !this.TimezonePopup.IsOpen;
    }

    private void TimezonePopup_Opened(object sender, EventArgs e)
    {
        this.TimezoneSearchBox.Text = string.Empty;
        this.TimezoneSearchBox.Focus();
    }

    private void TimezoneSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (this._timezoneOptions.Count == 0)
        {
            return;
        }

        var query = (this.TimezoneSearchBox.Text ?? string.Empty).Trim();
        var matches = new List<SelectionOption>();
        foreach (var option in this._timezoneOptions)
        {
            if (option.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(option);
            }
        }

        this._updatingTimezone = true;
        this.TimezoneList.ItemsSource = matches;
        foreach (var option in matches)
        {
            if (option.Value == this._selectedTimezone)
            {
                this.TimezoneList.SelectedItem = option;
                break;
            }
        }
        this._updatingTimezone = false;
    }

    private void TimezoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._updatingTimezone || this.TimezoneList.SelectedItem is not SelectionOption option)
        {
            return;
        }

        this._selectedTimezone = option.Value;
        this.TimezoneSelectionText.Text = option.Value;
        ToolTip.SetTip(this.TimezoneSelectionText, option.Value);
        this.TimezonePopup.IsOpen = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = (this.NameBox.Text ?? string.Empty).Trim();
        if (this._originalName is null && name == this._suggestedName)
        {
            name = string.Empty;
        }
        if (this._originalName is not null && name.Length == 0)
        {
            StyledDialog.ShowMessage(this, "无法保存", "请填写环境名字。");
            return;
        }

        var proxyType = SelectedValue(this.ProxyTypeBox);
        if (proxyType == "不使用代理" &&
            (!string.IsNullOrWhiteSpace(this.ProxyAddressBox.Text) ||
             !string.IsNullOrWhiteSpace(this.ProxyPortBox.Text) ||
             !string.IsNullOrWhiteSpace(this.ProxyUsernameBox.Text) ||
             !string.IsNullOrWhiteSpace(this.ProxyPasswordBox.Text)))
        {
            StyledDialog.ShowMessage(this, "无法保存", "已填写代理信息，请选择 HTTP、HTTPS 或 SOCKS5 代理类型；若不使用代理，请清空代理信息。");
            return;
        }
        int? port = null;
        if (proxyType != "不使用代理")
        {
            if ((this.ProxyAddressBox.Text ?? string.Empty).Trim().Length == 0 ||
                !int.TryParse((this.ProxyPortBox.Text ?? string.Empty).Trim(), out var parsedPort) ||
                parsedPort is < 1 or > 65535)
            {
                StyledDialog.ShowMessage(this, "无法保存", "请填写代理地址和 1–65535 的端口。");
                return;
            }
            port = parsedPort;
        }

        var environment = new BrowserEnvironment(
            name, this._originalSeed, proxyType, (this.ProxyAddressBox.Text ?? string.Empty).Trim(), port,
            (this.ProxyUsernameBox.Text ?? string.Empty).Trim(), this.ProxyPasswordBox.Text ?? string.Empty,
            this.LanguageSelectionText.Text ?? string.Empty, this._selectedTimezone,
            SelectedValue(this.PlatformBox), SelectedValue(this.BrowserBox));
        try
        {
            if (this._originalName is null)
            {
                this.SavedEnvironment = this._data.SaveEnvironment(environment);
            }
            else
            {
                this._data.UpdateEnvironment(this._originalName, environment);
                this.SavedEnvironment = environment;
            }
            this.StartAfterSave = ReferenceEquals(sender, this.SaveAndStartButton);
            Close(true);
        }
        catch (OverflowException ex)
        {
            Log.Warn(ex, "指纹种子上限阻止新建环境");
            StyledDialog.ShowMessage(this, "无法新建环境", ex.Message);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            Log.Warn(ex, "环境名字重复：{0}", name);
            StyledDialog.ShowMessage(this, "无法保存", "环境名字已存在，请换一个名字。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存环境失败：{0}", name);
            StyledDialog.ShowMessage(this, "无法保存", "保存失败，请查看日志。");
        }
    }

    private static string SelectedValue(ComboBox box)
    {
        return box.SelectedValue as string ?? (box.SelectedItem as SelectionOption)?.Value ?? string.Empty;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}



