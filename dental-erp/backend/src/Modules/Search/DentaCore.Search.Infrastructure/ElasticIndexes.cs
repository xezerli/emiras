using System.Globalization;

namespace DentaCore.Search.Infrastructure;

/// <summary>
/// İndeks adları və xəritələmə. HƏR tenant-ın ayrı indeksi var (<c>dc-patients-&lt;tenantId&gt;</c>, aylıq <c>dc-audit-&lt;tenantId&gt;-yyyy.MM</c>):
/// tenant-lar arası sızma təsadüfən yazılmış bir filtrdən asılı deyil. Sorğu həmişə tenant id-dən qurulan adla gedir, istifadəçi daxil etdiyi ad ilə yox;
/// əlavə olaraq hər sənəddə və hər sorğuda <c>tenantId</c> filtri var.
/// </summary>
internal static class ElasticIndexes
{
    public static string Patients(string prefix, Guid tenantId) => string.Create(CultureInfo.InvariantCulture, $"{prefix}-patients-{tenantId:N}");

    public static string Audit(string prefix, Guid tenantId, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}-audit-{tenantId:N}-{at.UtcDateTime:yyyy.MM}");

    public static string AuditPattern(string prefix, Guid tenantId) => string.Create(CultureInfo.InvariantCulture, $"{prefix}-audit-{tenantId:N}-*");

    /// <summary>
    /// Azərbaycan hərfləri: ə→e, ı→i, İ→i, ö→o, ü→u, ş→s, ç→c, ğ→g (böyük hərflər də). "Əliyev" ilə "Aliyev" və "Eliyev" eyni tokenə düşür.
    /// Əvvəl simvol xəritəsi, sonra lowercase və asciifolding. <c>az_prefix</c> indeksdə edge-ngram (yazdıqca axtarış), sorğuda isə yalnız <c>az_fold</c>.
    /// </summary>
    public static string PatientsBody(int shards, int replicas) => $$"""
        {
          "settings": {
            "number_of_shards": {{shards}}, "number_of_replicas": {{replicas}},
            "analysis": {
              "char_filter": { "az_map": { "type": "mapping", "mappings": [
                "ə=>e","Ə=>e","ı=>i","İ=>i","I=>i","ö=>o","Ö=>o","ü=>u","Ü=>u","ş=>s","Ş=>s","ç=>c","Ç=>c","ğ=>g","Ğ=>g" ] } },
              "filter": { "az_edge": { "type": "edge_ngram", "min_gram": 2, "max_gram": 20 } },
              "analyzer": {
                "az_fold":   { "type": "custom", "char_filter": ["az_map"], "tokenizer": "standard", "filter": ["lowercase", "asciifolding"] },
                "az_prefix": { "type": "custom", "char_filter": ["az_map"], "tokenizer": "standard", "filter": ["lowercase", "asciifolding", "az_edge"] }
              }
            }
          },
          "mappings": {
            "dynamic": "strict",
            "properties": {
              "tenantId":  { "type": "keyword" },
              "patientId": { "type": "keyword" },
              "branchId":  { "type": "keyword" },
              "chartNo":   { "type": "long" },
              "chartNoText": { "type": "keyword" },
              "status":    { "type": "keyword" },
              "firstName":  { "type": "text", "analyzer": "az_prefix", "search_analyzer": "az_fold", "fields": { "fold": { "type": "text", "analyzer": "az_fold" } } },
              "lastName":   { "type": "text", "analyzer": "az_prefix", "search_analyzer": "az_fold", "fields": { "fold": { "type": "text", "analyzer": "az_fold" } } },
              "fatherName": { "type": "text", "analyzer": "az_prefix", "search_analyzer": "az_fold", "fields": { "fold": { "type": "text", "analyzer": "az_fold" } } },
              "fullName":   { "type": "text", "analyzer": "az_prefix", "search_analyzer": "az_fold", "fields": { "fold": { "type": "text", "analyzer": "az_fold" } } }
            }
          }
        }
        """;

    public static string AuditBody(int shards, int replicas) => $$"""
        {
          "settings": { "number_of_shards": {{shards}}, "number_of_replicas": {{replicas}} },
          "mappings": {
            "dynamic": "strict",
            "properties": {
              "tenantId":   { "type": "keyword" },
              "auditId":    { "type": "long" },
              "occurredAt": { "type": "date" },
              "action":     { "type": "keyword" },
              "entity":     { "type": "keyword" },
              "entityId":   { "type": "keyword" },
              "userId":     { "type": "keyword" },
              "details":    { "type": "object", "enabled": false }
            }
          }
        }
        """;
}
