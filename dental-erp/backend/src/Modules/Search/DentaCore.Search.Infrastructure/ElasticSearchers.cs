using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DentaCore.BuildingBlocks.Application;
using DentaCore.Search.Application;
using Microsoft.Extensions.Options;

namespace DentaCore.Search.Infrastructure;

internal sealed class ElasticPatientSearch(ElasticClient client, ITenantContext tenant, IOptions<ElasticOptions> options) : IPatientSearch
{
    public async Task<IReadOnlyList<PatientHit>> FindAsync(string query, IReadOnlyCollection<Guid>? branches, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tenantId = tenant.TenantId.ToString("N", CultureInfo.InvariantCulture);
        var filters = new JsonArray { new JsonObject { ["term"] = new JsonObject { ["tenantId"] = tenantId } } };
        if (branches is not null)
        {
            // Filial filtri sorğunun özündədir: icazəsiz sətirlər nəticəyə heç düşmür (sonradan süzülmür), hit sayı da sızmır
            var ids = new JsonArray();
            foreach (var b in branches)
            {
                ids.Add(b.ToString("N", CultureInfo.InvariantCulture));
            }

            filters.Add(new JsonObject { ["terms"] = new JsonObject { ["branchId"] = ids } });
        }

        var should = new JsonArray
        {
            // 1) yazdıqca (prefiks) axtarış, bütün sözlər üst-üstə gəlməlidir
            new JsonObject { ["multi_match"] = new JsonObject { ["query"] = query, ["fields"] = new JsonArray("lastName^3", "firstName^2", "fatherName", "fullName"), ["operator"] = "and" } },
            // 2) səhvlərə dözümlü axtarış (hərf səhvi): tam token üzərində fuzzy
            new JsonObject
            {
                ["multi_match"] = new JsonObject
                {
                    ["query"] = query, ["fields"] = new JsonArray("lastName.fold^3", "firstName.fold^2", "fatherName.fold", "fullName.fold"),
                    ["operator"] = "and", ["fuzziness"] = "AUTO", ["prefix_length"] = 1,
                },
            },
            // 3) kart nömrəsi dəqiq uyğunluq
            new JsonObject { ["term"] = new JsonObject { ["chartNoText"] = new JsonObject { ["value"] = query, ["boost"] = 10 } } },
        };
        var body = new JsonObject
        {
            ["size"] = limit,
            ["_source"] = new JsonArray("patientId", "branchId", "chartNo", "firstName", "lastName", "fatherName"),
            ["query"] = new JsonObject { ["bool"] = new JsonObject { ["should"] = should, ["minimum_should_match"] = 1, ["filter"] = filters } },
            ["sort"] = new JsonArray("_score", new JsonObject { ["chartNo"] = "asc" }),
        };

        using var result = await client.SearchAsync(ElasticIndexes.Patients(options.Value.IndexPrefix, tenant.TenantId), body.ToJsonString(), cancellationToken);
        var hits = new List<PatientHit>();
        foreach (var hit in result.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            hits.Add(new PatientHit(
                Guid.Parse(source.GetProperty("patientId").GetString()!), source.GetProperty("chartNo").GetInt64(),
                source.GetProperty("firstName").GetString()!, source.GetProperty("lastName").GetString()!,
                source.TryGetProperty("fatherName", out var father) && father.ValueKind == JsonValueKind.String ? father.GetString() : null,
                Guid.Parse(source.GetProperty("branchId").GetString()!), hit.TryGetProperty("_score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 0));
        }

        return hits;
    }
}

internal sealed class ElasticAuditSearch(ElasticClient client, ITenantContext tenant, IOptions<ElasticOptions> options) : IAuditSearch
{
    public async Task<AuditPage> SearchAsync(AuditFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var filters = new JsonArray { Term("tenantId", tenant.TenantId.ToString("N", CultureInfo.InvariantCulture)) };
        if (filter.Entity is not null)
        {
            filters.Add(Term("entity", filter.Entity));
        }

        if (filter.EntityId is { } entityId)
        {
            filters.Add(Term("entityId", entityId.ToString("D", CultureInfo.InvariantCulture)));
        }

        if (filter.UserId is { } userId)
        {
            filters.Add(Term("userId", userId.ToString("D", CultureInfo.InvariantCulture)));
        }

        if (filter.Action is not null)
        {
            filters.Add(Term("action", filter.Action));
        }

        if (filter.From is not null || filter.To is not null)
        {
            var range = new JsonObject();
            if (filter.From is { } from)
            {
                range["gte"] = from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            if (filter.To is { } to)
            {
                range["lte"] = to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            filters.Add(new JsonObject { ["range"] = new JsonObject { ["occurredAt"] = range } });
        }

        var body = new JsonObject
        {
            ["size"] = limit + 1,
            ["_source"] = new JsonArray("auditId", "occurredAt", "action", "entity", "entityId", "userId"),
            ["query"] = new JsonObject { ["bool"] = new JsonObject { ["filter"] = filters } },
            ["sort"] = new JsonArray(new JsonObject { ["occurredAt"] = "desc" }, new JsonObject { ["auditId"] = "desc" }),
        };
        if (cursor is not null)
        {
            body["search_after"] = ParseCursor(cursor);
        }

        using var result = await client.SearchAsync(ElasticIndexes.AuditPattern(options.Value.IndexPrefix, tenant.TenantId), body.ToJsonString(), cancellationToken);
        var items = new List<AuditHit>();
        foreach (var hit in result.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var s = hit.GetProperty("_source");
            items.Add(new AuditHit(
                s.GetProperty("auditId").GetInt64(), s.GetProperty("occurredAt").GetDateTimeOffset(), s.GetProperty("action").GetString()!,
                Str(s, "entity"), Str(s, "entityId") is { } e ? Guid.Parse(e) : null, Str(s, "userId") is { } u ? Guid.Parse(u) : null));
        }

        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = string.Create(CultureInfo.InvariantCulture, $"{last.OccurredAt.ToUnixTimeMilliseconds()}_{last.AuditId}");
        }

        return new AuditPage(items, next);
    }

    private static JsonObject Term(string field, string value) => new() { ["term"] = new JsonObject { [field] = value } };

    private static string? Str(JsonElement source, string name) => source.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonArray ParseCursor(string cursor)
    {
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new FormatException("Invalid cursor.");
        }

        return new JsonArray(ms, id);
    }
}
