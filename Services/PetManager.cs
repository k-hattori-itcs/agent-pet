using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using TokenPet.Models;

namespace TokenPet.Services;

public class PetManager
{
    private static readonly JsonSerializerOptions ManifestReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ManifestWriteOptions = new() { WriteIndented = true };
    private const long MaxArchiveBytes = 16 * 1024 * 1024;
    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxImageBytes = 12 * 1024 * 1024;
    private const long MaxExpandedBytes = 24 * 1024 * 1024;
    private const int MaxEntries = 8;
    private const double MaxCompressionRatio = 100;

    private static readonly Regex SafeIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
        "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _petsDir;
    private readonly List<PetInfo> _pets = new();
    private string _activePetId = "";

    public string PetsDir => _petsDir;
    public IReadOnlyList<PetInfo> Pets => _pets;
    public string ActivePetId => _activePetId;

    public event Action? PetListChanged;
    public event Action<string>? PetChanged;

    public PetManager()
        : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet_data", "pets"))
    {
    }

    internal PetManager(string petsDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(petsDir);
        _petsDir = Path.GetFullPath(petsDir);
    }

    public void Setup()
    {
        Directory.CreateDirectory(_petsDir);
        RecoverInterruptedImports();
        ScanPets();
    }

    public string? GetActiveSpritePath()
    {
        var pet = _pets.FirstOrDefault(p => p.Id == _activePetId);
        if (pet == null || !IsSafeSpriteName(pet.SpritesheetPath))
            return null;

        var path = ResolveContainedPath(pet.Directory, pet.SpritesheetPath);
        return path != null && File.Exists(path) ? path : null;
    }

    public string? GetActiveDisplayName()
    {
        return _pets.FirstOrDefault(p => p.Id == _activePetId)?.DisplayName;
    }

    public void SetActivePet(string petId)
    {
        if (_activePetId == petId || !_pets.Any(p => p.Id == petId))
            return;

        _activePetId = petId;
        PetChanged?.Invoke(petId);
    }

    public string? ImportPet(string zipPath)
    {
        string? stagingDir = null;
        string? backupDir = null;
        try
        {
            var archiveFile = new FileInfo(zipPath);
            if (!archiveFile.Exists || archiveFile.Length is <= 0 or > MaxArchiveBytes)
                return "petファイルのサイズが不正です。";

            Directory.CreateDirectory(_petsDir);
            using var archive = ZipFile.OpenRead(archiveFile.FullName);
            var validationError = ValidateArchive(archive, out var manifestEntry, out var spriteEntry);
            if (validationError != null)
                return validationError;

            PetInfo? info;
            using (var stream = manifestEntry!.Open())
            {
                info = JsonSerializer.Deserialize<PetInfo>(stream, ManifestReadOptions);
            }

            if (info == null || !IsSafePetId(info.Id))
                return "pet.json のidが不正です。英数字、ハイフン、アンダースコアのみ使用できます。";
            if (!IsSafeSpriteName(info.SpritesheetPath))
                return "pet.json のspritesheetPathが不正です。";
            if (!info.SpritesheetPath.Equals(spriteEntry!.Name, StringComparison.OrdinalIgnoreCase))
                return "pet.json のspritesheetPathと画像ファイル名が一致しません。";

            var petDir = ResolveContainedPath(_petsDir, info.Id);
            if (petDir == null)
                return "pet.json のidが保存先の範囲外です。";

            stagingDir = ResolveContainedPath(_petsDir, $".import-{Guid.NewGuid():N}")!;
            Directory.CreateDirectory(stagingDir);
            long extractedBytes = 0;
            ExtractValidatedEntry(manifestEntry, Path.Combine(stagingDir, "pet.json"), MaxManifestBytes, ref extractedBytes);
            var stagedSpritePath = Path.Combine(stagingDir, spriteEntry.Name);
            ExtractValidatedEntry(spriteEntry, stagedSpritePath, MaxImageBytes, ref extractedBytes);
            SpriteLoader.ValidateImage(stagedSpritePath);

            var previewEntry = archive.Entries.FirstOrDefault(entry =>
                entry.Name.Equals("preview-idle.png", StringComparison.OrdinalIgnoreCase));
            if (previewEntry != null)
            {
                var stagedPreviewPath = Path.Combine(stagingDir, "preview-idle.png");
                ExtractValidatedEntry(previewEntry, stagedPreviewPath, MaxImageBytes, ref extractedBytes);
                SpriteLoader.ValidateImage(stagedPreviewPath);
            }

            WriteNormalizedManifest(stagingDir, info);

            if (Directory.Exists(petDir))
            {
                backupDir = ResolveContainedPath(_petsDir, $".backup-{Guid.NewGuid():N}-{info.Id}")!;
                Directory.Move(petDir, backupDir);
            }

            try
            {
                Directory.Move(stagingDir, petDir);
                stagingDir = null;
            }
            catch
            {
                if (backupDir != null && Directory.Exists(backupDir) && !Directory.Exists(petDir))
                    Directory.Move(backupDir, petDir);
                throw;
            }

            if (backupDir != null && Directory.Exists(backupDir))
                TryDeleteDirectory(backupDir);
            backupDir = null;

            ScanPets();
            SetActivePet(info.Id);
            PetListChanged?.Invoke();
            return null;
        }
        catch (InvalidDataException)
        {
            return "petファイルが破損しているか、対応していない形式です。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Petのインポートに失敗しました。", ex);
            return "インポートに失敗しました。詳細はログを確認してください。";
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    public string? ExportPet(string petId, string outputPath)
    {
        try
        {
            var pet = _pets.FirstOrDefault(p => p.Id == petId);
            if (pet == null)
                return "ペットが見つかりません。";

            using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
            foreach (var file in Directory.GetFiles(pet.Directory))
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Petのエクスポートに失敗しました。", ex);
            return "エクスポートに失敗しました。詳細はログを確認してください。";
        }
    }

    public string? DeletePet(string petId)
    {
        var pet = _pets.FirstOrDefault(p => p.Id == petId);
        if (pet == null || !IsSafePetId(petId))
            return "ペットが見つかりません。";

        try
        {
            var petDir = ResolveContainedPath(_petsDir, petId);
            if (petDir == null || !Path.GetFullPath(pet.Directory).Equals(petDir, StringComparison.OrdinalIgnoreCase))
                return "ペットの保存先が不正です。";

            if (Directory.Exists(petDir))
                Directory.Delete(petDir, true);
            if (_activePetId == petId)
                _activePetId = "";
            ScanPets();
            PetListChanged?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Petの削除に失敗しました。", ex);
            return "削除に失敗しました。詳細はログを確認してください。";
        }
    }

    private static string? ValidateArchive(
        ZipArchive archive,
        out ZipArchiveEntry? manifestEntry,
        out ZipArchiveEntry? spriteEntry)
    {
        manifestEntry = null;
        spriteEntry = null;
        if (archive.Entries.Count is 0 or > MaxEntries)
            return "petファイル内のファイル数が上限を超えています。";

        long expandedBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || !entry.FullName.Equals(entry.Name, StringComparison.Ordinal))
                return "petファイル内にフォルダまたは不正なパスが含まれています。";
            if (!names.Add(entry.Name))
                return "petファイル内に重複したファイル名があります。";
            if (Path.IsPathRooted(entry.Name) || entry.Name.Contains("..", StringComparison.Ordinal))
                return "petファイル内に不正なパスが含まれています。";

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxExpandedBytes)
                return "petファイルの展開後サイズが上限を超えています。";
            if (entry.CompressedLength == 0 && entry.Length > 0)
                return "petファイルの圧縮情報が不正です。";
            if (entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > MaxCompressionRatio)
                return "petファイルの圧縮率が上限を超えています。";

            if (entry.Name.Equals("pet.json", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Length is <= 0 or > MaxManifestBytes)
                    return "pet.json のサイズが不正です。";
                manifestEntry = entry;
                continue;
            }

            var extension = Path.GetExtension(entry.Name);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Length is <= 0 or > MaxImageBytes)
                    return "画像ファイルのサイズが不正です。";
                if (entry.Name.Equals("preview-idle.png", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (spriteEntry != null)
                    return "spritesheet画像は1個だけ含めてください。";
                spriteEntry = entry;
                continue;
            }

            return $"対応していないファイルが含まれています: {SanitizeForMessage(entry.Name)}";
        }

        if (manifestEntry == null)
            return "pet.json が見つかりません。";
        if (spriteEntry == null)
            return "spritesheet.webp または spritesheet.png が見つかりません。";
        return null;
    }

    private static bool IsSafePetId(string? id)
    {
        return id != null && SafeIdPattern.IsMatch(id) && !ReservedWindowsNames.Contains(id);
    }

    private static bool IsSafeSpriteName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal))
            return false;
        var extension = Path.GetExtension(name);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveContainedPath(string root, string child)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, child));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static void ExtractValidatedEntry(
        ZipArchiveEntry entry,
        string destination,
        long maxEntryBytes,
        ref long extractedBytes)
    {
        using var source = entry.Open();
        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long entryBytes = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            entryBytes = checked(entryBytes + read);
            extractedBytes = checked(extractedBytes + read);
            if (entryBytes > maxEntryBytes || extractedBytes > MaxExpandedBytes)
                throw new InvalidDataException("Extracted pet data exceeded the configured limit.");
            target.Write(buffer, 0, read);
        }
        target.Flush(true);
    }

    private static void WriteNormalizedManifest(string directory, PetInfo info)
    {
        var manifest = new
        {
            id = info.Id,
            displayName = info.DisplayName,
            description = info.Description,
            spritesheetPath = info.SpritesheetPath
        };
        var json = JsonSerializer.Serialize(manifest, ManifestWriteOptions);
        File.WriteAllText(Path.Combine(directory, "pet.json"), json);
    }

    private static string SanitizeForMessage(string value)
    {
        return value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path == null || !Directory.Exists(path))
            return;
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("一時Petフォルダの削除に失敗しました。", ex);
        }
    }

    private void RecoverInterruptedImports()
    {
        foreach (var staging in Directory.GetDirectories(_petsDir, ".import-*"))
            TryDeleteDirectory(staging);

        foreach (var backup in Directory.GetDirectories(_petsDir, ".backup-*"))
        {
            var name = Path.GetFileName(backup);
            const int idOffset = 8 + 32 + 1;
            if (name.Length <= idOffset || name[idOffset - 1] != '-' ||
                !Guid.TryParseExact(name.AsSpan(8, 32), "N", out _) ||
                !IsSafePetId(name[idOffset..]))
            {
                AppLogger.Warning($"Unrecognized Pet backup directory was preserved: {SanitizeForMessage(name)}");
                continue;
            }

            var destination = ResolveContainedPath(_petsDir, name[idOffset..]);
            if (destination == null)
                continue;
            try
            {
                if (Directory.Exists(destination))
                    TryDeleteDirectory(backup);
                else
                    Directory.Move(backup, destination);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Petバックアップの復旧に失敗しました。", ex);
            }
        }
    }
    private void ScanPets()
    {
        _pets.Clear();
        if (!Directory.Exists(_petsDir))
            return;

        foreach (var dir in Directory.GetDirectories(_petsDir))
        {
            if (Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal))
                continue;
            var info = LoadPetInfo(dir);
            if (info != null)
                _pets.Add(info);
        }
    }

    private static PetInfo? LoadPetInfo(string dir)
    {
        var jsonPath = Path.Combine(dir, "pet.json");
        if (!File.Exists(jsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var info = new PetInfo
            {
                Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                DisplayName = root.TryGetProperty("displayName", out var displayName) ? displayName.GetString() ?? "" : "",
                Description = root.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                SpritesheetPath = root.TryGetProperty("spritesheetPath", out var spritesheetPath) ? spritesheetPath.GetString() ?? "" : "",
                Directory = Path.GetFullPath(dir)
            };
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            return IsSafePetId(info.Id) && info.Id.Equals(directoryName, StringComparison.OrdinalIgnoreCase) &&
                   IsSafeSpriteName(info.SpritesheetPath)
                ? info
                : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("pet.json の読み込みに失敗しました。", ex);
            return null;
        }
    }
}
