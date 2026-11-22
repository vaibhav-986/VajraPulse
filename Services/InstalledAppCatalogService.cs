using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;

namespace VajraPulse.Services;

public sealed record InstalledAppCatalogEntry(
    string Id,
    string DisplayName,
    AppLaunchTargetKind TargetKind,
    string Target,
    string SearchText,
    string SourceLabel);

public sealed record InstalledAppSourceResult(
    IReadOnlyList<InstalledAppCatalogEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record InstalledAppCatalogResult(
    IReadOnlyList<InstalledAppCatalogEntry> Entries,
    IReadOnlyList<string> Warnings);

public interface IInstalledAppCatalogSource
{
    Task<InstalledAppSourceResult> EnumerateAsync(CancellationToken cancellationToken);
}

public sealed class InstalledAppCatalogService
{
    public const int MaximumCatalogEntries = 2500;
    public const int MaximumSearchResults = 100;
    private readonly IReadOnlyList<IInstalledAppCatalogSource> _sources;

    public InstalledAppCatalogService(IEnumerable<IInstalledAppCatalogSource>? sources = null)
    {
        _sources = sources?.ToArray() ?? new IInstalledAppCatalogSource[]
        {
            new StartMenuAppSource(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Desktop app"),
            new StartMenuAppSource(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Desktop app"),
            new PackagedAppSource()
        };
    }

    public async Task<InstalledAppCatalogResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        Task<InstalledAppSourceResult>[] tasks = _sources.Select(source => LoadSource(source, cancellationToken)).ToArray();
        InstalledAppSourceResult[] results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new Dictionary<string, InstalledAppCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (InstalledAppSourceResult result in results)
        {
            warnings.AddRange(result.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)));
            foreach (InstalledAppCatalogEntry entry in result.Entries)
            {
                if (!IsValidEntry(entry) || entries.ContainsKey(entry.Id)) continue;
                entries.Add(entry.Id, entry with
                {
                    DisplayName = Bound(entry.DisplayName, 100),
                    Target = Bound(entry.Target, 4096),
                    SearchText = Bound(entry.SearchText, 512)
                });
            }
        }

        InstalledAppCatalogEntry[] sorted = entries.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TargetKind)
            .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCatalogEntries)
            .ToArray();
        return new(Array.AsReadOnly(sorted), Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).ToArray()));
    }

    public static IReadOnlyList<InstalledAppCatalogEntry> Filter(
        IReadOnlyList<InstalledAppCatalogEntry> entries,
        string? query,
        int maximumResults = MaximumSearchResults)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int limit = Math.Clamp(maximumResults, 1, MaximumSearchResults);
        string[] terms = (query ?? "").Trim()[..Math.Min((query ?? "").Trim().Length, 256)]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();
        return entries.Where(entry => terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToArray();
    }

    private static async Task<InstalledAppSourceResult> LoadSource(IInstalledAppCatalogSource source, CancellationToken token)
    {
        try { return await source.EnumerateAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or COMException or InvalidOperationException or NotSupportedException)
        {
            return new(Array.Empty<InstalledAppCatalogEntry>(), new[] { "One installed-app source was unavailable." });
        }
    }

    private static bool IsValidEntry(InstalledAppCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Id) && entry.Id.Length <= 4600
        && !string.IsNullOrWhiteSpace(entry.DisplayName)
        && !string.IsNullOrWhiteSpace(entry.Target)
        && Enum.IsDefined(entry.TargetKind);

    private static string Bound(string? value, int maximum)
    {
        value = value?.Trim() ?? "";
        return value.Length <= maximum ? value : value[..maximum];
    }
}

internal sealed class StartMenuAppSource : IInstalledAppCatalogSource
{
    private const int MaximumShortcuts = 2000;
    private const int MaximumDepth = 16;
    private readonly string _root;
    private readonly string _sourceLabel;

    public StartMenuAppSource(string root, string sourceLabel)
    {
        _root = root;
        _sourceLabel = sourceLabel;
    }

    public Task<InstalledAppSourceResult> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Enumerate(cancellationToken), cancellationToken);

    private InstalledAppSourceResult Enumerate(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            return new(Array.Empty<InstalledAppCatalogEntry>(), Array.Empty<string>());

        var entries = new List<InstalledAppCatalogEntry>();
        var warnings = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_root, 0));
        while (pending.Count > 0 && entries.Count < MaximumShortcuts)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    if (entries.Count >= MaximumShortcuts) break;
                    string fullPath = Path.GetFullPath(file);
                    if (fullPath.StartsWith("\\\\", StringComparison.Ordinal)) continue;
                    string name = Path.GetFileNameWithoutExtension(fullPath).Trim();
                    if (name.Length == 0) continue;
                    string relative = Path.GetRelativePath(_root, fullPath);
                    entries.Add(new(
                        "shortcut:" + fullPath,
                        name.Length <= 100 ? name : name[..100],
                        AppLaunchTargetKind.StartMenuShortcut,
                        fullPath,
                        name + " " + Path.GetDirectoryName(relative),
                        _sourceLabel));
                }
                if (depth >= MaximumDepth) continue;
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push((child, depth + 1));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add("Part of the Start Menu could not be read.");
            }
        }
        return new(Array.AsReadOnly(entries.ToArray()), Array.AsReadOnly(warnings.Distinct().ToArray()));
    }
}

internal sealed class PackagedAppSource : IInstalledAppCatalogSource
{
    public async Task<InstalledAppSourceResult> EnumerateAsync(CancellationToken cancellationToken)
    {
        var entries = new List<InstalledAppCatalogEntry>();
        var warnings = new List<string>();
        Package[] packages;
        try { packages = new PackageManager().FindPackagesForUser("").Take(2000).ToArray(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return new(Array.Empty<InstalledAppCatalogEntry>(), new[] { "Windows app entries were unavailable." });
        }

        foreach (Package package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= InstalledAppCatalogService.MaximumCatalogEntries) break;
            try
            {
                IReadOnlyList<AppListEntry> appEntries = await package.GetAppListEntriesAsync().AsTask(cancellationToken);
                foreach (AppListEntry app in appEntries)
                {
                    string id = app.AppUserModelId?.Trim() ?? "";
                    if (id.Length == 0 || id.Length > 512) continue;
                    string name = app.DisplayInfo?.DisplayName?.Trim() ?? "";
                    if (name.Length == 0) name = package.DisplayName?.Trim() ?? package.Id.Name;
                    if (name.Length == 0) continue;
                    entries.Add(new(
                        "aumid:" + id,
                        name,
                        AppLaunchTargetKind.AppUserModelId,
                        id,
                        name + " " + package.DisplayName + " " + package.Id.Name,
                        "Windows app"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                warnings.Add("Some Windows app entries could not be read.");
            }
        }
        return new(Array.AsReadOnly(entries.ToArray()), Array.AsReadOnly(warnings.Distinct().ToArray()));
    }
}
