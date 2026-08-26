using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DecktamerEmbeddedInstaller;

internal enum RunMode
{
    Install,
    Uninstall,
    Verify,
    Update,
    Notices,
}

internal sealed record Options(
    RunMode Mode,
    string? GamePath,
    string? LocalizationRoot,
    bool SkipProcessCheck,
    bool NoPause
)
{
    public static Options Parse(string[] args)
    {
        var mode = RunMode.Install;
        string? gamePath = null;
        string? localizationRoot = null;
        var skipProcessCheck = false;
        var noPause = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--install": mode = RunMode.Install; break;
                case "--uninstall": mode = RunMode.Uninstall; break;
                case "--verify": mode = RunMode.Verify; break;
                case "--update": mode = RunMode.Update; break;
                case "--notices": mode = RunMode.Notices; break;
                case "--skip-process-check": skipProcessCheck = true; break;
                case "--no-pause": noPause = true; break;
                case "--game":
                    if (++index >= args.Length) throw new ArgumentException("--game 뒤에 게임 경로가 필요합니다.");
                    gamePath = args[index];
                    break;
                case "--localization-root":
                    if (++index >= args.Length) throw new ArgumentException("--localization-root 뒤에 경로가 필요합니다.");
                    localizationRoot = args[index];
                    break;
                default:
                    if (argument.StartsWith('-')) throw new ArgumentException($"알 수 없는 옵션입니다: {argument}");
                    if (gamePath is not null) throw new ArgumentException("게임 경로는 하나만 지정할 수 있습니다.");
                    gamePath = argument;
                    break;
            }
        }
        return new Options(mode, gamePath, localizationRoot, skipProcessCheck, noPause);
    }
}

internal sealed record BinaryDeltaInfo(
    string OriginalHash,
    string PatchedHash,
    long OriginalSize,
    long PatchedSize
);

internal sealed record BuildProfile(
    string GameVersion,
    int TranslationTables,
    int TranslationRows,
    BinaryDeltaInfo Assembly,
    BinaryDeltaInfo Asset
);

internal sealed class PackageData
{
    public required string Root { get; init; }
    public required string PatchVersion { get; init; }
    public required IReadOnlyList<BuildProfile> Profiles { get; init; }

    public static PackageData Load(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json"), Encoding.UTF8));
        var top = document.RootElement;
        var profiles = new List<BuildProfile>();
        foreach (var build in top.GetProperty("builds").EnumerateObject())
        {
            var value = build.Value;
            var translation = value.GetProperty("translation");
            var deltas = value.GetProperty("binary_deltas");
            profiles.Add(new BuildProfile(
                build.Name,
                translation.GetProperty("tables").GetInt32(),
                translation.GetProperty("rows").GetInt32(),
                ReadBinary(deltas.GetProperty("Assembly-CSharp.dll")),
                ReadBinary(deltas.GetProperty("sharedassets0.assets"))
            ));
        }
        return new PackageData
        {
            Root = root,
            PatchVersion = top.GetProperty("patch_version").GetString()!,
            Profiles = profiles.OrderBy(profile => Version.Parse(profile.GameVersion)).ToArray(),
        };
    }

    private static BinaryDeltaInfo ReadBinary(JsonElement element) => new(
        element.GetProperty("original_sha256").GetString()!,
        element.GetProperty("patched_sha256").GetString()!,
        element.GetProperty("original_size").GetInt64(),
        element.GetProperty("patched_size").GetInt64()
    );

    public string LocalizationDirectory(BuildProfile profile) =>
        Path.Combine(Root, "localization", profile.GameVersion, "ko");

    public string AssetPatch(BuildProfile profile) =>
        Path.Combine(Root, "patches", profile.GameVersion, "sharedassets0.assets.kpatch.gz");

    public string AssemblyPatch(BuildProfile profile) =>
        Path.Combine(Root, "patches", profile.GameVersion, "Assembly-CSharp.dll.kpatch.gz");
}

internal sealed record CompatibleLocalization(string Root, int Tables, int Rows);

internal sealed class Installer
{
    private const string MarkerName = ".decktamer-korean-patch.json";
    private const string ReleaseApi = "https://api.github.com/repos/zhtjstod-cell/Decktamer-Korean-Patch/releases/latest";
    private readonly PackageData package;
    private readonly Options options;
    private string gameRoot = "";
    private string localizationRoot = "";
    private string assetTarget = "";
    private string assemblyTarget = "";

    public Installer(PackageData package, Options options)
    {
        this.package = package;
        this.options = options;
    }

    public int Run()
    {
        if (options.Mode == RunMode.Notices)
        {
            PrintNotices();
            return 0;
        }
        if (!options.SkipProcessCheck && Process.GetProcessesByName("Decktamer").Length > 0)
            throw new InvalidOperationException("Decktamer가 실행 중입니다. 게임을 완전히 종료한 뒤 다시 실행하세요.");

        gameRoot = ResolveGameRoot(options.GamePath);
        localizationRoot = options.LocalizationRoot ?? GetDefaultLocalizationRoot();
        assetTarget = Path.Combine(gameRoot, "Decktamer_Data", "sharedassets0.assets");
        assemblyTarget = Path.Combine(gameRoot, "Decktamer_Data", "Managed", "Assembly-CSharp.dll");

        Console.WriteLine($"게임 경로: {gameRoot}");
        Console.WriteLine($"실행 작업: {options.Mode}");
        if (options.Mode == RunMode.Update)
        {
            Update();
            return 0;
        }

        var assetHash = Hash(assetTarget);
        var assemblyHash = Hash(assemblyTarget);
        var exact = package.Profiles.SingleOrDefault(profile =>
            IsKnown(assetHash, profile.Asset) && IsKnown(assemblyHash, profile.Assembly));
        if (exact is not null)
        {
            RunExact(exact);
            return 0;
        }

        RunCompatible(assetHash, assemblyHash);
        return 0;
    }

    private void RunExact(BuildProfile profile)
    {
        Console.WriteLine($"감지 버전: Decktamer {profile.GameVersion}");
        var binaryBackup = Path.Combine(gameRoot, $"KoreanPatch_Backup_{profile.GameVersion}");
        var localizationBackup = ResolveLocalizationBackupRoot(binaryBackup);
        var assetBackup = Path.Combine(binaryBackup, "sharedassets0.assets");
        var assemblyBackup = Path.Combine(binaryBackup, "Assembly-CSharp.dll");
        var source = package.LocalizationDirectory(profile);

        switch (options.Mode)
        {
            case RunMode.Install:
                Directory.CreateDirectory(binaryBackup);
                InstallPatchedFile(assetTarget, package.AssetPatch(profile), assetBackup, profile.Asset);
                InstallPatchedFile(assemblyTarget, package.AssemblyPatch(profile), assemblyBackup, profile.Assembly);
                InstallLocalization(source, localizationBackup, profile.GameVersion, profile.GameVersion, false,
                    profile.TranslationTables, profile.TranslationRows);
                AssertBinary(assetTarget, profile.Asset.PatchedHash, "폰트 패치");
                AssertBinary(assemblyTarget, profile.Assembly.PatchedHash, "언어 전환 패치");
                AssertLocalization(source, profile.TranslationTables);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n설치가 끝났습니다. 게임 설정에서 언어를 한국어로 선택하세요.");
                Console.ResetColor();
                break;
            case RunMode.Uninstall:
                RestorePatchedFile(assetTarget, assetBackup, profile.Asset);
                RestorePatchedFile(assemblyTarget, assemblyBackup, profile.Assembly);
                RestoreLocalization(localizationBackup);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n한글패치를 제거하고 원본을 복구했습니다. 백업 폴더는 안전을 위해 남겨 두었습니다.");
                Console.ResetColor();
                break;
            case RunMode.Verify:
                AssertBinary(assetTarget, profile.Asset.PatchedHash, "폰트 패치");
                AssertBinary(assemblyTarget, profile.Assembly.PatchedHash, "언어 전환 패치");
                AssertLocalization(source, profile.TranslationTables);
                Console.WriteLine($"검증 완료: Decktamer {profile.GameVersion} / 한글패치 {package.PatchVersion}");
                break;
            default: throw new InvalidOperationException("지원하지 않는 작업입니다.");
        }
    }

    private void RunCompatible(string assetHash, string assemblyHash)
    {
        var translationProfile = package.Profiles.MaxBy(profile => Version.Parse(profile.GameVersion))!;
        var assetProfile = package.Profiles
            .Where(profile => IsKnown(assetHash, profile.Asset))
            .MaxByOrDefault(profile => Version.Parse(profile.GameVersion));
        var assemblyProfile = package.Profiles
            .Where(profile => IsKnown(assemblyHash, profile.Assembly))
            .MaxByOrDefault(profile => Version.Parse(profile.GameVersion));
        var compatibilityBackup = Path.Combine(gameRoot, "KoreanPatch_Backup_Compatible");
        var localizationBackup = ResolveLocalizationBackupRoot(compatibilityBackup);

        Warn($"정확히 지원되는 게임 빌드는 아닙니다. {translationProfile.GameVersion} 번역과 현재 영어 템플릿에서 키가 일치하는 문구만 설치하는 호환 모드로 진행합니다.");
        Console.WriteLine($"sharedassets0.assets: {assetHash}");
        Console.WriteLine($"Assembly-CSharp.dll: {assemblyHash}");
        if (assetProfile is null) Warn("폰트 에셋 구조를 확인할 수 없어 폰트 바이너리는 변경하지 않습니다.");
        if (assemblyProfile is null) Warn("언어 전환 DLL 구조를 확인할 수 없어 DLL은 변경하지 않습니다.");

        switch (options.Mode)
        {
            case RunMode.Install:
            {
                var compatible = CreateCompatibleLocalization(package.LocalizationDirectory(translationProfile));
                try
                {
                    if (assetProfile is not null)
                    {
                        var backup = Path.Combine(gameRoot, $"KoreanPatch_Backup_{assetProfile.GameVersion}");
                        Directory.CreateDirectory(backup);
                        InstallPatchedFile(assetTarget, package.AssetPatch(assetProfile),
                            Path.Combine(backup, "sharedassets0.assets"), assetProfile.Asset);
                    }
                    if (assemblyProfile is not null)
                    {
                        var backup = Path.Combine(gameRoot, $"KoreanPatch_Backup_{assemblyProfile.GameVersion}");
                        Directory.CreateDirectory(backup);
                        InstallPatchedFile(assemblyTarget, package.AssemblyPatch(assemblyProfile),
                            Path.Combine(backup, "Assembly-CSharp.dll"), assemblyProfile.Assembly);
                    }
                    Directory.CreateDirectory(localizationBackup);
                    InstallLocalization(compatible.Root, localizationBackup, "unsupported",
                        translationProfile.GameVersion, true, compatible.Tables, compatible.Rows);
                    AssertLocalization(compatible.Root, compatible.Tables);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"\n호환 번역 설치 완료: {compatible.Tables}개 표 / {compatible.Rows}개 일치 문구");
                    Console.ResetColor();
                    Warn("새 버전 전용 문구는 영어로 표시될 수 있습니다. 폰트 바이너리를 적용하지 못했다면 한글 표시도 제한될 수 있습니다.");
                }
                finally { DeleteDirectory(compatible.Root); }
                break;
            }
            case RunMode.Uninstall:
                if (assetProfile is not null)
                {
                    var backup = Path.Combine(gameRoot, $"KoreanPatch_Backup_{assetProfile.GameVersion}", "sharedassets0.assets");
                    RestorePatchedFile(assetTarget, backup, assetProfile.Asset);
                }
                if (assemblyProfile is not null)
                {
                    var backup = Path.Combine(gameRoot, $"KoreanPatch_Backup_{assemblyProfile.GameVersion}", "Assembly-CSharp.dll");
                    RestorePatchedFile(assemblyTarget, backup, assemblyProfile.Assembly);
                }
                RestoreLocalization(localizationBackup);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n호환 번역을 제거했습니다. 확인되지 않은 게임 바이너리는 변경하지 않았습니다.");
                Console.ResetColor();
                break;
            case RunMode.Verify:
            {
                var compatible = CreateCompatibleLocalization(package.LocalizationDirectory(translationProfile));
                try
                {
                    if (assetProfile is not null) AssertBinary(assetTarget, assetProfile.Asset.PatchedHash, "호환 폰트 패치");
                    if (assemblyProfile is not null) AssertBinary(assemblyTarget, assemblyProfile.Assembly.PatchedHash, "호환 언어 전환 패치");
                    AssertLocalization(compatible.Root, compatible.Tables);
                    Console.WriteLine($"검증 완료: 호환 번역 {compatible.Tables}개 표 / {compatible.Rows}개 문구 / 한글패치 {package.PatchVersion}");
                }
                finally { DeleteDirectory(compatible.Root); }
                break;
            }
            default: throw new InvalidOperationException("지원하지 않는 작업입니다.");
        }
    }

    private CompatibleLocalization CreateCompatibleLocalization(string translationRoot)
    {
        var templateRoot = Path.Combine(Directory.GetParent(localizationRoot.TrimEnd(Path.DirectorySeparatorChar))!.FullName,
            "Localization Templates", "en");
        if (!Directory.Exists(templateRoot))
            throw new DirectoryNotFoundException($"호환되는 번역 키를 확인할 영어 템플릿이 없습니다: {templateRoot}\n게임을 영어로 한 번 실행해 메인 화면까지 진입한 뒤 종료하고 다시 설치하세요.");

        var temporary = Path.Combine(Path.GetTempPath(), $"Decktamer-Korean-Compatible-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var tables = 0;
        var rows = 0;
        try
        {
            foreach (var template in Directory.EnumerateFiles(templateRoot, "*.csv"))
            {
                var translation = Path.Combine(translationRoot, Path.GetFileName(template));
                if (!File.Exists(translation)) continue;
                var translatedRows = Csv.Read(translation);
                var templateRows = Csv.Read(template);
                if (translatedRows.Count == 0 || templateRows.Count == 0) continue;
                var translatedColumns = Csv.Columns(translatedRows[0]);
                var templateColumns = Csv.Columns(templateRows[0]);
                if (!translatedColumns.TryGetValue("Key", out var translatedKey) ||
                    !translatedColumns.TryGetValue("Value", out var translatedValue) ||
                    !templateColumns.TryGetValue("Key", out var templateKey)) continue;

                var translations = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in translatedRows.Skip(1))
                {
                    if (row.Length <= Math.Max(translatedKey, translatedValue)) continue;
                    if (!string.IsNullOrWhiteSpace(row[translatedValue])) translations[row[translatedKey]] = row[translatedValue];
                }
                var matched = new List<(string Key, string Value)>();
                foreach (var row in templateRows.Skip(1))
                {
                    if (row.Length <= templateKey) continue;
                    if (translations.TryGetValue(row[templateKey], out var value)) matched.Add((row[templateKey], value));
                }
                if (matched.Count == 0) continue;
                Csv.Write(Path.Combine(temporary, Path.GetFileName(template)), matched);
                tables++;
                rows += matched.Count;
            }
            if (tables == 0 || rows == 0) throw new InvalidOperationException("현재 게임의 영어 템플릿과 일치하는 한국어 번역을 찾지 못했습니다.");
            return new CompatibleLocalization(temporary, tables, rows);
        }
        catch
        {
            DeleteDirectory(temporary);
            throw;
        }
    }

    private void InstallLocalization(string sourceRoot, string backupRoot, string gameVersion,
        string translationProfile, bool compatibilityMode, int tables, int rows)
    {
        var sourceFiles = Directory.GetFiles(sourceRoot, "*.csv");
        if (sourceFiles.Length != tables) throw new InvalidDataException($"배포 번역 표가 {tables}개가 아닙니다.");
        Directory.CreateDirectory(localizationRoot);
        var target = Path.Combine(localizationRoot, "ko");
        var prePatch = Path.Combine(backupRoot, "Localization_ko_before_patch");
        var marker = Path.Combine(target, MarkerName);
        if (Directory.Exists(target))
        {
            if (File.Exists(marker)) DeleteDirectory(target);
            else
            {
                if (Directory.Exists(prePatch)) throw new IOException($"기존 한국어 번역 백업이 이미 있어 현재 ko 폴더를 덮어쓸 수 없습니다: {prePatch}");
                Directory.CreateDirectory(backupRoot);
                Directory.Move(target, prePatch);
            }
        }
        CopyDirectory(sourceRoot, target);
        var markerData = new Dictionary<string, object?>
        {
            ["patch"] = "Decktamer Korean Patch",
            ["patch_version"] = package.PatchVersion,
            ["game_version"] = gameVersion,
            ["translation_profile"] = translationProfile,
            ["compatibility_mode"] = compatibilityMode,
            ["backup_directory_name"] = Path.GetFileName(backupRoot),
            ["installed_at"] = DateTimeOffset.Now.ToString("o"),
            ["tables"] = tables,
            ["rows"] = rows,
            ["installer"] = "self-contained-exe",
        };
        File.WriteAllText(Path.Combine(target, MarkerName),
            JsonSerializer.Serialize(markerData, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        Console.WriteLine($"번역 적용 완료: {target}");
    }

    private string ResolveLocalizationBackupRoot(string fallback)
    {
        var marker = Path.Combine(localizationRoot, "ko", MarkerName);
        if (!File.Exists(marker)) return fallback;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var root = document.RootElement;
            if (root.TryGetProperty("backup_directory_name", out var backupNameElement))
            {
                var backupName = backupNameElement.GetString();
                if (!string.IsNullOrWhiteSpace(backupName) && Path.GetFileName(backupName) == backupName)
                    return Path.Combine(gameRoot, backupName);
            }
            if (root.TryGetProperty("game_version", out var versionElement))
            {
                var version = versionElement.GetString();
                if (package.Profiles.Any(profile => profile.GameVersion == version))
                    return Path.Combine(gameRoot, $"KoreanPatch_Backup_{version}");
            }
        }
        catch (Exception error) { Warn($"기존 한글패치 표식의 백업 정보를 읽지 못했습니다: {error.Message}"); }
        return fallback;
    }

    private void RestoreLocalization(string backupRoot)
    {
        var target = Path.Combine(localizationRoot, "ko");
        var marker = Path.Combine(target, MarkerName);
        var prePatch = Path.Combine(backupRoot, "Localization_ko_before_patch");
        if (Directory.Exists(target))
        {
            if (File.Exists(marker))
            {
                DeleteDirectory(target);
                Console.WriteLine("한글패치 번역 제거 완료");
            }
            else Warn($"ko 폴더가 한글패치 설치본으로 확인되지 않아 삭제하지 않습니다: {target}");
        }
        if (Directory.Exists(prePatch))
        {
            if (Directory.Exists(target)) throw new IOException($"기존 번역을 복원할 위치가 이미 사용 중입니다: {target}");
            Directory.Move(prePatch, target);
            Console.WriteLine("설치 전 ko 폴더 복원 완료");
        }
    }

    private void AssertLocalization(string sourceRoot, int expectedTables)
    {
        var target = Path.Combine(localizationRoot, "ko");
        if (!File.Exists(Path.Combine(target, MarkerName))) throw new InvalidDataException("한글패치 설치 표식이 없습니다.");
        var sourceFiles = Directory.GetFiles(sourceRoot, "*.csv");
        var targetFiles = Directory.Exists(target) ? Directory.GetFiles(target, "*.csv") : [];
        if (sourceFiles.Length != expectedTables || targetFiles.Length != expectedTables)
            throw new InvalidDataException("번역 표 개수 검증 실패");
        foreach (var source in sourceFiles)
        {
            var installed = Path.Combine(target, Path.GetFileName(source));
            if (!File.Exists(installed) || Hash(source) != Hash(installed))
                throw new InvalidDataException($"번역 파일 검증 실패: {Path.GetFileName(source)}");
        }
    }

    private static void InstallPatchedFile(string target, string patch, string backup, BinaryDeltaInfo expected)
    {
        if (!File.Exists(target)) throw new FileNotFoundException("게임 파일이 없습니다.", target);
        if (!File.Exists(patch)) throw new FileNotFoundException("배포 패치 파일이 없습니다.", patch);
        var header = DeltaHeader.Read(patch);
        if (header.OriginalHash != expected.OriginalHash || header.PatchedHash != expected.PatchedHash ||
            header.OriginalSize != expected.OriginalSize || header.PatchedSize != expected.PatchedSize)
            throw new InvalidDataException($"배포 패치의 무결성 정보가 예상값과 다릅니다: {patch}");

        var current = Hash(target);
        if (current == expected.PatchedHash)
        {
            if (!File.Exists(backup) || Hash(backup) != expected.OriginalHash)
                throw new InvalidDataException($"이미 패치된 파일의 정상 원본 백업을 찾지 못했습니다: {backup}");
            Console.WriteLine($"이미 적용됨: {Path.GetFileName(target)}");
            return;
        }
        if (current != expected.OriginalHash) throw new InvalidDataException($"지원하지 않는 파일입니다. 원본을 덮어쓰지 않습니다: {target}\nSHA-256: {current}");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (File.Exists(backup))
        {
            if (Hash(backup) != expected.OriginalHash) throw new InvalidDataException($"기존 백업 파일이 검증된 원본과 다릅니다: {backup}");
        }
        else File.Copy(target, backup);

        ApplyDelta(target, patch, header);
        if (Hash(target) != expected.PatchedHash) throw new InvalidDataException($"설치 후 파일 검증에 실패했습니다: {target}");
        Console.WriteLine($"적용 완료: {Path.GetFileName(target)}");
    }

    private static void RestorePatchedFile(string target, string backup, BinaryDeltaInfo expected)
    {
        if (!File.Exists(backup))
        {
            Warn($"원본 백업이 없어 건너뜁니다: {backup}");
            return;
        }
        if (Hash(backup) != expected.OriginalHash) throw new InvalidDataException($"백업 파일이 검증된 원본과 다릅니다: {backup}");
        var current = Hash(target);
        if (current == expected.OriginalHash)
        {
            Console.WriteLine($"이미 원본 상태: {Path.GetFileName(target)}");
            return;
        }
        if (current != expected.PatchedHash) throw new InvalidDataException($"현재 파일이 한글패치본과 달라 덮어쓰지 않습니다: {target}");
        File.Copy(backup, target, true);
        if (Hash(target) != expected.OriginalHash) throw new InvalidDataException($"원본 복구 검증에 실패했습니다: {target}");
        Console.WriteLine($"원본 복구 완료: {Path.GetFileName(target)}");
    }

    private static void ApplyDelta(string target, string patch, DeltaHeader header)
    {
        var temporary = $"{target}.decktamer-ko-{Guid.NewGuid():N}.tmp";
        File.Copy(target, temporary);
        try
        {
            {
                using var patchFile = File.OpenRead(patch);
                using var gzip = new GZipStream(patchFile, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip, Encoding.UTF8, true);
                using var output = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                reader.ReadBytes(8);
                reader.ReadUInt32();
                var segments = reader.ReadUInt32();
                reader.ReadInt64();
                reader.ReadInt64();
                reader.ReadBytes(32);
                reader.ReadBytes(32);
                long lastEnd = 0;
                for (var index = 0; index < segments; index++)
                {
                    var offset = reader.ReadInt64();
                    var length = reader.ReadInt32();
                    if (offset < lastEnd || length <= 0 || offset + length > header.PatchedSize)
                        throw new InvalidDataException($"패치 조각 정보가 올바르지 않습니다: {patch}");
                    var payload = reader.ReadBytes(length);
                    if (payload.Length != length) throw new EndOfStreamException($"패치 데이터가 중간에서 끝났습니다: {patch}");
                    output.Position = offset;
                    output.Write(payload);
                    lastEnd = offset + length;
                }
                output.SetLength(header.PatchedSize);
                output.Flush(true);
            }
            if (Hash(temporary) != header.PatchedHash) throw new InvalidDataException($"패치 결과 검증에 실패했습니다: {target}");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void AssertBinary(string path, string expectedHash, string label)
    {
        if (Hash(path) != expectedHash) throw new InvalidDataException($"{label} 파일 검증 실패: {path}");
    }

    private void Update()
    {
        Console.WriteLine("GitHub에서 최신 한글패치를 확인합니다...");
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Decktamer-Korean-Patch/{package.PatchVersion}");
        var json = client.GetStringAsync(ReleaseApi).GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("최신 릴리스 태그가 없습니다.");
        var latestVersion = Version.Parse(tag.TrimStart('v'));
        if (latestVersion <= Version.Parse(package.PatchVersion))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"이미 최신 한글패치입니다: v{package.PatchVersion}");
            Console.ResetColor();
            Console.WriteLine(root.GetProperty("html_url").GetString());
            return;
        }

        JsonElement? executableAsset = null;
        JsonElement? checksumAsset = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, @"^Decktamer-Korean-Patch-v.+\.exe$", RegexOptions.IgnoreCase)) executableAsset = asset.Clone();
        }
        if (executableAsset is null) throw new InvalidDataException("최신 릴리스에서 단일 실행형 설치기를 찾지 못했습니다.");
        var executableName = executableAsset.Value.GetProperty("name").GetString()!;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == executableName + ".sha256") checksumAsset = asset.Clone();

        var temporary = Path.Combine(Path.GetTempPath(), $"Decktamer-Korean-Update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var executable = Path.Combine(temporary, executableName);
        try
        {
            Download(client, executableAsset.Value.GetProperty("browser_download_url").GetString()!, executable);
            string? expected = null;
            if (executableAsset.Value.TryGetProperty("digest", out var digestElement))
            {
                var digest = digestElement.GetString();
                if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true) expected = digest[7..].ToLowerInvariant();
            }
            if (expected is null && checksumAsset is not null)
            {
                var checksum = Path.Combine(temporary, executableName + ".sha256");
                Download(client, checksumAsset.Value.GetProperty("browser_download_url").GetString()!, checksum);
                var match = Regex.Match(File.ReadAllText(checksum), @"\b[0-9a-fA-F]{64}\b");
                if (match.Success) expected = match.Value.ToLowerInvariant();
            }
            if (expected is null) throw new InvalidDataException("최신 설치기의 SHA-256 정보를 찾지 못해 실행하지 않습니다.");
            if (Hash(executable) != expected) throw new InvalidDataException("다운로드한 최신 설치기의 SHA-256 검증에 실패했습니다.");

            Console.WriteLine($"한글패치 {tag}을(를) 내려받았습니다. 새 설치기로 전환합니다.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("--install");
            start.ArgumentList.Add("--game");
            start.ArgumentList.Add(gameRoot);
            start.ArgumentList.Add("--localization-root");
            start.ArgumentList.Add(localizationRoot);
            start.ArgumentList.Add("--no-pause");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("새 설치기를 실행하지 못했습니다.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"최신 한글패치 설치가 실패했습니다(종료 코드 {process.ExitCode}).");
        }
        finally { DeleteDirectory(temporary); }
    }

    private static void Download(HttpClient client, string uri, string destination)
    {
        using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var source = response.Content.ReadAsStream();
        using var output = File.Create(destination);
        source.CopyTo(output);
    }

    private void PrintNotices()
    {
        foreach (var relative in new[]
                 {
                     "THIRD_PARTY_NOTICES.md", "LICENSE", "licenses/OFL-NanumPenScript.txt", "licenses/OFL-NotoSerifKR.txt"
                 })
        {
            Console.WriteLine($"\n===== {relative} =====\n");
            Console.WriteLine(File.ReadAllText(Path.Combine(package.Root, relative), Encoding.UTF8));
        }
    }

    private static bool IsKnown(string hash, BinaryDeltaInfo info) => hash == info.OriginalHash || hash == info.PatchedHash;

    private static string GetDefaultLocalizationRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(Directory.GetParent(local)!.FullName, "LocalLow", "Horizon Edge", "Decktamer", "Localization");
    }

    private static string ResolveGameRoot(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var normalized = NormalizeGameRoot(requested);
            if (TestGameRoot(normalized)) return Path.GetFullPath(normalized);
            throw new DirectoryNotFoundException($"지정한 경로에서 Decktamer 게임 파일을 찾지 못했습니다: {requested}");
        }

        var candidates = new List<string>
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName ?? AppContext.BaseDirectory,
        };
        candidates.AddRange(GetSteamCandidates());
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (TestGameRoot(candidate)) return Path.GetFullPath(candidate);

        if (Console.IsInputRedirected)
            throw new DirectoryNotFoundException("게임 폴더를 자동으로 찾지 못했습니다. 실행 파일을 게임 폴더에 놓거나 게임 경로를 인수로 지정하세요.");
        Console.WriteLine("게임 폴더를 자동으로 찾지 못했습니다.");
        Console.Write("Decktamer.exe가 있는 폴더를 붙여 넣고 Enter를 누르세요: ");
        var entered = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(entered)) throw new DirectoryNotFoundException("게임 경로가 입력되지 않았습니다.");
        var selected = NormalizeGameRoot(entered);
        if (!TestGameRoot(selected)) throw new DirectoryNotFoundException($"지정한 경로에서 Decktamer 게임 파일을 찾지 못했습니다: {entered}");
        return Path.GetFullPath(selected);
    }

    private static string NormalizeGameRoot(string path)
    {
        var candidate = path.Trim().Trim('"');
        if (File.Exists(candidate)) candidate = Path.GetDirectoryName(Path.GetFullPath(candidate))!;
        if (Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar)).Equals("Decktamer_Data", StringComparison.OrdinalIgnoreCase))
            candidate = Directory.GetParent(candidate.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        return candidate;
    }

    private static bool TestGameRoot(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "Decktamer_Data", "sharedassets0.assets")) &&
        File.Exists(Path.Combine(path, "Decktamer_Data", "Managed", "Assembly-CSharp.dll"));

    private static IEnumerable<string> GetSteamCandidates()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
                 })
        {
            try
            {
                using var key = pair.Item1.OpenSubKey(pair.Item2);
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                    if (key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)) roots.Add(value);
            }
            catch { }
        }

        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }

        foreach (var library in libraries)
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            var exact = Path.Combine(common, "Decktamer");
            if (TestGameRoot(exact)) yield return exact;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(common, "*Decktamer*").ToArray(); }
            catch { continue; }
            foreach (var directory in directories)
            {
                if (TestGameRoot(directory)) yield return directory;
                var nested = Path.Combine(directory, "game");
                if (TestGameRoot(nested)) yield return nested;
            }
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
    }

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static void Warn(string message)
    {
        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"경고: {message}");
        Console.ForegroundColor = color;
    }
}

internal sealed record DeltaHeader(
    uint Segments,
    long OriginalSize,
    long PatchedSize,
    string OriginalHash,
    string PatchedHash
)
{
    public static DeltaHeader Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8, true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "DKTKO174") throw new InvalidDataException($"지원하지 않는 패치 형식입니다: {path}");
        if (reader.ReadUInt32() != 1) throw new InvalidDataException($"지원하지 않는 패치 버전입니다: {path}");
        return new DeltaHeader(reader.ReadUInt32(), reader.ReadInt64(), reader.ReadInt64(),
            Convert.ToHexString(reader.ReadBytes(32)).ToLowerInvariant(),
            Convert.ToHexString(reader.ReadBytes(32)).ToLowerInvariant());
    }
}

internal static class Csv
{
    public static Dictionary<string, int> Columns(string[] header) =>
        header.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);

    public static List<string[]> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        while (true)
        {
            var value = reader.Read();
            if (value < 0)
            {
                if (field.Length > 0 || row.Count > 0)
                {
                    row.Add(field.ToString());
                    rows.Add(row.ToArray());
                }
                break;
            }
            var character = (char)value;
            if (quoted)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else if (character == '\r')
                {
                    if (reader.Peek() == '\n') reader.Read();
                    field.Append('\n');
                }
                else field.Append(character);
                continue;
            }
            if (character == '"' && field.Length == 0) quoted = true;
            else if (character == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n') reader.Read();
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row = new List<string>();
            }
            else field.Append(character);
        }
        return rows;
    }

    public static void Write(string path, IEnumerable<(string Key, string Value)> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.NewLine = "\r\n";
        writer.WriteLine("Key,Value");
        foreach (var row in rows) writer.WriteLine($"{Escape(row.Key)},{Escape(row.Value)}");
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}

internal static class EnumerableExtensions
{
    public static T? MaxByOrDefault<T, TKey>(this IEnumerable<T> source, Func<T, TKey> selector) where TKey : IComparable<TKey>
    {
        using var enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext()) return default;
        var best = enumerator.Current;
        var bestKey = selector(best);
        while (enumerator.MoveNext())
        {
            var key = selector(enumerator.Current);
            if (key.CompareTo(bestKey) <= 0) continue;
            best = enumerator.Current;
            bestKey = key;
        }
        return best;
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Options? options = null;
        string? payload = null;
        var exitCode = 0;
        try
        {
            options = Options.Parse(args);
            payload = ExtractPayload();
            var package = PackageData.Load(payload);
            Console.Title = $"Decktamer 한국어 패치 v{package.PatchVersion}";
            exitCode = new Installer(package, options).Run();
        }
        catch (Exception error)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n오류: {error.Message}");
            Console.ResetColor();
            exitCode = 1;
        }
        finally
        {
            if (payload is not null)
            {
                try { Installer.DeleteDirectory(payload); }
                catch { }
            }
        }

        if (options?.NoPause != true && !Console.IsInputRedirected)
        {
            Console.WriteLine("\n계속하려면 아무 키나 누르세요...");
            Console.ReadKey(true);
        }
        return exitCode;
    }

    private static string ExtractPayload()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"Decktamer-Korean-Payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DecktamerPayload")
                               ?? throw new InvalidDataException("내장 패치 데이터를 찾지 못했습니다.");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("내장 패치 경로가 올바르지 않습니다.");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                using var output = File.Create(target);
                source.CopyTo(output);
            }
            return destination;
        }
        catch
        {
            Installer.DeleteDirectory(destination);
            throw;
        }
    }
}
