using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Search.Windows;

internal sealed class BrowserWindow : Form
{
    private const string InvalidAddress = "Enter an HTTP or HTTPS address, or words to search for.";

    private readonly TabControl tabs = new()
    {
        Dock = DockStyle.Fill,
        ShowToolTips = true,
        SizeMode = TabSizeMode.Fixed,
        ItemSize = new Size(154, 26)
    };
    private readonly TextBox address = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        AccessibleName = "Address",
        PlaceholderText = "Search or enter an address"
    };
    private readonly Button back = NavButton("←", "Back");
    private readonly Button forward = NavButton("→", "Forward");
    private readonly Button reload = NavButton("↻", "Reload");
    private readonly Button newTab = NavButton("+", "New tab");
    private readonly Button closeTab = NavButton("×", "Close tab");
    private readonly Button restart = new()
    {
        Text = "WebView2 stopped. Reopen tabs",
        AutoSize = true,
        Dock = DockStyle.Top,
        Visible = false
    };
    private readonly TableLayoutPanel toolbar = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(5, 4, 5, 4),
        RowCount = 1,
        ColumnCount = 6
    };

    private readonly string sessionPath;
    private readonly string profilePath;
    private Task<CoreWebView2Environment>? environment;
    private bool closing;
    private bool engineFailed;

    internal BrowserWindow(string directory)
    {
        sessionPath = Path.Combine(directory, "session.json");
        profilePath = Path.Combine(directory, "WebView2");
        Text = "Search";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (Control item in new Control[] { back, forward, reload, address, newTab, closeTab })
        {
            toolbar.ColumnStyles.Add(item == address ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
            toolbar.Controls.Add(item);
        }
        Controls.Add(tabs);
        Controls.Add(restart);
        Controls.Add(toolbar);

        back.Click += (_, _) => GoBack();
        forward.Click += (_, _) => GoForward();
        reload.Click += (_, _) => ActiveWeb?.Reload();
        newTab.Click += (_, _) => NewTab();
        closeTab.Click += (_, _) => CloseTab(Current);
        restart.Click += async (_, _) => await RestartBrowserAsync();
        address.Leave += (_, _) => ShowAddress();
        address.KeyDown += async (_, key) =>
        {
            if (key.KeyCode == Keys.Escape)
            {
                ShowAddress();
                Current?.FocusPage();
                key.SuppressKeyPress = true;
            }
            else if (key.KeyCode == Keys.Enter)
            {
                key.SuppressKeyPress = true;
                await NavigateAsync();
            }
        };

        RestoreTabs(Session.Read(sessionPath));
        tabs.SelectedIndexChanged += async (_, _) => await ActivateTabAsync();
        ShowAddress();
        UpdateNavigation();
    }

    private BrowserTab? Current => tabs.SelectedTab as BrowserTab;
    private bool BrowserAvailable => !closing && !engineFailed;
    private CoreWebView2? ActiveWeb => BrowserAvailable ? Current?.Web : null;

    private Task<CoreWebView2Environment> EnvironmentTask => environment ??= OpenEnvironmentAsync();

    private async Task<CoreWebView2Environment> OpenEnvironmentAsync()
    {
        var engine = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
        engine.BrowserProcessExited += OnBrowserProcessExited;
        return engine;
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs exit)
    {
        if (closing || exit.BrowserProcessExitKind != CoreWebView2BrowserProcessExitKind.Failed)
            return;

        engineFailed = true;
        BeginInvoke(() =>
        {
            if (closing)
                return;
            toolbar.Enabled = false;
            tabs.Hide();
            restart.Show();
            restart.Select();
        });
    }

    private async Task RestartBrowserAsync()
    {
        var session = CaptureSession();
        foreach (var tab in tabs.TabPages.Cast<BrowserTab>().ToArray())
            tab.Dispose();
        if (environment is { IsCompletedSuccessfully: true })
            environment.Result.BrowserProcessExited -= OnBrowserProcessExited;
        environment = null;
        RestoreTabs(session);
        engineFailed = false;
        restart.Hide();
        toolbar.Enabled = true;
        tabs.Show();
        await ActivateTabAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Current?.Url == Address.Blank)
            FocusAddress();
        await ActivateTabAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
            return;

        closing = true;
        try
        {
            CaptureSession().Write(sessionPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Program.ShowError($"Search could not save the open tabs.\n\n{error.Message}", this);
        }
    }

    private Session CaptureSession() =>
        new(tabs.TabPages.Cast<BrowserTab>().Select(tab => tab.Url).ToArray(), tabs.SelectedIndex);

    private void RestoreTabs(Session session)
    {
        foreach (var url in session.Tabs)
            AddTab(url);
        tabs.SelectedIndex = session.Selected;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!BrowserAvailable || Command(keyData) is not { } command)
            return base.ProcessCmdKey(ref msg, keyData);
        command();
        return true;
    }

    private Action? Command(Keys keys) => keys switch
    {
        Keys.Control | Keys.L => FocusAddress,
        Keys.Control | Keys.T => NewTab,
        Keys.Control | Keys.W => () => CloseTab(Current),
        Keys.Control | Keys.R or Keys.F5 => () => ActiveWeb?.Reload(),
        Keys.Alt | Keys.Left => GoBack,
        Keys.Alt | Keys.Right => GoForward,
        Keys.Control | Keys.Tab => () => CycleTabs(1),
        Keys.Control | Keys.Shift | Keys.Tab => () => CycleTabs(-1),
        _ => null
    };

    private BrowserTab AddTab(string url)
    {
        var tab = new BrowserTab(url);
        tab.Changed += (_, _) =>
        {
            if (!BrowserAvailable || Current != tab)
                return;
            if (!address.Modified)
                ShowAddress();
            UpdateNavigation();
        };
        tab.PageKeyDown += (_, key) =>
        {
            if (!BrowserAvailable || Command(key.KeyData) is not { } command)
                return;

            key.Handled = true;
            // WebView2 blocks its browser process until the accelerator handler returns.
            BeginInvoke(command);
        };
        tab.BrowserProcessFailed += (_, _) => engineFailed = true;
        tab.PopupRequested += OpenPopup;
        tabs.TabPages.Add(tab);
        return tab;
    }

    private async Task ActivateTabAsync()
    {
        if (!BrowserAvailable || Current is not { } tab)
            return;
        ShowAddress();
        UpdateNavigation();
        await tab.OpenAsync(EnvironmentTask);
    }

    private void NewTab()
    {
        if (!BrowserAvailable)
            return;
        tabs.SelectedTab = AddTab(Address.Blank);
        FocusAddress();
    }

    private void CloseTab(BrowserTab? tab)
    {
        if (!BrowserAvailable || tab is null || !tabs.TabPages.Contains(tab))
            return;

        if (tabs.TabCount == 1)
            NewTab();
        else if (tab == Current)
        {
            var index = tabs.SelectedIndex;
            tabs.SelectedIndex = index == tabs.TabCount - 1 ? index - 1 : index + 1;
        }
        tabs.TabPages.Remove(tab);
        tab.Dispose();
    }

    private async void OpenPopup(object? sender, CoreWebView2NewWindowRequestedEventArgs request)
    {
        request.Handled = true;
        if (!BrowserAvailable || !request.IsUserInitiated || !Address.IsPage(request.Uri))
            return;

        using var deferral = request.GetDeferral();
        var tab = AddTab(Address.Blank);
        tab.CloseRequested += (_, _) => BeginInvoke(() => CloseTab(tab));
        tabs.SelectedTab = tab;
        var web = await tab.OpenAsync(EnvironmentTask);
        if (!BrowserAvailable || tab.IsDisposed)
            return;

        try
        {
            if (web is not null && sender is BrowserTab { IsDisposed: false })
            {
                request.NewWindow = web;
                return;
            }
        }
        catch (COMException) { }

        CloseTab(tab);
    }

    private async Task NavigateAsync()
    {
        var input = address.Text;
        if (!BrowserAvailable || Current is not { } tab || string.IsNullOrWhiteSpace(input))
            return;
        if (Address.FromInput(input) is not { } target)
        {
            Program.ShowError(InvalidAddress, this);
            return;
        }

        var ready = await tab.OpenAsync(EnvironmentTask);
        if (!BrowserAvailable || tab.IsDisposed || ready is null)
            return;
        if (Current == tab && address.Text == input)
        {
            address.Modified = false;
            tab.FocusPage();
        }
        if (!tab.TryNavigate(target.AbsoluteUri) && Current == tab)
            Program.ShowError(InvalidAddress, this);
    }

    private void UpdateNavigation()
    {
        var web = ActiveWeb;
        back.Enabled = web?.CanGoBack == true;
        forward.Enabled = web?.CanGoForward == true;
        reload.Enabled = web is not null;
    }

    private void ShowAddress()
    {
        var url = Current?.Url;
        address.Text = url == Address.Blank ? "" : url ?? "";
        address.Modified = false;
    }

    private void FocusAddress()
    {
        address.Focus();
        address.SelectAll();
    }

    private void GoBack()
    {
        if (ActiveWeb is { CanGoBack: true } web)
            web.GoBack();
    }

    private void GoForward()
    {
        if (ActiveWeb is { CanGoForward: true } web)
            web.GoForward();
    }

    private void CycleTabs(int direction)
    {
        if (!BrowserAvailable)
            return;
        var count = tabs.TabCount;
        tabs.SelectedIndex = (tabs.SelectedIndex + direction + count) % count;
    }

    private static Button NavButton(string label, string name) => new()
    {
        Text = label,
        AccessibleName = name,
        Size = new Size(32, 29),
        Margin = new Padding(2),
        Anchor = AnchorStyles.None
    };
}
