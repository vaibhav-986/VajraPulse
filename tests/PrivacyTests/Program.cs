using System;
using System.Collections.Generic;
using System.Linq;
using VajraPulse.Services;

var tests = new (string Name, Action Run)[]
{
    ("FILETIME QWORD parsing", TestFileTimeParsing),
    ("Malformed FILETIME values", TestMalformedFileTimes),
    ("Active marker semantics", TestActiveSemantics),
    ("Closed and stale markers", TestClosedAndStaleSemantics),
    ("Non-packaged identity parsing", TestNonPackagedIdentity),
    ("Packaged and unknown identity parsing", TestOtherIdentities),
    ("Name sanitizing", TestNameSanitizing),
    ("Settings URI helpers", TestSettingsUris),
    ("Bounded read smoke test", TestReadSmoke)
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void TestFileTimeParsing()
{
    var expected = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    long raw = expected.UtcDateTime.ToFileTimeUtc();

    PrivacyUsageService.FileTimeValue qword = PrivacyUsageService.ParseFileTimeValue(raw);
    True(qword.IsValid, "QWORD is valid");
    Equal(expected, qword.Timestamp, "QWORD timestamp");

    PrivacyUsageService.FileTimeValue bytes = PrivacyUsageService.ParseFileTimeValue(BitConverter.GetBytes(raw));
    Equal(expected, bytes.Timestamp, "little-endian byte timestamp");

    PrivacyUsageService.FileTimeValue zero = PrivacyUsageService.ParseFileTimeValue(0L);
    True(zero.IsValid && zero.IsExplicitZero && zero.Timestamp == null, "zero is the explicit active sentinel");
}

static void TestMalformedFileTimes()
{
    object?[] malformed = { null, "1337", -1L, new byte[7], decimal.One, ulong.MaxValue };
    foreach (object? value in malformed)
    {
        PrivacyUsageService.FileTimeValue parsed = PrivacyUsageService.ParseFileTimeValue(value);
        False(parsed.IsValid, "malformed value is invalid");
        Equal<DateTimeOffset?>(null, parsed.Timestamp, "malformed timestamp is absent");
    }
}

static void TestActiveSemantics()
{
    var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    PrivacyUsageService.FileTimeValue start = Timestamp(now.AddMinutes(-5));
    True(PrivacyUsageService.IsCurrentUsage(start, PrivacyUsageService.FileTimeValue.Zero, now), "explicit zero stop is active");

    PrivacyUsageService.FileTimeValue priorStop = Timestamp(now.AddMinutes(-10));
    True(PrivacyUsageService.IsCurrentUsage(start, priorStop, now), "a new start after the previous stop is active");
}

static void TestClosedAndStaleSemantics()
{
    var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    PrivacyUsageService.FileTimeValue start = Timestamp(now.AddMinutes(-5));
    False(PrivacyUsageService.IsCurrentUsage(start, Timestamp(now.AddMinutes(-1)), now), "stop after start is closed");
    False(PrivacyUsageService.IsCurrentUsage(start, PrivacyUsageService.FileTimeValue.Missing, now), "missing stop is not asserted active");
    False(PrivacyUsageService.IsCurrentUsage(Timestamp(now - PrivacyUsageService.MaximumUnclosedUsageAge - TimeSpan.FromSeconds(1)), PrivacyUsageService.FileTimeValue.Zero, now), "old unclosed record is stale");
    False(PrivacyUsageService.IsCurrentUsage(Timestamp(now.AddMinutes(6)), PrivacyUsageService.FileTimeValue.Zero, now), "future start is not active");
}

static void TestNonPackagedIdentity()
{
    (string? name, string? path) = PrivacyUsageService.ParseIdentity(new[] { "NonPackaged", "C:#Program Files#Meeting App#meeting.exe" });
    Equal("meeting", name, "non-packaged executable name");
    Equal(@"C:\Program Files\Meeting App\meeting.exe", path, "decoded non-packaged path");

    (name, path) = PrivacyUsageService.ParseIdentity(new[] { "_NonPackaged", "D:#Tools#camera-tool.exe" });
    Equal("camera-tool", name, "underscore container executable name");
    Equal(@"D:\Tools\camera-tool.exe", path, "underscore container path");

    (name, path) = PrivacyUsageService.ParseIdentity(new[] { "NonPackaged", "relative#tool.exe" });
    Equal<string?>(null, name, "relative identity has no fabricated name");
    Equal<string?>(null, path, "relative identity has no app path");
}

static void TestOtherIdentities()
{
    (string? name, string? path) = PrivacyUsageService.ParseIdentity(new[] { "Contoso.Meeting_abc123" });
    Equal("Contoso.Meeting_abc123", name, "package family identifier is preserved");
    Equal<string?>(null, path, "packaged identity does not invent an executable path");

    (name, path) = PrivacyUsageService.ParseIdentity(Array.Empty<string>());
    Equal<string?>(null, name, "aggregate identity is unknown");
    Equal<string?>(null, path, "aggregate path is unknown");
}

static void TestNameSanitizing()
{
    Equal("BadName", PrivacyUsageService.SanitizeName("  Bad\0Name\r\n "), "control characters are removed");
    Equal<string?>(null, PrivacyUsageService.SanitizeName("\0\r\n"), "empty sanitized names remain unknown");
    string longName = new('a', 500);
    Equal(160, PrivacyUsageService.SanitizeName(longName)?.Length, "names are bounded");
}

static void TestSettingsUris()
{
    Equal("ms-settings:privacy-microphone", PrivacyUsageService.GetSettingsUri(PrivacyDeviceKind.Microphone), "microphone URI");
    Equal("ms-settings:privacy-webcam", PrivacyUsageService.GetSettingsUri(PrivacyDeviceKind.Camera), "camera URI");
}

static void TestReadSmoke()
{
    using var service = new PrivacyUsageService();
    PrivacyUsageSnapshot snapshot = service.Read();
    True(!string.IsNullOrWhiteSpace(snapshot.Status), "read always reports a status");
    True(snapshot.Usages.Count <= PrivacyUsageService.MaximumRegistryEntries, "result count is bounded");
    True(snapshot.Usages.All(usage => usage.AppName == null || !usage.AppName.Any(char.IsControl)), "returned names are sanitized");

    service.Dispose();
    PrivacyUsageSnapshot closed = service.Read();
    False(closed.IsAvailable, "disposed service is unavailable");
    Equal(0, closed.Usages.Count, "disposed service returns no readings");
}

static PrivacyUsageService.FileTimeValue Timestamp(DateTimeOffset value) =>
    PrivacyUsageService.ParseFileTimeValue(value.UtcDateTime.ToFileTimeUtc());

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message + ": expected true.");
}

static void False(bool condition, string message) => True(!condition, message);

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
}
