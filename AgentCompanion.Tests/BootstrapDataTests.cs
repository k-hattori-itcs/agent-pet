using System.Reflection;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class BootstrapDataTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AgentCompanion.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void ExtractEmbeddedPets_PreservesExistingCharacterFiles()
    {
        var petsDirectory = Path.Combine(_root, "pets");
        var koharuDirectory = Path.Combine(petsDirectory, "koharu");
        Directory.CreateDirectory(koharuDirectory);
        var existingManifest = Path.Combine(koharuDirectory, "pet.json");
        File.WriteAllText(existingManifest, "user customized");

        App.ExtractEmbeddedPets(Assembly.GetAssembly(typeof(App))!, petsDirectory);

        Assert.Equal("user customized", File.ReadAllText(existingManifest));
        Assert.True(File.Exists(Path.Combine(koharuDirectory, "spritesheet.webp")));
        Assert.True(File.Exists(Path.Combine(petsDirectory, "luna", "pet.json")));
    }
}
