using System.Text.Json.Nodes;

namespace NexPane.Storage.Settings;

/// <summary>Default content of <c>settings.json</c> (schema version <see cref="SettingsStore.CurrentSchemaVersion"/>).</summary>
public static class SettingsDefaults
{
    /// <summary>Section holding the search configuration (type map used by the <c>type:</c> operator, §8.2).</summary>
    public const string SearchSection = "search";

    /// <summary>Section holding the indexing configuration (content locations, excludes, limits, §7.6).</summary>
    public const string IndexingSection = "indexing";

    /// <summary>Default extension lists per <c>type:</c> category. <c>folder</c> and <c>file</c> are structural.</summary>
    public static IReadOnlyDictionary<string, string[]> TypeMap { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["image"] = ["jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "ico", "svg", "raw", "cr2", "nef", "arw", "dng", "psd"],
        ["audio"] = ["mp3", "wav", "flac", "aac", "m4a", "ogg", "oga", "opus", "wma", "aiff", "aif", "mid", "midi"],
        ["video"] = ["mp4", "m4v", "mkv", "avi", "mov", "wmv", "webm", "mpg", "mpeg", "3gp", "flv", "ts", "m2ts"],
        ["document"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "md", "csv", "epub", "eml", "msg", "one", "xps"],
        ["archive"] = ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "iso", "wim", "lz", "lzma"],
        ["code"] = ["cs", "csproj", "sln", "c", "h", "cpp", "hpp", "cc", "java", "kt", "py", "js", "mjs", "ts", "tsx", "jsx", "go", "rs", "rb", "php", "swift", "ps1", "psm1", "sh", "bat", "cmd", "sql", "json", "xml", "yaml", "yml", "toml", "html", "htm", "css", "scss", "xaml", "lua", "r", "m", "vb", "fs"],
        ["executable"] = ["exe", "msi", "msix", "appx", "com", "scr", "dll", "sys", "bat", "cmd", "ps1", "vbs", "jar"],
        ["font"] = ["ttf", "otf", "woff", "woff2", "fon", "ttc"],
    };

    public static JsonObject Create()
    {
        var typeMap = new JsonObject();
        foreach (var (category, extensions) in TypeMap)
        {
            typeMap[category] = new JsonArray(extensions.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new JsonObject
        {
            [SettingsStore.SchemaVersionProperty] = SettingsStore.CurrentSchemaVersion,
            [SearchSection] = new JsonObject
            {
                ["typeMap"] = typeMap,
                ["contentRegexMaxFiles"] = 5000,
                ["contentRegexMaxBytesPerFile"] = 50L * 1024 * 1024,
            },
            [IndexingSection] = new JsonObject
            {
                ["contentLocations"] = new JsonArray(
                    JsonValue.Create(Path.Combine(home, "Documents")),
                    JsonValue.Create(Path.Combine(home, "Desktop")),
                    JsonValue.Create(Path.Combine(home, "Downloads"))),
                ["excludes"] = new JsonArray(),
                ["maxContentFileBytes"] = 50L * 1024 * 1024,
                ["maxExtractedChars"] = 2 * 1024 * 1024,
                ["skipReparsePoints"] = true,
                ["pauseOnBattery"] = true,
                ["pauseCpuPercent"] = 80,
            },
        };
    }
}
