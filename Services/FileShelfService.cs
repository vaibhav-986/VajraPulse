using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace VajraPulse.Services;

public sealed record ShelfFile(string Path, string Name, bool IsDirectory)
{
    public bool Exists => IsDirectory ? Directory.Exists(Path) : File.Exists(Path);
    public string Detail => !Exists ? "File no longer available" : IsDirectory ? "Folder · drag to another app" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() + " · drag to another app";
}

public sealed class FileShelfService
{
    public const int Capacity = 30;
    public ObservableCollection<ShelfFile> Files { get; } = new();
    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string value in paths.Take(100))
        {
            if (Files.Count >= Capacity) break;
            string? path = Normalize(value);
            if (path == null || Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            bool directory = Directory.Exists(path);
            if (!directory && !File.Exists(path)) continue;
            Files.Add(new(path, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), directory));
            added++;
        }
        return added;
    }
    public void Remove(ShelfFile file) => Files.Remove(file);
    public string[] SavedPaths() => Files.Select(f => f.Path).ToArray();
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try
        {
            string path = System.IO.Path.GetFullPath(value);
            return path.StartsWith("\\\\", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(path) ? null : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
