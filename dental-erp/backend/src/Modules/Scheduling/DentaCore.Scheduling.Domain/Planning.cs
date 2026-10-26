namespace DentaCore.Scheduling.Domain;

/// <summary>Provayderin iş pəncərəsi (yerli vaxtla), məs. 09:00–13:00.</summary>
public readonly record struct WorkWindow(TimeOnly Start, TimeOnly End);

/// <summary>Bir günün məlumatı: iş pəncərələri, məzuniyyət və artıq tutulmuş vaxtlar.</summary>
public sealed record ProviderDay(bool HasAnySchedule, IReadOnlyList<WorkWindow> Windows, IReadOnlyList<TimeSlot> TimeOff, IReadOnlyList<TimeSlot> Busy);

public enum ScheduleVerdict
{
    Allowed,
    OutsideSchedule,
    TimeOff,
}

/// <summary>Təmiz (I/O-suz) planlaşdırma məntiqi: slot hesablanması və iş qrafiki yoxlaması. Unit-test olunur.</summary>
public static class SchedulePlanner
{
    /// <summary>
    /// Qəbulun provayderin iş pəncərəsinə tam sığıb-sığmadığını yoxlayır. İş qrafiki heç təyin olunmayıbsa məhdudiyyət yoxdur
    /// (yeni klinika qrafiki doldurana qədər işləyə bilsin). Qrafik var, amma həmin gün yoxdursa: iş günü deyil.
    /// </summary>
    public static ScheduleVerdict Check(ProviderDay day, TimeSlot slot, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(zone);
        if (day.TimeOff.Any(slot.Overlaps))
        {
            return ScheduleVerdict.TimeOff;
        }

        if (!day.HasAnySchedule)
        {
            return ScheduleVerdict.Allowed;
        }

        var localStart = TimeZoneInfo.ConvertTime(slot.Start, zone);
        var localEnd = TimeZoneInfo.ConvertTime(slot.End, zone);
        if (localStart.Date != localEnd.Date && localEnd.TimeOfDay != TimeSpan.Zero)
        {
            return ScheduleVerdict.OutsideSchedule;   // gecəyarısını keçir
        }

        var start = TimeOnly.FromTimeSpan(localStart.TimeOfDay);
        var end = localEnd.TimeOfDay == TimeSpan.Zero ? TimeOnly.MaxValue : TimeOnly.FromTimeSpan(localEnd.TimeOfDay);
        return day.Windows.Any(w => start >= w.Start && end <= w.End) ? ScheduleVerdict.Allowed : ScheduleVerdict.OutsideSchedule;
    }

    /// <summary>
    /// Boş slotlar: iş pəncərəsində step addımı ilə, məzuniyyət və mövcud qəbullarla kəsişməyən, keçmişdə olmayan başlanğıclar.
    /// Nəticə məsləhət xarakterlidir: son hakim DB-dəki EXCLUDE constraint-dir.
    /// </summary>
    public static IReadOnlyList<TimeSlot> FreeSlots(ProviderDay day, DateOnly localDate, TimeSpan duration, TimeSpan step, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(zone);
        var result = new List<TimeSlot>();
        foreach (var window in day.Windows.OrderBy(w => w.Start))
        {
            var windowStart = ToUtc(localDate, window.Start, zone);
            var windowEnd = ToUtc(localDate, window.End, zone);
            for (var t = windowStart; t + duration <= windowEnd; t += step)
            {
                var candidate = new TimeSlot(t, t + duration);
                if (candidate.Start < now || day.TimeOff.Any(candidate.Overlaps) || day.Busy.Any(candidate.Overlaps))
                {
                    continue;
                }

                result.Add(candidate);
            }
        }

        return result;
    }

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>Xatırlatma planı: kanal və "neçə dəqiqə əvvəl" cütləri; keçmişə düşənlər atılır, dublikatlar birləşdirilir.</summary>
    public static IReadOnlyList<(string Channel, DateTimeOffset SendAt)> PlanReminders(DateTimeOffset appointmentStart, string channel, IEnumerable<int> minutesBefore, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(minutesBefore);
        if (channel == "none")
        {
            return [];
        }

        return minutesBefore
            .Where(m => m > 0)
            .Distinct()
            .Select(m => (Channel: channel, SendAt: appointmentStart - TimeSpan.FromMinutes(m)))
            .Where(r => r.SendAt > now)
            .OrderBy(r => r.SendAt)
            .ToList();
    }
}
