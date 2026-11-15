# R1-A / T03-F09 补修报告

- 任务：task-24，R1-A · T03/F09：逐接口+地址族采集参数，IPv6 缺失必须成为缺口
- 基线：`main` HEAD `a4b07a0`（工作区另有其他任务未提交改动；本任务只改下列允许文件）
- 日期：2026-11-06
- 结论：已修复 F09；新增回归在旧代码下失败，修复后通过。未改 Application/Adapters 公共合同、会话、UI、诊断、导出。

## 1. 允许文件清单与实际改动

允许/实际改动：

- `src/NetScope.Collectors.Windows/WindowsSources.cs`（唯一产品代码改动）
- `tests/Windows/PartialSourceTests.cs`（更新 1 个锁定旧缺陷的用例 + 新增 3 个 F09 回归用例）
- `docs/reports/R1-A-T03-F09-report.md`（本报告）

未改：Application/Adapters 合同、其他 collectors、session/UI/diagnostics/export、README、slnx、artifacts、脚本。

## 2. F09 事实与复现证据

外部复核：

- `docs/reports/G3-final-review.md` 第 4 节 F09：`WindowsSources.cs:92/103/124` 只调 IPv4 原生读取；双栈接口返回单条 ipv4 行，IPv6 metric / IgnoreDefaultRoutes 不可见且无缺口。
- `docs/reports/evidence/G3-review-20261106/findings-localized.json` F09：
  - trigger：同一接口同时有 IPv4/IPv6，且两族 metric 与 IgnoreDefaultRoutes 不同。
  - 复现值：IPv4 metric=25、DisableDefaultRoutes=false；IPv6 metric=900、DisableDefaultRoutes=true。
  - 旧结果：单条 `Family=ipv4`，`Addresses` 同时含 `192.0.2.2/24` 和 `2001:db8::2/64`，`nativeMetricReads=["ipv4"]`，状态 `ok`。
  - 期望：按接口+地址族分别读取；IPv6 未读必须单列缺口。

## 3. 实现

`src/NetScope.Collectors.Windows/WindowsSources.cs`：

- `WindowsInterfaceSource.ReadAsync`（约 39–75 行）现在接受每 NIC 的多行描述；skip 仅用于“无任何 index 且无任何 address”的 filter miniport。
- `Describe`（约 95–185 行）：
  - 按地址字符串解析 `IPAddress`，把地址分入 IPv4/IPv6/未分类；未分类地址计入 `address_family_unclassified` 缺口，不分配给任一家庭。
  - 分别以 `Ipv4Index`、`Ipv6Index` 判断家庭是否有原生索引；有 index 或该家庭有地址即产生该家庭行。
  - 双栈接口产生两条 `InterfaceState`：同 GUID/name/kind，`Family`、`IfIndex`、`Addresses`、`Metric`、`AutomaticMetric`、`DisableDefaultRoutes`、`Luid` 各自独立。
  - 两族 index 缺失但两族地址均存在时，产生两条 index=0 的诚实缺口行；只有一个家庭地址且无 index 时保留单条 `unknown` 行。
- `DescribeFamily`（约 192–230 行）：
  - 每个家庭独立调用 `ReadInterfaceMetrics(index, family)`；IPv6 失败绝不回退 IPv4，IPv4 失败也不会借用 IPv6。
  - 失败/无 index/超范围均在该家庭行 `Reason` 中写入稳定原因码，例如：
    - `ipv6_interface_metrics_unavailable: ...`
    - `ipv6_interface_index_unavailable: the ipv6 interface index could not be read.`
    - `ipv4_interface_metric_out_of_range: ...`
  - 行状态通过 `InterfaceState.Reason` 与领域 `Collected.Errors` 双通道暴露；领域状态为 `Partial`，不会显示 `Ok`。
- `CreateRow`（约 232–258 行）只填充该家庭地址与标量；`InterfaceDescription` 记录行与 common errors。
- 未改 `Application/Adapters` 合同：仍使用现有 `InterfaceState`（每行带 `Family`/`IfIndex` 等），因此无需 R1-B 合同变更。

## 4. 测试与“修前失败 / 修后通过”

### 新增/更新

`tests/Windows/PartialSourceTests.cs`：

- 新增 `Dual_stack_interface_keeps_one_row_per_family_with_its_own_metrics_and_addresses`（约 330 行）
  - 双栈 index 12/13；IPv4 metric=25/DisableDefaultRoutes=false；IPv6 metric=900/DisableDefaultRoutes=true。
  - 断言两条行、地址分族、两族原生 metric 各读取一次。
- 新增 `Dual_stack_IPv6_metric_failure_is_a_separate_gap_and_IPv4_success_is_not_reused`（约 375 行）
  - IPv6 metric 注入 `AccessDenied`；断言 IPv4 行健康、IPv6 行 Metric/Flags/Luid 为 null、Reason 含注入原因、领域 Partial、两族 metric 均尝试一次。
- 新增 `Dual_stack_IPv6_index_gap_keeps_an_IPv6_row_instead_of_hiding_it_behind_IPv4`（约 415 行）
  - IPv4 index 存在、IPv6 index=null 但 IPv6 地址存在；断言仍产生 IPv6 缺口行（IfIndex=0、原因码），且不调用 IPv6 metric。
- 更新 `An_unreadable_selected_IPv4_row_never_borrows_IPv6_metric_or_flags`（约 137 行）
  - 旧断言锁定“单条 ipv4 行/只读 IPv4”；新断言两条行、IPv4 缺口、IPv6 成功值独立保留。

### 修前执行（旧代码，未改 WindowsSources）

命令与结果（exit code 1）：

```text
dotnet test tests/Windows/NetScope.Windows.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Dual_stack" -v:m
失败 3，通过 0，总计 3
- Dual_stack_interface_keeps_one_row_per_family_with_its_own_metrics_and_addresses
  Assert.Equal Failure: Expected 2, Actual 1（仅返回一条 ipv4 行）
- Dual_stack_IPv6_metric_failure_is_a_separate_gap_and_IPv4_success_is_not_reused
  Assert.Equal Failure: Expected Partial, Actual Ok（IPv6 缺口从未被观察）
- Dual_stack_IPv6_index_gap_keeps_an_IPv6_row_instead_of_hiding_it_behind_IPv4
  Assert.Equal Failure: Expected Partial, Actual Ok（IPv6 index 缺失未形成缺口）
```

### 修后执行

```text
dotnet build src/NetScope.Collectors.Windows/NetScope.Collectors.Windows.csproj
exit 0；0 warnings，0 errors

dotnet build tests/Windows/NetScope.Windows.Tests.csproj
exit 0；0 warnings，0 errors

dotnet test tests/Windows/NetScope.Windows.Tests.csproj --no-build --no-restore
exit 0；通过 82，失败 0，跳过 0，总计 82

dotnet test tests/Windows/NetScope.Windows.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Dual_stack" -v:m
exit 0；通过 3，失败 0，跳过 0
```

## 5. 未解决 / 未验证

1. **未做真实双栈机器的实时采集**：F09 回归全部通过确定性 seam（合成地址/索引/metric），未对用户当前网卡执行真实 IPv6 metric 读取；真实 Windows IP Helper 返回值仍需 Lead 的只读 smoke 复核。
2. **双栈接口现在产生两行**：这是 `InterfaceState` 合同兼容下的语义变化；其他 UI/规则/导出消费者若假设“每 NIC 一行”需由 Lead 全量测试确认。未修改会话/UI/诊断/导出。
3. **DNS 源未按家庭拆分**：本次 F09 范围是 `WindowsInterfaceSource` 的接口参数；`WindowsDnsSource` 仍按 `Ipv4Index ?? Ipv6Index` 选择索引，不是 F09 所述 metric/IgnoreDefaultRoutes 缺陷，未扩大改动。
4. **未验证非字节对齐/无 index 双栈组合之外的分类**：已覆盖双 index、IPv4 成功/IPv6 失败、IPv6 index 缺失、双族无 index；未覆盖真实驱动返回空 index 名称等厂商异常。
5. **不涉及 Application 合同变更**：无公共合同 diff；若后续需要每接口显式“多族集合”类型，应由 R1-B 评估，本次未越界。

## 6. 变更边界确认

- 无 git commit/push。
- 无外网探测、无真实网络配置写入、无 UAC、无 TUN/代理/路由/系统代理修改。
- 仅构建 `NetScope.Collectors.Windows` 与 `tests/Windows`，未执行全解决方案构建/UI 渲染/smoke。
