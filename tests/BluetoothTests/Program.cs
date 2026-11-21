using System.Collections.Generic;
using VajraPulse.Services;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

Check(BluetoothBatteryService.ConvertBatteryPercent((byte)73) == 73, "Byte battery property converts exactly");
Check(BluetoothBatteryService.ConvertBatteryPercent((ushort)42) == 42, "Unsigned battery property converts exactly");
Check(BluetoothBatteryService.ConvertBatteryPercent(" 9 ") == 9, "Numeric string battery property converts invariantly");
Check(BluetoothBatteryService.ConvertBatteryPercent(61.0) == 61, "Whole floating battery property converts exactly");
Check(BluetoothBatteryService.ConvertBatteryPercent(-1) == null, "Negative battery property remains unknown");
Check(BluetoothBatteryService.ConvertBatteryPercent(101) == null, "Over-range battery property remains unknown");
Check(BluetoothBatteryService.ConvertBatteryPercent((byte)255) == null, "Windows unknown sentinel is not fabricated as a level");
Check(BluetoothBatteryService.ConvertBatteryPercent(22.5) == null, "Fractional battery property remains unknown");
Check(BluetoothBatteryService.ConvertBatteryPercent(true) == null, "Boolean battery property remains unknown");
Check(BluetoothBatteryService.ConvertBatteryPercent("85%") == null, "Decorated battery text remains unknown");

string dirtyName = " \u0000  Vajra\tHeadphones\r\n‮ ";
Check(BluetoothBatteryService.SanitizeName(dirtyName) == "Vajra Headphones", "Device names remove controls and collapse whitespace");
Check(BluetoothBatteryService.SanitizeName("\u0000‍") == "Bluetooth device", "Empty sanitized names receive a neutral fallback");
Check(BluetoothBatteryService.SanitizeName(new string('x', 100)).Length == 80, "Device names are length bounded");

DateTimeOffset seen = new(2026, 11, 18, 10, 30, 0, TimeSpan.Zero);
var firstProperties = new Dictionary<string, object>
{
    [BluetoothBatteryService.IsConnectedProperty] = false
};
var duplicateProperties = new Dictionary<string, object>
{
    [BluetoothBatteryService.BatteryPercentProperty] = (byte)64,
    [BluetoothBatteryService.IsConnectedProperty] = true
};
var candidates = new List<BluetoothBatteryCandidate>
{
    new("stable-id", "\u0000", firstProperties),
    new("STABLE-ID", "Travel Buds", duplicateProperties),
    new("unknown-level", "Keyboard", new Dictionary<string, object>
    {
        [BluetoothBatteryService.BatteryPercentProperty] = (byte)255
    }),
    new("   ", "Invalid id", new Dictionary<string, object>())
};
var snapshot = BluetoothBatteryService.CreateSnapshot(candidates, seen);
Check(snapshot.IsSupported, "Snapshot carries supported state");
Check(snapshot.Devices.Count == 2, "Duplicate stable IDs merge case-insensitively and empty IDs are discarded");
BluetoothBatteryDevice merged = snapshot.Devices.Single(device => device.Id.Equals("stable-id", StringComparison.OrdinalIgnoreCase));
Check(merged.Name == "Travel Buds", "Duplicate merge prefers a real sanitized name");
Check(merged.Percent == 64 && merged.IsConnected, "Duplicate merge retains exposed battery and connection state");
Check(merged.LastSeen == seen, "Last-seen timestamp is the observation time");
Check(snapshot.Devices.Single(device => device.Id == "unknown-level").Percent == null, "Unknown levels stay nullable in snapshots");

var many = Enumerable.Range(0, 50).Select(index => new BluetoothBatteryCandidate(
    $"id-{index:D2}", $"Device {index:D2}", new Dictionary<string, object>
    {
        [BluetoothBatteryService.BatteryPercentProperty] = index
    }));
var bounded = BluetoothBatteryService.CreateSnapshot(many, seen);
Check(bounded.Devices.Count == BluetoothBatteryService.MaximumDevices, "Snapshot device list is capped at 30");
Check(bounded.Devices.All(device => device.Percent is >= 0 and <= 100), "Every exposed battery level is within 0..100");
Check(bounded.Devices.Select(device => device.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == bounded.Devices.Count,
    "Bounded snapshot contains unique stable IDs");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await new BluetoothBatteryService().PollAsync(cancelled.Token);
    throw new InvalidOperationException("Pre-cancelled poll should not complete normally");
}
catch (OperationCanceledException)
{
    Console.WriteLine("PASS Poll honors pre-cancelled tokens without querying hardware");
    passed++;
}

try
{
    _ = new BluetoothBatteryService(TimeSpan.FromSeconds(31));
    throw new InvalidOperationException("Timeout above 30 seconds should be rejected");
}
catch (ArgumentOutOfRangeException)
{
    Console.WriteLine("PASS Poll timeout is capped at 30 seconds");
    passed++;
}

Console.WriteLine($"{passed} Bluetooth battery checks passed. No real devices were queried.");
return 0;
