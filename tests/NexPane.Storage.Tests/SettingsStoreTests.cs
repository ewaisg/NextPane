using System.Text.Json.Nodes;
using NexPane.Storage.Settings;

namespace NexPane.Storage.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexpane-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_yields_defaults_with_schema_version_and_is_written()
    {
        using var store = SettingsStore.Load(FilePath);

        store.SchemaVersion.Should().Be(SettingsStore.CurrentSchemaVersion);
        File.Exists(FilePath).Should().BeTrue();
        JsonNode.Parse(File.ReadAllText(FilePath))![SettingsStore.SchemaVersionProperty]!.GetValue<int>().Should().Be(1);
        store.Get<Dictionary<string, string[]>>("search", new Dictionary<string, string[]>()).Should().NotBeNull();
        store.GetSection("search")!["typeMap"]!["image"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("jpg");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"search\": {}}")]
    [InlineData("{\"schemaVersion\": \"one\"}")]
    [InlineData("{\"schemaVersion\": 0}")]
    [InlineData("")]
    public void Corrupt_file_yields_defaults_and_a_backup_copy(string content)
    {
        File.WriteAllText(FilePath, content);

        using var store = SettingsStore.Load(FilePath);

        store.RecoveredCorruptCopy.Should().NotBeNull();
        File.ReadAllText(store.RecoveredCorruptCopy!).Should().Be(content);
        store.SchemaVersion.Should().Be(1);
        JsonNode.Parse(File.ReadAllText(FilePath))![SettingsStore.SchemaVersionProperty]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Values_round_trip_and_unknown_sections_are_preserved()
    {
        File.WriteAllText(FilePath, """{ "schemaVersion": 1, "futureFeature": { "x": 5 } }""");
        using (var store = SettingsStore.Load(FilePath))
        {
            store.RecoveredCorruptCopy.Should().BeNull();
            await store.SetAsync("theme", new ThemeSettings("Dark", 14), TestContext.Current.CancellationToken);
        }

        using var reloaded = SettingsStore.Load(FilePath);
        reloaded.Get("theme", new ThemeSettings("Light", 12)).Should().Be(new ThemeSettings("Dark", 14));
        reloaded.GetSection("futureFeature")!["x"]!.GetValue<int>().Should().Be(5);
        reloaded.GetSection("search").Should().NotBeNull("missing default sections are filled in");
    }

    [Fact]
    public async Task Concurrent_writes_serialize_and_none_is_lost()
    {
        using var store = SettingsStore.Load(FilePath);

        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            store.UpdateAsync(root =>
            {
                var counters = root["counters"] as JsonObject ?? new JsonObject();
                counters["k" + i] = i;
                root["counters"] = counters.DeepClone();
            }, TestContext.Current.CancellationToken)));
        await Task.WhenAll(tasks);

        using var reloaded = SettingsStore.Load(FilePath);
        reloaded.GetSection("counters")!.AsObject().Count.Should().Be(64);
        Directory.GetFiles(_dir, "settings.json.tmp-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Interrupted_write_leaves_the_previous_valid_file()
    {
        using (var store = SettingsStore.Load(FilePath))
        {
            await store.SetAsync("theme", new ThemeSettings("Dark", 14), TestContext.Current.CancellationToken);
        }

        var before = File.ReadAllText(FilePath);
        // Simulates a process killed while writing: the temp file is half written and File.Replace never ran.
        File.WriteAllText(FilePath + ".tmp-" + Guid.NewGuid().ToString("N"), "{ \"schemaVersion\": 1, \"theme\": { \"Na");

        using var reloaded = SettingsStore.Load(FilePath);

        File.ReadAllText(FilePath).Should().Be(before);
        reloaded.RecoveredCorruptCopy.Should().BeNull();
        reloaded.Get("theme", new ThemeSettings("Light", 12)).Name.Should().Be("Dark");
        Directory.GetFiles(_dir, "settings.json.tmp-*").Should().BeEmpty("leftover temp files are cleaned up");
    }

    [Fact]
    public async Task In_process_update_raises_changed_for_the_changed_section_only()
    {
        using var store = SettingsStore.Load(FilePath);
        var events = new List<SettingsChangedEventArgs>();
        store.Changed += (_, e) => events.Add(e);

        await store.SetAsync("theme", new ThemeSettings("Dark", 14), TestContext.Current.CancellationToken);
        await store.SetAsync("theme", new ThemeSettings("Dark", 14), TestContext.Current.CancellationToken);

        events.Should().ContainSingle();
        events[0].Sections.Should().Equal("theme");
        events[0].External.Should().BeFalse();
    }

    [Fact]
    public void External_edit_is_detected_on_reload()
    {
        using var store = SettingsStore.Load(FilePath);
        var events = new List<SettingsChangedEventArgs>();
        store.Changed += (_, e) => events.Add(e);

        var doc = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        doc["theme"] = new JsonObject { ["Name"] = "HighContrast", ["FontSize"] = 20 };
        File.WriteAllText(FilePath, doc.ToJsonString());

        store.ReloadFromDisk().Should().BeTrue();
        events.Should().ContainSingle().Which.External.Should().BeTrue();
        store.Get("theme", new ThemeSettings("Light", 12)).Name.Should().Be("HighContrast");
        store.ReloadFromDisk().Should().BeFalse("unchanged content is not reported twice");
    }

    [Fact]
    public void External_edit_with_invalid_json_is_ignored()
    {
        using var store = SettingsStore.Load(FilePath);
        File.WriteAllText(FilePath, "{ broken");

        store.ReloadFromDisk().Should().BeFalse();
        store.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task File_watcher_reports_external_edits()
    {
        using var store = SettingsStore.Load(FilePath);
        var signal = new TaskCompletionSource<SettingsChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) => signal.TrySetResult(e);
        store.StartWatching();

        var doc = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        doc["watched"] = 1;
        File.WriteAllText(FilePath, doc.ToJsonString());

        var e = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        e.External.Should().BeTrue();
        e.Sections.Should().Contain("watched");
    }

    public sealed record ThemeSettings(string Name, int FontSize);
}
