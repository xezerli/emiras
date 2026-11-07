using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Sync.Contracts;
using FluentValidation;

namespace DentaCore.Sync.Application;

public static class SyncLimits
{
    public const int MaxPushChanges = 200;
    public const int DefaultPullLimit = 500;
    public const int MaxPullLimit = 2000;
    public const int MaxSnapshotLimit = 1000;
}

public sealed record SyncChangeDto(long Seq, string Entity, Guid EntityId, string Op, int? RowVersion, JsonElement? Data);

/// <summary>Cursor = "bu xid-dən əvvəlki bütün tranzaksiyalar bitib" sərhədidir. Klient onu olduğu kimi saxlayıb növbəti sorğuda göndərir.</summary>
public sealed record SyncPullDto(IReadOnlyList<SyncChangeDto> Changes, long Cursor, bool HasMore);

public sealed record SnapshotItemDto(Guid EntityId, int? RowVersion, JsonElement Data);

public sealed record SnapshotDto(string Entity, IReadOnlyList<SnapshotItemDto> Items, Guid? NextAfter, long Cursor);

/// <summary>Oxuma tərəfi: cari istifadəçinin icazə və scope-una görə süzür.</summary>
public interface ISyncReader
{
    IReadOnlyList<string> Entities { get; }

    Task<Result<SyncPullDto>> PullAsync(long cursor, int limit, CancellationToken cancellationToken);

    Task<Result<SnapshotDto>> SnapshotAsync(string entity, Guid? after, int limit, CancellationToken cancellationToken);
}

public sealed record StoredChange(Guid UserId, string Outcome, string ResponseJson);

public interface ISyncClientChangeStore
{
    Task<StoredChange?> FindAsync(Guid clientChangeId, CancellationToken cancellationToken);

    Task SaveAsync(Guid clientChangeId, Guid deviceId, Guid userId, string entity, Guid entityId, string outcome, string responseJson, CancellationToken cancellationToken);
}

// ---------------- Pull ----------------
public sealed record PullChangesQuery(long Cursor, int Limit) : IQuery<SyncPullDto>;

public sealed class PullChangesQueryValidator : AbstractValidator<PullChangesQuery>
{
    public PullChangesQueryValidator()
    {
        RuleFor(x => x.Cursor).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Limit).InclusiveBetween(1, SyncLimits.MaxPullLimit);
    }
}

internal sealed class PullChangesQueryHandler(ISyncReader reader) : IRequestHandler<PullChangesQuery, SyncPullDto>
{
    public Task<Result<SyncPullDto>> Handle(PullChangesQuery request, CancellationToken cancellationToken) =>
        reader.PullAsync(request.Cursor, request.Limit, cancellationToken);
}

public sealed record SnapshotQuery(string Entity, Guid? After, int Limit) : IQuery<SnapshotDto>;

public sealed class SnapshotQueryValidator : AbstractValidator<SnapshotQuery>
{
    public SnapshotQueryValidator()
    {
        RuleFor(x => x.Entity).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Limit).InclusiveBetween(1, SyncLimits.MaxSnapshotLimit);
    }
}

internal sealed class SnapshotQueryHandler(ISyncReader reader) : IRequestHandler<SnapshotQuery, SnapshotDto>
{
    public Task<Result<SnapshotDto>> Handle(SnapshotQuery request, CancellationToken cancellationToken) =>
        reader.SnapshotAsync(request.Entity, request.After, request.Limit, cancellationToken);
}

// ---------------- Push ----------------
public sealed record PushChangeInput(string Entity, Guid EntityId, string Op, int? BaseVersion, JsonElement? Data, Guid ClientChangeId);

public sealed record AppliedDto(Guid ClientChangeId, Guid EntityId, int? RowVersion);

public sealed record ConflictDto(Guid ClientChangeId, string Entity, Guid EntityId, int? ServerVersion, JsonElement? ServerData, string Resolution);

public sealed record RejectedDto(Guid ClientChangeId, string Code, string? Message);

public sealed record PushResultDto(IReadOnlyList<AppliedDto> Applied, IReadOnlyList<ConflictDto> Conflicts, IReadOnlyList<RejectedDto> Rejected);

public sealed record PushChangesCommand(Guid DeviceId, IReadOnlyList<PushChangeInput> Changes) : ICommand<PushResultDto>, IAuditable<PushResultDto>
{
    public AuditDescriptor Describe(PushResultDto response) =>
        new("sync.push", "device", DeviceId, System.Text.Json.JsonSerializer.Serialize(new { applied = response?.Applied.Count, conflicts = response?.Conflicts.Count, rejected = response?.Rejected.Count }));
}

public sealed class PushChangesCommandValidator : AbstractValidator<PushChangesCommand>
{
    private static readonly string[] Ops = ["I", "U", "D"];

    public PushChangesCommandValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Changes).NotNull().Must(c => c is { Count: >= 1 and <= SyncLimits.MaxPushChanges }).WithMessage($"Between 1 and {SyncLimits.MaxPushChanges} changes are required.");
        RuleFor(x => x.Changes).Must(c => c.Select(x => x.ClientChangeId).Distinct().Count() == c.Count).WithMessage("clientChangeId values must be unique within a request.");
        RuleForEach(x => x.Changes).ChildRules(change =>
        {
            change.RuleFor(c => c.Entity).NotEmpty().MaximumLength(64);
            change.RuleFor(c => c.EntityId).NotEmpty();
            change.RuleFor(c => c.ClientChangeId).NotEmpty();
            change.RuleFor(c => c.Op).Must(op => Ops.Contains(op, StringComparer.Ordinal)).WithMessage("Op must be I, U or D.");
        });
    }
}

/// <summary>
/// Dəyişikliklər ardıcıl tətbiq olunur (klientin sırası). Hər dəyişiklik modulun öz əmri ilə gedir, ona görə icazə, scope, domen qaydaları və audit
/// adi API ilə eynidir. Nəticə (applied/conflict/rejected) <c>clientChangeId</c> ilə saxlanır: təkrar göndərmə effekti təkrarlamır, eyni cavabı qaytarır.
/// Gözlənilməz istisna nəticə kimi yazılmır (klient təkrar göndərə bilsin).
/// </summary>
internal sealed class PushChangesCommandHandler(IEnumerable<ISyncEntityHandler> handlers, ISyncClientChangeStore store, ICurrentUser user)
    : IRequestHandler<PushChangesCommand, PushResultDto>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<PushResultDto>> Handle(PushChangesCommand request, CancellationToken cancellationToken)
    {
        var byEntity = handlers.ToDictionary(h => h.Entity, StringComparer.Ordinal);
        var applied = new List<AppliedDto>();
        var conflicts = new List<ConflictDto>();
        var rejected = new List<RejectedDto>();

        foreach (var change in request.Changes)
        {
            var stored = await store.FindAsync(change.ClientChangeId, cancellationToken);
            if (stored is not null)
            {
                if (stored.UserId != user.UserId)
                {
                    rejected.Add(new RejectedDto(change.ClientChangeId, "sync.client_change_id_taken", "This clientChangeId belongs to another user."));
                    continue;
                }

                Replay(change.ClientChangeId, stored, applied, conflicts, rejected);
                continue;
            }

            if (!byEntity.TryGetValue(change.Entity, out var handler))
            {
                // Qeyd olunmur: dəstəklənməyən obyekt sonradan dəstəklənə bilər, klient yenidən göndərə bilsin
                rejected.Add(new RejectedDto(change.ClientChangeId, "sync.entity_not_writable", $"'{change.Entity}' cannot be changed offline."));
                continue;
            }

            var outcome = await handler.ApplyAsync(new SyncChangeRequest(change.Entity, change.EntityId, change.Op, change.BaseVersion, change.Data), cancellationToken);
            switch (outcome.Kind)
            {
                case SyncOutcomeKind.Applied:
                    var done = new AppliedDto(change.ClientChangeId, change.EntityId, outcome.RowVersion);
                    applied.Add(done);
                    await store.SaveAsync(change.ClientChangeId, request.DeviceId, user.UserId, change.Entity, change.EntityId, "applied", JsonSerializer.Serialize(done, Json), cancellationToken);
                    break;
                case SyncOutcomeKind.Conflict:
                    var conflict = new ConflictDto(change.ClientChangeId, change.Entity, change.EntityId, outcome.RowVersion, outcome.ServerData, "needs_manual");
                    conflicts.Add(conflict);
                    await store.SaveAsync(change.ClientChangeId, request.DeviceId, user.UserId, change.Entity, change.EntityId, "conflict", JsonSerializer.Serialize(conflict, Json), cancellationToken);
                    break;
                default:
                    var reject = new RejectedDto(change.ClientChangeId, outcome.Code ?? "sync.rejected", outcome.Message);
                    rejected.Add(reject);
                    await store.SaveAsync(change.ClientChangeId, request.DeviceId, user.UserId, change.Entity, change.EntityId, "rejected", JsonSerializer.Serialize(reject, Json), cancellationToken);
                    break;
            }
        }

        return new PushResultDto(applied, conflicts, rejected);
    }

    private static void Replay(Guid id, StoredChange stored, List<AppliedDto> applied, List<ConflictDto> conflicts, List<RejectedDto> rejected)
    {
        switch (stored.Outcome)
        {
            case "applied":
                applied.Add(JsonSerializer.Deserialize<AppliedDto>(stored.ResponseJson, Json)!);
                break;
            case "conflict":
                conflicts.Add(JsonSerializer.Deserialize<ConflictDto>(stored.ResponseJson, Json)!);
                break;
            default:
                rejected.Add(JsonSerializer.Deserialize<RejectedDto>(stored.ResponseJson, Json) ?? new RejectedDto(id, "sync.rejected", null));
                break;
        }
    }
}
