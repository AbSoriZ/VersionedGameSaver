using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using VersionedGameSaver.Models;
using YamlDotNet.Serialization;

namespace VersionedGameSaver.Services;

public sealed class GameSaveManifestScanner
{
    private const int MaxMatchesPerPattern = 500;
    private const int MaxChildSavesPerFolder = 250;
    private const int MaxTraversalEntriesPerPattern = 25000;
    private const int MaxTraversalDepth = 12;
    private readonly Lazy<Dictionary<string, ManifestGame>> _manifest = new(LoadManifest);

    public ManifestScanResult Scan(
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var context = new ScanContext(progress, cancellationToken);
        context.Report("Loading bundled game save manifest...");
        var manifest = _manifest.Value;
        context.Report($"Loaded {manifest.Count:N0} manifest entries.");

        context.Report("Checking installed games and common save locations...");
        var installed = GameRootFinder.FindInstalledGames(progress, cancellationToken);
        context.Report($"Found {installed.Roots.Count:N0} game install root(s), {installed.SteamAppIds.Count:N0} Steam game(s), {installed.GogFolderNames.Count:N0} GOG folder(s), and {installed.CommonSaveRoots.Count:N0} common save root(s).");

        var candidates = manifest
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value.Alias))
            .Where(entry => IsScanCandidate(entry.Key, entry.Value, installed, context))
            .OrderBy(entry => entry.Key)
            .ToList();

        context.Report($"Selected {candidates.Count:N0} manifest candidate(s) from {manifest.Count:N0} entries.");

        var games = new List<GameEntry>();
        var skippedEntries = manifest.Count - candidates.Count;
        var processedEntries = 0;

        foreach (var (title, manifestGame) in candidates)
        {
            context.ThrowIfCancellationRequested();
            processedEntries++;
            context.ReportOccasionally($"Scanning candidate {processedEntries:N0}/{candidates.Count:N0}: {title}");

            var items = new List<LiveSaveItem>();
            foreach (var item in FindFileItems(title, manifestGame, installed.Roots, context))
            {
                AddUnique(items, item);
            }

            if (items.Count == 0)
            {
                continue;
            }

            var game = new GameEntry
            {
                GameKey = Slug(title),
                Name = title,
                OriginalName = title,
                IsDetected = true
            };

            foreach (var liveSave in BuildLiveSaveEntries(items, context))
            {
                game.LiveSaves.Add(liveSave);
            }

            context.Report($"Found {title}: {game.LiveSaves.Count:N0} live save entr{(game.LiveSaves.Count == 1 ? "y" : "ies")}.");
            games.Add(game);
        }

        context.Report($"Scan found {games.Count:N0} game(s) and {games.Sum(game => game.LiveSaves.Count):N0} live save entr{(games.Sum(game => game.LiveSaves.Count) == 1 ? "y" : "ies")}.");
        return new ManifestScanResult(games, games.Sum(game => game.LiveSaves.Count), 0, skippedEntries);
    }

    private static IEnumerable<LiveSaveItem> FindFileItems(
        string title,
        ManifestGame manifestGame,
        IReadOnlyCollection<GameRoot> roots,
        ScanContext context)
    {
        if (manifestGame.Files is null || manifestGame.Files.Count == 0)
        {
            yield break;
        }

        foreach (var (manifestPath, metadata) in manifestGame.Files)
        {
            context.ThrowIfCancellationRequested();
            if (!AppliesToWindows(metadata.When))
            {
                continue;
            }

            context.ReportOccasionally($"Checking {title}: {manifestPath}");
            foreach (var expandedPath in ExpandPath(title, manifestGame, manifestPath, roots, context))
            {
                var matches = 0;
                foreach (var match in Glob(title, expandedPath, context))
                {
                    context.ThrowIfCancellationRequested();
                    if (Directory.Exists(match))
                    {
                        matches++;
                        yield return new LiveSaveItem
                        {
                            SourcePath = match,
                            Kind = LiveSaveItemKind.Directory,
                            ManifestPath = manifestPath,
                            Tags = metadata.Tags ?? []
                        };
                    }
                    else if (File.Exists(match))
                    {
                        matches++;
                        yield return new LiveSaveItem
                        {
                            SourcePath = match,
                            Kind = LiveSaveItemKind.File,
                            ManifestPath = manifestPath,
                            Tags = metadata.Tags ?? []
                        };
                    }

                    if (matches >= MaxMatchesPerPattern)
                    {
                        context.Report($"Stopped matching {title} pattern after {MaxMatchesPerPattern:N0} result(s): {manifestPath}");
                        break;
                    }
                }
            }
        }
    }

    private static bool IsScanCandidate(
        string title,
        ManifestGame manifestGame,
        InstalledGameIndex installed,
        ScanContext context)
    {
        context.ThrowIfCancellationRequested();
        if (manifestGame.Files is null || manifestGame.Files.Count == 0)
        {
            return false;
        }

        if (manifestGame.Steam?.Id is int steamId && installed.SteamAppIds.Contains(steamId))
        {
            return true;
        }

        foreach (var installName in InstallNames(title, manifestGame))
        {
            if (installed.InstalledFolderNames.Contains(NormalizeNameKey(installName)))
            {
                return true;
            }
        }

        foreach (var (manifestPath, metadata) in manifestGame.Files)
        {
            context.ThrowIfCancellationRequested();
            if (!AppliesToWindows(metadata.When) || RequiresGameRoot(manifestPath))
            {
                continue;
            }

            if (HasExistingConcreteSaveRoot(title, manifestGame, manifestPath, installed, context))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasExistingConcreteSaveRoot(
        string title,
        ManifestGame manifestGame,
        string manifestPath,
        InstalledGameIndex installed,
        ScanContext context)
    {
        foreach (var expandedPath in ExpandPath(title, manifestGame, manifestPath, installed.Roots, context))
        {
            context.ThrowIfCancellationRequested();
            var searchRoot = HasGlob(expandedPath)
                ? StaticSearchRoot(expandedPath)
                : (Directory.Exists(expandedPath) ? expandedPath : Path.GetDirectoryName(expandedPath) ?? "");

            if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
            {
                continue;
            }

            var fullSearchRoot = Path.GetFullPath(searchRoot);
            if (IsOverlyBroadSearchRoot(fullSearchRoot) || IsCommonSaveRoot(fullSearchRoot, installed.CommonSaveRoots))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static IEnumerable<LiveSaveEntry> BuildLiveSaveEntries(List<LiveSaveItem> items, ScanContext context)
    {
        var deduped = items
            .GroupBy(item => NormalizeItemKey(item), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = new List<LiveSaveEntry>();
        if (deduped.Count > 1)
        {
            entries.Add(new LiveSaveEntry
            {
                Label = "All save data",
                OriginalName = "All save data",
                Kind = LiveSaveEntryKind.AllSaveData,
                Source = "Bundled manifest",
                Items = deduped.Select(CloneItem).ToList()
            });
        }

        foreach (var item in deduped)
        {
            context.ThrowIfCancellationRequested();
            entries.Add(new LiveSaveEntry
            {
                Label = ItemLabel(item),
                OriginalName = ItemLabel(item),
                Kind = item.Kind == LiveSaveItemKind.RegistryKey ? LiveSaveEntryKind.Registry : LiveSaveEntryKind.DetectedLocation,
                Source = "Bundled manifest",
                Items = [CloneItem(item)]
            });
        }

        foreach (var item in deduped.Where(item => item.Kind == LiveSaveItemKind.Directory))
        {
            foreach (var child in FindChildSaveFolders(item.SourcePath, context))
            {
                var childItem = new LiveSaveItem
                {
                    SourcePath = child,
                    Kind = LiveSaveItemKind.Directory,
                    ManifestPath = item.ManifestPath,
                    Tags = item.Tags.ToList()
                };
                var childEntry = new LiveSaveEntry
                {
                    Label = ItemLabel(childItem),
                    OriginalName = ItemLabel(childItem),
                    Kind = LiveSaveEntryKind.DetectedChildFolder,
                    Source = "Bundled manifest",
                    Items = [childItem]
                };

                if (!entries.Any(entry => SameItems(entry.Items, childEntry.Items)))
                {
                    entries.Add(childEntry);
                }
            }
        }

        return entries;
    }

    private static IEnumerable<string> FindChildSaveFolders(string root, ScanContext context)
    {
        context.ThrowIfCancellationRequested();
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!LooksLikeSaveFolder(rootName))
        {
            yield break;
        }

        var count = 0;
        foreach (var child in EnumerateLeafSaveDirectories(root, maxDepth: 2, context))
        {
            context.ThrowIfCancellationRequested();
            yield return child;
            count++;
            if (count >= MaxChildSavesPerFolder)
            {
                context.Report($"Stopped child save discovery after {MaxChildSavesPerFolder:N0} folder(s): {root}");
                yield break;
            }
        }
    }

    private static IEnumerable<string> EnumerateLeafSaveDirectories(string root, int maxDepth, ScanContext context)
    {
        context.ThrowIfCancellationRequested();
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            context.ReportOccasionally($"Could not read save folder: {root}");
            yield break;
        }

        foreach (var child in children)
        {
            context.ThrowIfCancellationRequested();
            var grandChildren = Enumerable.Empty<string>();
            try
            {
                grandChildren = Directory.EnumerateDirectories(child).ToList();
            }
            catch
            {
                // A folder can still be a useful save target even if its children cannot be listed.
            }

            if (maxDepth > 1 && grandChildren.Any())
            {
                foreach (var nested in EnumerateLeafSaveDirectories(child, maxDepth - 1, context))
                {
                    yield return nested;
                }
            }
            else if (DirectoryContainsFiles(child))
            {
                yield return child;
            }
        }
    }

    private static bool DirectoryContainsFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeSaveFolder(string name)
    {
        var normalized = name.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        return normalized.Equals("save", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("saves", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("savegame", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("savegames", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("savedgames", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExpandPath(
        string title,
        ManifestGame manifestGame,
        string path,
        IReadOnlyCollection<GameRoot> roots,
        ScanContext context)
    {
        context.ThrowIfCancellationRequested();
        var installDirs = manifestGame.InstallDir?.Keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList();
        if (installDirs is null || installDirs.Count == 0)
        {
            installDirs = [title];
        }

        var storeIds = StoreIds(manifestGame).ToList();
        if (!RequiresGameRoot(path))
        {
            foreach (var storeId in storeIds.DefaultIfEmpty("*"))
            {
                context.ThrowIfCancellationRequested();
                var expanded = ReplaceCommonPlaceholders(path, storeId);
                if (!HasUnknownPlaceholders(expanded))
                {
                    yield return NormalizePath(expanded);
                }
            }

            yield break;
        }

        foreach (var root in roots)
        {
            foreach (var installDir in installDirs)
            {
                foreach (var storeId in storeIds.DefaultIfEmpty(root.StoreUserId ?? "*"))
                {
                    context.ThrowIfCancellationRequested();
                    var expanded = ReplaceCommonPlaceholders(path, storeId);
                    expanded = expanded
                        .Replace("<root>", root.Path, StringComparison.OrdinalIgnoreCase)
                        .Replace("<game>", installDir, StringComparison.OrdinalIgnoreCase)
                        .Replace("<base>", root.BasePath(installDir), StringComparison.OrdinalIgnoreCase);

                    if (!HasUnknownPlaceholders(expanded))
                    {
                        yield return NormalizePath(expanded);
                    }
                }
            }
        }
    }

    private static string ReplaceCommonPlaceholders(string path, string storeGameId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? Path.Combine(Path.GetPathRoot(home) ?? "C:\\", "Users", "Public");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var winDir = Environment.GetEnvironmentVariable("WINDIR") ?? "C:\\Windows";
        var userName = Environment.UserName;

        return path
            .Replace("<home>", home, StringComparison.OrdinalIgnoreCase)
            .Replace("<winAppData>", appData, StringComparison.OrdinalIgnoreCase)
            .Replace("<winLocalAppData>", localAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("<winLocalAppDataLow>", Path.Combine(home, "AppData", "LocalLow"), StringComparison.OrdinalIgnoreCase)
            .Replace("<winDocuments>", documents, StringComparison.OrdinalIgnoreCase)
            .Replace("<winPublic>", publicFolder, StringComparison.OrdinalIgnoreCase)
            .Replace("<winProgramData>", programData, StringComparison.OrdinalIgnoreCase)
            .Replace("<winDir>", winDir, StringComparison.OrdinalIgnoreCase)
            .Replace("<osUserName>", userName, StringComparison.OrdinalIgnoreCase)
            .Replace("<storeGameId>", storeGameId, StringComparison.OrdinalIgnoreCase)
            .Replace("<storeUserId>", "*", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresGameRoot(string path) =>
        path.Contains("<root>", StringComparison.OrdinalIgnoreCase)
        || path.Contains("<base>", StringComparison.OrdinalIgnoreCase)
        || path.Contains("<game>", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> StoreIds(ManifestGame manifestGame)
    {
        if (manifestGame.Steam?.Id is not null)
        {
            yield return manifestGame.Steam.Id.Value.ToString();
        }

        if (manifestGame.Gog?.Id is not null)
        {
            yield return manifestGame.Gog.Id.Value.ToString();
        }
    }

    private static IEnumerable<string> InstallNames(string title, ManifestGame manifestGame)
    {
        yield return title;

        if (manifestGame.InstallDir is null)
        {
            yield break;
        }

        foreach (var installDir in manifestGame.InstallDir.Keys)
        {
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                yield return installDir;
            }
        }
    }

    private static IEnumerable<string> Glob(string title, string pattern, ScanContext context)
    {
        context.ThrowIfCancellationRequested();
        if (!HasGlob(pattern))
        {
            if (Directory.Exists(pattern) || File.Exists(pattern))
            {
                yield return Path.GetFullPath(pattern);
            }

            yield break;
        }

        var searchRoot = StaticSearchRoot(pattern);
        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
        {
            yield break;
        }

        if (IsOverlyBroadSearchRoot(searchRoot))
        {
            context.Report($"Skipping overly broad search root for {title}: {searchRoot}");
            yield break;
        }

        var regex = GlobRegex(pattern);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((searchRoot, 0));
        var scannedEntries = 0;
        var reportedDepthLimit = false;

        while (pending.Count > 0)
        {
            context.ThrowIfCancellationRequested();
            var (current, depth) = pending.Pop();
            scannedEntries++;
            if (scannedEntries > MaxTraversalEntriesPerPattern)
            {
                context.Report($"Skipping rest of large search root for {title}: {searchRoot}");
                yield break;
            }

            if (scannedEntries % 1000 == 0)
            {
                context.ReportOccasionally($"Scanning {title}: {scannedEntries:N0} entries under {searchRoot}");
            }

            if (regex.IsMatch(NormalizeForMatch(current)))
            {
                yield return current;
            }

            if (depth >= MaxTraversalDepth)
            {
                if (!reportedDepthLimit)
                {
                    context.Report($"Limited deep search under {searchRoot} for {title}.");
                    reportedDepthLimit = true;
                }

                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
            }
            catch
            {
                files = [];
            }

            foreach (var file in files)
            {
                context.ThrowIfCancellationRequested();
                scannedEntries++;
                if (scannedEntries > MaxTraversalEntriesPerPattern)
                {
                    context.Report($"Skipping rest of large search root for {title}: {searchRoot}");
                    yield break;
                }

                if (regex.IsMatch(NormalizeForMatch(file)))
                {
                    yield return file;
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                pending.Push((directory, depth + 1));
            }
        }
    }

    private static bool IsOverlyBroadSearchRoot(string searchRoot)
    {
        var fullPath = Path.GetFullPath(searchRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommonSaveRoot(string searchRoot, IReadOnlyCollection<string> commonSaveRoots)
    {
        var normalized = NormalizeDirectory(searchRoot);
        return commonSaveRoots.Any(root => string.Equals(normalized, NormalizeDirectory(root), StringComparison.OrdinalIgnoreCase));
    }

    private static string StaticSearchRoot(string pattern)
    {
        var firstGlob = pattern.IndexOfAny(['*', '?']);
        if (firstGlob < 0)
        {
            return Directory.Exists(pattern) ? pattern : Path.GetDirectoryName(pattern) ?? "";
        }

        var prefix = pattern[..firstGlob];
        var separator = Math.Max(prefix.LastIndexOf('\\'), prefix.LastIndexOf('/'));
        if (separator < 0)
        {
            return "";
        }

        var root = prefix[..separator];
        return string.IsNullOrWhiteSpace(root) ? "" : root;
    }

    private static Regex GlobRegex(string pattern)
    {
        var normalized = NormalizeForMatch(pattern);
        var regex = Regex.Escape(normalized)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"^{regex}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static bool HasGlob(string path) => path.Contains('*') || path.Contains('?');

    private static bool HasUnknownPlaceholders(string path) => Regex.IsMatch(path, "<[^>]+>");

    private static bool AppliesToWindows(List<ManifestConstraint>? constraints)
    {
        if (constraints is null || constraints.Count == 0)
        {
            return true;
        }

        return constraints.Any(constraint =>
            string.IsNullOrWhiteSpace(constraint.Os)
            || string.Equals(constraint.Os, "windows", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddUnique(List<LiveSaveItem> items, LiveSaveItem item)
    {
        if (!items.Any(existing => string.Equals(NormalizeItemKey(existing), NormalizeItemKey(item), StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(item);
        }
    }

    private static bool SameItems(IReadOnlyCollection<LiveSaveItem> left, IReadOnlyCollection<LiveSaveItem> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftKeys = left.Select(NormalizeItemKey).OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        var rightKeys = right.Select(NormalizeItemKey).OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        return leftKeys.SequenceEqual(rightKeys, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeItemKey(LiveSaveItem item) =>
        item.Kind == LiveSaveItemKind.RegistryKey
            ? $"registry:{item.SourcePath.Replace('\\', '/').TrimEnd('/')}"
            : $"path:{Path.GetFullPath(item.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}";

    private static string ItemLabel(LiveSaveItem item)
    {
        if (item.Kind == LiveSaveItemKind.RegistryKey)
        {
            var normalized = item.SourcePath.TrimEnd('/', '\\').Replace('\\', '/');
            var index = normalized.LastIndexOf('/');
            return index < 0 ? normalized : normalized[(index + 1)..];
        }

        var trimmed = item.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? item.SourcePath : name;
    }

    private static LiveSaveItem CloneItem(LiveSaveItem item) =>
        new()
        {
            SourcePath = item.SourcePath,
            Kind = item.Kind,
            ManifestPath = item.ManifestPath,
            Tags = item.Tags.ToList()
        };

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeForMatch(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeNameKey(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static Dictionary<string, ManifestGame> LoadManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Assets.GameSaveManifest.manifest.yaml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Bundled game save manifest was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Bundled game save manifest could not be opened.");
        using var reader = new StreamReader(stream);
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<Dictionary<string, ManifestGame>>(reader) ?? [];
    }

    private sealed class ScanContext
    {
        private readonly IProgress<OperationStatus>? _progress;
        private readonly CancellationToken _cancellationToken;
        private DateTime _lastOccasionalReportUtc = DateTime.MinValue;

        public ScanContext(IProgress<OperationStatus>? progress, CancellationToken cancellationToken)
        {
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        public void ThrowIfCancellationRequested() => _cancellationToken.ThrowIfCancellationRequested();

        public void Report(string message) => _progress?.Report(new OperationStatus(message));

        public void ReportOccasionally(string message)
        {
            var now = DateTime.UtcNow;
            if (now - _lastOccasionalReportUtc < TimeSpan.FromMilliseconds(350))
            {
                return;
            }

            _lastOccasionalReportUtc = now;
            Report(message);
        }
    }
}

public sealed record ManifestScanResult(
    List<GameEntry> Games,
    int LiveSaveEntries,
    int RegistryEntries,
    int SkippedEntries);

public sealed record GameRoot(string Path, string Store, string? StoreUserId = null)
{
    public string BasePath(string installDir)
    {
        if (string.Equals(Store, "steam", StringComparison.OrdinalIgnoreCase))
        {
            return System.IO.Path.Combine(Path, "steamapps", "common", installDir);
        }

        return System.IO.Path.Combine(Path, installDir);
    }
}

public sealed record InstalledGameIndex(
    List<GameRoot> Roots,
    HashSet<int> SteamAppIds,
    HashSet<string> InstalledFolderNames,
    HashSet<string> GogFolderNames,
    List<string> CommonSaveRoots);

internal static partial class GameRootFinder
{
    public static InstalledGameIndex FindInstalledGames(
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var roots = FindRoots(progress, cancellationToken)
            .DistinctBy(root => $"{root.Store}:{root.Path}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var steamAppIds = new HashSet<int>();
        var installedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gogFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in roots.Where(root => string.Equals(root.Store, "steam", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var app in SteamApps(steamRoot.Path, cancellationToken))
            {
                steamAppIds.Add(app.AppId);
                AddName(installedFolderNames, app.InstallDir);
            }
        }

        progress?.Report(new OperationStatus($"Found {steamAppIds.Count:N0} installed Steam game(s)."));

        foreach (var gogRoot in roots.Where(root => string.Equals(root.Store, "gog", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var folder in ImmediateChildFolderNames(gogRoot.Path, cancellationToken))
            {
                AddName(gogFolderNames, folder);
                AddName(installedFolderNames, folder);
            }
        }

        progress?.Report(new OperationStatus($"Found {gogFolderNames.Count:N0} installed GOG folder(s)."));

        var commonSaveRoots = CommonSaveRoots()
            .Where(Directory.Exists)
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress?.Report(new OperationStatus($"Found {commonSaveRoots.Count:N0} common save root(s)."));

        return new InstalledGameIndex(roots, steamAppIds, installedFolderNames, gogFolderNames, commonSaveRoots);
    }

    public static IEnumerable<GameRoot> FindRoots(
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var root in SteamRoots(progress, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GameRoot(root, "steam");
        }

        foreach (var root in GogRoots(progress, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GameRoot(root, "gog");
        }
    }

    private static IEnumerable<(int AppId, string InstallDir)> SteamApps(string steamRoot, CancellationToken cancellationToken)
    {
        var steamApps = Path.Combine(steamRoot, "steamapps");
        if (!Directory.Exists(steamApps))
        {
            yield break;
        }

        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = File.ReadAllText(manifest);
            }
            catch
            {
                continue;
            }

            var appIdMatch = SteamAppIdRegex().Match(text);
            var installDirMatch = SteamInstallDirRegex().Match(text);
            if (appIdMatch.Success
                && installDirMatch.Success
                && int.TryParse(appIdMatch.Groups["id"].Value, out var appId))
            {
                yield return (appId, installDirMatch.Groups["dir"].Value);
            }
        }
    }

    private static IEnumerable<string> ImmediateChildFolderNames(string root, CancellationToken cancellationToken)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> CommonSaveRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? "";

        if (!string.IsNullOrWhiteSpace(documents))
        {
            yield return documents;
            yield return Path.Combine(documents, "My Games");
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, "Saved Games");
            yield return Path.Combine(home, "AppData", "LocalLow");
        }

        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return appData;
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return localAppData;
        }

        if (!string.IsNullOrWhiteSpace(programData))
        {
            yield return programData;
        }

        if (!string.IsNullOrWhiteSpace(publicFolder))
        {
            yield return Path.Combine(publicFolder, "Documents");
        }
    }

    private static void AddName(HashSet<string> names, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            names.Add(NormalizeNameKey(value));
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeNameKey(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static IEnumerable<string> SteamRoots(IProgress<OperationStatus>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new OperationStatus("Checking Steam roots..."));
        var candidates = new List<string>();
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "Steam"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Steam"));
        }

        foreach (var root in candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationStatus($"Found Steam root: {root}"));
            yield return root;

            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (var libraryRoot in ParseSteamLibraries(libraryFile, cancellationToken))
            {
                progress?.Report(new OperationStatus($"Found Steam library: {libraryRoot}"));
                yield return libraryRoot;
            }
        }
    }

    private static IEnumerable<string> ParseSteamLibraries(string libraryFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(libraryFile))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(libraryFile);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in SteamLibraryPathRegex().Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> GogRoots(IProgress<OperationStatus>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new OperationStatus("Checking GOG roots..."));
        var candidates = new[]
        {
            @"C:\GOG Games",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GOG Galaxy", "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy", "Games")
        };

        foreach (var root in candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationStatus($"Found GOG root: {root}"));
            yield return root;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathRegex();

    [GeneratedRegex("\"appid\"\\s+\"(?<id>\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamAppIdRegex();

    [GeneratedRegex("\"installdir\"\\s+\"(?<dir>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamInstallDirRegex();
}

internal sealed class ManifestGame
{
    [YamlMember(Alias = "files")]
    public Dictionary<string, ManifestFileEntry>? Files { get; set; }

    [YamlMember(Alias = "installDir")]
    public Dictionary<string, object>? InstallDir { get; set; }

    [YamlMember(Alias = "registry")]
    public Dictionary<string, ManifestRegistryEntry>? Registry { get; set; }

    [YamlMember(Alias = "steam")]
    public ManifestStore? Steam { get; set; }

    [YamlMember(Alias = "gog")]
    public ManifestStore? Gog { get; set; }

    [YamlMember(Alias = "alias")]
    public string? Alias { get; set; }
}

internal sealed class ManifestFileEntry
{
    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "when")]
    public List<ManifestConstraint>? When { get; set; }
}

internal sealed class ManifestRegistryEntry
{
    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "when")]
    public List<ManifestConstraint>? When { get; set; }
}

internal sealed class ManifestConstraint
{
    [YamlMember(Alias = "os")]
    public string? Os { get; set; }

    [YamlMember(Alias = "store")]
    public string? Store { get; set; }
}

internal sealed class ManifestStore
{
    [YamlMember(Alias = "id")]
    public int? Id { get; set; }
}
