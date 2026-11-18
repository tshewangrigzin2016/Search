using System.Text.Json;

namespace Search.Windows;

internal sealed record Session(string[] Tabs, int Selected)
{
    internal static Session Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (JsonSerializer.Deserialize<Session>(file) is { Tabs.Length: > 0 } session)
            {
                var tabs = Array.ConvertAll(session.Tabs,
                    url => url is not null && Address.IsPage(url) ? url : Address.Blank);
                return new Session(tabs, Math.Clamp(session.Selected, 0, tabs.Length - 1));
            }
        }
        catch (FileNotFoundException) { }
        catch (JsonException) { }

        return new Session([Address.Blank], 0);
    }

    internal void Write(string path)
    {
        var temporary = path + ".tmp";
        using (var file = File.Create(temporary))
            JsonSerializer.Serialize(file, this);
        File.Move(temporary, path, overwrite: true);
    }
}
