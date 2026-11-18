using System.Net;

namespace Search.Windows;

internal static class Address
{
    internal const string Blank = "about:blank";

    internal static bool IsPage(string text) => text == Blank || WebUrl(text) is not null;

    internal static Uri? FromInput(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
            return null;

        if (text.Equals(Blank, StringComparison.OrdinalIgnoreCase))
            return new Uri(Blank);

        if (text.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            return WebUrl(text);

        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator > 0 && Uri.CheckSchemeName(text[..separator]))
            return null;

        if (WebUrl("https://" + text) is { } candidate && LooksLikeHost(candidate))
        {
            if (IsLocal(candidate))
                candidate = WebUrl("http://" + text) ?? candidate;
            return candidate.Host == "0.0.0.0" ? new UriBuilder(candidate) { Host = "localhost" }.Uri : candidate;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var other) &&
            (other.IsFile || UriParser.IsKnownScheme(other.Scheme) ||
             other.Scheme is "about" or "blob" or "data" or "javascript" or "sms" or "tel" or "vbscript" or "view-source"))
            return null;

        return new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(text));
    }

    private static Uri? WebUrl(string text)
    {
        if (text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || text.Contains('\\') ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.UserInfo.Length != 0)
            return null;

        try
        {
            var host = uri.IdnHost.TrimEnd('.');
            if (host.Length == 0)
                return null;
            if (uri.HostNameType == UriHostNameType.Dns)
            {
                // Chromium interprets a numeric final label as an IPv4 address.
                var label = host[(host.LastIndexOf('.') + 1)..];
                var numeric = label.All(char.IsAsciiDigit) ||
                    (label.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && label[2..].All(char.IsAsciiHexDigit));
                if (numeric)
                    return IPAddress.TryParse(host, out var ip) ? new UriBuilder(uri) { Host = ip.ToString() }.Uri : null;
            }
            return uri;
        }
        catch (UriFormatException) { return null; }
    }

    private static bool LooksLikeHost(Uri uri)
    {
        if (IsLocal(uri) || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return true;

        var host = uri.IdnHost.TrimEnd('.');
        var dot = host.LastIndexOf('.');
        return uri.HostNameType == UriHostNameType.Dns && dot > 0 && char.IsAsciiLetter(host[dot + 1]);
    }

    private static bool IsLocal(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        if (uri.IsLoopback || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false;

        return address.GetAddressBytes() is
            [0, 0, 0, 0] or [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _];
    }
}
