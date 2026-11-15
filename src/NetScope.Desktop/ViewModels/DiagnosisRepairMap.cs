namespace NetScope.Desktop.ViewModels;

/// <summary>
/// UI-layer mapping from diagnosis rule to repair action kinds. Diagnostic rules themselves
/// never carry actions; this only drives navigation and pre-selection.
/// </summary>
public static class DiagnosisRepairMap
{
    public static IReadOnlyList<string> ActionKindsFor(string ruleId) => ruleId switch
    {
        "DX-001" => new[] { "ClearProxyEnv", "AlignProxyEnv", "DisableSystemProxy" },
        "DX-006" => new[] { "RaiseInterfaceMetric", "ResetInterfaceDns", "RemoveGhostDevice" },
        _ => Array.Empty<string>(),
    };
}