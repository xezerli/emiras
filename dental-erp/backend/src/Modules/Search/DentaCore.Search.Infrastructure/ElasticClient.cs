using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DentaCore.Search.Application;
using Microsoft.Extensions.Options;

namespace DentaCore.Search.Infrastructure;

public sealed class ElasticOptions
{
    public const string Section = "Elasticsearch";

    public string Uri { get; set; } = "http://localhost:9200";

    /// <summary>İstehsalda API açarı (Base64 "id:key"). Boşdursa Username/Password, o da boşdursa autentifikasiyasız (yalnız lokal).</summary>
    public string? ApiKey { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Bütün indekslərin prefiksi: mühitləri və testləri bir klasterdə izolə edir.</summary>
    public string IndexPrefix { get; set; } = "dc";

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int Shards { get; set; } = 1;

    public int Replicas { get; set; }

    /// <summary>Indexer dövrləri arasındakı gözləmə.</summary>
    public int IndexerIntervalSeconds { get; set; } = 2;
}

/// <summary>
/// Elasticsearch REST sarğısı (rəsmi client paketi əvəzinə, çünki istifadə olunan əməliyyatlar azdır: indeks yarat, bulk, axtarış).
/// Şəbəkə xətası, timeout və 5xx <see cref="SearchUnavailableException"/> olur (klient ehtiyat yola keçə bilər);
/// 4xx proqramçı xətasıdır və açıq mesajla istisna atır.
/// </summary>
public sealed class ElasticClient(HttpClient http)
{
    private static readonly ConcurrentDictionary<string, bool> Ensured = new(StringComparer.Ordinal);

    internal async Task EnsureIndexAsync(string index, string bodyJson, CancellationToken cancellationToken)
    {
        var key = $"{http.BaseAddress}|{index}";
        if (Ensured.ContainsKey(key))
        {
            return;
        }

        using var response = await SendAsync(HttpMethod.Put, $"/{index}", bodyJson, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!text.Contains("resource_already_exists_exception", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Elasticsearch rejected index '{index}': {text}");
            }
        }
        else
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        Ensured[key] = true;
    }

    internal async Task<JsonDocument> SearchAsync(string indexOrPattern, string bodyJson, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/{indexOrPattern}/_search?ignore_unavailable=true&allow_no_indices=true", bodyJson, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// Sənədi yalnız mövcud deyilsə yazır (<c>_create</c>): audit sənədi təkrar çatdırılmada dəyişmir. 409 = artıq var, uğurdur.
    /// </summary>
    internal async Task CreateIfAbsentAsync(string index, string id, string bodyJson, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/{index}/_create/{Uri.EscapeDataString(id)}", bodyJson, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    /// <summary>
    /// Bulk. Per-sənəd nəticə yoxlanılır: bulk HTTP 200 qaytarsa da ayrı sətirlər uğursuz ola bilər. 409 (daha yeni versiya artıq var)
    /// və silmədə 404 gözlənilir; qalan xətalar istisna atır ki, indexer kursoru irəli aparmasın.
    /// </summary>
    internal async Task BulkAsync(string ndjson, CancellationToken cancellationToken)
    {
        if (ndjson.Length == 0)
        {
            return;
        }

        using var response = await SendAsync(HttpMethod.Post, "/_bulk", ndjson, cancellationToken, "application/x-ndjson");
        await EnsureSuccessAsync(response, cancellationToken);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!result.RootElement.GetProperty("errors").GetBoolean())
        {
            return;
        }

        foreach (var item in result.RootElement.GetProperty("items").EnumerateArray())
        {
            var op = item.EnumerateObject().First();
            var status = op.Value.GetProperty("status").GetInt32();
            if (status is < 300 or 409 || (op.Name == "delete" && status == 404))
            {
                continue;
            }

            throw new InvalidOperationException($"Elasticsearch bulk item failed ({status}): {op.Value.GetProperty("error").GetRawText()}");
        }
    }

    public async Task RefreshAsync(string indexOrPattern, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/{indexOrPattern}/_refresh?ignore_unavailable=true", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteIndexAsync(string indexOrPattern, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"/{indexOrPattern}?ignore_unavailable=true", null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        foreach (var key in Ensured.Keys.Where(k => k.EndsWith("|" + indexOrPattern, StringComparison.Ordinal)).ToList())
        {
            Ensured.TryRemove(key, out _);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        try
        {
            var response = await http.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                throw new SearchUnavailableException($"Elasticsearch answered {(int)response.StatusCode}: {Truncate(text)}");
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new SearchUnavailableException("Elasticsearch is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SearchUnavailableException("Elasticsearch timed out.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Elasticsearch request failed ({(int)response.StatusCode}): {Truncate(await response.Content.ReadAsStringAsync(cancellationToken))}");
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}

internal static class ElasticHttp
{
    public static void Configure(HttpClient client, ElasticOptions options)
    {
        client.BaseAddress = new Uri(options.Uri.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", options.ApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        }
    }
}
