using System.Security.Cryptography;
using System.Text;
using NetScope.Adapters;
using NetScope.Application;

namespace NetScope.Adapters.Tests;

public sealed class ApexAdapterTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ModifiedAt = new(2026, 11, 5, 8, 30, 0, TimeSpan.Zero);
    private static readonly string ConfigPath = RepoPaths.Fixture("apex-shared_preferences.json");

    [Fact]
    public async Task Parses_only_the_whitelist_and_labels_disk_provenance()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var evidence = await new ApexAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.True(evidence.Valid);
        Assert.Equal(KnownClientIds.Apex, evidence.ClientId);
        Assert.Equal(ConfigProvenance.DiskOnly, evidence.Provenance);
        Assert.Equal("apex-shared-preferences-json/1.0.0", evidence.ParserVersion);
        Assert.Equal(ConfigPath, evidence.SourcePath);
        Assert.Equal(ReadAt, evidence.ReadAt);
        Assert.Equal(ModifiedAt, evidence.FileModifiedAt);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(RepoPaths.FixtureBytes("apex-shared_preferences.json"))).ToLowerInvariant(),
            evidence.ContentHash);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dns_fake_ip_range"] = "198.18.0.1/16",
                ["dns_mode"] = "fake-ip",
                ["mixed_port"] = "7891",
                ["mode"] = "rule",
                ["tun_auto_route"] = "true",
            },
            evidence.Facts);
    }

    [Fact]
    public async Task Unknown_keys_and_the_synthetic_secret_never_appear_in_evidence()
    {
        Assert.Contains(
            RepoPaths.SyntheticSecret,
            RepoPaths.FixtureText("apex-shared_preferences.json"),
            StringComparison.Ordinal);

        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var evidence = await new ApexAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.True(evidence.Valid);
        var text = EvidenceText.Describe(evidence);
        Assert.DoesNotContain(RepoPaths.SyntheticSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(evidence.Facts.Values, value => value.Contains(RepoPaths.SyntheticSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runtime_api_seam_wins_and_avoids_reading_disk()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var runtime = new FakeApexRuntimeApi
        {
            Evidence = new ApexRuntimeEvidence
            {
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mixed_port"] = "7892",
                    ["mode"] = "global",
                    ["subscription_url"] = RepoPaths.SyntheticSecret,
                },
            },
        };

        var evidence = await new ApexAdapter(files, ConfigPath, runtime).ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(ConfigProvenance.RuntimeApi, evidence.Provenance);
        Assert.True(evidence.Valid);
        Assert.Equal("7892", evidence.Facts["mixed_port"]);
        Assert.Equal("global", evidence.Facts["mode"]);
        Assert.False(evidence.Facts.ContainsKey("subscription_url"));
        Assert.Equal(1, runtime.ReadCount);
        Assert.Equal(0, files.ReadCount);
        Assert.Null(evidence.SourcePath);
        Assert.Null(evidence.FileModifiedAt);
        Assert.Null(evidence.ContentHash);
        Assert.DoesNotContain(RepoPaths.SyntheticSecret, EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_api_without_usable_facts_falls_back_to_disk_with_a_gap()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var runtime = new FakeApexRuntimeApi
        {
            Evidence = new ApexRuntimeEvidence
            {
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["api_key"] = RepoPaths.SyntheticSecret,
                },
            },
        };

        var evidence = await new ApexAdapter(files, ConfigPath, runtime).ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(ConfigProvenance.DiskOnly, evidence.Provenance);
        Assert.True(evidence.Valid);
        Assert.Equal(1, runtime.ReadCount);
        Assert.Equal(1, files.ReadCount);
        Assert.Contains(evidence.Gaps, gap => gap.Contains("runtime API", StringComparison.Ordinal));
        Assert.DoesNotContain(RepoPaths.SyntheticSecret, EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matching_start_reference_upgrades_disk_evidence_to_process_start_reference()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var references = new FakeStartReferences
        {
            Reference = new ProcessStartReference
            {
                ClientId = KnownClientIds.Apex,
                ConfigPath = ConfigPath,
                Detail = "synthetic start reference",
            },
        };

        var evidence = await new ApexAdapter(files, ConfigPath, startReferences: references)
            .ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(ConfigProvenance.ProcessStartReference, evidence.Provenance);
        Assert.True(evidence.Valid);
        Assert.Equal("7891", evidence.Facts["mixed_port"]);
        Assert.Equal(1, references.ReadCount);
    }

    [Fact]
    public async Task Non_matching_start_reference_does_not_upgrade_provenance()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var references = new FakeStartReferences
        {
            Reference = new ProcessStartReference
            {
                ClientId = KnownClientIds.Apex,
                ConfigPath = Path.Combine(Path.GetTempPath(), "some-other-config.json"),
            },
        };

        var evidence = await new ApexAdapter(files, ConfigPath, startReferences: references)
            .ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(ConfigProvenance.DiskOnly, evidence.Provenance);
        Assert.Contains(evidence.Gaps, gap => gap.Contains("另一个配置路径", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provenance_matrix_covers_runtime_api_process_start_reference_and_disk_only()
    {
        var runtimeFiles = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var runtime = new FakeApexRuntimeApi
        {
            Evidence = new ApexRuntimeEvidence
            {
                Facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["mixed_port"] = "7892" },
            },
        };
        var runtimeEvidence = await new ApexAdapter(runtimeFiles, ConfigPath, runtime)
            .ReadAsync(ReadAt, CancellationToken.None);

        var referenceFiles = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var references = new FakeStartReferences
        {
            Reference = new ProcessStartReference { ClientId = KnownClientIds.Apex, ConfigPath = ConfigPath },
        };
        var referenceEvidence = await new ApexAdapter(referenceFiles, ConfigPath, startReferences: references)
            .ReadAsync(ReadAt, CancellationToken.None);

        var diskFiles = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences.json", ModifiedAt);
        var diskEvidence = await new ApexAdapter(diskFiles, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(
            new[] { ConfigProvenance.RuntimeApi, ConfigProvenance.ProcessStartReference, ConfigProvenance.DiskOnly },
            new[] { runtimeEvidence.Provenance, referenceEvidence.Provenance, diskEvidence.Provenance });
        Assert.All(new[] { runtimeEvidence, referenceEvidence, diskEvidence }, evidence => Assert.True(evidence.Valid));
    }

    [Fact]
    public async Task Missing_file_is_a_gap()
    {
        var files = new SyntheticConfigFileSource();
        var evidence = await new ApexAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(1, files.ReadCount);
        Assert.False(evidence.Valid);
        Assert.Contains("不存在", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.Null(evidence.ContentHash);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task Malformed_json_is_invalid_without_echoing_content()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences-malformed.json", ModifiedAt);
        var evidence = await new ApexAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("JSON", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoPaths.SyntheticSecret, EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_json_root_is_invalid()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "apex-shared_preferences-unexpected.json", ModifiedAt);
        var evidence = await new ApexAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("根节点", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task Object_without_whitelisted_keys_is_invalid()
    {
        const string path = "synthetic://apex/no-whitelist.json";
        var files = new SyntheticConfigFileSource()
            .AddFile(path, Encoding.UTF8.GetBytes("{\"flutter.unrelated\":\"value\"}"), ModifiedAt);

        var evidence = await new ApexAdapter(files, path).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("白名单", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task Invalid_whitelisted_value_invalidates_the_file()
    {
        const string path = "synthetic://apex/invalid-port.json";
        var files = new SyntheticConfigFileSource()
            .AddFile(path, Encoding.UTF8.GetBytes("{\"flutter.mixed-port\":\"not-a-port\",\"flutter.mode\":\"rule\"}"), ModifiedAt);

        var evidence = await new ApexAdapter(files, path).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("端口", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-port", EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }
}
