using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentCompanion.Services;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class PetManagerTests : IDisposable
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");
    private static readonly byte[] OversizedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAJxAAACcQCAYAAAC6TmInAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AgentCompanion.Tests",
        Guid.NewGuid().ToString("N"));

    private string PetsDirectory => Path.Combine(_root, "pets");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData("../Desktop")]
    [InlineData(@"..\..\Windows\Temp\evil")]
    [InlineData(@"C:\Windows\System32\config")]
    [InlineData("pet/name")]
    [InlineData("pet.name")]
    [InlineData("")]
    public void ImportPet_RejectsUnsafeIdsWithoutDeletingExistingData(string id)
    {
        Directory.CreateDirectory(PetsDirectory);
        var sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var zip = CreatePackage(id, "spritesheet.webp");

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.NotNull(result);
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("../spritesheet.webp")]
    [InlineData(@"..\spritesheet.webp")]
    [InlineData(@"C:\temp\spritesheet.webp")]
    [InlineData("images/spritesheet.webp")]
    [InlineData("spritesheet.gif")]
    public void ImportPet_RejectsUnsafeSpritePaths(string spritePath)
    {
        var zip = CreatePackage("safe-pet", spritePath);

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.NotNull(result);
    }

    [Fact]
    public void ImportPet_ImportsValidatedRootPackage()
    {
        var zip = CreatePackage("safe-pet", "spritesheet.png");

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.Null(result);
        Assert.True(File.Exists(Path.Combine(PetsDirectory, "safe-pet", "pet.json")));
        Assert.True(File.Exists(Path.Combine(PetsDirectory, "safe-pet", "spritesheet.png")));
    }

    [Fact]
    public void ImportPet_ReplacingActiveCharacterRaisesRefreshEvent()
    {
        CreateInstalledPet("safe-pet");
        var manager = new PetManager(PetsDirectory);
        manager.Setup();
        var changedIds = new List<string>();
        manager.PetChanged += changedIds.Add;
        var zip = CreatePackage("safe-pet", "spritesheet.png");

        var result = manager.ImportPet(zip);

        Assert.Null(result);
        Assert.Equal(["safe-pet"], changedIds);
    }

    [Fact]
    public void ImportPet_RejectsInvalidImageContent()
    {
        var zip = CreatePackage("safe-pet", "spritesheet.png", spriteContent: [1, 2, 3, 4]);

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.NotNull(result);
        Assert.False(Directory.Exists(Path.Combine(PetsDirectory, "safe-pet")));
    }

    [Fact]
    public void ImportPet_RejectsOversizedDecodedImage()
    {
        var zip = CreatePackage("safe-pet", "spritesheet.png", spriteContent: OversizedPng);

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.NotNull(result);
        Assert.False(Directory.Exists(Path.Combine(PetsDirectory, "safe-pet")));
    }
    [Fact]
    public void Setup_RecoversInterruptedPetBackup()
    {
        Directory.CreateDirectory(PetsDirectory);
        var backup = Path.Combine(PetsDirectory, $".backup-{Guid.NewGuid():N}-recovered-pet");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "pet.json"), JsonSerializer.Serialize(new
        {
            id = "recovered-pet",
            displayName = "Recovered Pet",
            description = "test",
            spritesheetPath = "spritesheet.png"
        }));
        File.WriteAllBytes(Path.Combine(backup, "spritesheet.png"), ValidPng);

        var manager = new PetManager(PetsDirectory);
        manager.Setup();

        Assert.True(Directory.Exists(Path.Combine(PetsDirectory, "recovered-pet")));
        Assert.Contains(manager.Pets, pet => pet.Id == "recovered-pet");
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void Setup_SelectsKoharuByDefault()
    {
        CreateInstalledPet("luna");
        CreateInstalledPet("koharu");

        var manager = new PetManager(PetsDirectory);
        manager.Setup();

        Assert.Equal("koharu", manager.ActivePetId);
        Assert.NotNull(manager.GetActiveSpritePath());
    }

    [Fact]
    public void Setup_SelectsFirstCharacterWhenKoharuIsUnavailable()
    {
        CreateInstalledPet("luna");

        var manager = new PetManager(PetsDirectory);
        manager.Setup();

        Assert.Equal("luna", manager.ActivePetId);
        Assert.NotNull(manager.GetActiveSpritePath());
    }
    [Fact]
    public void ImportPet_RejectsNestedEntries()
    {
        var zip = CreatePackage("safe-pet", "images/spritesheet.webp");

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.NotNull(result);
    }

    [Fact]
    public void ImportPet_RejectsPackagesWithTooManyEntries()
    {
        var extras = Enumerable.Range(0, 128)
            .ToDictionary(index => $"extra-{index}.txt", _ => new byte[] { 1 });
        var zip = CreatePackage("safe-pet", "spritesheet.webp", extras);

        var result = new PetManager(PetsDirectory).ImportPet(zip);

        Assert.NotNull(result);
    }

    private string CreatePackage(
        string id,
        string spritePath,
        IReadOnlyDictionary<string, byte[]>? extras = null,
        byte[]? spriteContent = null)
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, $"{Guid.NewGuid():N}.agentcompanion");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = new
        {
            id,
            name = "Test Pet",
            description = "test",
            spritesheetPath = spritePath,
            frameWidth = 128,
            frameHeight = 128,
            animations = new Dictionary<string, int[]> { ["idle"] = [0, 0, 1] }
        };
        WriteEntry(archive, "pet.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
        WriteEntry(archive, spritePath, spriteContent ?? ValidPng);
        if (extras is not null)
        {
            foreach (var entry in extras)
            {
                WriteEntry(archive, entry.Key, entry.Value);
            }
        }

        return zipPath;
    }

    private void CreateInstalledPet(string id)
    {
        var directory = Path.Combine(PetsDirectory, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pet.json"), JsonSerializer.Serialize(new
        {
            id,
            displayName = id,
            description = "test",
            spritesheetPath = "spritesheet.png"
        }));
        File.WriteAllBytes(Path.Combine(directory, "spritesheet.png"), ValidPng);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path);
        using var output = entry.Open();
        output.Write(content);
    }
}
