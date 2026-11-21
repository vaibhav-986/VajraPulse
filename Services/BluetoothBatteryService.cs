using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace VajraPulse.Services;

public sealed record BluetoothBatteryDevice(
    string Id,
    string Name,
    int? Percent,
    bool IsConnected,
    DateTimeOffset LastSeen);

public sealed record BluetoothBatterySnapshot(
    bool IsSupported,
    string Status,
    IReadOnlyList<BluetoothBatteryDevice> Devices);

public sealed class BluetoothBatteryService
{
    public const int MaximumDevices = 30;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    internal const string BatteryPercentProperty = "System.Devices.Power.BatteryLifePercent";
    internal const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    private static readonly string[] RequestedProperties =
    {
        BatteryPercentProperty,
        IsConnectedProperty
    };

    private readonly TimeSpan _timeout;

    public BluetoothBatteryService(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "The Bluetooth query timeout must be between zero and 30 seconds.");
    }

    public async Task<BluetoothBatterySnapshot> PollAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            Task<QueryResult> classic = QueryAsync(
                () => BluetoothDevice.GetDeviceSelectorFromPairingState(true), timeout.Token);
            Task<QueryResult> lowEnergy = QueryAsync(
                () => BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), timeout.Token);
            QueryResult[] results = await Task.WhenAll(classic, lowEnergy);

            var candidates = results.SelectMany(result => result.Devices);
            int successfulQueries = results.Count(result => result.Succeeded);
            if (successfulQueries == 0)
            {
                bool denied = results.Any(result => result.AccessDenied);
                return Empty(false, denied
                    ? "Windows denied access to paired Bluetooth device information."
                    : "Bluetooth device enumeration is not available on this Windows system.");
            }

            BluetoothBatterySnapshot snapshot = CreateSnapshot(candidates, DateTimeOffset.UtcNow, true, string.Empty);
            int levels = snapshot.Devices.Count(device => device.Percent.HasValue);
            string status = snapshot.Devices.Count == 0
                ? "No paired Bluetooth devices were reported by Windows."
                : levels == 0
                    ? $"Showing {snapshot.Devices.Count} paired Bluetooth device(s); Windows did not expose standard battery levels for them."
                    : $"Showing {snapshot.Devices.Count} paired Bluetooth device(s); battery level is available for {levels}.";
            if (successfulQueries < results.Length)
                status += " Some Bluetooth device types could not be queried.";
            return snapshot with { Status = status };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty(false, "The Windows Bluetooth device query timed out.");
        }
    }

    private static async Task<QueryResult> QueryAsync(Func<string> selectorFactory, CancellationToken cancellationToken)
    {
        try
        {
            string selector = selectorFactory();
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken);
            var candidates = devices.Select(device => new BluetoothBatteryCandidate(
                device.Id, device.Name, device.Properties)).ToArray();
            return new(true, false, candidates);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return new(false, ex is UnauthorizedAccessException, Array.Empty<BluetoothBatteryCandidate>());
        }
    }

    private static BluetoothBatterySnapshot Empty(bool isSupported, string status) =>
        new(isSupported, status, Array.Empty<BluetoothBatteryDevice>());

    internal static BluetoothBatterySnapshot CreateSnapshot(
        IEnumerable<BluetoothBatteryCandidate> candidates,
        DateTimeOffset observedAt,
        bool isSupported = true,
        string status = "Test snapshot")
    {
        var devices = new Dictionary<string, BluetoothBatteryDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (BluetoothBatteryCandidate candidate in candidates)
        {
            string id = candidate.Id?.Trim() ?? string.Empty;
            if (id.Length == 0) continue;

            string name = SanitizeName(candidate.Name);
            int? percent = candidate.Properties.TryGetValue(BatteryPercentProperty, out object? battery)
                ? ConvertBatteryPercent(battery) : null;
            bool connected = candidate.Properties.TryGetValue(IsConnectedProperty, out object? connectedValue)
                && connectedValue is bool value && value;
            var current = new BluetoothBatteryDevice(id, name, percent, connected, observedAt);

            if (devices.TryGetValue(id, out BluetoothBatteryDevice? existing))
            {
                devices[id] = existing with
                {
                    Name = existing.Name == "Bluetooth device" && name != "Bluetooth device" ? name : existing.Name,
                    Percent = existing.Percent ?? current.Percent,
                    IsConnected = existing.IsConnected || current.IsConnected,
                    LastSeen = observedAt
                };
            }
            else devices.Add(id, current);
        }

        BluetoothBatteryDevice[] bounded = devices.Values
            .OrderByDescending(device => device.IsConnected)
            .ThenByDescending(device => device.Percent.HasValue)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumDevices)
            .ToArray();
        return new(isSupported, status, Array.AsReadOnly(bounded));
    }

    internal static int? ConvertBatteryPercent(object? value)
    {
        if (value is null or bool or char || value.GetType().IsEnum) return null;

        if (value is string text)
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed is >= 0 and <= 100 ? parsed : null;

        try
        {
            decimal number = value switch
            {
                byte item => item,
                sbyte item => item,
                short item => item,
                ushort item => item,
                int item => item,
                uint item => item,
                long item => item,
                ulong item => item,
                float item when float.IsFinite(item) => (decimal)item,
                double item when double.IsFinite(item) => (decimal)item,
                decimal item => item,
                _ => decimal.MinValue
            };
            return number is >= 0 and <= 100 && decimal.Truncate(number) == number ? (int)number : null;
        }
        catch (OverflowException) { return null; }
    }

    internal static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Bluetooth device";
        var clean = new StringBuilder(Math.Min(name.Length, 80));
        bool pendingSpace = false;
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = clean.Length > 0;
                continue;
            }
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
                continue;
            if (pendingSpace && clean.Length < 80) clean.Append(' ');
            pendingSpace = false;
            string text = rune.ToString();
            if (clean.Length + text.Length > 80) break;
            clean.Append(text);
        }
        return clean.Length == 0 ? "Bluetooth device" : clean.ToString();
    }

    private sealed record QueryResult(
        bool Succeeded,
        bool AccessDenied,
        IReadOnlyList<BluetoothBatteryCandidate> Devices);
}

internal sealed record BluetoothBatteryCandidate(
    string? Id,
    string? Name,
    IReadOnlyDictionary<string, object> Properties);
