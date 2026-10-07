using System.Security.Cryptography;
using DentaCore.BuildingBlocks.Infrastructure.Security;
using DentaCore.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DentaCore.Identity.IntegrationTests;

/// <summary>İnteqrasiya testi yalnız DENTACORE_TEST_PG təyin olunubsa işləyir (Docker/Testcontainers CI-də Mərhələ 9-da).</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFixture.EnvVar)))
        {
            Skip = $"Set {PostgresFixture.EnvVar} to a PostgreSQL superuser connection string to run integration tests.";
        }
    }
}

/// <summary>
/// Hər test sinfi üçün təzə verilənlər bazası yaradır və PRODUKSİYADAKI EYNİ SQL miqrasiyalarını tətbiq edir
/// (db/migrations: trigger, constraint, partition daxil). Beləliklə testlər real sxemə qarşı işləyir.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string EnvVar = "DENTACORE_TEST_PG";
    public const string Password = "Correct-Horse-9!";

    private readonly string _dbName = "dc_it_" + Guid.NewGuid().ToString("N")[..10];
    private string _adminConnection = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;

    public string EncryptionKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string HashKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public Guid DemoTenantId { get; } = Guid.NewGuid();

    public Guid OtherTenantId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(baseConn))
        {
            return;
        }

        _adminConnection = baseConn;
        await ExecuteAsync(_adminConnection, $"CREATE DATABASE {_dbName}");
        ConnectionString = new NpgsqlConnectionStringBuilder(baseConn) { Database = _dbName }.ConnectionString;

        var migrations = FindMigrationsDirectory();
        foreach (var file in Directory.GetFiles(Path.Combine(migrations, "platform"), "P*.sql").Order())
        {
            await ExecuteAsync(ConnectionString, await File.ReadAllTextAsync(file));
        }

        foreach (var (slug, schema, id) in new[] { ("demo", "t_demo", DemoTenantId), ("other", "t_other", OtherTenantId) })
        {
            await ExecuteAsync(ConnectionString, $"CREATE SCHEMA {schema}");
            var tenantConn = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = $"{schema},public" }.ConnectionString;
            foreach (var file in Directory.GetFiles(Path.Combine(migrations, "tenant"), "T*.sql").Order())
            {
                await ExecuteAsync(tenantConn, await File.ReadAllTextAsync(file));
            }

            await ExecuteAsync(
                ConnectionString,
                $"INSERT INTO platform.tenants(id, slug, name, schema_name, plan_code) VALUES ('{id}', '{slug}', '{slug}', '{schema}', 'starter')");
        }
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_adminConnection))
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(_adminConnection, $"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    /// <summary>Tenant sxemində istifadəçi yaradır (parol Argon2id ilə heşlənir, email şifrələnir). Rol: reception.</summary>
    public async Task<Guid> SeedUserAsync(string schema, string email, string fullName = "Test User", string role = "reception")
    {
        var pii = new AesGcmPiiProtector(Options.Create(new PiiOptions { PiiEncryptionKey = EncryptionKey, PiiHashKey = HashKey }));
        var id = Guid.NewGuid();
        await using var conn = await OpenTenantAsync(schema);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users(id, email_hash, email_enc, password_hash, full_name) VALUES ($1, $2, $3, $4, $5)", conn);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(pii.BlindIndex(email));
        cmd.Parameters.AddWithValue(pii.Encrypt(email));
        cmd.Parameters.AddWithValue(new Argon2idPasswordHasher().Hash(Password));
        cmd.Parameters.AddWithValue(fullName);
        await cmd.ExecuteNonQueryAsync();

        await using var roleCmd = new NpgsqlCommand(
            "INSERT INTO user_roles(user_id, role_id) SELECT $1, id FROM roles WHERE code = $2", conn);
        roleCmd.Parameters.AddWithValue(id);
        roleCmd.Parameters.AddWithValue(role);
        await roleCmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<NpgsqlConnection> OpenTenantAsync(string schema)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = $"{schema},public" }.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<T?> ScalarAsync<T>(string schema, string sql, params object[] args)
    {
        await using var conn = await OpenTenantAsync(schema);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args)
        {
            cmd.Parameters.AddWithValue(a);
        }

        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public async Task ExecAsync(string schema, string sql, params object[] args)
    {
        await using var conn = await OpenTenantAsync(schema);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args)
        {
            cmd.Parameters.AddWithValue(a);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static string FindMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new DirectoryNotFoundException("db/migrations not found") : Path.Combine(dir.FullName, "db", "migrations");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
