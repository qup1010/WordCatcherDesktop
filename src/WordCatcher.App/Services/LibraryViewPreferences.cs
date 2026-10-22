using System.IO;
using System.Text.Json;

namespace WordCatcher.App.Services;

// Presentation preferences stay local and separate from the synced vocabulary.
public sealed record LibraryViewPreferences(int Filter = 0, int Sort = 0, string Source = "")
{
    public static LibraryViewPreferences Load(string? path)
    {
        try { return path == null ? new() : JsonSerializer.Deserialize<LibraryViewPreferences>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? path)
    {
        if (path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
