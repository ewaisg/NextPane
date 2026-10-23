namespace NexPane.Storage;

/// <summary>
/// File locations of §9: databases under <c>%LOCALAPPDATA%\NexPane</c>, <c>settings.json</c> under <c>%APPDATA%\NexPane</c>.
/// Both roots can be redirected (tests, portable mode).
/// </summary>
public sealed record AppDataPaths(string LocalRoot, string RoamingRoot)
{
    public static AppDataPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexPane"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexPane"));

    public string IndexDatabase => Path.Combine(LocalRoot, "index.db");

    public string AppDatabase => Path.Combine(LocalRoot, "app.db");

    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    public string LogsDirectory => Path.Combine(LocalRoot, "logs");
}
