using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>
/// T08-R2 privacy counterexamples. A device name can exist only in an error message, can be a
/// combined virtual-switch/filter name, or can appear in a different traversal order than the
/// structured interface list; all three must still be redacted to the same stable alias.
/// Every value here is synthetic.
/// </summary>
public sealed class T08R2AnonymizerTests
{
    private static readonly string[] ErrorOnlyNames =
    {
        "Synthetic-Uplink-B",
        "Synthetic-Uplink-C",
        "Synthetic-Uplink-E",
        "Synthetic-Uplink-F",
        "Synthetic-Uplink-G",
        "vEthernet (Default Switch)",
        "Default Switch",
        "vEthernet (WSL (Hyper-V firewall))",
        "Hyper-V firewall",
        "Hyper-V Virtual Ethernet Adapter #2",
        "WAN Miniport (PPPOE)",
        "Npcap Loopback Adapter",
        "Nice WFP Filter",
        "以太网 2",
    };

    [Fact]
    public async Task Names_only_in_error_text_and_combined_switch_filter_names_are_aliased()
    {
        using var workspace = new TempWorkspace();
        var session = workspace.File("t08r2-errors.ndjson");
        await WriteErrorOnlySessionAsync(session);
        var entries = T08R2Fixture.ExportOrThrow(session, workspace);

        T08R2Fixture.AssertAbsent(entries, ErrorOnlyNames);
        T08R2Fixture.AssertAbsent(entries,
            "Synthetic-Uplink-A", "sentinel-user", "SYNTHETIC_T08R2_TOKEN", "192.168.7.10");

        var all = T08R2Fixture.AllStrings(entries);
        var adapterErrors = all
            .Where(value => value.Contains("GetIfEntry2 failed for adapter", StringComparison.Ordinal) &&
                            value.Contains("synthetic metric unavailable", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(adapterErrors);
        Assert.All(adapterErrors, error => Assert.Contains("device-", error, StringComparison.Ordinal));

        // Over-redaction check: an ordinary rule name and technical error wording must survive.
        Assert.Contains(all, value => value.Contains("Synthetic Rule A", StringComparison.Ordinal));
        Assert.Contains(all, value => value.Contains("读取失败", StringComparison.Ordinal));

        // The structured interface identity and the same name quoted in an error share an alias.
        var names = InterfaceNames(entries);
        var structuredAlias = Assert.Single(names, name => name.StartsWith("device-", StringComparison.Ordinal) &&
            ErrtextOf(entries).Contains("\"" + name + "\"", StringComparison.Ordinal));
        Assert.StartsWith("device-", structuredAlias, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traversal_order_does_not_change_which_alias_an_identity_gets()
    {
        using var workspace = new TempWorkspace();

        var errorFirst = workspace.File("t08r2-error-first.ndjson");
        await T08R2Fixture.WriteAsync(errorFirst, new[]
        {
            new CycleEvidence
            {
                Snapshot = T08R2Fixture.Snapshot("snapshot-order-a-1", T08R2Fixture.Origin, 0,
                    interfaces: new[] { T08R2Fixture.Interface("interface-other-a", "Synthetic-Other-A", 12) },
                    proxyNotice: "interface \"Synthetic-Uplink-D\" is down"),
            },
            new CycleEvidence
            {
                Snapshot = T08R2Fixture.Snapshot("snapshot-order-a-2", T08R2Fixture.Origin.AddSeconds(10), 10000,
                    interfaces: new[]
                    {
                        T08R2Fixture.Interface("interface-other-a", "Synthetic-Other-A", 12),
                        T08R2Fixture.Interface("interface-d", "Synthetic-Uplink-D", 44),
                    }),
            },
        });

        var fieldFirst = workspace.File("t08r2-field-first.ndjson");
        await T08R2Fixture.WriteAsync(fieldFirst, new[]
        {
            new CycleEvidence
            {
                Snapshot = T08R2Fixture.Snapshot("snapshot-order-b-1", T08R2Fixture.Origin, 0,
                    interfaces: new[]
                    {
                        T08R2Fixture.Interface("interface-other-b", "Synthetic-Other-B", 12),
                        T08R2Fixture.Interface("interface-d", "Synthetic-Uplink-D", 44),
                    }),
            },
            new CycleEvidence
            {
                Snapshot = T08R2Fixture.Snapshot("snapshot-order-b-2", T08R2Fixture.Origin.AddSeconds(10), 10000,
                    interfaces: new[] { T08R2Fixture.Interface("interface-other-b", "Synthetic-Other-B", 12) },
                    proxyNotice: "interface \"Synthetic-Uplink-D\" is down"),
            },
        });

        var errorFirstEntries = T08R2Fixture.ExportOrThrow(errorFirst, workspace, "packages-error-first");
        var fieldFirstEntries = T08R2Fixture.ExportOrThrow(fieldFirst, workspace, "packages-field-first");

        foreach (var entries in new[] { errorFirstEntries, fieldFirstEntries })
        {
            T08R2Fixture.AssertAbsent(entries, "Synthetic-Uplink-D");
            var (aliasInNotice, interfaceNames) = ReadTraversalAlias(entries);
            Assert.False(string.IsNullOrEmpty(aliasInNotice), "the error text must still show a device alias");
            Assert.Contains(aliasInNotice, interfaceNames);
        }
    }

    private static async Task WriteErrorOnlySessionAsync(string path)
    {
        var snapshot = T08R2Fixture.Snapshot("snapshot-t08r2-1", T08R2Fixture.Origin, 0,
            interfaces: new[]
            {
                T08R2Fixture.Interface("interface-a", "Synthetic-Uplink-A", 12,
                    description: "Synthetic uplink with a synthetic path C:\\Users\\sentinel-user\\uplink.ini"),
            },
            interfacesStatus: EventStatus.Partial,
            interfacesReason: "Synthetic partial interface read.",
            interfacesErrors: new[]
            {
                "GetIfEntry2 failed for adapter \"Synthetic-Uplink-A\": synthetic metric unavailable",
                "GetIfEntry2 failed for adapter \"Synthetic-Uplink-B\": synthetic metric unavailable",
                "interface Synthetic-Uplink-C link down",
                "vEthernet (Default Switch) media state unknown",
                "vEthernet (WSL (Hyper-V firewall)) status read failed",
                "Hyper-V Virtual Ethernet Adapter #2 counters unavailable",
                "WAN Miniport (PPPOE) not readable",
                "Npcap Loopback Adapter read error",
                "filter 'Nice WFP Filter' rejected read",
                "接口 以太网 2 读取失败",
            },
            proxyNotice: "synthetic proxy read from C:\\Users\\sentinel-user\\proxy.ini",
            proxyErrors: new[] { "proxy read failed for adapter \"Synthetic-Uplink-E\"" });

        var probe = T08R2Fixture.Probe("probe-t08r2-1",
            "connect https://api.synthetic.invalid/v1/path?access_token=SYNTHETIC_T08R2_TOKEN failed for adapter \"Synthetic-Uplink-G\"",
            "synthetic probe notice mentions adapter \"Synthetic-Uplink-G\"",
            status: EventStatus.Timeout);
        var record = T08R2Fixture.Record("record-t08r2-1",
            "读取 C:\\Users\\sentinel-user\\AppData\\Roaming\\client\\config.json 失败 for interface Synthetic-Uplink-F",
            new Dictionary<string, string>
            {
                ["snapshot_id"] = "snapshot-t08r2-1",
                ["user_name"] = "sentinel-user",
                ["computer_name"] = "SYNTHETIC-DESKTOP",
                ["rule_name"] = "Synthetic Rule A",
            });
        var diagnosis = T08R2Fixture.Diagnosis("diagnosis-t08r2-1",
            "adapter \"Synthetic-Uplink-G\" timed out; see https://api.synthetic.invalid/v1/path?access_token=SYNTHETIC_T08R2_TOKEN",
            "read-only check for interface Synthetic-Uplink-F; no repair",
            "probe-t08r2-1", "record-t08r2-1");

        await T08R2Fixture.WriteAsync(path, new[]
        {
            new CycleEvidence
            {
                Snapshot = snapshot,
                Probes = new[] { probe },
                Records = new[] { record },
                Diagnoses = new[] { diagnosis },
            },
        });
    }

    private static IReadOnlyList<string> InterfaceNames(IReadOnlyDictionary<string, byte[]> entries) =>
        T08R2Fixture.Envelopes(entries)
            .SelectMany(envelope => envelope["payload"]?["snapshot"]?["interfaces"]?["value"] as JsonArray
                ?? new JsonArray())
            .Select(item => item?["name"]?.GetValue<string>())
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

    private static string ErrtextOf(IReadOnlyDictionary<string, byte[]> entries) =>
        string.Join("\n", T08R2Fixture.Envelopes(entries)
            .SelectMany(envelope => envelope["payload"]?["snapshot"]?["interfaces"]?["errors"] as JsonArray
                ?? new JsonArray())
            .Select(item => item?.GetValue<string>() ?? string.Empty));

    private static (string AliasInNotice, IReadOnlyList<string> InterfaceNames) ReadTraversalAlias(
        IReadOnlyDictionary<string, byte[]> entries)
    {
        var envelopes = T08R2Fixture.Envelopes(entries);
        var notice = envelopes
            .Select(envelope => envelope["payload"]?["snapshot"]?["proxies"]?["value"]?["tool_process_environment"]?["value"]?["notice"]?.GetValue<string>())
            .FirstOrDefault(value => value is not null && value.Contains("is down", StringComparison.Ordinal));
        var alias = notice is null ? string.Empty : Regex.Match(notice, "device-[A-Z]{2}").Value;
        return (alias, InterfaceNames(entries));
    }
}
