using System.Reflection;
using NetScope.Adapters;

namespace NetScope.Adapters.Tests;

/// <summary>
/// Static boundary assertions. They complement the behavioural tests: the product assembly must not
/// even contain a write, process-launch or network API, and no public member may offer a stop,
/// switch or restart action for a client.
/// </summary>
public sealed class ReadOnlyBoundaryTests
{
    private static readonly string[] ForbiddenApiFragments =
    [
        "Process.Start",
        "ProcessStartInfo",
        "Process.Kill",
        "Environment.SetEnvironmentVariable",
        "SetEnvironmentVariable(",
        "Registry.SetValue",
        "CreateSubKey",
        "DeleteSubKey",
        "SetValue(",
        "HttpClient",
        "WebRequest",
        "WebClient",
        "TcpClient",
        "UdpClient",
        "Socket.Connect",
        "Dns.GetHost",
        "File.WriteAll",
        "File.AppendAll",
        "File.Create(",
        "File.Delete(",
        "Directory.CreateDirectory",
        "Directory.Delete(",
        "Start-Process",
        "Stop-Process",
        "netsh",
        "schtasks",
        "reg add",
        "reg delete",
        "Set-ItemProperty",
        "New-ItemProperty",
    ];

    private static readonly string[] ForbiddenMemberFragments =
    [
        "stop",
        "switch",
        "restart",
        "kill",
        "terminate",
        "shutdown",
        "takeover",
    ];

    [Fact]
    public void Product_source_contains_no_write_launch_or_network_api()
    {
        var directory = RepoPaths.ProductSourceDirectory;
        var files = Directory
            .GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenApiFragments)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Product_project_has_no_package_reference_and_only_application_domain_references()
    {
        var projectPath = Path.Combine(RepoPaths.ProductSourceDirectory, "NetScope.Adapters.csproj");
        var text = File.ReadAllText(projectPath);

        Assert.DoesNotContain("PackageReference", text, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows</TargetFramework>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Collectors.Windows", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NetScope.Storage", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NetScope.Desktop", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_api_exposes_no_stop_switch_or_restart_action()
    {
        var assembly = typeof(GenericAdapter).Assembly;
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var name = member.Name.ToLowerInvariant();
                foreach (var forbidden in ForbiddenMemberFragments)
                {
                    Assert.DoesNotContain(forbidden, name, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Tests_use_only_synthetic_fixtures_under_the_adapters_test_tree()
    {
        var sourceFixtureDirectory = Path.Combine(RepoPaths.RepoRoot, "tests", "Adapters", "Fixtures");
        Assert.True(Directory.Exists(sourceFixtureDirectory));
        Assert.True(Directory.Exists(RepoPaths.FixtureDirectory));

        var files = Directory.GetFiles(sourceFixtureDirectory).Where(path =>
            path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.Contains("synthetic", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }
}
