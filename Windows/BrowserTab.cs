using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Search.Windows;

internal sealed class BrowserTab : TabPage
{
    private readonly WebView2 page = new() { Dock = DockStyle.Fill, AccessibleName = "Web page" };
    private Task<CoreWebView2?>? initialization;

    internal BrowserTab(string url)
    {
        Url = url;
        UpdateTitle();
        Controls.Add(page);
    }

    internal string Url { get; private set; }
    internal CoreWebView2? Web => page.CoreWebView2;
    internal event EventHandler? Changed;
    internal event EventHandler<CoreWebView2NewWindowRequestedEventArgs>? PopupRequested;
    internal event EventHandler? CloseRequested;
    internal event EventHandler? BrowserProcessFailed;
    internal event KeyEventHandler PageKeyDown
    {
        add => page.KeyDown += value;
        remove => page.KeyDown -= value;
    }

    internal void FocusPage() => page.Focus();

    internal Task<CoreWebView2?> OpenAsync(Task<CoreWebView2Environment> environment) =>
        initialization ??= InitializeAsync(environment);

    internal bool TryNavigate(string url)
    {
        if (Web is not { } web)
            return false;

        try
        {
            web.Navigate(url);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private async Task<CoreWebView2?> InitializeAsync(Task<CoreWebView2Environment> environment)
    {
        try
        {
            var engine = await environment;
            if (IsDisposed)
                return null;

            await page.EnsureCoreWebView2Async(engine);
            if (IsDisposed)
                return null;

            var web = page.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.NavigationStarting += (_, request) => request.Cancel = !Address.IsPage(request.Uri);
            web.LaunchingExternalUriScheme += (_, request) => request.Cancel = true;
            web.NewWindowRequested += (_, request) => PopupRequested?.Invoke(this, request);
            web.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
            web.ProcessFailed += (_, failure) =>
            {
                if (failure.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    BrowserProcessFailed?.Invoke(this, EventArgs.Empty);
            };
            web.SourceChanged += (_, _) =>
            {
                if (Address.IsPage(web.Source))
                    Url = web.Source;
                UpdateTitle();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            web.DocumentTitleChanged += (_, _) => UpdateTitle();
            web.HistoryChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

            if (Url != Address.Blank && !TryNavigate(Url))
            {
                Url = Address.Blank;
                UpdateTitle();
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return web;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or IOException
            or UnauthorizedAccessException or WebView2RuntimeNotFoundException or OperationCanceledException)
        {
            if (!IsDisposed)
            {
                page.Hide();
                Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = $"WebView2 could not open this tab.\n\n{error.Message}"
                });
            }
            return null;
        }
    }

    private void UpdateTitle()
    {
        var title = Web?.DocumentTitle.Trim();
        if (string.IsNullOrEmpty(title))
            title = Url == Address.Blank ? "New tab" : new Uri(Url).IdnHost;

        ToolTipText = title;
        Text = title.Replace("&", "&&", StringComparison.Ordinal);
    }
}
