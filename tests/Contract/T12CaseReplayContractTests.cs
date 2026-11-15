using NetScope.Application;
using NetScope.Storage;

namespace NetScope.Contract.Tests;

/// <summary>
/// T12 contract checks: the untrusted-package admission limits are the frozen design-04 values,
/// the package format is pinned, and the Application replay contract has no network/collector
/// capability.
/// </summary>
public sealed class T12CaseReplayContractTests
{
    [Fact]
    public void Case_replay_default_limits_match_design_04()
    {
        var limits = CaseReplayLimits.Default;
        Assert.Equal(1000, limits.MaxEntries);
        Assert.Equal(100L * 1024 * 1024, limits.MaxTotalUncompressedBytes);
        Assert.Equal(20L * 1024 * 1024, limits.MaxEntryUncompressedBytes);
        Assert.Equal(8, limits.MaxPathDepth);
    }

    [Fact]
    public void Package_format_and_version_are_pinned()
    {
        Assert.Equal("netscope-incident-package", CaseReplayService.RequiredPackageFormat);
        Assert.Equal("1", CaseReplayService.SupportedPackageFormatVersion);
        Assert.Equal(CaseReplayStatus.Ok, Enum.Parse<CaseReplayStatus>("Ok"));
        Assert.Equal(3, Enum.GetValues<CaseReplayIssueSeverity>().Length);
    }

    [Fact]
    public void Application_case_replay_contract_has_no_network_or_system_capability()
    {
        var application = typeof(ICaseReplayService).Assembly.GetReferencedAssemblies().Select(item => item.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain("System.Net.Http", application);
        Assert.DoesNotContain("NetScope.Probes", application);
        Assert.DoesNotContain("NetScope.Collectors.Windows", application);
        Assert.DoesNotContain("NetScope.Broker", application);
        Assert.DoesNotContain("NetScope.Adapters", application);

        // The shared Application project also carries pure repair-planning types (T13), so the
        // assembly graph may cite NetScope.Repair. The replay contract itself must not expose or
        // accept any repair/collector/probe/broker type.
        var forbidden = new[] { "NetScope.Repair", "NetScope.Probes", "NetScope.Collectors.Windows", "NetScope.Broker", "NetScope.Adapters" };
        var replaySurface = typeof(ICaseReplayService).GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .Concat(new[] { typeof(CaseReplayResult), typeof(ICaseRuleEvaluator) })
            .ToArray();
        foreach (var type in replaySurface)
        {
            Assert.DoesNotContain(forbidden, ns => (type.Namespace ?? "").StartsWith(ns, StringComparison.Ordinal));
        }

        var serviceMethods = typeof(ICaseReplayService).GetMethods();
        Assert.Single(serviceMethods);
        Assert.Equal("Replay", serviceMethods[0].Name);
        Assert.Equal(typeof(CaseReplayResult), serviceMethods[0].ReturnType);
        Assert.Equal(typeof(string), serviceMethods[0].GetParameters().Single().ParameterType);
    }

    [Fact]
    public void Recorded_and_recomputed_diagnoses_are_separate_contract_fields()
    {
        var properties = typeof(CaseReplayResult).GetProperties().Select(item => item.Name).ToArray();
        Assert.Contains(nameof(CaseReplayResult.RecordedDiagnoses), properties);
        Assert.Contains(nameof(CaseReplayResult.RecomputedDiagnoses), properties);
        Assert.Contains(nameof(CaseReplayResult.Differences), properties);
        Assert.Contains(nameof(CaseReplayResult.Admission), properties);
        Assert.Contains(nameof(CaseReplayResult.DeterministicFingerprint), properties);
        Assert.Contains(nameof(CaseReplayResult.Incidents), properties);
    }
}
