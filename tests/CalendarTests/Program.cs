using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VajraPulse.Services;

internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("UTC, floating, TZID, and all-day dates", DateFormats),
            ("Escaped text and unfolded lines", EscapingAndUnfolding),
            ("Safe join-link selection", JoinLinks),
            ("Upcoming filtering and clear", UpcomingFiltering),
            ("Daily recurrence and exclusions", DailyRecurrence),
            ("Weekly recurrence, interval, BYDAY, and UNTIL", WeeklyRecurrence),
            ("Unsupported recurrence and timezone accounting", UnsupportedDefinitions),
            ("Cancelled and malformed input", CancelledAndMalformed),
            ("UTF-8 file loading", FileLoading),
            ("DURATION defines meeting and all-day end times", DurationFields),
            ("A new calendar import replaces rather than duplicates", ReplaceImport),
            ("Unsupported recurrence overrides never fabricate base instances", RecurrenceOverrides)
        };

        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
                return 1;
            }
        }

        Console.WriteLine($"All {_passed} calendar tests passed.");
        return 0;
    }

    private static void DateFormats()
    {
        CalendarService service = new();
        CalendarLoadResult result = service.LoadFromText(Calendar(
            Event("utc", "UTC", "DTSTART:20300102T030405Z\r\nDTEND:20300102T040405Z"),
            Event("floating", "Floating", "DTSTART:20300102T050000\r\nDTEND:20300102T060000"),
            Event("tzid", "Zone", "DTSTART;TZID=UTC:20300102T070000\r\nDTEND;TZID=UTC:20300102T080000"),
            Event("day", "All day", "DTSTART;VALUE=DATE:20300103\r\nDTEND;VALUE=DATE:20300105")));

        Equal(4, result.ImportedCount, "all supported date definitions should import");
        Equal(0, result.UnsupportedCount, "supported dates should not be unsupported");
        Null(result.Error, "valid date definitions should not report errors");

        IReadOnlyList<CalendarMeeting> meetings = service.GetUpcoming(
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), 10);
        Equal(4, meetings.Count, "all date variants should be upcoming");
        CalendarMeeting utc = Single(meetings, "utc");
        Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), utc.Start, "UTC date parsing");
        CalendarMeeting zone = Single(meetings, "tzid");
        Equal(TimeSpan.Zero, zone.Start.Offset, "UTC TZID parsing");
        CalendarMeeting floating = Single(meetings, "floating");
        Equal(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 2, 5, 0, 0)), floating.Start.Offset,
            "floating time should retain the local zone");
        CalendarMeeting allDay = Single(meetings, "day");
        Equal(TimeSpan.FromDays(2), allDay.End - allDay.Start, "all-day DTEND should be exclusive");
    }

    private static void EscapingAndUnfolding()
    {
        CalendarService service = new();
        string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n" +
                         "UID:folded\r\nSUMMARY:Road\\, Map\\; Review\\nNext \\ \\ \\ \r\n" +
                         " line\r\nDTSTART:20300201T100000Z\r\nDTEND:20300201T110000Z\r\n" +
                         "END:VEVENT\r\nEND:VCALENDAR\r\n";
        CalendarLoadResult result = service.LoadFromText(content);
        Equal(1, result.ImportedCount, "folded event should import");
        CalendarMeeting meeting = service.GetUpcoming(
            new DateTimeOffset(2030, 2, 1, 9, 0, 0, TimeSpan.Zero), 1)[0];
        True(meeting.Title.StartsWith("Road, Map; Review\nNext", StringComparison.Ordinal),
            "SUMMARY escapes should be decoded");
        True(meeting.Title.EndsWith("line", StringComparison.Ordinal), "folded content should be joined");
    }

    private static void JoinLinks()
    {
        CalendarService service = new();
        string first = Event(
            "join-1",
            "Join",
            "DTSTART:20300301T100000Z\r\nDTEND:20300301T110000Z\r\n" +
            "URL:javascript:alert(1)\r\nLOCATION:file:///tmp/not-safe\r\n" +
            "DESCRIPTION:Use https://meet.example.test/room?id=7 for the call");
        string second = Event(
            "join-2",
            "No unsafe link",
            "DTSTART:20300301T120000Z\r\nDTEND:20300301T130000Z\r\n" +
            "URL:file:///C:/secret\r\nDESCRIPTION:javascript:alert(1)");
        service.LoadFromText(Calendar(first, second));

        IReadOnlyList<CalendarMeeting> meetings = service.GetUpcoming(
            new DateTimeOffset(2030, 3, 1, 0, 0, 0, TimeSpan.Zero), 10);
        Equal("https://meet.example.test/room?id=7", Single(meetings, "join-1").JoinUrl,
            "safe HTTPS link should be extracted");
        Null(Single(meetings, "join-2").JoinUrl, "unsafe schemes should be rejected");
    }

    private static void UpcomingFiltering()
    {
        CalendarService service = new();
        service.LoadFromText(Calendar(
            Event("ended", "Ended", "DTSTART:20300401T080000Z\r\nDTEND:20300401T090000Z"),
            Event("active", "Active", "DTSTART:20300401T090000Z\r\nDTEND:20300401T110000Z"),
            Event("soon", "Soon", "DTSTART:20300402T090000Z\r\nDTEND:20300402T100000Z"),
            Event("far", "Far", "DTSTART:20300601T090000Z\r\nDTEND:20300601T100000Z")));

        IReadOnlyList<CalendarMeeting> meetings = service.GetUpcoming(
            new DateTimeOffset(2030, 4, 1, 10, 0, 0, TimeSpan.Zero), 10);
        SequenceEqual(new[] { "active", "soon" }, meetings.Select(item => item.Id),
            "in-progress and near-future meetings should be returned in order");
        Equal(0, service.GetUpcoming(new DateTimeOffset(2030, 4, 1, 10, 0, 0, TimeSpan.Zero), 0).Count,
            "non-positive max count should return no meetings");
        service.Clear();
        Equal(0, service.GetUpcoming(new DateTimeOffset(2030, 4, 1, 10, 0, 0, TimeSpan.Zero)).Count,
            "clear should remove imported definitions");
    }

    private static void DailyRecurrence()
    {
        CalendarService service = new();
        string recurring = Event(
            "daily",
            "Daily",
            "DTSTART:20300501T100000Z\r\nDTEND:20300501T103000Z\r\n" +
            "RRULE:FREQ=DAILY;INTERVAL=2;COUNT=4\r\nEXDATE:20300503T100000Z");
        CalendarLoadResult load = service.LoadFromText(Calendar(recurring));
        Equal(1, load.ImportedCount, "daily recurrence definition should import once");

        IReadOnlyList<CalendarMeeting> meetings = service.GetUpcoming(
            new DateTimeOffset(2030, 5, 1, 9, 0, 0, TimeSpan.Zero), 10);
        SequenceEqual(
            new[] { 1, 5, 7 },
            meetings.Select(item => item.Start.Day),
            "COUNT should count generated occurrences before EXDATE removal");
        True(meetings.All(item => item.IsRecurring), "expanded instances should be marked recurring");
        True(meetings.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == meetings.Count,
            "recurrence instance IDs should be unique");
    }

    private static void WeeklyRecurrence()
    {
        CalendarService service = new();
        string recurring = Event(
            "weekly",
            "Weekly",
            "DTSTART:20300107T090000Z\r\nDTEND:20300107T100000Z\r\n" +
            "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20300201T000000Z");
        service.LoadFromText(Calendar(recurring));
        IReadOnlyList<CalendarMeeting> meetings = service.GetUpcoming(
            new DateTimeOffset(2030, 1, 7, 8, 0, 0, TimeSpan.Zero), 10);
        SequenceEqual(
            new[] { 7, 9, 21, 23 },
            meetings.Select(item => item.Start.Day),
            "weekly interval and BYDAY should use Monday-anchored weeks and respect UNTIL");
    }

    private static void UnsupportedDefinitions()
    {
        CalendarService service = new();
        string monthly = Event(
            "monthly",
            "Monthly",
            "DTSTART:20300601T100000Z\r\nDTEND:20300601T110000Z\r\nRRULE:FREQ=MONTHLY");
        string badZone = Event(
            "bad-zone",
            "Bad zone",
            "DTSTART;TZID=Not/A_Real_Zone:20300601T100000\r\n" +
            "DTEND;TZID=Not/A_Real_Zone:20300601T110000");
        string good = Event("good", "Good", "DTSTART:20300602T100000Z\r\nDTEND:20300602T110000Z");
        CalendarLoadResult result = service.LoadFromText(Calendar(monthly, badZone, good));
        Equal(1, result.ImportedCount, "supported definitions should still import");
        Equal(2, result.UnsupportedCount, "unsupported recurrence and timezone should be explicit");
        Equal(1, service.GetUpcoming(new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero), 10).Count,
            "unsupported definitions should not fabricate meetings");
    }

    private static void CancelledAndMalformed()
    {
        CalendarService service = new();
        string cancelled = Event(
            "cancelled",
            "Cancelled",
            "STATUS:CANCELLED\r\nDTSTART:20300701T100000Z\r\nDTEND:20300701T110000Z");
        string missingUid = "BEGIN:VEVENT\r\nSUMMARY:Missing UID\r\n" +
                            "DTSTART:20300701T120000Z\r\nDTEND:20300701T130000Z\r\nEND:VEVENT\r\n";
        CalendarLoadResult mixed = service.LoadFromText(Calendar(cancelled, missingUid));
        Equal(0, mixed.ImportedCount, "cancelled and malformed events should not import");
        NotNull(mixed.Error, "malformed event should be reported");

        CalendarLoadResult corrupt = service.LoadFromText("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nEND:VCALENDAR\r\n");
        Equal(0, corrupt.ImportedCount, "structurally corrupt input should not import");
        NotNull(corrupt.Error, "structurally corrupt input should report an error");
    }

    private static void FileLoading()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VajraPulse-CalendarTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string validPath = Path.Combine(directory, "valid.ics");
            File.WriteAllText(
                validPath,
                Calendar(Event("file", "From file", "DTSTART:20300801T100000Z\r\nDTEND:20300801T110000Z")),
                new System.Text.UTF8Encoding(false));
            CalendarService service = new();
            Equal(1, service.LoadFromFile(validPath).ImportedCount, "valid UTF-8 file should import");

            string invalidPath = Path.Combine(directory, "invalid.ics");
            File.WriteAllBytes(invalidPath, new byte[] { 0xFF, 0xFE, 0xFA });
            CalendarLoadResult invalid = service.LoadFromFile(invalidPath);
            Equal(0, invalid.ImportedCount, "invalid UTF-8 should not import");
            NotNull(invalid.Error, "invalid UTF-8 should report an error");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void DurationFields()
    {
        var service = new CalendarService();
        var result = service.LoadFromText(Calendar(
            Event("timed", "Timed fixture", "DTSTART:20300102T100000Z\r\nDURATION:PT30M"),
            Event("day", "All-day fixture", "DTSTART;VALUE=DATE:20300103\r\nDURATION:P1W")));
        Equal(2, result.ImportedCount, "DURATION events import");
        var meetings = service.GetUpcoming(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Equal(TimeSpan.FromMinutes(30), Single(meetings, "timed").End - Single(meetings, "timed").Start, "timed duration");
        Equal(TimeSpan.FromDays(7), Single(meetings, "day").End - Single(meetings, "day").Start, "week duration");
        True(Single(meetings, "day").IsAllDay, "all-day events are distinct from timed meetings");
    }
    private static void ReplaceImport()
    {
        var service = new CalendarService();
        string content = Calendar(Event("one", "Fixture", "DTSTART:20300102T100000Z\r\nDTEND:20300102T110000Z"));
        service.LoadFromText(content); service.LoadFromText(content);
        Equal(1, service.GetUpcoming(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)).Count, "re-import does not duplicate meetings");
    }
    private static void RecurrenceOverrides()
    {
        var service = new CalendarService();
        var result = service.LoadFromText(Calendar(
            Event("series", "Fixture", "DTSTART:20300102T100000Z\r\nDTEND:20300102T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3"),
            Event("series", "Cancelled instance", "RECURRENCE-ID:20300103T100000Z\r\nSTATUS:CANCELLED\r\nDTSTART:20300103T100000Z\r\nDTEND:20300103T110000Z")));
        Equal(2, result.UnsupportedCount, "unsupported overrides are counted explicitly");
        Equal(0, result.ImportedCount, "base instances of an unsupported override are not fabricated");
    }

    private static string Calendar(params string[] events)
    {
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";
    }

    private static string Event(string id, string title, string body)
    {
        return "BEGIN:VEVENT\r\nUID:" + id + "\r\nSUMMARY:" + title + "\r\n" + body + "\r\nEND:VEVENT\r\n";
    }

    private static CalendarMeeting Single(IEnumerable<CalendarMeeting> meetings, string id)
    {
        return meetings.Single(item => item.Id == id || item.Id.StartsWith(id + "#", StringComparison.Ordinal));
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}. Expected: {expected}; actual: {actual}");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{message}. Expected: [{string.Join(", ", expected)}]; actual: [{string.Join(", ", actual)}]");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(message + ". Actual: " + value);
        }
    }

    private static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }
}
