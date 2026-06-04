using System.IO;
using System.Text.Json;

namespace VersionedGameSaver.Services;

public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static T? Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(path, json);
    }
}
