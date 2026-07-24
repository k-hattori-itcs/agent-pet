using AgentCompanion.Services;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentCompanion.Persistence.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void AtomicFile_PreservesPreviousVersionAsBackup()
    {
        var path = Path.Combine(_root, "data.json");
        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal("first", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void TokenHistory_LoadsBackupWhenPrimaryIsCorrupt()
    {
        var path = Path.Combine(_root, "token_history.json");
        var history = new TokenHistory(path);
        history.Record("OpenAI", 10, 5);
        history.Flush();
        history.Record("OpenAI", 20, 10);
        history.Flush();
        File.WriteAllText(path, "{broken");

        var recovered = new TokenHistory(path);
        recovered.Load();

        Assert.Equal(15, recovered.GetTodayTotal());
        Assert.Equal(1, recovered.GetTodayCalls());
    }
}
