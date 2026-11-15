namespace NetScope.Repair;

/// <summary>
/// Mirrors <c>NetScope.Application.ProposedChange</c> as consumed by DX-010. The diagnostic side
/// only has a field name, a target GUID and a desired string; that is not enough to build a
/// typed plan.
/// </summary>
public sealed record UntypedRepairProposal
{
    public required string Field { get; init; }

    public required string TargetInterfaceGuid { get; init; }

    public required string DesiredValue { get; init; }

    public string? Rationale { get; init; }
}

/// <summary>
/// DX-010's current constraint set is structurally insufficient for a repair plan. This guard
/// documents and enforces the missing dimensions: store, address family, typed value semantics,
/// current-boot identity and trusted attribution. It always returns issues, by design.
/// </summary>
public static class RepairProposalGuard
{
    private static readonly HashSet<string> CanonicalFields = new(StringComparer.Ordinal)
    {
        "interface_metric",
        "automatic_metric",
        "ignore_default_routes",
    };

    public static IReadOnlyList<RepairRefusal> Validate(UntypedRepairProposal proposal)
    {
        var issues = new List<RepairRefusal>();

        if (!CanonicalFields.Contains(proposal.Field))
        {
            issues.Add(RepairRefusal.Of(
                RepairRefusalCode.ProposalUntyped,
                "字段 '" + proposal.Field + "' 不是 typed 动作的 canonical 字段（interface_metric / automatic_metric / ignore_default_routes）；"
                + "例如 DX-010 的 disable_default_routes 与 IgnoreDefaultRoutes 之间不能靠字符串猜映射。",
                field: proposal.Field));
        }

        issues.Add(RepairRefusal.Of(
            RepairRefusalCode.ProposalUntyped,
            "缺少 store：无法区分 ActiveStore 与 PersistentStore，禁止静默双写。",
            field: proposal.Field));

        issues.Add(RepairRefusal.Of(
            RepairRefusalCode.ProposalUntyped,
            "缺少地址族：同名的 IPv4/IPv6 值不能互相抵消，也不能跨族恢复。",
            field: proposal.Field));

        issues.Add(RepairRefusal.Of(
            RepairRefusalCode.ProposalUntyped,
            "缺少原始类型与手动态：'" + proposal.DesiredValue + "' 无法证明是手动配置的 uint、是否在 0..4294967295，也无法与有效派生值区分。",
            field: proposal.Field));

        issues.Add(RepairRefusal.Of(
            RepairRefusalCode.ProposalUntyped,
            "缺少当前 boot 的身份细节（GUID 之外的 LUID/ifIndex/compartment/设备实例）以及来源时间、状态和 TTL；"
            + "GUID 单独存在不能证明这是当前会话的同一对象。",
            field: proposal.Field,
            action: "plan_preflight"));

        issues.Add(RepairRefusal.Of(
            RepairRefusalCode.ProposalUntyped,
            "缺少核心→TUN 的可信归属；名称 token、198.18/15 地址、磁盘 tun=true 或用户点选都不是强归属。",
            field: proposal.Field,
            action: "tun_attribution"));

        if (!Guid.TryParse(proposal.TargetInterfaceGuid, out _))
        {
            issues.Add(RepairRefusal.Of(
                RepairRefusalCode.ProposalUntyped,
                "目标 GUID '" + proposal.TargetInterfaceGuid + "' 不是可解析的 GUID。",
                field: proposal.Field));
        }

        return issues;
    }

    /// <summary>Always false for the DX-010 shape; a plan requires the typed request and facts.</summary>
    public static bool CanBecomePlan(UntypedRepairProposal proposal) => Validate(proposal).Count == 0;
}
