using System.Text.Json;

namespace DentaCore.Sync.Contracts;

/// <summary>Klientdən gələn tək dəyişiklik (push). <c>Data</c> obyektin biznes sahələridir (id və rowVersion ayrıca verilir).</summary>
public sealed record SyncChangeRequest(string Entity, Guid EntityId, string Op, int? BaseVersion, JsonElement? Data);

public enum SyncOutcomeKind
{
    Applied,
    Conflict,
    Rejected,
}

/// <summary>
/// Tətbiq nəticəsi. Applied: yeni rowVersion; Conflict: serverin cari vəziyyəti (klient əl ilə həll edir);
/// Rejected: biznes qaydası qəbul etmədi (kod və mesaj ilə), təkrar göndərməyin mənası yoxdur.
/// </summary>
public sealed record SyncApplyOutcome(SyncOutcomeKind Kind, int? RowVersion = null, JsonElement? ServerData = null, string? Code = null, string? Message = null)
{
    public static SyncApplyOutcome Applied(int rowVersion) => new(SyncOutcomeKind.Applied, rowVersion);

    public static SyncApplyOutcome Conflict(int serverVersion, JsonElement serverData) => new(SyncOutcomeKind.Conflict, serverVersion, serverData);

    public static SyncApplyOutcome Rejected(string code, string message) => new(SyncOutcomeKind.Rejected, Code: code, Message: message);
}

/// <summary>
/// Modulun offline yazıla bilən obyekti. Dəyişiklik modulun ÖZ əmrləri (domen qaydaları, icazə, audit) ilə tətbiq olunur;
/// Sync heç vaxt cədvələ birbaşa yazmır. İstisna atmaq "təkrar cəhd et" deməkdir (nəticə qeyd olunmur).
/// </summary>
public interface ISyncEntityHandler
{
    string Entity { get; }

    Task<SyncApplyOutcome> ApplyAsync(SyncChangeRequest change, CancellationToken cancellationToken);
}
