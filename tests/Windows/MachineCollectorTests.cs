using System.Net;
using NetScope.Application;
using NetScope.Collectors.Windows;
using NetScope.Domain;

namespace NetScope.Windows.Tests;

/// <summary>
/// Structural checks against the real machine. They are read-only and never assert a specific
/// topology: the goal is to prove that a successful collection is well formed and that an
/// unavailable domain is reported as unavailable rather than as an empty, healthy value.
/// </summary>
public sealed class MachineCollectorTests
{
    private static CollectionContext Context() => new()
    {
        SessionId = "machine-test",
        BootId = "machine-boot",
        SnapshotId = "machine-snapshot",
        ObservedAt = DateTimeOffset.Now,
        MonotonicMs = 0,
        Synthetic = false,
    };

    private static Task<NetworkSnapshot> CollectAsync() =>
        new WindowsSnapshotCollector().CollectAsync(Context(), CancellationToken.None);

    [Fact]
    public async Task Every_domain_reports_a_status_and_at_a_minimum_the_collection_completes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = await CollectAsync();
        var completeness = snapshot.Completeness();

        Assert.Equal(4, completeness.Count);
        Assert.All(completeness.Values, status => Assert.True(Enum.IsDefined(status)));
        Assert.False(snapshot.Synthetic);
    }

    [Fact]
    public async Task A_successful_interface_read_is_never_an_empty_healthy_list()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = await CollectAsync();
        var interfaces = snapshot.Interfaces;

        if (interfaces.HasValue)
        {
            Assert.NotEmpty(interfaces.Value!);
            Assert.All(interfaces.Value!, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.InterfaceGuid));
                Assert.False(string.IsNullOrWhiteSpace(item.Name));
                if (item.IfIndex <= 0)
                {
                    Assert.Equal(EventStatus.Partial, interfaces.Status);
                    Assert.False(string.IsNullOrWhiteSpace(item.Reason));
                }
                Assert.False(string.IsNullOrWhiteSpace(item.Kind));
            });
        }
        else
        {
            // A gap must explain itself; it may not present as "no interfaces exist".
            Assert.False(string.IsNullOrWhiteSpace(interfaces.Reason));
            Assert.NotEqual(EventStatus.Ok, interfaces.Status);
        }
    }

    [Fact]
    public async Task Collected_routes_have_parseable_prefixes_and_a_known_protocol()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = await CollectAsync();
        var routes = snapshot.Routes;

        if (!routes.HasValue)
        {
            Assert.False(string.IsNullOrWhiteSpace(routes.Reason));
            return;
        }

        foreach (var route in routes.Value!)
        {
            Assert.True(route.Family is "ipv4" or "ipv6", $"unexpected family {route.Family}");

            var parts = route.DestinationPrefix.Split('/');
            Assert.Equal(2, parts.Length);
            Assert.True(IPAddress.TryParse(parts[0], out var address), $"unparsable prefix {route.DestinationPrefix}");

            var maxPrefix = route.Family == "ipv4" ? 32 : 128;
            Assert.InRange(int.Parse(parts[1]), 0, maxPrefix);
            Assert.Equal(route.Family == "ipv4", address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            Assert.False(string.IsNullOrWhiteSpace(route.Protocol));
            Assert.True(route.IfIndex >= 0);
        }
    }

    [Fact]
    public async Task Unknown_route_interface_metrics_are_reported_as_observation_gaps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = await CollectAsync();

        if (!snapshot.Routes.HasValue)
        {
            return;
        }

        foreach (var route in snapshot.Routes.Value!.Where(route => route.InterfaceMetric is null))
        {
            Assert.Null(route.TotalMetric);
            Assert.Equal(EventStatus.Partial, snapshot.Routes.Status);
            Assert.False(string.IsNullOrWhiteSpace(route.Reason));
            Assert.NotEmpty(snapshot.Routes.Errors);
        }
    }

    [Fact]
    public async Task Proxy_layers_each_report_a_status_and_a_provenance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = await CollectAsync();

        if (!snapshot.Proxies.HasValue)
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Proxies.Reason));
            return;
        }

        var proxies = snapshot.Proxies.Value!;

        var layers = new (string Name, Collected<ProxyLayer> Layer)[]
        {
            ("tool_process_environment", proxies.ToolProcessEnvironment),
            ("user_environment", proxies.UserEnvironment),
            ("machine_environment", proxies.MachineEnvironment),
            ("wininet", proxies.WinInet),
            ("winhttp", proxies.WinHttp),
        };

        Assert.All(layers, entry =>
        {
            Assert.True(Enum.IsDefined(entry.Layer.Status));

            if (entry.Layer.HasValue)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Layer.Value!.Provenance));

                Assert.All(entry.Layer.Value.Variables, AssertRedacted);
                if (entry.Layer.Value.ProxyServer is string proxy)
                {
                    Assert.Equal(proxy, SecretRedaction.RedactUrl(proxy));
                }
                if (entry.Layer.Value.AutoConfigUrl is string pac)
                {
                    Assert.Equal(pac, SecretRedaction.RedactPacUrl(pac));
                }
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Layer.Reason));
            }
        });
    }

    /// <summary>Collected values must already satisfy the field's minimization policy.</summary>
    private static void AssertRedacted(string variable)
    {
        var separator = variable.IndexOf('=', StringComparison.Ordinal);
        Assert.True(separator > 0);
        var name = variable[..separator];
        var value = variable[(separator + 1)..];
        var sanitized = name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase)
            ? SecretRedaction.RedactBypassList(value) : SecretRedaction.RedactUrl(value);
        Assert.Equal(value, sanitized);
    }

    [Fact]
    public async Task Collection_does_not_throw_when_the_machine_denies_a_domain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The collector turns every non-cancellation failure into a gap, so a denied registry or
        // a missing IP Helper entry cannot crash the diagnostic session.
        var snapshot = await CollectAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot.Completeness().Count);
    }
}
