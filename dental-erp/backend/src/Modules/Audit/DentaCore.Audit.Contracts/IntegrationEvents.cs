using System.Text.Json;

namespace DentaCore.Audit.Contracts;

/// <summary>
/// Routing key <c>audit.recorded</c>. Elasticsearch indexer və gələcək SIEM inteqrasiyası bunu oxuyur. Yük PHI-sizdir:
/// ad, telefon, diaqnoz yoxdur, yalnız kim, nə, hansı obyekt və dəyişən sahələr. Sahə əlavə etmək geriyə uyğundur.
/// </summary>
public sealed record AuditRecordedV1(long AuditId, string Action, string? Entity, Guid? EntityId, Guid? UserId, DateTimeOffset OccurredAt, JsonElement? Details);
