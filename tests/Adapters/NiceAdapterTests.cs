using System.Security.Cryptography;
using System.Text;
using NetScope.Adapters;
using NetScope.Application;

namespace NetScope.Adapters.Tests;

public sealed class NiceAdapterTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ModifiedAt = new(2026, 11, 5, 8, 30, 0, TimeSpan.Zero);
    private static readonly string ConfigPath = RepoPaths.Fixture("nice-runtime.yaml");

    [Fact]
    public async Task Parses_only_the_whitelist_and_records_disk_provenance_metadata()
    {
        var files = new SyntheticConfigFileSource().AddFile(
            ConfigPath,
            RepoPaths.FixtureBytes("nice-runtime.yaml"),
            ModifiedAt);
        var evidence = await new NiceAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.True(evidence.Valid);
        Assert.Null(evidence.ValidityReason);
        Assert.Equal(KnownClientIds.Nice, evidence.ClientId);
        Assert.Equal(ConfigProvenance.DiskOnly, evidence.Provenance);
        Assert.Equal("nice-runtime-yaml/1.0.1", evidence.ParserVersion);
        Assert.Equal(ConfigPath, evidence.SourcePath);
        Assert.Equal(ReadAt, evidence.ReadAt);
        Assert.Equal(ModifiedAt, evidence.FileModifiedAt);
        Assert.Equal(ExpectedHash("nice-runtime.yaml"), evidence.ContentHash);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["allow_lan"] = "false",
                ["dns_fake_ip_range"] = "198.18.0.1/16",
                ["mixed_port"] = "7890",
                ["mode"] = "rule",
                ["tun_enable"] = "true",
                ["tun_mtu"] = "9000",
            },
            evidence.Facts);
        Assert.Equal(1, files.ReadCount);
    }

    [Fact]
    public async Task Unknown_keys_and_the_synthetic_secret_never_appear_in_evidence()
    {
        Assert.Contains(RepoPaths.SyntheticSecret, RepoPaths.FixtureText("nice-runtime.yaml"), StringComparison.Ordinal);

        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "nice-runtime.yaml", ModifiedAt);
        var evidence = await new NiceAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.True(evidence.Valid);
        Assert.DoesNotContain(RepoPaths.SyntheticSecret, EvidenceText.Describe(evidence), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", EvidenceText.Describe(evidence), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(evidence.Facts.Values, value => value.Contains(RepoPaths.SyntheticSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_yaml_is_invalid_without_echoing_the_line()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "nice-runtime-malformed.yaml", ModifiedAt);
        var evidence = await new NiceAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.False(string.IsNullOrWhiteSpace(evidence.ValidityReason));
        Assert.NotEmpty(evidence.Gaps);
        Assert.NotNull(evidence.ContentHash);
        Assert.DoesNotContain(RepoPaths.SyntheticSecret, EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_yaml_structure_is_invalid_with_a_reason()
    {
        var files = new SyntheticConfigFileSource().AddFixture(ConfigPath, "nice-runtime-unexpected.yaml", ModifiedAt);
        var evidence = await new NiceAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("白名单", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task Missing_file_is_a_gap_without_reading_any_real_path()
    {
        var files = new SyntheticConfigFileSource();
        var evidence = await new NiceAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(1, files.ReadCount);
        Assert.Equal(ConfigPath, evidence.SourcePath);
        Assert.False(evidence.Valid);
        Assert.Contains("不存在", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.NotEmpty(evidence.Gaps);
        Assert.Null(evidence.FileModifiedAt);
        Assert.Null(evidence.ContentHash);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task Unreadable_file_keeps_its_reason_and_does_not_throw()
    {
        var files = new SyntheticConfigFileSource().AddUnreadable(ConfigPath, "access_denied");
        var evidence = await new NiceAdapter(files, ConfigPath).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("access_denied", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.Null(evidence.FileModifiedAt);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task An_invalid_whitelisted_value_invalidates_without_echoing_the_value()
    {
        const string path = "synthetic://nice/invalid-port.yaml";
        const string yaml = "mixed-port: 70000\nmode: rule\n";
        var files = new SyntheticConfigFileSource().AddFile(path, Encoding.UTF8.GetBytes(yaml), ModifiedAt);

        var evidence = await new NiceAdapter(files, path).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("端口", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("70000", EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_whitelisted_keys_stay_absent_and_are_never_defaulted()
    {
        const string path = "synthetic://nice/only-mode.yaml";
        var files = new SyntheticConfigFileSource()
            .AddFile(path, Encoding.UTF8.GetBytes("mode: global\n"), ModifiedAt);

        var evidence = await new NiceAdapter(files, path).ReadAsync(ReadAt, CancellationToken.None);

        Assert.True(evidence.Valid);
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["mode"] = "global" }, evidence.Facts);
        Assert.DoesNotContain("mixed_port", evidence.Facts.Keys);
        Assert.DoesNotContain("tun_enable", evidence.Facts.Keys);
        Assert.Contains(evidence.Gaps, gap => gap.Contains("mixed_port", StringComparison.Ordinal));
        Assert.Contains(evidence.Gaps, gap => gap.Contains("tun_enable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_file_is_invalid_with_a_reason()
    {
        const string path = "synthetic://nice/empty.yaml";
        var files = new SyntheticConfigFileSource().AddFile(path, [], ModifiedAt);

        var evidence = await new NiceAdapter(files, path).ReadAsync(ReadAt, CancellationToken.None);

        Assert.False(evidence.Valid);
        Assert.Contains("空", evidence.ValidityReason!, StringComparison.Ordinal);
        Assert.Empty(evidence.Facts);
    }

    [Fact]
    public async Task Hash_is_lowercase_sha256_of_the_raw_bytes()
    {
        const string path = "synthetic://nice/hash.yaml";
        var bytes = Encoding.UTF8.GetBytes("mode: rule\n");
        var files = new SyntheticConfigFileSource().AddFile(path, bytes, ModifiedAt);

        var evidence = await new NiceAdapter(files, path).ReadAsync(ReadAt, CancellationToken.None);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), evidence.ContentHash);
    }

    private static string ExpectedHash(string fixtureName) =>
        Convert.ToHexString(SHA256.HashData(RepoPaths.FixtureBytes(fixtureName))).ToLowerInvariant();
}
