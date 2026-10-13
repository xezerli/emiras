namespace DentaCore.BuildingBlocks.Application;

/// <summary>
/// Audit qeydi. PHI (ad, telefon, diaqnoz) bura YAZILMIR: yalnız kim, nə, hansı obyekt, hansı sahələr dəyişib.
/// </summary>
public sealed record AuditRecord(
    string Action,
    string? Entity,
    Guid? EntityId,
    Guid? UserId,
    string? Ip,
    DateTimeOffset OccurredAt,
    string? DetailsJson = null);

/// <summary>Audit zənciri (Mərhələ 2 §2): append-only, hər qeyd əvvəlkinin heş-ini daşıyır.</summary>
public interface IAuditTrail
{
    Task RecordAsync(AuditRecord record, CancellationToken cancellationToken);
}

public sealed record AuditDescriptor(string Action, string? Entity, Guid? EntityId, string? DetailsJson = null);

/// <summary>Auditə yazılmalı sorğu. Describe uğurlu nəticədən istifadə edir (məs. yaradılan obyektin id-si).</summary>
public interface IAuditable<in TResponse>
{
    AuditDescriptor Describe(TResponse response);
}
