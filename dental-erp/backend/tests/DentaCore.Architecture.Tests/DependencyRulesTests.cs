using System.Xml.Linq;

namespace DentaCore.Architecture.Tests;

/// <summary>
/// Clean Architecture və modul sərhədlərini .csproj ProjectReference qrafı üzərində yoxlayır.
/// Boş layihələrdə belə işləyir (kompilyator istifadə olunmayan referansı assembly-dən silir, ona görə
/// assembly əsaslı testlər etibarsız olardı).
/// </summary>
public class DependencyRulesTests
{
    private sealed record Proj(string Name, string Layer, string? Module, IReadOnlyList<string> Refs, IReadOnlyList<string> Packages);

    private static readonly Lazy<List<Proj>> Projects = new(Load);

    private static List<Proj> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);

        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var xml = XDocument.Load(path);
                var refs = xml.Descendants("ProjectReference")
                    .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
                    .ToList();
                var pkgs = xml.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToList();
                var parts = name.Split('.');   // DentaCore.<Module>.<Layer> | DentaCore.BuildingBlocks.<Layer> | DentaCore.<Host>(.Api)
                var layer = parts[^1];
                var module = path.Contains($"{Path.DirectorySeparatorChar}Modules{Path.DirectorySeparatorChar}") ? parts[1] : null;
                if (path.Contains($"{Path.DirectorySeparatorChar}Hosts{Path.DirectorySeparatorChar}")) layer = "Host";
                return new Proj(name, layer, module, refs, pkgs);
            }).ToList();
    }

    private static IEnumerable<Proj> Layer(string layer) => Projects.Value.Where(p => p.Layer == layer);

    [Fact]
    public void Solution_is_not_empty() => Assert.True(Projects.Value.Count >= 30);

    [Fact]
    public void Domain_depends_only_on_BuildingBlocks_Domain_and_has_no_packages()
    {
        foreach (var p in Layer("Domain"))
        {
            Assert.All(p.Refs, r => Assert.Equal("DentaCore.BuildingBlocks.Domain", r));
            Assert.Empty(p.Packages);
        }
    }

    [Fact]
    public void Application_never_references_Infrastructure_or_Hosts()
    {
        foreach (var p in Layer("Application"))
            Assert.DoesNotContain(p.Refs, r => r.EndsWith(".Infrastructure", StringComparison.Ordinal) || r.EndsWith(".Api", StringComparison.Ordinal) || r.EndsWith("Workers", StringComparison.Ordinal));
    }

    [Fact]
    public void Contracts_have_no_project_dependencies()
    {
        foreach (var p in Layer("Contracts")) Assert.Empty(p.Refs);
    }

    [Fact]
    public void Modules_talk_to_each_other_only_through_Contracts()
    {
        foreach (var p in Projects.Value.Where(x => x.Module is not null))
            foreach (var r in p.Refs.Where(r => r.StartsWith("DentaCore.", StringComparison.Ordinal) && !r.StartsWith("DentaCore.BuildingBlocks", StringComparison.Ordinal)))
            {
                var otherModule = r.Split('.')[1];
                if (otherModule != p.Module) Assert.EndsWith(".Contracts", r, StringComparison.Ordinal);
            }
    }

    [Fact]
    public void Hosts_reference_only_module_Infrastructure_projects()
    {
        foreach (var h in Layer("Host"))
            Assert.All(h.Refs, r => Assert.EndsWith(".Infrastructure", r, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildingBlocks_Domain_has_no_dependencies()
    {
        var p = Projects.Value.Single(x => x.Name == "DentaCore.BuildingBlocks.Domain");
        Assert.Empty(p.Refs);
        Assert.Empty(p.Packages);
    }
}
