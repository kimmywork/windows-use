using System.Text.Json;
using WinUse.Core.Scripts;

namespace WinUse.Core.Tests;

public class ScriptStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ScriptStore _store;

    public ScriptStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winuse-script-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ScriptStore(_dir);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var doc = ScriptStore.Template("demo");
        _store.Save(doc);

        var loaded = _store.Load("demo");
        Assert.Equal("demo", loaded.Name);
        Assert.Single(loaded.Steps);
        Assert.Equal("display.list", loaded.Steps[0].Capability);
    }

    [Fact]
    public void List_reports_load_errors_not_silent()
    {
        _store.Save(ScriptStore.Template("good"));
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");

        var docs = _store.List(out var errors);
        Assert.Single(docs);
        Assert.Single(errors);
        Assert.Equal("bad.json", errors[0].File);
    }

    [Fact]
    public void Rejects_empty_steps()
    {
        var doc = new ScriptDocument("x", "", "", Array.Empty<ScriptStep>(), "abort");
        Assert.Throws<ScriptFormatException>(() => _store.Save(doc));
    }

    [Fact]
    public void Rejects_bad_on_error()
    {
        var step = new ScriptStep("display.list", JsonSerializer.SerializeToElement(new Dictionary<string, object?>()));
        var doc = new ScriptDocument("x", "", "", new[] { step }, "nope");
        Assert.Throws<ScriptFormatException>(() => _store.Save(doc));
    }

    [Fact]
    public void Rename_moves_file()
    {
        _store.Save(ScriptStore.Template("old"));
        _store.Rename("old", "new");

        Assert.True(_store.Exists("new"));
        Assert.False(_store.Exists("old"));
    }

    [Fact]
    public void TryParseText_reports_json_errors()
    {
        var ok = ScriptStore.TryParseText("{oops", "x", out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
