using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetScope.Application;

/// <summary>Identity of fixed request options, independent of the selected route or proxy.</summary>
public static class ProbeIdentity
{
    public static string OptionsKey(ProbeGroupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // A fixed property order, invariant JSON numbers, and sorted set values make replay stable.
        var canonical = JsonSerializer.Serialize(new
        {
            endpoint = plan.Target.Endpoint,
            method = plan.Target.Method.Trim().ToUpperInvariant(),
            expectedStatuses = plan.Target.ExpectedStatusCodes.Distinct().Order().ToArray(),
            addressFamily = plan.AddressFamily?.Trim().ToLowerInvariant(),
            perProbeTimeoutTicks = plan.PerProbeTimeout.Ticks,
            groupBudgetTicks = plan.GroupBudget.Ticks,
            maxResponseBytes = plan.MaxResponseBytes,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
