using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;

namespace VajraPulse.Services;

public enum AppLaunchResultCode
{
    Success,
    SlotEmpty,
    TargetMissing,
    InvalidPath,
    UnsupportedTarget,
    ShortcutTargetUnavailable,
    AccessDenied,
    LaunchFailed
}

public sealed record AppLaunchResult(AppLaunchResultCode Code, string Message, string? ResolvedTarget = null)
{
    public bool IsSuccess => Code == AppLaunchResultCode.Success;
}

public interface IPackagedAppActivator
{
    Task<AppLaunchResult> LaunchAsync(string appUserModelId, CancellationToken cancellationToken);
}

public sealed class AppLauncherService
{
    public const int MaximumPathLength = 4096;
    public const int MaximumDisplayNameLength = 80;

    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly IPackagedAppActivator _packagedAppActivator;

    public AppLauncherService(
        Func<ProcessStartInfo, Process?>? processStarter = null,
        IPackagedAppActivator? packagedAppActivator = null)
    {
        _processStarter = processStarter ?? Process.Start;
        _packagedAppActivator = packagedAppActivator ?? new WindowsPackagedAppActivator();
    }

    public AppLaunchResult Validate(AppLaunchSlotSettings? slot)
    {
        if (slot == null) return new(AppLaunchResultCode.SlotEmpty, "This slot is not configured.");
        return slot.TargetKind switch
        {
            AppLaunchTargetKind.LocalPath => Validate(slot.TargetPath),
            AppLaunchTargetKind.StartMenuShortcut => ValidateStartMenuShortcut(slot.TargetPath),
            AppLaunchTargetKind.AppUserModelId => ValidateAppUserModelId(slot.AppUserModelId),
            _ => new(AppLaunchResultCode.UnsupportedTarget, "This app reference is not supported.")
        };
    }

    public AppLaunchResult Validate(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return new(AppLaunchResultCode.SlotEmpty, "This slot is not configured.");
        string path = targetPath.Trim();
        if (path.Length > MaximumPathLength || !Path.IsPathFullyQualified(path) || IsNetworkPath(path))
            return new(AppLaunchResultCode.InvalidPath, "Choose an application stored on this computer.");
        if (!File.Exists(path)) return new(AppLaunchResultCode.TargetMissing, "The selected application is no longer available.");

        string extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(AppLaunchResultCode.Success, "Ready", path);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return new(AppLaunchResultCode.UnsupportedTarget, "Only .exe applications and .lnk shortcuts are supported.");

        string? shortcutTarget = ResolveShortcut(path);
        if (string.IsNullOrWhiteSpace(shortcutTarget)
            || shortcutTarget.Length > MaximumPathLength
            || !Path.IsPathFullyQualified(shortcutTarget)
            || IsNetworkPath(shortcutTarget)
            || !Path.GetExtension(shortcutTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(shortcutTarget))
            return new(AppLaunchResultCode.ShortcutTargetUnavailable, "The shortcut does not resolve to a local application.");

        return new(AppLaunchResultCode.Success, "Ready", shortcutTarget);
    }

    public AppLaunchResult Launch(string? targetPath)
    {
        AppLaunchResult validation = Validate(targetPath);
        if (!validation.IsSuccess) return validation;
        try
        {
            using Process? process = _processStarter(CreateStartInfo(targetPath!.Trim()));
            return new(AppLaunchResultCode.Success, "Application opened.", validation.ResolvedTarget);
        }
        catch (UnauthorizedAccessException)
        {
            return new(AppLaunchResultCode.AccessDenied, "Windows denied permission to open this application.", validation.ResolvedTarget);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return new(AppLaunchResultCode.AccessDenied, "Windows denied permission to open this application.", validation.ResolvedTarget);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(AppLaunchResultCode.LaunchFailed, "Windows could not open this application.", validation.ResolvedTarget);
        }
    }

    public async Task<AppLaunchResult> LaunchAsync(AppLaunchSlotSettings? slot, CancellationToken cancellationToken = default)
    {
        AppLaunchResult validation = Validate(slot);
        if (!validation.IsSuccess) return validation;
        if (slot!.TargetKind == AppLaunchTargetKind.AppUserModelId)
            return await _packagedAppActivator.LaunchAsync(slot.AppUserModelId, cancellationToken);
        return LaunchPath(slot.TargetPath, slot.TargetKind == AppLaunchTargetKind.StartMenuShortcut);
    }

    internal static ProcessStartInfo CreateStartInfo(string path)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = true };
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            start.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
        return start;
    }

    private AppLaunchResult LaunchPath(string path, bool shortcut)
    {
        AppLaunchResult validation = shortcut ? ValidateStartMenuShortcut(path) : Validate(path);
        if (!validation.IsSuccess) return validation;
        try
        {
            using Process? process = _processStarter(CreateStartInfo(path));
            return new(AppLaunchResultCode.Success, "Application opened.", path);
        }
        catch (UnauthorizedAccessException)
        {
            return new(AppLaunchResultCode.AccessDenied, "Windows denied permission to open this application.", path);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return new(AppLaunchResultCode.AccessDenied, "Windows denied permission to open this application.", path);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(AppLaunchResultCode.LaunchFailed, "Windows could not open this application.", path);
        }
    }

    private static AppLaunchResult ValidateStartMenuShortcut(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(AppLaunchResultCode.SlotEmpty, "This slot is not configured.");
        string value = path.Trim();
        if (value.Length > MaximumPathLength || !Path.IsPathFullyQualified(value) || IsNetworkPath(value))
            return new(AppLaunchResultCode.InvalidPath, "This Start Menu shortcut is not local.");
        if (!Path.GetExtension(value).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return new(AppLaunchResultCode.UnsupportedTarget, "This catalog entry is not a Start Menu shortcut.");
        return File.Exists(value)
            ? new(AppLaunchResultCode.Success, "Ready", value)
            : new(AppLaunchResultCode.TargetMissing, "This Start Menu shortcut is no longer installed.");
    }

    private static AppLaunchResult ValidateAppUserModelId(string? appId)
    {
        appId = appId?.Trim();
        if (string.IsNullOrWhiteSpace(appId)) return new(AppLaunchResultCode.SlotEmpty, "This slot is not configured.");
        if (appId.Length > 512 || appId.Any(char.IsControl) || !appId.Contains('!'))
            return new(AppLaunchResultCode.UnsupportedTarget, "This Windows app reference is invalid.");
        return new(AppLaunchResultCode.Success, "Ready", appId);
    }

    public static string SuggestedDisplayName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path)?.Trim() ?? "Application";
        return name.Length <= MaximumDisplayNameLength ? name : name[..MaximumDisplayNameLength];
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? ResolveShortcut(string path)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var builder = new StringBuilder(MaximumPathLength);
            link.GetPath(builder, builder.Capacity, IntPtr.Zero, 4);
            return builder.ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}

internal sealed class WindowsPackagedAppActivator : IPackagedAppActivator
{
    public async Task<AppLaunchResult> LaunchAsync(string appUserModelId, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser("").Take(2000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<AppListEntry> entries;
                try { entries = await package.GetAppListEntriesAsync().AsTask(cancellationToken); }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { continue; }
                AppListEntry? match = entries.FirstOrDefault(item => string.Equals(item.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                bool launched = await match.LaunchAsync().AsTask(cancellationToken);
                return launched
                    ? new(AppLaunchResultCode.Success, "Application opened.", appUserModelId)
                    : new(AppLaunchResultCode.LaunchFailed, "Windows rejected the app activation request.", appUserModelId);
            }
            return new(AppLaunchResultCode.TargetMissing, "This Windows app is no longer installed.", appUserModelId);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException)
        {
            return new(AppLaunchResultCode.AccessDenied, "Windows denied permission to open this app.", appUserModelId);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            return new(AppLaunchResultCode.LaunchFailed, "Windows could not open this app.", appUserModelId);
        }
    }
}
