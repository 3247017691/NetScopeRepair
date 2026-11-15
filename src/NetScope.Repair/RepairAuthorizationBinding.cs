using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// One authorization record bound to a plan digest, target, user and logon session. This type
/// only describes the fields and validation; it is not issued by this delivery.
/// </summary>
public sealed record RepairAuthorizationBinding
{
    public required string BindingId { get; init; }

    public required string PlanId { get; init; }

    public required string PlanDigest { get; init; }

    public required string TargetFingerprint { get; init; }

    public required string UserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required DateTimeOffset IssuedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required IReadOnlyList<string> ConfirmedActionIds { get; init; }

    /// <summary>Broker-side one-time nonce, bound in T14; never logged or put on a command line.</summary>
    public required string Nonce { get; init; }

    public bool UacIsNotConsent { get; init; } = true;
}

/// <summary>
/// Validates a binding against the current plan, clock and user. A changed plan value, target,
/// user, session, expiry or a partially confirmed dependency group invalidates it instead of
/// silently adopting a new baseline.
/// </summary>
public static class RepairAuthorizationBindingValidator
{
    public static IReadOnlyList<RepairRefusal> Validate(
        RepairAuthorizationBinding binding,
        RepairPlan plan,
        DateTimeOffset nowUtc,
        string currentUserSid,
        string currentLogonSessionId)
    {
        var refusals = new List<RepairRefusal>();

        if (!plan.CanAuthorize)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.PlanNotExecutable,
                "计划当前不可执行（capability 未通过或不是 planned），不得签发/使用授权绑定。"));
        }

        if (!string.Equals(binding.PlanId, plan.PlanId, StringComparison.Ordinal)
            || !string.Equals(binding.PlanDigest, plan.Digest, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.PlanDigestMismatch,
                "授权绑定的计划摘要与当前计划不一致；修改值、目标或 store 必须重新预览。"));
        }

        if (!string.Equals(binding.TargetFingerprint, plan.TargetFingerprint, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AuthorizationMismatch,
                "授权目标与计划目标不一致，拒绝应用到另一个接口/族/compartment。"));
        }

        if (nowUtc >= binding.ExpiresAtUtc || nowUtc >= plan.ExpiresAtUtc)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.PlanExpired,
                "授权或计划已过期；过期计划不能继续 applying。"));
        }

        if (binding.IssuedAtUtc > nowUtc + RepairLimits.ClockSkewTolerance)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AuthorizationMismatch,
                "授权签发时间晚于当前时钟，超出容忍窗口，拒绝使用。"));
        }

        if (!string.Equals(binding.UserSid, currentUserSid, StringComparison.OrdinalIgnoreCase))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AuthorizationMismatch,
                "授权绑定的用户 SID 与当前发起用户不一致。"));
        }

        if (!string.Equals(binding.LogonSessionId, currentLogonSessionId, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AuthorizationMismatch,
                "授权绑定的登录会话与当前会话不一致。"));
        }

        if (!binding.UacIsNotConsent)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AuthorizationMismatch,
                "UAC 是操作系统的权限授予，不能替代逐动作同意。"));
        }

        var changedIds = plan.ChangedActions.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        var confirmed = binding.ConfirmedActionIds.ToHashSet(StringComparer.Ordinal);

        foreach (var actionId in changedIds.Where(id => !confirmed.Contains(id)).Order(StringComparer.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.ConsentMissing,
                "动作 " + actionId + " 没有逐动作确认。"));
        }

        foreach (var actionId in confirmed.Where(id => !changedIds.Contains(id)).Order(StringComparer.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AuthorizationMismatch,
                "授权包含当前计划中不存在的动作 " + actionId + "。"));
        }

        foreach (var group in plan.Authorization.DependencyGroups.Where(group => group.RequiredTogether))
        {
            var confirmedInGroup = group.ActionIds.Count(confirmed.Contains);
            if (confirmedInGroup > 0 && confirmedInGroup < group.ActionIds.Count)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ConsentMissing,
                    "依赖组 " + group.GroupId + " 未整组确认；取消前置动作会使依赖动作失效。"));
            }
        }

        return refusals;
    }
}
