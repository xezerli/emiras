using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Npgsql;

// DEV/E2E ALƏTİ: platform sxemini, tenant sxemini və başlanğıc verilənləri yaradır. Təkrar işə salmaq təhlükəsizdir (idempotent).
//   DENTACORE_DB=<Npgsql bağlantısı>  Security__PiiEncryptionKey=<base64>  Security__PiiHashKey=<base64>
//   dotnet run --project backend/tools/DentaCore.DevSeed -- [slug=demo] [parol]
// Parol arqumenti verilməsə təsadüfi yaradılır və yalnız stdout-a yazılır.
var slug = args.Length > 0 ? args[0] : "demo";
var password = args.Length > 1 && args[1].Length >= 8 ? args[1] : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
var db = Required("DENTACORE_DB");
if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9]{1,30}$"))
{
    Console.Error.WriteLine("slug yalnız kiçik hərf və rəqəm ola bilər.");
    return 2;
}

var schema = $"t_{slug}";
var pii = new AesGcmPiiProtector(Options.Create(new PiiOptions { PiiEncryptionKey = Required("Security__PiiEncryptionKey"), PiiHashKey = Required("Security__PiiHashKey") }));
var migrations = FindMigrations();

await using var admin = new NpgsqlDataSourceBuilder(db).Build();
if (await ScalarAsync(admin, "SELECT to_regclass('platform.tenants')::text") is null)
{
    foreach (var f in Directory.GetFiles(Path.Combine(migrations, "platform"), "P*.sql").Order())
    {
        await ExecAsync(admin, await File.ReadAllTextAsync(f));
    }
}

var tenantId = Guid.NewGuid();
if (await ScalarAsync(admin, $"SELECT 1 FROM information_schema.schemata WHERE schema_name = '{schema}'") is null)
{
    await ExecAsync(admin, $"CREATE SCHEMA {schema}");
    var tenantDb = new NpgsqlConnectionStringBuilder(db) { SearchPath = $"{schema},public" }.ConnectionString;
    await using var tenantSource = NpgsqlDataSource.Create(tenantDb);
    foreach (var f in Directory.GetFiles(Path.Combine(migrations, "tenant"), "T*.sql").Order())
    {
        await ExecAsync(tenantSource, await File.ReadAllTextAsync(f));
    }
}

await using (var cmd = admin.CreateCommand("INSERT INTO platform.tenants(id, slug, name, schema_name, plan_code) VALUES ($1, $2, $3, $4, 'starter') ON CONFLICT (slug) DO NOTHING"))
{
    cmd.Parameters.AddWithValue(tenantId);
    cmd.Parameters.AddWithValue(slug);
    cmd.Parameters.AddWithValue($"DentaCore {slug}");
    cmd.Parameters.AddWithValue(schema);
    await cmd.ExecuteNonQueryAsync();
}

var effectiveTenantId = (Guid)(await ScalarAsync(admin, $"SELECT id FROM platform.tenants WHERE slug = '{slug}'"))!;
await using var tenant = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db) { SearchPath = $"{schema},public" }.ConnectionString);

var branchId = await UpsertBranchAsync(tenant);
var hasher = new Argon2idPasswordHasher();
var users = new[]
{
    ("admin@" + slug + ".test", "Klinika Admini", "clinic_admin", (string?)null),
    ("reception@" + slug + ".test", "Qəbul Əməkdaşı", "reception", null),
    ("doctor@" + slug + ".test", "Dr. Əliyev", "doctor", "general"),
};
var providerId = Guid.Empty;
foreach (var (email, name, role, specialty) in users)
{
    var id = await UpsertUserAsync(tenant, email, name, role, specialty, branchId, hasher.Hash(password));
    if (specialty is not null)
    {
        providerId = id;
    }
}

// Həkim həftənin hər günü bütün gün işləyir (e2e günün istənilən saatında qəbul yarada bilsin). Təkrar işə salma qrafiki sıfırlayır.
await using (var cleanup = tenant.CreateCommand("DELETE FROM provider_schedules WHERE provider_id = $1"))
{
    cleanup.Parameters.AddWithValue(providerId);
    await cleanup.ExecuteNonQueryAsync();
}

for (var weekday = 1; weekday <= 7; weekday++)
{
    await using var cmd = tenant.CreateCommand(
        "INSERT INTO provider_schedules(provider_id, branch_id, weekday, start_time, end_time, valid_from) VALUES ($1, $2, $3, '00:00', '23:59', current_date - 1)");
    cmd.Parameters.AddWithValue(providerId);
    cmd.Parameters.AddWithValue(branchId);
    cmd.Parameters.AddWithValue((short)weekday);
    await cmd.ExecuteNonQueryAsync();
}

await using (var cmd = tenant.CreateCommand("INSERT INTO services(code, name, category, price) VALUES ('CONSULT', 'Konsultasiya', 'general', 30), ('FILLING', 'Plomb', 'restorative', 80) ON CONFLICT (code) DO NOTHING"))
{
    await cmd.ExecuteNonQueryAsync();
}

Console.WriteLine($"tenant={slug} id={effectiveTenantId} branch={branchId} provider={providerId}");
foreach (var (email, _, role, _) in users)
{
    Console.WriteLine($"{role,-13} {email}  password={password}");
}

return 0;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{name} təyin olunmalıdır.");

static string FindMigrations()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
    {
        dir = dir.Parent;
    }

    return dir is null ? throw new DirectoryNotFoundException("db/migrations tapılmadı") : Path.Combine(dir.FullName, "db", "migrations");
}

static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql)
{
    await using var cmd = source.CreateCommand(sql);
    var r = await cmd.ExecuteScalarAsync();
    return r is DBNull ? null : r;
}

static async Task ExecAsync(NpgsqlDataSource source, string sql)
{
    await using var cmd = source.CreateCommand(sql);
    await cmd.ExecuteNonQueryAsync();
}

async Task<Guid> UpsertBranchAsync(NpgsqlDataSource t)
{
    await using var cmd = t.CreateCommand("INSERT INTO branches(code, name, timezone) VALUES ('MAIN', 'Mərkəz', 'Asia/Baku') ON CONFLICT (code) DO NOTHING");
    await cmd.ExecuteNonQueryAsync();
    var id = (Guid)(await ScalarAsync(t, "SELECT id FROM branches WHERE code = 'MAIN'"))!;
    await using var room = t.CreateCommand("INSERT INTO rooms(branch_id, name) VALUES ($1, 'Kabinet 1') ON CONFLICT (branch_id, name) DO NOTHING");
    room.Parameters.AddWithValue(id);
    await room.ExecuteNonQueryAsync();
    return id;
}

async Task<Guid> UpsertUserAsync(NpgsqlDataSource t, string email, string fullName, string role, string? specialty, Guid branch, string hash)
{
    await using (var cmd = t.CreateCommand(
        "INSERT INTO users(email_hash, email_enc, password_hash, full_name, specialty, default_branch_id) VALUES ($1, $2, $3, $4, $5, $6) " +
        "ON CONFLICT (email_hash) DO UPDATE SET password_hash = EXCLUDED.password_hash, failed_logins = 0, locked_until = NULL, status = 'active'"))
    {
        cmd.Parameters.AddWithValue(pii.BlindIndex(email));
        cmd.Parameters.AddWithValue(pii.Encrypt(email));
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(fullName);
        cmd.Parameters.AddWithValue((object?)specialty ?? DBNull.Value);
        cmd.Parameters.AddWithValue(branch);
        await cmd.ExecuteNonQueryAsync();
    }

    await using var idCmd = t.CreateCommand("SELECT id FROM users WHERE email_hash = $1");
    idCmd.Parameters.AddWithValue(pii.BlindIndex(email));
    var id = (Guid)(await idCmd.ExecuteScalarAsync())!;
    await using var roleCmd = t.CreateCommand("INSERT INTO user_roles(user_id, role_id) SELECT $1, id FROM roles WHERE code = $2 ON CONFLICT DO NOTHING");
    roleCmd.Parameters.AddWithValue(id);
    roleCmd.Parameters.AddWithValue(role);
    await roleCmd.ExecuteNonQueryAsync();
    return id;
}
