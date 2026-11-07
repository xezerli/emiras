using System.Text.Json;
using System.Text.RegularExpressions;

namespace DentaCore.Architecture.Tests;

/// <summary>Deploy-a təsir edən konfiqurasiya qaydaları: sirr olmaması və PHI-yə yol açan log səviyyəsi.</summary>
public partial class HostConfigurationTests
{
    private static List<string> AppSettingsFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var files = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "Hosts"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count >= 6, "every host must be found");
        return files;
    }

    [Fact]
    public void Request_logging_stays_at_Warning_so_query_strings_never_reach_the_logs()
    {
        // "Request starting HTTP/1.1 GET /v1/patients?q=<ad>" Information səviyyəsində yazılır və axtarış mətni PHI-dir
        foreach (var file in AppSettingsFiles().Where(f => Path.GetFileName(f) == "appsettings.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var levels = doc.RootElement.GetProperty("Logging").GetProperty("LogLevel");
            var level = levels.GetProperty("Microsoft.AspNetCore").GetString();

            Assert.True(level is "Warning" or "Error" or "Critical" or "None", $"{Path.GetFileName(Path.GetDirectoryName(file))}: Microsoft.AspNetCore={level}");
        }
    }

    [Fact]
    public void Committed_settings_contain_no_secrets()
    {
        var forbidden = new[]
        {
            Pem(), new Regex(@"password\s*=\s*[^;""\s]+", RegexOptions.IgnoreCase), new Regex(@"amqps?://[^:/\s""]+:[^@\s""]+@"), new Regex(@"(Encryption|Hash)Key""\s*:\s*""[^""]+"""),
        };
        foreach (var file in AppSettingsFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in forbidden)
            {
                Assert.False(pattern.IsMatch(text), $"{file}: secret-looking value matched {pattern}");
            }
        }
    }

    [GeneratedRegex("-----BEGIN [A-Z ]*(PRIVATE KEY|PUBLIC KEY)-----")]
    private static partial Regex Pem();
}
