using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using NetScope.Application;
using NetScope.Desktop.Services;

namespace NetScope.Desktop.ViewModels;

/// <summary>One selectable planned action. It starts unselected, always.</summary>
public sealed class RepairActionRow : ViewModelBase
{
    private readonly Action? _selectionChanged;
    private bool _isSelected;

    public RepairActionRow(RepairActionView view, Action? selectionChanged = null)
    {
        View = view;
        _selectionChanged = selectionChanged;
    }

    public RepairActionView View { get; }

    public string ActionId => View.ActionId;

    public string KindText => View.KindText;

    public string FieldText => View.FieldText;

    public string IdentityText => View.IdentityText;

    public string Ipv4Text => View.Ipv4Text;

    public string StoreText => View.StoreText;

    public string BeforeText => View.BeforeText;

    public string DesiredText => View.DesiredText;

    public string DependencyText => View.DependencyText;

    public string EvidenceText => View.EvidenceText;

    public string TtlText => View.TtlText;

    public string RiskText => View.RiskText;

    public string EligibilityText => View.EligibilityText;

    public string BlockingText => View.BlockingText;

    public bool IsChanged => View.IsChanged;

    public bool IsNoOp => View.IsNoOp;

    public bool CanSelect => View.CanSelect;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect) value = false;
            if (!Set(ref _isSelected, value)) return;
            _selectionChanged?.Invoke();
        }
    }

    public string StatusText => IsNoOp ? "跳过（no_op）" : IsSelected ? "已选择" : "未选择";
}

public sealed record RepairNoOpRow(string FieldText, string StoreText, string ValueText, string ReasonText);

public sealed record RepairLayerRow(string Name, string StatusText, string Detail, Brush Tone)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
}

public sealed record RepairRecoveryItemRow(
    string TransactionId,
    string StateText,
    string RestorableText,
    string UncertainText,
    string AuthorizationText,
    string RecoveryPlanId);

/// <summary>
/// The repair page: preview, per-action selection, explicit confirmation, transaction progress,
/// recovery preview and the separate business observation. Every privileged action goes through
/// <see cref="IRepairGateway"/>; the page never writes a network field itself.
/// </summary>
public sealed class RepairViewModel : ViewModelBase
{
    private readonly IRepairGateway _gateway;
    private readonly IClock _clock;
    private readonly Func<string> _userSid;
    private readonly Func<string> _logonSession;
    private readonly RepairFunnelCounter _funnel = new();
    private readonly RepairBusinessObservation _business = new();
    private RepairPlanView? _preview;
    private RepairTransactionView? _currentTransaction;
    private bool _isBusy;
    private string _gateText;
    private string _gateReasonsText = "";
    private string _selectionSummary = "尚未选择任何动作。";
    private string _exitTransactionText = "";
    private string _recoveryNotice = "尚未扫描未终结事务。";
    private string _businessText = BusinessObservationView.NotStarted.StatusText;
    private bool _businessStarted;
    private RepairExecutionOutcomeView? _lastOutcome;

    public RepairViewModel(
        IRepairGateway gateway,
        IClock clock,
        Func<string>? userSid = null,
        Func<string>? logonSession = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _userSid = userSid ?? (() => "");
        _logonSession = logonSession ?? (() => "");
        _gateText = gateway.Gate.Text;
        _gateReasonsText = string.Join("；", gateway.Gate.Reasons);

        PreviewCommand = new AsyncRelayCommand(() => PreviewAsync(), null, ex => ErrorText = ErrorOf(ex));
        ExecuteCommand = new AsyncRelayCommand(
            () => ExecuteSelectedAsync(userConfirmed: true), () => CanExecute, ex => ErrorText = ErrorOf(ex));
        CancelCommand = new AsyncRelayCommand(() => CancelAsync(), () => _currentTransaction is not null,
            ex => ErrorText = ErrorOf(ex));
        ScanRecoveryCommand = new AsyncRelayCommand(() => ScanRecoveryAsync(), null, ex => ErrorText = ErrorOf(ex));
        StartBusinessObservationCommand = new AsyncRelayCommand(() =>
        {
            StartBusinessObservation();
            return Task.CompletedTask;
        });
    }

    public ICommand PreviewCommand { get; }

    public ICommand ExecuteCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ScanRecoveryCommand { get; }

    public ICommand StartBusinessObservationCommand { get; }

    public ObservableCollection<RepairActionRow> Actions { get; } = new();

    public ObservableCollection<RepairNoOpRow> NoOpFields { get; } = new();

    public ObservableCollection<RepairLayerRow> Layers { get; } = new();

    public ObservableCollection<RepairRecoveryItemRow> RecoveryItems { get; } = new();

    public ObservableCollection<BusinessTargetRow> BusinessTargets { get; } = new();

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    private string _errorText = "";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanExecute));
                Raise(nameof(CanCancel));
            }
        }
    }

    public RepairPlanView? Preview
    {
        get => _preview;
        private set
        {
            if (!Set(ref _preview, value)) return;
            Raise(nameof(HasPreview));
            Raise(nameof(IsUnavailable));
            Raise(nameof(PlanSummaryText));
            Raise(nameof(PlanTtlText));
            Raise(nameof(TargetText));
            Raise(nameof(DigestText));
            Raise(nameof(ShowNoOp));
            Raise(nameof(NoOpSummaryText));
            Raise(nameof(CanExecute));
        }
    }

    public bool HasPreview => Preview is not null;

    public bool IsUnavailable => Preview is null || Preview.Availability == RepairPreviewAvailability.Unavailable;

    public bool ShowNoOp => NoOpFields.Count > 0;

    public string PlanSummaryText => Preview?.SummaryText ?? "尚未生成修复计划。";

    public string PlanTtlText => Preview?.TtlText ?? "无预览";

    public string TargetText => Preview?.TargetText ?? "没有可信的目标身份";

    public string DigestText => Preview is null || string.IsNullOrEmpty(Preview.Digest)
        ? ""
        : "计划摘要 " + Preview.Digest;

    public string NoOpSummaryText => NoOpFields.Count == 0
        ? ""
        : "no_op 字段 " + NoOpFields.Count + " 个：没有变化的字段不计修复次数，也不会被写入。";

    public string GateText
    {
        get => _gateText;
        private set => Set(ref _gateText, value);
    }

    public string GateReasonsText
    {
        get => _gateReasonsText;
        private set => Set(ref _gateReasonsText, value);
    }

    public string SelectionSummary
    {
        get => _selectionSummary;
        private set => Set(ref _selectionSummary, value);
    }

    public string ExitTransactionText
    {
        get => _exitTransactionText;
        private set => Set(ref _exitTransactionText, value);
    }

    public string RecoveryNotice
    {
        get => _recoveryNotice;
        private set => Set(ref _recoveryNotice, value);
    }

    public string BusinessText
    {
        get => _businessText;
        private set => Set(ref _businessText, value);
    }

    public string BusinessNotice => _business.Evaluate(_clock.UtcNow).Notice;

    public string FunnelText => _funnel.DenominatorText;

    public RepairExecutionOutcomeView? LastOutcome => _lastOutcome;

    public bool CanCancel => _currentTransaction is not null && !IsBusy;

    /// <summary>
    /// Selection must be explicit, dependency-complete and the gate/plan must still be valid.
    /// A no-op plan, drift, expiry or untrusted input disables the action entirely.
    /// </summary>
    public bool CanExecute
    {
        get
        {
            if (IsBusy || Preview is null) return false;
            if (Preview.Availability != RepairPreviewAvailability.Ready) return false;
            if (!Preview.Gate.Enabled) return false;
            if (Preview.Expired || Preview.TargetDrifted) return false;
            if (Preview.InputTrust != RepairInputTrust.Verified) return false;
            var selected = Actions.Where(action => action.IsSelected).ToArray();
            if (selected.Length == 0) return false;
            if (selected.Any(action => !action.IsChanged || !action.CanSelect)) return false;
            var selectedIds = selected.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
            return selected.All(action => action.View.DependsOnActionIds.All(selectedIds.Contains));
        }
    }

    public string TransactionStateText => _currentTransaction is null
        ? "没有进行中的事务。"
        : "事务 " + _currentTransaction.TransactionId + " · " + _currentTransaction.StateText
          + (_currentTransaction.NeedsRecovery ? " · 需要用户确认恢复" : "")
          + (_currentTransaction.IsTerminal ? "（已终结）" : "（未终结）");

    public async Task PreviewAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = "";
        try
        {
            var request = new RepairPreviewRequest
            {
                SnapshotId = "ui-preview",
                DiagnosisId = null,
                NowUtc = _clock.UtcNow,
                CurrentBootId = "",
                InputTrust = RepairInputTrust.Unverified,
            };
            var preview = await _gateway.PreviewAsync(request, cancellationToken).ConfigureAwait(true);
            ApplyPreview(preview);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyPreview(RepairPlanView preview)
    {
        Preview = preview;
        Actions.Clear();
        foreach (var action in preview.Actions)
            Actions.Add(new RepairActionRow(action, RecomputeSelection));
        NoOpFields.Clear();
        foreach (var noOp in preview.NoOpFields)
            NoOpFields.Add(new RepairNoOpRow(noOp.FieldText, noOp.StoreText, noOp.ValueText, noOp.ReasonText));
        GateText = preview.Gate.Text;
        GateReasonsText = string.Join("；", preview.Gate.Reasons);
        Raise(nameof(ShowNoOp));
        Raise(nameof(NoOpSummaryText));
        RecomputeSelection();
        StatusMessage = preview.Availability == RepairPreviewAvailability.NoOp
            ? "计划没有实际变更（no_op）；no_op 不计修复次数。"
            : preview.Availability == RepairPreviewAvailability.Unavailable
                ? "预览不可用；执行入口保持 Disabled。"
                : "计划已生成；逐动作默认未选中，请显式选择并确认。";
    }

    /// <summary>
    /// The explicit confirmation step. Without <paramref name="userConfirmed"/> no execution is
    /// requested; with it the gateway still owns the UAC/broker decision (disabled in this delivery).
    /// </summary>
    public async Task ExecuteSelectedAsync(bool userConfirmed, CancellationToken cancellationToken = default)
    {
        if (Preview is null)
        {
            StatusMessage = "请先生成修复计划。";
            return;
        }

        if (Preview.Availability == RepairPreviewAvailability.Unavailable)
        {
            StatusMessage = "执行入口已禁用（Disabled）：" + Preview.Gate.Text;
            _funnel.Record(RepairAttemptOutcome.Denied);
            Raise(nameof(FunnelText));
            return;
        }

        if (Preview.Availability == RepairPreviewAvailability.NoOp)
        {
            StatusMessage = "no_op 计划不计修复次数，也不会执行写入。";
            return;
        }

        if (Preview.Expired)
        {
            StatusMessage = "计划已过期；必须重新预览，旧授权不可复用。";
            return;
        }

        if (Preview.TargetDrifted)
        {
            StatusMessage = "目标身份已漂移；必须重新预览，不能沿用旧计划。";
            return;
        }

        if (Preview.InputTrust != RepairInputTrust.Verified)
        {
            StatusMessage = "输入事实/回放包未验证；执行入口禁用。";
            return;
        }

        if (!Preview.Gate.Enabled)
        {
            StatusMessage = "执行入口已禁用（Disabled）：" + Preview.Gate.Text;
            _funnel.Record(RepairAttemptOutcome.Denied);
            Raise(nameof(FunnelText));
            return;
        }

        var selected = Actions.Where(action => action.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "尚未选择任何动作；默认不选中，必须显式确认。";
            return;
        }

        if (!userConfirmed)
        {
            StatusMessage = "尚未得到显式确认；未发起 UAC 或任何写入。";
            return;
        }

        if (!CanExecute)
        {
            StatusMessage = "选择不满足依赖或资格要求，未发起执行。";
            return;
        }

        IsBusy = true;
        ErrorText = "";
        try
        {
            var transactionId = "tx-ui-" + Guid.NewGuid().ToString("N")[..12];
            var outcome = await _gateway.ExecuteAsync(new RepairExecutionRequest
            {
                TransactionId = transactionId,
                PlanId = Preview.PlanId,
                PlanDigest = Preview.Digest,
                ConfirmedActionIds = selected.Select(action => action.ActionId).ToArray(),
                UserSid = _userSid(),
                LogonSessionId = _logonSession(),
                NowUtc = _clock.UtcNow,
            }, cancellationToken).ConfigureAwait(true);
            ApplyOutcome(outcome);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyOutcome(RepairExecutionOutcomeView outcome)
    {
        _lastOutcome = outcome;
        Layers.Clear();
        foreach (var layer in outcome.Layers)
            Layers.Add(new RepairLayerRow(layer.Name, LayerStatusText(layer.Status), layer.Detail, ToneOf(layer.Status)));
        _currentTransaction = new RepairTransactionView
        {
            TransactionId = outcome.TransactionId,
            StateText = outcome.StateText,
            IsTerminal = outcome.IsTerminal,
            NeedsRecovery = outcome.NeedsRecovery,
            WroteAnyField = outcome.WroteAnyField,
            Detail = outcome.RecoveryNotice,
            UpdatedAtUtc = _clock.UtcNow,
        };
        Raise(nameof(TransactionStateText));
        Raise(nameof(CanCancel));
        Raise(nameof(CanExecute));

        var funnelOutcome = FunnelOf(outcome.StateText, outcome.NeedsRecovery);
        _funnel.Record(funnelOutcome);
        Raise(nameof(FunnelText));
        StatusMessage = outcome.StateText + "；" + outcome.RecoveryNotice;

        if (outcome.StateText == "committed")
        {
            _business.Start(_clock.UtcNow);
            _businessStarted = true;
            BusinessText = "技术提交完成；十分钟 / 三轮业务观察由低权主进程执行，业务结果独立。";
            Raise(nameof(BusinessNotice));
            Raise(nameof(BusinessText));
            RefreshBusinessTargets();
        }
        else if (outcome.NeedsRecovery)
        {
            BusinessText = "事务未终结，不开始业务观察；请先确认恢复计划。";
            RecoveryNotice = "事务未终结（" + outcome.StateText
                + "）；请扫描未终结记录并确认新的恢复计划，恢复不依赖互联网。";
            Raise(nameof(BusinessText));
            Raise(nameof(RecoveryNotice));
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null) return;
        var state = await _gateway.CancelAsync(cancellationToken).ConfigureAwait(true);
        if (state is not null)
        {
            _currentTransaction = state;
            _funnel.Record(RepairAttemptOutcome.Cancelled);
            Raise(nameof(TransactionStateText));
            Raise(nameof(FunnelText));
            StatusMessage = "已请求取消：事务 " + state.TransactionId + " 状态 " + state.StateText + "。";
        }
        else
        {
            StatusMessage = "没有可取消的事务。";
        }
    }

    public async Task ScanRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var recovery = await _gateway.ScanRecoveryAsync(cancellationToken).ConfigureAwait(true);
        RecoveryItems.Clear();
        foreach (var item in recovery.Items)
        {
            RecoveryItems.Add(new RepairRecoveryItemRow(
                item.TransactionId,
                item.StateText,
                "可恢复字段 " + item.RestorableFieldCount + " 个",
                item.UncertainActionCount == 0 ? "无未决 intent" : "未决 intent " + item.UncertainActionCount + " 个",
                item.RequiresNewAuthorization ? "需要新的恢复授权" : "按 journal 记录恢复",
                item.RecoveryPlanId));
        }

        RecoveryNotice = recovery.Available
            ? "检测到 " + recovery.PendingCount + " 个未终结事务；恢复需要用户确认新计划并再次授权，且不依赖互联网。"
            : "恢复不可用：" + string.Join("；", recovery.BlockedReasons);
        Raise(nameof(RecoveryNotice));
        Raise(nameof(RecoveryItems));
        StatusMessage = recovery.Notice;
    }

    public void ReportDisconnect()
    {
        var state = _gateway.CurrentTransaction;
        if (state is null)
        {
            StatusMessage = "连接已断开；没有进行中的修复事务。";
            return;
        }

        _currentTransaction = state;
        _funnel.Record(RepairAttemptOutcome.Disconnected);
        Raise(nameof(TransactionStateText));
        Raise(nameof(FunnelText));
        StatusMessage = "连接已断开；事务 " + state.TransactionId + " 状态 " + state.StateText
            + (state.NeedsRecovery ? "，需要用户确认恢复。" : "。");
    }

    /// <summary>
    /// Called by the shell on close/exit. It reports the final transaction state and never starts
    /// a new write; recovery data stays in the authoritative journal for the next explicit step.
    /// </summary>
    public async Task<string> PrepareExitAsync(CancellationToken cancellationToken = default)
    {
        var final = await _gateway.FinalizeAsync(cancellationToken).ConfigureAwait(true);
        if (final is not null)
        {
            _currentTransaction = final;
            Raise(nameof(TransactionStateText));
        }

        var state = _currentTransaction;
        ExitTransactionText = state is null
            ? "退出：没有修复事务记录。"
            : state.IsTerminal
                ? "退出：事务 " + state.TransactionId + " 已终结（" + state.StateText + "）；未启动新的写入。"
                : "退出：事务 " + state.TransactionId + " 尚未终结（" + state.StateText
                  + "）；已保留 journal 记录，下次启动需用户确认恢复，恢复不依赖互联网。";
        Raise(nameof(ExitTransactionText));
        return ExitTransactionText;
    }

    public void StartBusinessObservation()
    {
        _business.Start(_clock.UtcNow);
        _businessStarted = true;
        BusinessText = "业务观察已开始：10 分钟 / 3 轮；未满窗口不宣称稳定。";
        Raise(nameof(BusinessText));
        Raise(nameof(BusinessNotice));
        RefreshBusinessTargets();
    }

    public bool RecordBusinessRound(IEnumerable<BusinessTargetResult> results, DateTimeOffset? now = null)
    {
        if (!_businessStarted) StartBusinessObservation();
        var accepted = _business.TryRecordRound(results, now ?? _clock.UtcNow, out var rejection);
        BusinessText = accepted
            ? _business.Evaluate(now ?? _clock.UtcNow).StatusText
            : "轮次未记录：" + rejection;
        Raise(nameof(BusinessText));
        Raise(nameof(BusinessNotice));
        RefreshBusinessTargets();
        return accepted;
    }

    public BusinessObservationView BusinessView => _business.Evaluate(_clock.UtcNow);

    private void RefreshBusinessTargets()
    {
        BusinessTargets.Clear();
        foreach (var row in BusinessView.Targets) BusinessTargets.Add(row);
        Raise(nameof(BusinessTargets));
    }

    private void RecomputeSelection()
    {
        var selected = Actions.Where(action => action.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            SelectionSummary = "尚未选择任何动作（默认全部未选中）。";
        }
        else
        {
            var missing = selected
                .SelectMany(action => action.View.DependsOnActionIds)
                .Where(id => !selected.Any(action => action.ActionId == id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            SelectionSummary = "已选择 " + selected.Length + " 个动作"
                + (missing.Length == 0
                    ? "；依赖已作为组合授权一并选择。"
                    : "；缺少依赖：" + string.Join("、", missing) + "（依赖动作必须一并显式选择）。");
        }

        Raise(nameof(SelectionSummary));
        Raise(nameof(CanExecute));
    }

    private static RepairAttemptOutcome FunnelOf(string stateText, bool needsRecovery) => stateText switch
    {
        "committed" => RepairAttemptOutcome.Committed,
        "no_op" => RepairAttemptOutcome.NoOp,
        "aborted" => RepairAttemptOutcome.Aborted,
        "rolled_back" => RepairAttemptOutcome.RolledBack,
        "rollback_conflict" => RepairAttemptOutcome.RollbackConflict,
        "rollback_failed" => RepairAttemptOutcome.RollbackFailed,
        "recovery_required" => RepairAttemptOutcome.RecoveryRequired,
        "cancelled" => RepairAttemptOutcome.Cancelled,
        "denied" => RepairAttemptOutcome.Denied,
        "disconnected" => RepairAttemptOutcome.Disconnected,
        _ => needsRecovery ? RepairAttemptOutcome.RecoveryRequired : RepairAttemptOutcome.IncompleteObservation,
    };

    private static string LayerStatusText(RepairLayerStatus status) => status switch
    {
        RepairLayerStatus.Passed => "通过",
        RepairLayerStatus.Failed => "失败",
        RepairLayerStatus.NotTested => "未测试",
        RepairLayerStatus.NotApplicable => "不适用",
        _ => "进行中",
    };

    private static Brush ToneOf(RepairLayerStatus status) => status switch
    {
        RepairLayerStatus.Passed => UiText.OkBrush,
        RepairLayerStatus.Failed => UiText.BadBrush,
        RepairLayerStatus.Pending => UiText.WarnBrush,
        _ => UiText.GapBrush,
    };

    private static string ErrorOf(Exception exception) => exception.GetType().Name + ": " + exception.Message;
}


