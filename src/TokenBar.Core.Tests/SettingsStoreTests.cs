using Xunit;

namespace TokenBar.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void RoundTripsAcrossInstances()
    {
        var store = new SettingsStore(StorePath);
        store.SetString("tokenbar.chart.stackBy", "agent");
        store.SetBool("tokenbar.trace.detailed", true);
        store.SetInt("tokenbar.refresh.intervalMin", 5);
        store.SetDouble("tokenbar.popover.height", 620.5);

        var reloaded = new SettingsStore(StorePath);
        Assert.Equal("agent", reloaded.GetString("tokenbar.chart.stackBy"));
        Assert.True(reloaded.GetBool("tokenbar.trace.detailed", fallback: false));
        Assert.Equal(5, reloaded.GetInt("tokenbar.refresh.intervalMin", fallback: 30));
        Assert.Equal(620.5, reloaded.GetDouble("tokenbar.popover.height", fallback: 0));
    }

    [Fact]
    public void MissingKeysFallBack()
    {
        var store = new SettingsStore(StorePath);
        Assert.Null(store.GetString("tokenbar.dashboard.year"));
        Assert.Equal("model", store.GetString("tokenbar.chart.stackBy", "model"));
        Assert.True(store.GetBool("tokenbar.tray.animate", fallback: true));
        Assert.Equal(30, store.GetInt("tokenbar.refresh.intervalMin", fallback: 30));
    }

    [Fact]
    public void WrongTypeFallsBack()
    {
        var store = new SettingsStore(StorePath);
        store.SetString("tokenbar.tray.animate", "yes");
        store.SetInt("tokenbar.quota.source", 7);

        Assert.True(store.GetBool("tokenbar.tray.animate", fallback: true));
        Assert.Equal("auto", store.GetString("tokenbar.quota.source", "auto"));
        Assert.Equal(0.0, store.GetDouble("tokenbar.tray.animate", fallback: 0));
    }

    [Fact]
    public void CorruptFileResetsToEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json !!");

        var store = new SettingsStore(StorePath);
        Assert.Null(store.GetString("tokenbar.chart.metric"));

        store.SetString("tokenbar.chart.metric", "cost");
        var reloaded = new SettingsStore(StorePath);
        Assert.Equal("cost", reloaded.GetString("tokenbar.chart.metric"));
    }

    [Fact]
    public void RemoveDeletesAndPersists()
    {
        var store = new SettingsStore(StorePath);
        store.SetString("tokenbar.dashboard.year", "2025");
        store.Remove("tokenbar.dashboard.year");

        Assert.Null(store.GetString("tokenbar.dashboard.year"));
        Assert.Null(new SettingsStore(StorePath).GetString("tokenbar.dashboard.year"));
    }

    [Fact]
    public void ChangedFiresOnChangeOnly()
    {
        var store = new SettingsStore(StorePath);
        var events = new List<string>();
        store.Changed += events.Add;

        store.SetString("tokenbar.chart.stackBy", "agent");
        store.SetString("tokenbar.chart.stackBy", "agent"); // same value: no event
        store.SetString("tokenbar.chart.stackBy", "model");
        store.Remove("tokenbar.chart.stackBy");
        store.Remove("tokenbar.chart.stackBy"); // already gone: no event

        Assert.Equal(
            ["tokenbar.chart.stackBy", "tokenbar.chart.stackBy", "tokenbar.chart.stackBy"],
            events);
    }

    [Fact]
    public void CreatesParentDirectoryOnFirstWrite()
    {
        var store = new SettingsStore(StorePath);
        Assert.False(File.Exists(StorePath)); // no writes yet — no file

        store.SetBool("tokenbar.limits.asUsed", true);
        Assert.True(File.Exists(StorePath));
    }
}
