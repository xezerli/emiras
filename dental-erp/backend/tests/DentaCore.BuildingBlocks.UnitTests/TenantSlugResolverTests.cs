using DentaCore.BuildingBlocks.Infrastructure.Tenancy;

namespace DentaCore.BuildingBlocks.UnitTests;

public class TenantSlugResolverTests
{
    private static readonly string[] None = [];

    private static TenancyOptions Cloud(bool header = false) => new() { BaseDomain = "dentacore.app", AllowTenantHeader = header };

    [Theory]
    [InlineData("klinika.dentacore.app", "klinika")]
    [InlineData("KLINIKA.DentaCore.App", "KLINIKA")]
    [InlineData("a.b.dentacore.app", "a.b")]
    public void Cloud_resolves_the_slug_from_the_host_suffix(string host, string expected) =>
        Assert.Equal(expected, TenantSlugResolver.Resolve(host, None, Cloud()));

    [Theory]
    [InlineData("dentacore.app")]
    [InlineData(".dentacore.app")]
    [InlineData("evil.example")]
    [InlineData("klinika.dentacore.app.evil.example")]
    [InlineData("localhost")]
    public void Cloud_rejects_hosts_outside_the_base_domain_and_the_bare_domain(string host) =>
        Assert.Null(TenantSlugResolver.Resolve(host, None, Cloud()));

    [Fact]
    public void Header_is_ignored_unless_explicitly_allowed()
    {
        Assert.Null(TenantSlugResolver.Resolve("localhost", ["demo"], Cloud(header: false)));
        Assert.Equal("demo", TenantSlugResolver.Resolve("localhost", ["demo"], Cloud(header: true)));
    }

    [Fact]
    public void A_repeated_tenant_header_is_not_trusted()
    {
        Assert.Null(TenantSlugResolver.Resolve("localhost", ["a", "b"], Cloud(header: true)));
        Assert.Equal("fromhost", TenantSlugResolver.Resolve("fromhost.dentacore.app", ["a", "b"], Cloud(header: true)));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("clinic.local")]
    [InlineData("klinika.dentacore.app")]
    public void Local_server_always_uses_the_fixed_tenant_whatever_host_is_used(string host) =>
        Assert.Equal("klinika", TenantSlugResolver.Resolve(host, None, new TenancyOptions { FixedTenant = "klinika" }));

    [Fact]
    public void Fixed_tenant_cannot_be_overridden_by_header_or_host()
    {
        var options = new TenancyOptions { FixedTenant = "klinika", AllowTenantHeader = true };

        Assert.Equal("klinika", TenantSlugResolver.Resolve("other.dentacore.app", ["other"], options));
    }

    [Fact]
    public void Blank_fixed_tenant_means_cloud_mode()
    {
        Assert.Equal("k", TenantSlugResolver.Resolve("k.dentacore.app", None, new TenancyOptions { FixedTenant = "  " }));
    }
}
