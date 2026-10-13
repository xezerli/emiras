namespace DentaCore.Audit.Application;

/// <summary>Zəncir yoxlamasının nəticəsi. FirstBrokenId null isə zəncir bütövdür.</summary>
public sealed record AuditChainResult(bool IsIntact, long EntriesChecked, long? FirstBrokenId);

public interface IAuditChainVerifier
{
    /// <summary>Bütün audit_log zəncirini yenidən hesablayıb saxlanmış heşlərlə müqayisə edir (dəyişdirmə/silmə/əlavə aşkarı).</summary>
    Task<AuditChainResult> VerifyAsync(CancellationToken cancellationToken);
}
