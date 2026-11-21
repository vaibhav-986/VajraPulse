using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VajraPulse.Services;

public sealed record CalendarMeeting(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? JoinUrl,
    bool IsRecurring)
{
    public bool IsAllDay { get; init; }
}

public sealed record CalendarLoadResult(int ImportedCount, int UnsupportedCount, string? Error);

public sealed class CalendarService
{
    private const int MaxInputBytes = 5 * 1024 * 1024;
    private const int MaxEvents = 5000;
    private const int MaxRecurrenceEvaluations = 10000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _gate = new();
    private readonly List<EventDefinition> _events = new();

    public CalendarLoadResult LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CalendarLoadResult(0, 0, "A calendar file path is required.");
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxInputBytes)
            {
                return new CalendarLoadResult(0, 0, "The calendar exceeds the 5 MB input limit.");
            }

            byte[] buffer = new byte[MaxInputBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxInputBytes || stream.ReadByte() != -1)
            {
                return new CalendarLoadResult(0, 0, "The calendar exceeds the 5 MB input limit.");
            }

            string content = StrictUtf8.GetString(buffer, 0, total);
            return LoadFromText(content);
        }
        catch (DecoderFallbackException)
        {
            return new CalendarLoadResult(0, 0, "The calendar is not valid UTF-8.");
        }
        catch (Exception exception) when (exception is IOException ||
                                          exception is UnauthorizedAccessException ||
                                          exception is ArgumentException ||
                                          exception is NotSupportedException)
        {
            return new CalendarLoadResult(0, 0, "The calendar could not be read: " + exception.Message);
        }
    }

    public CalendarLoadResult LoadFromText(string content)
    {
        if (content is null)
        {
            return new CalendarLoadResult(0, 0, "Calendar content is required.");
        }

        try
        {
            if (StrictUtf8.GetByteCount(content) > MaxInputBytes)
            {
                return new CalendarLoadResult(0, 0, "The calendar exceeds the 5 MB input limit.");
            }
        }
        catch (EncoderFallbackException)
        {
            return new CalendarLoadResult(0, 0, "The calendar contains invalid Unicode text.");
        }

        ParseCalendarResult parsed = ParseCalendar(content);
        if (parsed.StructuralError is not null)
        {
            return new CalendarLoadResult(0, 0, parsed.StructuralError);
        }

        if (parsed.Accepted.Count > 0)
        {
            lock (_gate)
            {
                _events.Clear();
                _events.AddRange(parsed.Accepted);
            }
        }

        string? error = parsed.MalformedCount == 0
            ? null
            : string.Format(
                CultureInfo.InvariantCulture,
                "Skipped {0} malformed event definition(s).",
                parsed.MalformedCount);

        return new CalendarLoadResult(parsed.Accepted.Count, parsed.UnsupportedCount, error);
    }

    public IReadOnlyList<CalendarMeeting> GetUpcoming(DateTimeOffset now, int maxCount = 10)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<CalendarMeeting>();
        }

        DateTimeOffset horizon;
        try
        {
            horizon = now.AddDays(30);
        }
        catch (ArgumentOutOfRangeException)
        {
            horizon = DateTimeOffset.MaxValue;
        }

        EventDefinition[] snapshot;
        lock (_gate)
        {
            snapshot = _events.ToArray();
        }

        List<CalendarMeeting> meetings = new();

        foreach (EventDefinition definition in snapshot)
        {
            if (definition.Recurrence is not null)
            {
                continue;
            }

            if (definition.End > now && definition.Start <= horizon)
            {
                meetings.Add(CreateMeeting(definition, definition.Start, definition.End, false));
            }
        }

        int evaluations = 0;
        foreach (EventDefinition definition in snapshot)
        {
            if (definition.Recurrence is null || evaluations >= MaxRecurrenceEvaluations)
            {
                continue;
            }

            AddRecurringMeetings(definition, now, horizon, meetings, ref evaluations);
        }

        return meetings
            .OrderBy(static meeting => meeting.Start)
            .ThenBy(static meeting => meeting.End)
            .ThenBy(static meeting => meeting.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static meeting => meeting.Id, StringComparer.Ordinal)
            .Take(maxCount)
            .ToArray();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    private static CalendarMeeting CreateMeeting(
        EventDefinition definition,
        DateTimeOffset start,
        DateTimeOffset end,
        bool recurring)
    {
        string id = recurring
            ? definition.Id + "#" + start.UtcTicks.ToString(CultureInfo.InvariantCulture)
            : definition.Id;

        return new CalendarMeeting(id, definition.Title, start, end, definition.JoinUrl, recurring) { IsAllDay = definition.AllDayDays > 0 };
    }

    private static void AddRecurringMeetings(
        EventDefinition definition,
        DateTimeOffset now,
        DateTimeOffset horizon,
        List<CalendarMeeting> meetings,
        ref int evaluations)
    {
        RecurrenceRule rule = definition.Recurrence!;
        int ordinal = 0;

        void Consider(DateTime localStart, ref int occurrenceOrdinal, ref int evaluationCount)
        {
            if (evaluationCount >= MaxRecurrenceEvaluations)
            {
                return;
            }

            evaluationCount++;
            occurrenceOrdinal++;

            if (!TryCreateOffset(localStart, definition.Zone, out DateTimeOffset occurrenceStart))
            {
                return;
            }

            if (IsAfterUntil(rule, localStart, occurrenceStart))
            {
                return;
            }

            DateTimeOffset occurrenceEnd;
            if (definition.AllDayDays > 0)
            {
                DateTime localEnd;
                try
                {
                    localEnd = localStart.AddDays(definition.AllDayDays);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return;
                }

                if (!TryCreateOffset(localEnd, definition.Zone, out occurrenceEnd))
                {
                    return;
                }
            }
            else
            {
                try
                {
                    occurrenceEnd = occurrenceStart.Add(definition.Duration);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return;
                }
            }

            if (occurrenceStart > horizon || occurrenceEnd <= now)
            {
                return;
            }

            if (definition.ExcludedUtcTicks.Contains(occurrenceStart.UtcTicks))
            {
                return;
            }

            meetings.Add(CreateMeeting(definition, occurrenceStart, occurrenceEnd, true));
        }

        Consider(definition.LocalStart, ref ordinal, ref evaluations);
        if (evaluations >= MaxRecurrenceEvaluations ||
            (rule.Count.HasValue && ordinal >= rule.Count.Value) ||
            IsDefinitelyPastUntil(rule, definition.LocalStart, definition.Start))
        {
            return;
        }

        if (rule.Frequency == RecurrenceFrequency.Daily)
        {
            DateTime candidate = definition.LocalStart;
            while (evaluations < MaxRecurrenceEvaluations &&
                   (!rule.Count.HasValue || ordinal < rule.Count.Value))
            {
                try
                {
                    candidate = candidate.AddDays(rule.Interval);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                evaluations++;
                if (rule.ByDay is not null && !rule.ByDay.Contains(candidate.DayOfWeek))
                {
                    if (CandidatePastLimits(candidate, definition.Zone, horizon, rule))
                    {
                        break;
                    }

                    continue;
                }

                if (!TryCreateOffset(candidate, definition.Zone, out DateTimeOffset instant))
                {
                    ordinal++;
                    continue;
                }

                if (IsAfterUntil(rule, candidate, instant) || instant > horizon)
                {
                    break;
                }

                evaluations--;
                Consider(candidate, ref ordinal, ref evaluations);
            }

            return;
        }

        DateTime weeklyCandidate = definition.LocalStart.Date.Add(definition.LocalStart.TimeOfDay);
        while (evaluations < MaxRecurrenceEvaluations &&
               (!rule.Count.HasValue || ordinal < rule.Count.Value))
        {
            try
            {
                weeklyCandidate = weeklyCandidate.AddDays(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            evaluations++;
            int daysFromStartOfWeek = DaysSinceMonday(definition.LocalStart.DayOfWeek);
            DateTime anchorWeek = definition.LocalStart.Date.AddDays(-daysFromStartOfWeek);
            int weekOffset = (int)((weeklyCandidate.Date - anchorWeek).TotalDays / 7);
            HashSet<DayOfWeek> days = rule.ByDay ?? new HashSet<DayOfWeek> { definition.LocalStart.DayOfWeek };
            if (weekOffset < 0 || weekOffset % rule.Interval != 0 || !days.Contains(weeklyCandidate.DayOfWeek))
            {
                if (CandidatePastLimits(weeklyCandidate, definition.Zone, horizon, rule))
                {
                    break;
                }

                continue;
            }

            if (!TryCreateOffset(weeklyCandidate, definition.Zone, out DateTimeOffset instant))
            {
                ordinal++;
                continue;
            }

            if (IsAfterUntil(rule, weeklyCandidate, instant) || instant > horizon)
            {
                break;
            }

            evaluations--;
            Consider(weeklyCandidate, ref ordinal, ref evaluations);
        }
    }

    private static bool CandidatePastLimits(
        DateTime localCandidate,
        TimeZoneInfo zone,
        DateTimeOffset horizon,
        RecurrenceRule rule)
    {
        if (!TryCreateOffset(localCandidate, zone, out DateTimeOffset instant))
        {
            return false;
        }

        return instant > horizon || IsAfterUntil(rule, localCandidate, instant);
    }

    private static bool IsAfterUntil(RecurrenceRule rule, DateTime local, DateTimeOffset instant)
    {
        if (!rule.Until.HasValue)
        {
            return false;
        }

        return rule.UntilIsDate
            ? local.Date > rule.UntilLocalDate
            : instant > rule.Until.Value;
    }

    private static bool IsDefinitelyPastUntil(RecurrenceRule rule, DateTime local, DateTimeOffset instant)
    {
        return IsAfterUntil(rule, local, instant);
    }

    private static int DaysSinceMonday(DayOfWeek day)
    {
        return ((int)day + 6) % 7;
    }

    private static ParseCalendarResult ParseCalendar(string content)
    {
        if (!TryUnfold(content, out List<string> lines, out string? unfoldError))
        {
            return ParseCalendarResult.Failed(unfoldError!);
        }

        List<string> components = new();
        List<List<ContentProperty>> eventProperties = new();
        List<ContentProperty>? currentEvent = null;
        bool sawCalendar = false;
        bool closedCalendar = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine;
            if (!sawCalendar && line.Length > 0 && line[0] == '﻿')
            {
                line = line.Substring(1);
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (!TryParseProperty(line, out ContentProperty? property))
            {
                return ParseCalendarResult.Failed("The calendar contains a malformed content line.");
            }

            ContentProperty parsedProperty = property!;
            if (parsedProperty.Name == "BEGIN")
            {
                string component = parsedProperty.Value.Trim().ToUpperInvariant();
                if (components.Count == 0)
                {
                    if (sawCalendar || component != "VCALENDAR")
                    {
                        return ParseCalendarResult.Failed("The content is not a single VCALENDAR document.");
                    }

                    sawCalendar = true;
                }
                else if (closedCalendar)
                {
                    return ParseCalendarResult.Failed("Content appears after the VCALENDAR document.");
                }

                if (component == "VEVENT")
                {
                    if (currentEvent is not null)
                    {
                        return ParseCalendarResult.Failed("Nested VEVENT components are invalid.");
                    }

                    if (eventProperties.Count >= MaxEvents)
                    {
                        return ParseCalendarResult.Failed("The calendar exceeds the 5000 event limit.");
                    }

                    currentEvent = new List<ContentProperty>();
                }

                components.Add(component);
                continue;
            }

            if (parsedProperty.Name == "END")
            {
                string component = parsedProperty.Value.Trim().ToUpperInvariant();
                if (components.Count == 0 || components[^1] != component)
                {
                    return ParseCalendarResult.Failed("The calendar has mismatched component boundaries.");
                }

                components.RemoveAt(components.Count - 1);
                if (component == "VEVENT")
                {
                    if (currentEvent is null)
                    {
                        return ParseCalendarResult.Failed("The calendar has an invalid VEVENT boundary.");
                    }

                    eventProperties.Add(currentEvent);
                    currentEvent = null;
                }
                else if (component == "VCALENDAR")
                {
                    closedCalendar = true;
                }

                continue;
            }

            if (components.Count == 0)
            {
                return ParseCalendarResult.Failed("A content line appears outside VCALENDAR.");
            }

            if (currentEvent is not null && components[^1] == "VEVENT")
            {
                currentEvent.Add(parsedProperty);
            }
        }

        if (!sawCalendar || !closedCalendar || components.Count != 0 || currentEvent is not null)
        {
            return ParseCalendarResult.Failed("The VCALENDAR document is incomplete.");
        }

        List<EventDefinition> accepted = new();
        int unsupported = 0;
        int malformed = 0;
        var overridden = new HashSet<string>(eventProperties.Where(p => p.Any(v => v.Name == "RECURRENCE-ID")).Select(p => First(p, "UID")?.Value ?? ""), StringComparer.Ordinal);
        foreach (List<ContentProperty> properties in eventProperties)
        {
            if (overridden.Contains(First(properties, "UID")?.Value ?? ""))
            {
                unsupported++;
                continue;
            }
            EventParseStatus status = TryParseEvent(properties, out EventDefinition? definition);
            if (status == EventParseStatus.Accepted)
            {
                accepted.Add(definition!);
            }
            else if (status == EventParseStatus.Unsupported)
            {
                unsupported++;
            }
            else if (status == EventParseStatus.Malformed)
            {
                malformed++;
            }
        }

        return new ParseCalendarResult(accepted, unsupported, malformed, null);
    }

    private static EventParseStatus TryParseEvent(
        List<ContentProperty> properties,
        out EventDefinition? definition)
    {
        definition = null;

        ContentProperty? status = First(properties, "STATUS");
        if (status is not null && status.Value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            return EventParseStatus.Cancelled;
        }

        if (properties.Any(static property =>
                property.Name == "RDATE" || property.Name == "EXRULE" || property.Name == "RECURRENCE-ID"))
        {
            return EventParseStatus.Unsupported;
        }

        List<ContentProperty> rrules = All(properties, "RRULE");
        if (rrules.Count > 1)
        {
            return EventParseStatus.Unsupported;
        }

        List<ContentProperty> uids = All(properties, "UID");
        ContentProperty? uidProperty = uids.Count == 1 ? uids[0] : null;
        List<ContentProperty> starts = All(properties, "DTSTART");
        if (uidProperty is null || string.IsNullOrWhiteSpace(uidProperty.Value) || starts.Count != 1)
        {
            return EventParseStatus.Malformed;
        }

        DateParseStatus startStatus = TryParseDate(starts[0], null, out ParsedDate start);
        if (startStatus == DateParseStatus.UnsupportedTimezone)
        {
            return EventParseStatus.Unsupported;
        }

        if (startStatus != DateParseStatus.Success)
        {
            return EventParseStatus.Malformed;
        }

        List<ContentProperty> ends = All(properties, "DTEND");
        var durations = All(properties, "DURATION");
        if (ends.Count > 1 || durations.Count > 1 || (ends.Count > 0 && durations.Count > 0))
        {
            return EventParseStatus.Malformed;
        }

        ParsedDate end;
        if (ends.Count == 0 && durations.Count == 1)
        {
            try
            {
                string durationText = durations[0].Value.Trim().ToUpperInvariant();
                if (durationText.EndsWith("W", StringComparison.Ordinal) && int.TryParse(durationText.AsSpan(1, durationText.Length - 2), out int weeks))
                    durationText = "P" + checked(weeks * 7).ToString(CultureInfo.InvariantCulture) + "D";
                TimeSpan length = System.Xml.XmlConvert.ToTimeSpan(durationText);
                if (length <= TimeSpan.Zero || (start.IsDate && length.Ticks % TimeSpan.TicksPerDay != 0)) return EventParseStatus.Malformed;
                DateTimeOffset endInstant;
                DateTime localEnd;
                if (start.IsDate)
                {
                    localEnd = start.Local.AddDays(length.TotalDays);
                    if (!TryCreateOffset(localEnd, start.Zone, out endInstant)) return EventParseStatus.Malformed;
                }
                else
                {
                    endInstant = start.Instant.Add(length);
                    localEnd = TimeZoneInfo.ConvertTime(endInstant, start.Zone).DateTime;
                }
                end = new ParsedDate(localEnd, endInstant, start.Zone, start.IsDate);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return EventParseStatus.Malformed; }
        }
        else if (ends.Count == 0)
        {
            if (start.IsDate)
            {
                DateTime nextDay;
                try
                {
                    nextDay = start.Local.AddDays(1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return EventParseStatus.Malformed;
                }

                if (!TryCreateOffset(nextDay, start.Zone, out DateTimeOffset nextDayOffset))
                {
                    return EventParseStatus.Malformed;
                }

                end = new ParsedDate(nextDay, nextDayOffset, start.Zone, true);
            }
            else
            {
                end = new ParsedDate(start.Local, start.Instant, start.Zone, false);
            }
        }
        else
        {
            DateParseStatus endStatus = TryParseDate(ends[0], null, out end);
            if (endStatus == DateParseStatus.UnsupportedTimezone)
            {
                return EventParseStatus.Unsupported;
            }

            if (endStatus != DateParseStatus.Success || start.IsDate != end.IsDate || end.Instant <= start.Instant)
            {
                return EventParseStatus.Malformed;
            }
        }

        RecurrenceRule? recurrence = null;
        if (rrules.Count == 1)
        {
            if (!TryParseRecurrence(rrules[0].Value, start, out recurrence))
            {
                return EventParseStatus.Unsupported;
            }
        }

        HashSet<long> exclusions = new();
        foreach (ContentProperty exdate in All(properties, "EXDATE"))
        {
            string[] values = exdate.Value.Split(',', StringSplitOptions.None);
            if (values.Length == 0)
            {
                return EventParseStatus.Malformed;
            }

            foreach (string value in values)
            {
                ContentProperty item = exdate.WithValue(value);
                DateParseStatus exclusionStatus = TryParseDate(item, null, out ParsedDate exclusion);
                if (exclusionStatus == DateParseStatus.UnsupportedTimezone)
                {
                    return EventParseStatus.Unsupported;
                }

                if (exclusionStatus != DateParseStatus.Success || exclusion.IsDate != start.IsDate)
                {
                    return EventParseStatus.Malformed;
                }

                exclusions.Add(exclusion.Instant.UtcTicks);
            }
        }

        string title = UnescapeText(First(properties, "SUMMARY")?.Value ?? string.Empty);
        string? joinUrl = FindJoinUrl(properties);
        TimeSpan duration = end.Instant - start.Instant;
        int allDayDays = 0;
        if (start.IsDate)
        {
            double days = (end.Local.Date - start.Local.Date).TotalDays;
            if (days < 1 || days > int.MaxValue)
            {
                return EventParseStatus.Malformed;
            }

            allDayDays = (int)days;
        }

        definition = new EventDefinition(
            UnescapeText(uidProperty.Value).Trim(),
            title,
            start.Instant,
            end.Instant,
            start.Local,
            start.Zone,
            duration,
            allDayDays,
            joinUrl,
            recurrence,
            exclusions);
        return EventParseStatus.Accepted;
    }

    private static bool TryParseRecurrence(string text, ParsedDate start, out RecurrenceRule? rule)
    {
        rule = null;
        Dictionary<string, string> parts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in text.Split(';', StringSplitOptions.None))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0 || equals == part.Length - 1)
            {
                return false;
            }

            string key = part.Substring(0, equals).Trim();
            string value = part.Substring(equals + 1).Trim();
            if (!parts.TryAdd(key, value))
            {
                return false;
            }
        }

        foreach (string key in parts.Keys)
        {
            if (!key.Equals("FREQ", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("COUNT", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("UNTIL", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("BYDAY", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!parts.TryGetValue("FREQ", out string? frequencyText))
        {
            return false;
        }

        RecurrenceFrequency frequency;
        if (frequencyText.Equals("DAILY", StringComparison.OrdinalIgnoreCase))
        {
            frequency = RecurrenceFrequency.Daily;
        }
        else if (frequencyText.Equals("WEEKLY", StringComparison.OrdinalIgnoreCase))
        {
            frequency = RecurrenceFrequency.Weekly;
        }
        else
        {
            return false;
        }

        int interval = 1;
        if (parts.TryGetValue("INTERVAL", out string? intervalText) &&
            (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1))
        {
            return false;
        }

        int? count = null;
        if (parts.TryGetValue("COUNT", out string? countText))
        {
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedCount) ||
                parsedCount < 1)
            {
                return false;
            }

            count = parsedCount;
        }

        HashSet<DayOfWeek>? byDay = null;
        if (parts.TryGetValue("BYDAY", out string? byDayText))
        {
            byDay = new HashSet<DayOfWeek>();
            foreach (string token in byDayText.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseDay(token.Trim(), out DayOfWeek day) || !byDay.Add(day))
                {
                    return false;
                }
            }

            if (byDay.Count == 0)
            {
                return false;
            }
        }

        DateTimeOffset? until = null;
        bool untilIsDate = false;
        DateTime untilLocalDate = default;
        if (parts.TryGetValue("UNTIL", out string? untilText))
        {
            ContentProperty untilProperty = new(
                "UNTIL",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                untilText);
            DateParseStatus untilStatus = TryParseDate(untilProperty, start.Zone, out ParsedDate parsedUntil);
            if (untilStatus != DateParseStatus.Success || (parsedUntil.IsDate && !start.IsDate))
            {
                return false;
            }

            until = parsedUntil.Instant;
            untilIsDate = parsedUntil.IsDate;
            untilLocalDate = parsedUntil.Local.Date;
        }

        rule = new RecurrenceRule(frequency, interval, count, until, untilIsDate, untilLocalDate, byDay);
        return true;
    }

    private static bool TryParseDay(string token, out DayOfWeek day)
    {
        switch (token.ToUpperInvariant())
        {
            case "MO": day = DayOfWeek.Monday; return true;
            case "TU": day = DayOfWeek.Tuesday; return true;
            case "WE": day = DayOfWeek.Wednesday; return true;
            case "TH": day = DayOfWeek.Thursday; return true;
            case "FR": day = DayOfWeek.Friday; return true;
            case "SA": day = DayOfWeek.Saturday; return true;
            case "SU": day = DayOfWeek.Sunday; return true;
            default: day = default; return false;
        }
    }

    private static DateParseStatus TryParseDate(
        ContentProperty property,
        TimeZoneInfo? defaultZone,
        out ParsedDate parsed)
    {
        parsed = default;
        string value = property.Value.Trim();
        property.Parameters.TryGetValue("VALUE", out string? valueType);
        bool declaredDate = valueType is not null && valueType.Equals("DATE", StringComparison.OrdinalIgnoreCase);
        if (valueType is not null &&
            !declaredDate &&
            !valueType.Equals("DATE-TIME", StringComparison.OrdinalIgnoreCase))
        {
            return DateParseStatus.Invalid;
        }

        bool looksLikeDate = value.Length == 8;
        if (declaredDate || looksLikeDate)
        {
            if (!DateTime.TryParseExact(
                    value,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime date))
            {
                return DateParseStatus.Invalid;
            }

            DateParseStatus zoneStatus = ResolveZone(property, defaultZone, out TimeZoneInfo? dateZone);
            if (zoneStatus != DateParseStatus.Success)
            {
                return zoneStatus;
            }

            date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            if (!TryCreateOffset(date, dateZone!, out DateTimeOffset dateInstant))
            {
                return DateParseStatus.Invalid;
            }

            parsed = new ParsedDate(date, dateInstant, dateZone!, true);
            return DateParseStatus.Success;
        }

        bool utc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        string core = utc ? value.Substring(0, value.Length - 1) : value;
        string[] formats = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" };
        if (!DateTime.TryParseExact(
                core,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime local))
        {
            return DateParseStatus.Invalid;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (utc)
        {
            if (property.Parameters.ContainsKey("TZID"))
            {
                return DateParseStatus.Invalid;
            }

            DateTimeOffset instant = new(local, TimeSpan.Zero);
            parsed = new ParsedDate(local, instant, TimeZoneInfo.Utc, false);
            return DateParseStatus.Success;
        }

        DateParseStatus resolved = ResolveZone(property, defaultZone, out TimeZoneInfo? zone);
        if (resolved != DateParseStatus.Success)
        {
            return resolved;
        }

        if (!TryCreateOffset(local, zone!, out DateTimeOffset offset))
        {
            return DateParseStatus.Invalid;
        }

        parsed = new ParsedDate(local, offset, zone!, false);
        return DateParseStatus.Success;
    }

    private static DateParseStatus ResolveZone(
        ContentProperty property,
        TimeZoneInfo? defaultZone,
        out TimeZoneInfo? zone)
    {
        if (!property.Parameters.TryGetValue("TZID", out string? zoneId) || string.IsNullOrWhiteSpace(zoneId))
        {
            zone = defaultZone ?? TimeZoneInfo.Local;
            return DateParseStatus.Success;
        }

        zoneId = Unquote(zoneId.Trim());
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            return DateParseStatus.Success;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = null;
            return DateParseStatus.UnsupportedTimezone;
        }
        catch (InvalidTimeZoneException)
        {
            zone = null;
            return DateParseStatus.UnsupportedTimezone;
        }
    }

    private static bool TryCreateOffset(DateTime local, TimeZoneInfo zone, out DateTimeOffset value)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            value = default;
            return false;
        }

        try
        {
            TimeSpan offset = zone.GetUtcOffset(local);
            value = new DateTimeOffset(local, offset);
            return true;
        }
        catch (ArgumentException)
        {
            value = default;
            return false;
        }
    }

    private static string? FindJoinUrl(List<ContentProperty> properties)
    {
        foreach (ContentProperty property in All(properties, "URL"))
        {
            string? url = ExtractHttpsUrl(UnescapeText(property.Value));
            if (url is not null)
            {
                return url;
            }
        }

        foreach (string name in new[] { "LOCATION", "DESCRIPTION" })
        {
            foreach (ContentProperty property in All(properties, name))
            {
                string? url = ExtractHttpsUrl(UnescapeText(property.Value));
                if (url is not null)
                {
                    return url;
                }
            }
        }

        return null;
    }

    private static string? ExtractHttpsUrl(string text)
    {
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int start = text.IndexOf("https://", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            int end = start;
            while (end < text.Length &&
                   !char.IsWhiteSpace(text[end]) &&
                   text[end] != '<' && text[end] != '>' &&
                   text[end] != '"' && text[end] != '\'')
            {
                end++;
            }

            string candidate = text.Substring(start, end - start).TrimEnd('.', ',', ';', ')', ']', '}');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return candidate;
            }

            searchFrom = Math.Max(end, start + 8);
        }

        return null;
    }

    private static bool TryUnfold(string content, out List<string> lines, out string? error)
    {
        lines = new List<string>(); error = null;
        using var reader = new StringReader(content);
        StringBuilder? current = null;
        string? physical;
        int count = 0;
        while ((physical = reader.ReadLine()) != null)
        {
            if (++count > 100000) { error = "The calendar has too many content lines."; return false; }
            bool continuation = physical.Length > 0 && (physical[0] == ' ' || physical[0] == '\t');
            if (continuation)
            {
                if (current == null) { error = "The calendar begins with an invalid folded line."; return false; }
                current.Append(physical.AsSpan(1));
            }
            else
            {
                if (current != null) lines.Add(current.ToString());
                current = new StringBuilder(physical);
            }
            if (current.Length > 131072) { error = "A calendar content line is too long."; return false; }
        }
        if (current != null) lines.Add(current.ToString());
        return true;
    }

    private static bool TryParseProperty(string line, out ContentProperty? property)
    {
        property = null;
        bool quoted = false;
        int colon = -1;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ':' && !quoted)
            {
                colon = index;
                break;
            }
        }

        if (colon <= 0 || quoted)
        {
            return false;
        }

        string header = line.Substring(0, colon);
        string value = line.Substring(colon + 1);
        List<string> segments = SplitOutsideQuotes(header, ';');
        string name = segments[0].Trim().ToUpperInvariant();
        if (name.Length == 0)
        {
            return false;
        }

        Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < segments.Count; index++)
        {
            int equals = segments[index].IndexOf('=');
            if (equals <= 0 || equals == segments[index].Length - 1)
            {
                return false;
            }

            string key = segments[index].Substring(0, equals).Trim();
            string parameterValue = segments[index].Substring(equals + 1).Trim();
            if (!parameters.TryAdd(key, parameterValue))
            {
                return false;
            }
        }

        property = new ContentProperty(name, parameters, value);
        return true;
    }

    private static List<string> SplitOutsideQuotes(string text, char delimiter)
    {
        List<string> result = new();
        bool quoted = false;
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                quoted = !quoted;
            }
            else if (text[index] == delimiter && !quoted)
            {
                result.Add(text.Substring(start, index - start));
                start = index + 1;
            }
        }

        result.Add(text.Substring(start));
        return result;
    }

    private static string UnescapeText(string value)
    {
        StringBuilder result = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '\\' && index + 1 < value.Length)
            {
                char escaped = value[++index];
                result.Append(escaped == 'n' || escaped == 'N' ? '\n' : escaped);
            }
            else
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value.Substring(1, value.Length - 2)
            : value;
    }

    private static ContentProperty? First(List<ContentProperty> properties, string name)
    {
        return properties.FirstOrDefault(property => property.Name == name);
    }

    private static List<ContentProperty> All(List<ContentProperty> properties, string name)
    {
        return properties.Where(property => property.Name == name).ToList();
    }

    private enum EventParseStatus
    {
        Accepted,
        Cancelled,
        Unsupported,
        Malformed
    }

    private enum DateParseStatus
    {
        Success,
        Invalid,
        UnsupportedTimezone
    }

    private enum RecurrenceFrequency
    {
        Daily,
        Weekly
    }

    private sealed record ContentProperty(
        string Name,
        Dictionary<string, string> Parameters,
        string Value)
    {
        public ContentProperty WithValue(string value)
        {
            return new ContentProperty(Name, Parameters, value);
        }
    }

    private readonly record struct ParsedDate(
        DateTime Local,
        DateTimeOffset Instant,
        TimeZoneInfo Zone,
        bool IsDate);

    private sealed record RecurrenceRule(
        RecurrenceFrequency Frequency,
        int Interval,
        int? Count,
        DateTimeOffset? Until,
        bool UntilIsDate,
        DateTime UntilLocalDate,
        HashSet<DayOfWeek>? ByDay);

    private sealed record EventDefinition(
        string Id,
        string Title,
        DateTimeOffset Start,
        DateTimeOffset End,
        DateTime LocalStart,
        TimeZoneInfo Zone,
        TimeSpan Duration,
        int AllDayDays,
        string? JoinUrl,
        RecurrenceRule? Recurrence,
        HashSet<long> ExcludedUtcTicks);

    private sealed record ParseCalendarResult(
        List<EventDefinition> Accepted,
        int UnsupportedCount,
        int MalformedCount,
        string? StructuralError)
    {
        public static ParseCalendarResult Failed(string error)
        {
            return new ParseCalendarResult(new List<EventDefinition>(), 0, 0, error);
        }
    }
}
