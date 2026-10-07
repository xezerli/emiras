using System.Globalization;
using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Contracts;
using DentaCore.Scheduling.Domain;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DentaCore.Scheduling.Application;

public sealed record AppointmentDto(
    Guid Id, Guid BranchId, Guid PatientId, string? PatientName, Guid ProviderId, Guid? RoomId,
    DateTimeOffset Start, DateTimeOffset End, string Status, string? Reason, string Source, int RowVersion);

public sealed record SlotDto(DateTimeOffset Start, DateTimeOffset End);

internal static class AppointmentMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string StatusText(AppointmentStatus s) => s switch
    {
        AppointmentStatus.CheckedIn => "checked_in",
        AppointmentStatus.InProgress => "in_progress",
        AppointmentStatus.NoShow => "no_show",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static AppointmentDto ToDto(Appointment a, string? patientName) =>
        new(a.Id, a.BranchId, a.PatientId, patientName, a.ProviderId, a.RoomId, a.Slot.Start, a.Slot.End, StatusText(a.Status), a.Reason, a.Source, a.RowVersion);

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);
}

/// <summary>Booking və reschedule üçün ortaq yoxlamalar və konflikt xəritələməsi.</summary>
internal static class SchedulingRules
{
    public static async Task<Result> ValidateRoomAsync(ISchedulingReadModel readModel, Guid? roomId, Guid branchId, CancellationToken ct)
    {
        if (roomId is not { } room)
        {
            return Result.Success();
        }

        var roomBranch = await readModel.GetRoomBranchAsync(room, ct);
        if (roomBranch is null)
        {
            return Error.Validation("appointment.room_not_found", "Room not found.");
        }

        return roomBranch == branchId ? Result.Success() : Error.Validation("appointment.room_branch_mismatch", "The room belongs to a different branch.");
    }

    public static async Task<Result> ValidateScheduleAsync(
        ISchedulingReadModel readModel, Guid providerId, TimeSlot slot, TimeZoneInfo zone, Guid? excludeAppointmentId, CancellationToken ct)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.Start, zone).DateTime);
        var day = await readModel.GetProviderDayAsync(providerId, localDate, zone, excludeAppointmentId, ct);
        return SchedulePlanner.Check(day, slot, zone) switch
        {
            ScheduleVerdict.TimeOff => Error.Validation("appointment.provider_off", "The provider is not available at this time (time off)."),
            ScheduleVerdict.OutsideSchedule => Error.Validation("appointment.outside_schedule", "The appointment is outside the provider's working hours."),
            _ => Result.Success(),
        };
    }

    /// <summary>DB constraint pozuntusunu biznes xətasına çevirir. Overlap zamanı alternativ boş slotlar təklif olunur.</summary>
    public static async Task<Error> MapConflictAsync(
        ConstraintViolationException ex, ISchedulingReadModel readModel, Guid providerId, TimeSlot slot, SchedulingOptions options,
        Guid? excludeAppointmentId, DateTimeOffset now, CancellationToken ct)
    {
        var name = ex.ConstraintName ?? string.Empty;
        if (ex.Kind == ConstraintKind.Exclusion && name.Contains("provider", StringComparison.Ordinal))
        {
            var error = Error.Conflict("appointment.overlap", "The provider is already booked at this time.");
            var alternatives = await SuggestAsync(readModel, providerId, slot, options, excludeAppointmentId, now, ct);
            return alternatives.Count == 0 ? error : error with { Details = new Dictionary<string, object?> { ["alternativeSlots"] = alternatives } };
        }

        if (ex.Kind == ConstraintKind.Exclusion)
        {
            return Error.Conflict("appointment.room_busy", "The room is already in use at this time.");
        }

        if (ex.Kind == ConstraintKind.ForeignKey)
        {
            foreach (var entity in new[] { "provider", "room", "patient", "branch" })
            {
                if (name.Contains(entity, StringComparison.Ordinal))
                {
                    return Error.Validation($"appointment.{entity}_not_found", $"The {entity} does not exist.");
                }
            }
        }

        throw ex;   // gözlənilməz constraint: gizlətmirik
    }

    private static async Task<List<SlotDto>> SuggestAsync(
        ISchedulingReadModel readModel, Guid providerId, TimeSlot requested, SchedulingOptions options, Guid? exclude, DateTimeOffset now, CancellationToken ct)
    {
        var zone = options.Zone;
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(requested.Start, zone).DateTime);
        var day = await readModel.GetProviderDayAsync(providerId, localDate, zone, exclude, ct);
        var free = SchedulePlanner.FreeSlots(day, localDate, requested.Duration, TimeSpan.FromMinutes(options.SlotStepMinutes), zone, now);
        var after = free.Where(s => s.Start >= requested.Start);
        return (after.Any() ? after : free).Take(3).Select(s => new SlotDto(s.Start, s.End)).ToList();
    }
}

// ---------------- Book ----------------
public sealed record BookAppointmentCommand(
    Guid BranchId, Guid PatientId, Guid ProviderId, Guid? RoomId, DateTimeOffset Start, DateTimeOffset End,
    string? Reason, string? Source, int[]? ReminderMinutes) : ICommand<AppointmentDto>, IRequiresAccess, IAuditable<AppointmentDto>
{
    public string Permission => SchedulingPermissions.Write;

    public AuditDescriptor Describe(AppointmentDto response) => new("appointment.create", "appointment", response?.Id);
}

public sealed class BookAppointmentCommandValidator : AbstractValidator<BookAppointmentCommand>
{
    public BookAppointmentCommandValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ProviderId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500);
        RuleFor(x => x.ReminderMinutes).Must(r => r is null || (r.Length <= 5 && r.All(m => m is >= 5 and <= 43200)))
            .WithMessage("Up to 5 reminders, each 5..43200 minutes before the start.");
    }
}

internal sealed class BookAppointmentCommandHandler(
    ISchedulingRepository repository,
    ISchedulingReadModel readModel,
    IPatientDirectory patients,
    ICurrentUser user,
    ISchedulingUnitOfWork unitOfWork,
    IOptions<SchedulingOptions> options,
    IClock clock) : IRequestHandler<BookAppointmentCommand, AppointmentDto>
{
    public async Task<Result<AppointmentDto>> Handle(BookAppointmentCommand request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var now = clock.UtcNow;

        // own scope: həkim yalnız öz təqvimində qəbul yarada bilər (owner = provayder)
        if (!user.CanAccess(SchedulingPermissions.Write, request.BranchId, request.ProviderId))
        {
            return Error.Forbidden("permission.denied", "You cannot book appointments in this branch.");
        }

        var patient = await patients.FindAsync(request.PatientId, cancellationToken);
        if (patient is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var slot = new TimeSlot(request.Start.ToUniversalTime(), request.End.ToUniversalTime());
        var appointment = Appointment.Book(Guid.NewGuid(), request.BranchId, request.PatientId, request.ProviderId, request.RoomId, slot, request.Reason, request.Source ?? "reception", user.UserId, now);
        if (appointment.IsFailure)
        {
            return appointment.Error!;
        }

        var room = await SchedulingRules.ValidateRoomAsync(readModel, request.RoomId, request.BranchId, cancellationToken);
        if (room.IsFailure)
        {
            return room.Error!;
        }

        var schedule = await SchedulingRules.ValidateScheduleAsync(readModel, request.ProviderId, slot, o.Zone, null, cancellationToken);
        if (schedule.IsFailure)
        {
            return schedule.Error!;
        }

        repository.Add(appointment.Value);
        repository.AddReminders(SchedulePlanner
            .PlanReminders(slot.Start, patient.PreferredChannel, request.ReminderMinutes ?? o.DefaultReminderMinutes, now)
            .Select(r => AppointmentReminder.Create(appointment.Value.Id, r.Channel, r.SendAt)));

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex)
        {
            // Double-booking-in son hakimi DB-dir: paralel iki sorğudan yalnız biri keçir
            return await SchedulingRules.MapConflictAsync(ex, readModel, request.ProviderId, slot, o, null, now, cancellationToken);
        }

        return AppointmentMapper.ToDto(appointment.Value, patient.FullName);
    }
}

// ---------------- Get / List ----------------
public sealed record GetAppointmentQuery(Guid Id) : IQuery<AppointmentDto>, IRequiresAccess, IAuditable<AppointmentDto>
{
    public string Permission => SchedulingPermissions.Read;

    public AuditDescriptor Describe(AppointmentDto response) => new("appointment.read", "appointment", Id);
}

internal sealed class GetAppointmentQueryHandler(ISchedulingRepository repository, IPatientDirectory patients, ICurrentUser user)
    : IRequestHandler<GetAppointmentQuery, AppointmentDto>
{
    public async Task<Result<AppointmentDto>> Handle(GetAppointmentQuery request, CancellationToken cancellationToken)
    {
        var a = await repository.GetAsync(request.Id, cancellationToken);
        if (a is null || !user.CanAccess(SchedulingPermissions.Read, a.BranchId, a.ProviderId))
        {
            return Error.NotFound("appointment.not_found", "Appointment not found.");
        }

        var names = await patients.GetNamesAsync([a.PatientId], cancellationToken);
        return AppointmentMapper.ToDto(a, names.GetValueOrDefault(a.PatientId));
    }
}

public sealed record ListAppointmentsQuery(DateTimeOffset From, DateTimeOffset To, Guid? ProviderId, Guid? RoomId, Guid? BranchId, string[]? Statuses)
    : IQuery<IReadOnlyList<AppointmentDto>>, IRequiresAccess, IAuditable<IReadOnlyList<AppointmentDto>>
{
    public string Permission => SchedulingPermissions.Read;

    public AuditDescriptor Describe(IReadOnlyList<AppointmentDto> response) =>
        new("appointment.list", "appointment", null, AppointmentMapper.ToJson(new { results = response?.Count ?? 0 }));
}

public sealed class ListAppointmentsQueryValidator : AbstractValidator<ListAppointmentsQuery>
{
    private static readonly string[] Known = ["booked", "confirmed", "checked_in", "in_progress", "completed", "cancelled", "no_show", "rescheduled"];

    public ListAppointmentsQueryValidator()
    {
        RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("'to' must be after 'from'.");
        RuleFor(x => x).Must(x => x.To - x.From <= TimeSpan.FromDays(62)).WithMessage("The range cannot exceed 62 days.");
        RuleFor(x => x.Statuses).Must(s => s is null || s.All(Known.Contains)).WithMessage("Unknown status.");
    }
}

internal sealed class ListAppointmentsQueryHandler(ISchedulingReadModel readModel, IPatientDirectory patients, ICurrentUser user)
    : IRequestHandler<ListAppointmentsQuery, IReadOnlyList<AppointmentDto>>
{
    public async Task<Result<IReadOnlyList<AppointmentDto>>> Handle(ListAppointmentsQuery request, CancellationToken cancellationToken)
    {
        var scope = user.ScopeOf(SchedulingPermissions.Read);
        var query = new AppointmentQuery(
            request.From.ToUniversalTime(), request.To.ToUniversalTime(), request.ProviderId, request.RoomId, request.BranchId,
            scope == PermissionScope.Own ? null : user.BranchFilterFor(SchedulingPermissions.Read),
            scope == PermissionScope.Own ? user.UserId : null,
            request.Statuses);
        if (query.AllowedBranchIds is { Count: 0 })
        {
            return Result.Success<IReadOnlyList<AppointmentDto>>([]);
        }

        var rows = await readModel.ListAsync(query, cancellationToken);
        var names = await patients.GetNamesAsync(rows.Select(r => r.PatientId).Distinct().ToArray(), cancellationToken);
        IReadOnlyList<AppointmentDto> items = rows
            .Select(r => new AppointmentDto(r.Id, r.BranchId, r.PatientId, names.GetValueOrDefault(r.PatientId), r.ProviderId, r.RoomId, r.PeriodStart, r.PeriodEnd, r.Status, r.Reason, r.Source, r.RowVersion))
            .ToList();
        return Result.Success(items);
    }
}

// ---------------- Availability ----------------
public sealed record GetAvailabilityQuery(Guid ProviderId, DateOnly Date, int DurationMin) : IQuery<IReadOnlyList<SlotDto>>, IRequiresAccess
{
    public string Permission => SchedulingPermissions.Read;
}

public sealed class GetAvailabilityQueryValidator : AbstractValidator<GetAvailabilityQuery>
{
    public GetAvailabilityQueryValidator()
    {
        RuleFor(x => x.ProviderId).NotEmpty();
        RuleFor(x => x.DurationMin).InclusiveBetween(5, 480);
    }
}

internal sealed class GetAvailabilityQueryHandler(ISchedulingReadModel readModel, IOptions<SchedulingOptions> options, IClock clock)
    : IRequestHandler<GetAvailabilityQuery, IReadOnlyList<SlotDto>>
{
    public async Task<Result<IReadOnlyList<SlotDto>>> Handle(GetAvailabilityQuery request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var day = await readModel.GetProviderDayAsync(request.ProviderId, request.Date, o.Zone, null, cancellationToken);
        IReadOnlyList<SlotDto> slots = SchedulePlanner
            .FreeSlots(day, request.Date, TimeSpan.FromMinutes(request.DurationMin), TimeSpan.FromMinutes(o.SlotStepMinutes), o.Zone, clock.UtcNow)
            .Select(s => new SlotDto(s.Start, s.End))
            .ToList();
        return Result.Success(slots);
    }
}

// ---------------- Reschedule (merge-patch) ----------------
public sealed record RescheduleAppointmentCommand(Guid Id, int ExpectedRowVersion, IReadOnlyDictionary<string, JsonElement> Fields)
    : ICommand<AppointmentDto>, IRequiresAccess, IAuditable<AppointmentDto>
{
    public string Permission => SchedulingPermissions.Write;

    // Yalnız dəyişən sahələrin adları
    public AuditDescriptor Describe(AppointmentDto response) =>
        new("appointment.update", "appointment", Id, AppointmentMapper.ToJson(new { changed = Fields.Keys.Order().ToArray() }));
}

internal sealed class RescheduleAppointmentCommandHandler(
    ISchedulingRepository repository,
    ISchedulingReadModel readModel,
    IPatientDirectory patients,
    ICurrentUser user,
    ISchedulingUnitOfWork unitOfWork,
    IOptions<SchedulingOptions> options,
    IClock clock) : IRequestHandler<RescheduleAppointmentCommand, AppointmentDto>
{
    private static readonly HashSet<string> Known = ["providerId", "roomId", "start", "end", "reason"];

    public async Task<Result<AppointmentDto>> Handle(RescheduleAppointmentCommand request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var appointment = await repository.GetAsync(request.Id, cancellationToken);
        if (appointment is null || !user.CanAccess(SchedulingPermissions.Write, appointment.BranchId, appointment.ProviderId))
        {
            return Error.NotFound("appointment.not_found", "Appointment not found.");
        }

        if (appointment.RowVersion != request.ExpectedRowVersion)
        {
            return Error.PreconditionFailed("concurrency.stale", "The appointment was modified by someone else. Reload and retry.");
        }

        var unknown = request.Fields.Keys.FirstOrDefault(k => !Known.Contains(k));
        if (unknown is not null)
        {
            return Error.Validation("patch.unknown_field", $"Field '{unknown}' cannot be updated.");
        }

        var provider = appointment.ProviderId;
        var room = appointment.RoomId;
        var start = appointment.Slot.Start;
        var end = appointment.Slot.End;
        var reason = appointment.Reason;
        var startGiven = false;
        var endGiven = false;

        foreach (var (key, value) in request.Fields)
        {
            switch (key)
            {
                case "providerId" when value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var p):
                    provider = p;
                    break;
                case "roomId" when value.ValueKind == JsonValueKind.Null:
                    room = null;
                    break;
                case "roomId" when value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var r):
                    room = r;
                    break;
                case "start" when TryTime(value, out var s):
                    start = s;
                    startGiven = true;
                    break;
                case "end" when TryTime(value, out var e):
                    end = e;
                    endGiven = true;
                    break;
                case "reason" when value.ValueKind is JsonValueKind.Null or JsonValueKind.String:
                    reason = value.GetString();
                    break;
                default:
                    return Error.Validation("patch.invalid_value", $"Field '{key}' has an invalid value.");
            }
        }

        // Yalnız başlanğıc verilibsə müddət qorunur (sürüklə-burax)
        if (startGiven && !endGiven)
        {
            end = start + appointment.Slot.Duration;
        }

        var slot = new TimeSlot(start, end);
        if (provider != appointment.ProviderId && !user.CanAccess(SchedulingPermissions.Write, appointment.BranchId, provider))
        {
            return Error.Forbidden("permission.denied", "You cannot move the appointment to this provider's calendar.");
        }

        var changed = appointment.Reschedule(provider, room, slot, reason, now);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        var roomCheck = await SchedulingRules.ValidateRoomAsync(readModel, room, appointment.BranchId, cancellationToken);
        if (roomCheck.IsFailure)
        {
            return roomCheck.Error!;
        }

        var schedule = await SchedulingRules.ValidateScheduleAsync(readModel, provider, slot, o.Zone, appointment.Id, cancellationToken);
        if (schedule.IsFailure)
        {
            return schedule.Error!;
        }

        var patient = await patients.FindAsync(appointment.PatientId, cancellationToken);
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await repository.CancelPendingRemindersAsync(appointment.Id, cancellationToken);
            repository.AddReminders(SchedulePlanner
                .PlanReminders(slot.Start, patient?.PreferredChannel ?? "none", o.DefaultReminderMinutes, now)
                .Select(r => AppointmentReminder.Create(appointment.Id, r.Channel, r.SendAt)));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex)
        {
            return await SchedulingRules.MapConflictAsync(ex, readModel, provider, slot, o, appointment.Id, now, cancellationToken);
        }

        return AppointmentMapper.ToDto(appointment, patient?.FullName);
    }

    private static bool TryTime(JsonElement v, out DateTimeOffset result)
    {
        result = default;
        if (v.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        result = parsed.ToUniversalTime();
        return true;
    }
}

// ---------------- Cancel / No-show ----------------
public sealed record CancelAppointmentCommand(Guid Id, string? Reason) : ICommand<AppointmentDto>, IRequiresAccess, IAuditable<AppointmentDto>
{
    public string Permission => SchedulingPermissions.Write;

    public AuditDescriptor Describe(AppointmentDto response) => new("appointment.cancel", "appointment", Id);
}

public sealed record MarkNoShowCommand(Guid Id) : ICommand<AppointmentDto>, IRequiresAccess, IAuditable<AppointmentDto>
{
    public string Permission => SchedulingPermissions.Write;

    public AuditDescriptor Describe(AppointmentDto response) => new("appointment.no_show", "appointment", Id);
}

internal sealed class CancelAppointmentCommandHandler(
    ISchedulingRepository repository, IPatientDirectory patients, ICurrentUser user, ISchedulingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CancelAppointmentCommand, AppointmentDto>
{
    public Task<Result<AppointmentDto>> Handle(CancelAppointmentCommand request, CancellationToken cancellationToken) =>
        Transition.RunAsync(request.Id, a => a.Cancel(request.Reason, clock.UtcNow), repository, patients, user, unitOfWork, cancellationToken);
}

internal sealed class MarkNoShowCommandHandler(
    ISchedulingRepository repository, IPatientDirectory patients, ICurrentUser user, ISchedulingUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<MarkNoShowCommand, AppointmentDto>
{
    public Task<Result<AppointmentDto>> Handle(MarkNoShowCommand request, CancellationToken cancellationToken) =>
        Transition.RunAsync(request.Id, a => a.MarkNoShow(clock.UtcNow), repository, patients, user, unitOfWork, cancellationToken);
}

internal static class Transition
{
    /// <summary>Statusu dəyişir və gözləyən xatırlatmaları bir tranzaksiyada ləğv edir (ləğv olunmuş qəbula SMS getməsin).</summary>
    public static async Task<Result<AppointmentDto>> RunAsync(
        Guid id, Func<Appointment, Result> action, ISchedulingRepository repository, IPatientDirectory patients,
        ICurrentUser user, ISchedulingUnitOfWork unitOfWork, CancellationToken ct)
    {
        var appointment = await repository.GetAsync(id, ct);
        if (appointment is null || !user.CanAccess(SchedulingPermissions.Write, appointment.BranchId, appointment.ProviderId))
        {
            return Error.NotFound("appointment.not_found", "Appointment not found.");
        }

        var result = action(appointment);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(ct))
        {
            await repository.CancelPendingRemindersAsync(id, ct);
            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var names = await patients.GetNamesAsync([appointment.PatientId], ct);
        return AppointmentMapper.ToDto(appointment, names.GetValueOrDefault(appointment.PatientId));
    }
}
