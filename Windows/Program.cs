using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Search.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Search");
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            Directory.CreateDirectory(directory);
            using var profileLock = new FileStream(
                Path.Combine(directory, "profile.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            using var window = new BrowserWindow(directory);
            Application.Run(window);
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 32)
        {
            ShowError("Search is already open for this Windows profile.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("Install the Microsoft Edge WebView2 Evergreen Runtime to open Search.\n\n"
                + "https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            ShowError($"Search could not start.\n\n{error.Message}");
        }
    }

    internal static void ShowError(string message, IWin32Window? owner = null) =>
        MessageBox.Show(owner, message, "Search", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
