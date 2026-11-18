using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Search.Windows.Smoke;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        var directory = Directory.CreateTempSubdirectory("search-windows-smoke-").FullName;
        try
        {
            Run(directory);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void Run(string directory)
    {
        Assert(Address.FromInput("example.com/?next=https://example.org")?.Host == "example.com", "Bare URL with a query");
        Assert(Address.FromInput("localhost:8080")?.AbsoluteUri == "http://localhost:8080/", "Bare localhost with port");
        Assert(Address.FromInput("0.0.0.0:8080")?.AbsoluteUri == "http://localhost:8080/", "Local wildcard address");
        Assert(Address.FromInput("café & cake")?.Query == "?q=caf%C3%A9%20%26%20cake", "Encoded search query");
        Assert(Address.FromInput("site:example.org")?.Query == "?q=site%3Aexample.org", "Search operator");
        Assert(Address.FromInput("weather: tomorrow")?.Query == "?q=weather%3A%20tomorrow", "Search phrase with a colon");
        const string invalidHost = "https://a\u200db.example/";
        const string invalidIp = "https://1.2.3.999/";
        const string invalidHexIp = "https://1.2.3.0x100/";
        const string dottedIp = "https://127.0.0.1./";
        Assert(Address.FromInput(invalidHost) is null && !Address.IsPage(invalidHost), "Invalid IDN rejected");
        Assert(Address.FromInput(invalidIp) is null && !Address.IsPage(invalidIp), "Malformed IPv4 address rejected");
        Assert(Address.FromInput(invalidHexIp) is null && !Address.IsPage(invalidHexIp), "Malformed hexadecimal IPv4 address rejected");
        Assert(Address.FromInput(dottedIp)?.AbsoluteUri == "https://127.0.0.1/" && Address.IsPage(dottedIp),
            "Explicit IPv4 address with trailing dot normalized");
        Assert(Address.FromInput("127.0.0.1.:8080/one")?.AbsoluteUri == "http://127.0.0.1:8080/one",
            "Bare IPv4 address with trailing dot and port stays local");
        Assert(Address.FromInput("foo.bar://example.com") is null, "Dotted custom URL scheme rejected");
        Assert(Address.FromInput("file:///C:/Windows/win.ini") is null && Address.FromInput("javascript:alert(1)") is null,
            "Non-web addresses stay out of the omnibox");

        var session = Path.Combine(directory, "session.json");
        File.WriteAllText(session, "{partial");
        Assert(Session.Read(session) is { Tabs: [Address.Blank], Selected: 0 }, "Damaged session starts blank");
        new Session([invalidHost, invalidIp, invalidHexIp, dottedIp], 3).Write(session);
        Assert(Session.Read(session) is { Tabs: [Address.Blank, Address.Blank, Address.Blank, dottedIp], Selected: 3 },
            "Session retains the valid dotted address and clears invalid addresses");
        File.Delete(session);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        server.MapGet("/one", () => Html("""
            <title>First page</title>
            <input id="draft" aria-label="Draft">
            <p><a id="popup" href="/two" target="_blank">Open second page</a></p>
            <p><a id="download" href="/download">Download fixture</a></p>
            <script>window.open('/two');</script>
            """));
        server.MapGet("/two", () => Html("<title>Second page</title><h1>Rendered by WebView2</h1>"));
        server.MapGet("/download", () => Results.File("test download"u8.ToArray(), "text/plain", "fixture.txt"));
        server.StartAsync().GetAwaiter().GetResult();
        try
        {
            var origin = server.Urls.Single();
            var first = origin + "/one";
            var second = origin + "/two";
            RunWindow(directory, async window =>
            {
                var tabs = window.Controls.OfType<TabControl>().Single();
                var toolbar = window.Controls.OfType<TableLayoutPanel>().Single();
                var address = toolbar.Controls.OfType<TextBox>().Single();
                var originalTab = (BrowserTab)tabs.SelectedTab!;
                var web = await WaitForPage(originalTab, Address.Blank);
                Console.WriteLine($"Runtime {web.Environment.BrowserVersionString}");
                Assert(!web.Settings.AreHostObjectsAllowed && !web.Settings.IsWebMessageEnabled, "No native page bridge");
                Assert(web.Environment.UserDataFolder == Path.Combine(directory, "WebView2"), "Test has its own browser process");
                Assert(!originalTab.TryNavigate(invalidIp), "Native navigation rejection stays in the tab");

                bool? popupGesture = null;
                web.NewWindowRequested += (_, request) => popupGesture = request.IsUserInitiated;
                EnterAddress(address, first.Replace("http://127.0.0.1", "127.0.0.1.", StringComparison.Ordinal));
                await WaitForPage(originalTab, first, "First page");
                await WaitUntil(() => popupGesture is not null, "Fixture requested a popup");
                Assert(popupGesture == false && tabs.TabCount == 1, "Script-only popup blocked");
                await web.ExecuteScriptAsync("document.querySelector('#draft').value = 'kept'");

                await ClickLink(web, "popup");
                await WaitUntil(() => tabs.TabCount == 2, "User-initiated popup creates a tab");
                var popup = (BrowserTab)tabs.SelectedTab!;
                var popupWeb = await WaitForPage(popup, second, "Second page");
                Assert(await popupWeb.ExecuteScriptAsync("document.querySelector('h1').textContent") == "\"Rendered by WebView2\"",
                    "Popup page renders");
                Assert(address.Text == second, "Popup address shown");
                Button(toolbar, "Close tab").PerformClick();
                Assert(tabs.SelectedTab == originalTab && tabs.TabCount == 1, "Closing a popup returns to the remaining tab");
                Assert(await web.ExecuteScriptAsync("document.querySelector('#draft').value") == "\"kept\"", "Tab retained its page state");

                EnterAddress(address, second);
                await WaitForPage(originalTab, second, "Second page");
                Button(toolbar, "Back").PerformClick();
                await WaitForPage(originalTab, first, "First page");
                Button(toolbar, "Forward").PerformClick();
                await WaitForPage(originalTab, second, "Second page");
                Button(toolbar, "Back").PerformClick();
                await WaitForPage(originalTab, first, "First page");
                await web.ExecuteScriptAsync("document.title = 'Changed'");
                Button(toolbar, "Reload").PerformClick();
                await WaitForPage(originalTab, first, "First page");

                web.Profile.DefaultDownloadFolderPath = directory;
                CoreWebView2DownloadOperation? download = null;
                web.DownloadStarting += (_, request) => download = request.DownloadOperation;
                await ClickLink(web, "download");
                await WaitUntil(() => download?.State == CoreWebView2DownloadState.Completed, "Download completed");
                Assert(File.ReadAllText(Path.Combine(directory, "fixture.txt")) == "test download", "Downloaded contents match");
                web.CloseDefaultDownloadDialog();

                Button(toolbar, "New tab").PerformClick();
                Assert(tabs.TabCount == 2, "New tab button adds a tab");
                EnterAddress(address, second);
                var secondTab = (BrowserTab)tabs.SelectedTab!;
                await WaitForPage(secondTab, second, "Second page");
                Assert(address.Text == second, "Address follows navigation");

                window.Activate();
                secondTab.FocusPage();
                await WaitUntil(() => secondTab.ContainsFocus, "WebView2 has the keyboard focus");
                await PressShortcut(window, "^l");
                await WaitUntil(() => address.Focused, "Ctrl+L focuses the address while the web page has focus");
                address.Text = "https://pretend.example/";
                address.Modified = true;
                secondTab.FocusPage();
                await WaitUntil(() => address.Text == second && !address.Focused, "Leaving the field discards an unfinished address");
                await PressShortcut(window, "^t");
                await WaitUntil(() => tabs.TabCount == 3, "Ctrl+T opens a new tab");
                await PressShortcut(window, "^w");
                await WaitUntil(() => tabs.TabCount == 2, "Ctrl+W closes the selected tab");
                await PressShortcut(window, "^{TAB}");
                await WaitUntil(() => tabs.SelectedTab == originalTab, "Ctrl+Tab selects the next tab");
                await PressShortcut(window, "^+{TAB}");
                await WaitUntil(() => tabs.SelectedTab == secondTab, "Ctrl+Shift+Tab selects the previous tab");

                var oldProcess = secondTab.Web!.BrowserProcessId;
                using var engine = Process.GetProcessById((int)oldProcess);
                engine.Kill();
                var restart = window.Controls.OfType<Button>().Single();
                await WaitUntil(() => restart.Visible, "WebView2 crash offers to reopen tabs");
                restart.PerformClick();
                Assert(tabs.TabCount == 2 && tabs.SelectedIndex == 1, "Restart preserves the selected tab and order");
                Assert(((BrowserTab)tabs.TabPages[0]).Web is null, "Restart loads background tabs lazily");
                var recovered = await WaitForPage((BrowserTab)tabs.SelectedTab!, second, "Second page");
                Assert(recovered.BrowserProcessId != oldProcess, "Tab renders in a new browser process");
            });

            RunWindow(directory, async window =>
            {
                var tabs = window.Controls.OfType<TabControl>().Single();
                Assert(tabs.TabCount == 2 && tabs.SelectedIndex == 1, "Session restores the selected tab and order");
                Assert(((BrowserTab)tabs.TabPages[0]).Web is null, "Background tab restores lazily");
                await WaitForPage((BrowserTab)tabs.SelectedTab!, second, "Second page");
                tabs.SelectedIndex = 0;
                await WaitForPage((BrowserTab)tabs.SelectedTab!, first, "First page");
            });
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Console.WriteLine("Windows WebView2 smoke passed.");
    }

    private static void RunWindow(string directory, Func<BrowserWindow, Task> test)
    {
        using var window = new BrowserWindow(directory);
        Exception? failure = null;
        window.Shown += async (_, _) =>
        {
            try { await test(window).WaitAsync(TimeSpan.FromSeconds(90)); }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        };
        Application.Run(window);
        if (failure is not null)
            throw new InvalidOperationException("Search window smoke failed.", failure);
    }

    private static async Task ClickLink(CoreWebView2 web, string id)
    {
        var expression = $"document.getElementById({JsonSerializer.Serialize(id)}).click()";
        await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, userGesture = true }));
    }

    private static async Task<CoreWebView2> WaitForPage(BrowserTab tab, string url, string? title = null)
    {
        await WaitUntil(() => tab.Web is { } web && web.Source == url && (title is null || web.DocumentTitle == title),
            $"Load {url} ({title ?? "blank"})");
        return tab.Web!;
    }

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (!condition())
                await Task.Delay(25, timeout.Token);
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException(message, error);
        }
    }

    private static Button Button(TableLayoutPanel toolbar, string name) =>
        toolbar.Controls.OfType<Button>().Single(button => button.AccessibleName == name);

    private static void EnterAddress(TextBox address, string text)
    {
        address.Text = text;
        SendMessage(address.Handle, 0x0100, (nint)Keys.Enter, 1);
    }

    private static async Task PressShortcut(BrowserWindow window, string keys)
    {
        if (GetForegroundWindow() != window.Handle)
            window.Activate();
        await WaitUntil(() => GetForegroundWindow() == window.Handle, "Search window is in the foreground");
        SendKeys.SendWait(keys);
    }

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
}
