# T01-R1 执行报告：契约评审缺陷修复

日期：2026-11-04 15:40 (Asia/Shanghai)
前置：T01 首轮评审发现 5 项契约缺陷，T01 暂缓放行。
结论：**5 项全部修复，36 → 72 个契约测试通过；未开始 T02 之前的功能实现。**

## 1. 修复项与证据

| # | 缺陷 | 修复 | 回归证据 |
|---|---|---|---|
| 1 | 合法封套序列化后不合规：可选 `refs` 被写成 `null` | `EnvelopeJson.Options` 加 `DefaultIgnoreCondition = WhenWritingNull` | `Absent_optional_references_are_omitted_instead_of_written_as_null`；`Every_serialized_fixture_envelope_still_satisfies_the_schema`（6/6） |
| 2 | 显式 `null` 绕过 `required` | 新增 `EnvelopeIntegrity.EnsureComplete`，读写两侧都校验必填值非 null 且非空白 | `Explicit_null_required_text_member_is_rejected`（6 例）、`Explicit_null_required_object_member_is_rejected`（2 例）、`Blank_identifier_is_rejected_by_the_reader`（2 例） |
| 3 | 数值枚举被接受：`status:0` 变成 `Ok` | 用 `WireEnumJsonConverter<T>` 替换内置转换器，只接受声明的线名；未定义枚举值无法序列化 | `Numeric_status_value_is_rejected`（3 例）、`Numeric_string_status_value_is_rejected`（2 例）、`Numeric_event_type_value_is_rejected`（2 例）、`An_undefined_enum_member_cannot_be_serialized` |
| 4 | CLI 把未通过 schema 的事件计入事件数与成功数 | 抽出 `ContractInspection`：只有反序列化与 schema 校验都通过才计入；新增 `rejected_lines` 计数 | `Inspection_counts_only_events_that_pass_both_reader_and_schema`、`Inspection_counts_no_success_for_a_non_synthetic_event` |
| 5 | `date-time` 只校验外形 | `IsRfc3339Timestamp` 做真实日历与范围校验（日、时、分、秒、真实时区偏移 ±14:00） | `Schema_rejects_impossible_timestamps`（8 例）、`Schema_accepts_real_timestamps`（5 例，含闰日与闰秒） |

## 2. 实际执行的命令与结果

```text
dotnet build NetScopeRepair.slnx   exit=0   0 警告 0 错误
dotnet test  NetScopeRepair.slnx   exit=0   失败 0，通过 72，已跳过 0，总计 72
netscope check                     exit=0   events=6 ok=4 partial=1 observation_gap=1 rejected_lines=0
```

负向对照（人工构造，未提交）：把第 2 条事件 `sequence` 改为 0，CLI 输出
`events=1 ok=1 ... rejected_lines=1` 且退出码 1 —— 坏行不再污染统计。

## 3. 与首轮评审的差异

- 评审第 2 项原建议"启用 `RespectNullableAnnotations`"。实际选择显式 `EnvelopeIntegrity`：
  同一条拒绝路径给出可读原因，而不是序列化器内部消息；两者行为等价，理由写入代码注释。
- 评审额外指出的 `integer` 语义与 UTF-16 长度计数未在本轮修改：当前契约未使用
  `minimum`/`maxLength` 于非 ASCII 场景，属非阻断项，已记入待办。

## 4. 已知限制

1. `payload` 仍无按事件类型的专用 schema（T05 范围内另行处理，见 M0 报告）。
2. `SchemaConformance` 仍是窄实现，新增未支持关键字会让测试失败而不是静默放宽。
3. 空字符串的拒绝逻辑在 reader 与 schema 两侧重复，属刻意冗余。

## 5. 边界声明

本轮只改代码与测试。未运行旧修复脚本、未切换 TUN、未修改网络/代理/防火墙配置、
未上传、未创建远程仓库。
