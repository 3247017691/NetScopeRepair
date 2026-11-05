using NetScope.Application;

namespace NetScope.Diagnostics;

/// <summary>
/// The M0 rule catalog. It deliberately contains only rules whose output is an explanation:
/// DX-001, DX-002, DX-005, DX-006, DX-008, DX-010 and DX-011. Rules that would eventually need a
/// repair decision (DX-003, DX-004, DX-007, DX-009, DX-012) are not shipped yet.
/// </summary>
public static class RuleCatalog
{
    public static IReadOnlyList<IDiagnosticRule> All { get; } = new IDiagnosticRule[]
    {
        new Rules.Dx001DeadProxyDependency(),
        new Rules.Dx002FakeIpContext(),
        new Rules.Dx005PortOwnershipConflict(),
        new Rules.Dx006StaleConfiguration(),
        new Rules.Dx008IntermittentFailure(),
        new Rules.Dx010RoutingNoOp(),
        new Rules.Dx011CollectionContradiction(),
    };
}
