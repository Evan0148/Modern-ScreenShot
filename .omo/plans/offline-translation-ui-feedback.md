# 离线翻译 UI 反馈改进（Toast + 取消能力）

## TL;DR

> **Quick Summary**: 为离线翻译（及 OCR）的截图路径补上攻防完整的 UI 反馈——新建共享 ToastWindow 组件分阶段显示"识别中→加载模型→翻译中"，全链路接线 CancellationToken（截图路径 toast 取消按钮 + 编辑器按钮变取消），修复 Swap 按钮重入、拒绝下载静默退出、取消被误报为失败等问题。
>
> **Deliverables**:
> - 新共享组件 `src/ModernScreenShot.App/Output/ToastWindow.cs`（提取 PinWindow.ShowToast 模式）
> - `TranslationFlow.EnsureModelAsync` 签名变更（ct + 可区分结果枚举）+ OCE 过滤
> - 截图路径翻译/OCR 的 toast 反馈接线（App.Translation.cs / App.Ocr.cs）
> - 编辑器翻译按钮运行期间变为取消按钮（EditorWindow.xaml.cs）
> - Swap 按钮防重入 + 忙碌状态（TranslationResultWindow.xaml.cs）
> - 诊断探针 `--render-translate-toast`（agent QA 快照通道）
> - 双语本地化新键 + check-i18n 通过
>
> **Estimated Effort**: Medium
> **Parallel Execution**: YES - 2 waves + final verification
> **Critical Path**: Task 1 → Task 4 → F1-F4

---

## Context

### Original Request
> 帮我探索这个项目中有关"离线翻译"不合理的地方，主要探索 UI 前端提示方面。比如目前使用离线翻译翻译的过程中，没有任何提示，可能导致用户以为程序卡死等问题

### Interview Summary
**Key Discussions**:
- 反馈形式：悬浮 Toast 窗（用户选定，复用 PinWindow.ShowToast 模式）
- 范围（用户全选）：截图路径翻译反馈 + OCR 截图路径同步补齐 + Swap 防重入 + 取消能力接线 + 拒绝下载提示
- 编辑器路径也可取消（用户确认：翻译按钮运行期间变为取消按钮）
- 测试策略：仅 agent 手动 QA（不搭建测试基础设施）

**Research Findings**（均带 file:line，详见 Context 末尾 References）:
- 截图路径 `RunTranslateCapture`（App.Translation.cs:31-90）从零反馈到结果窗口弹出，用户干等 2~180 秒
- 编辑器路径已有完整反馈（EditorWindow.xaml.cs:1232-1271）—— 被证明的模式
- Core 层 ct 管道端到端存在（TranslationModelDownloader.cs:90, TranslationService.cs:74, OcrService.cs:91）但 UI 全传 default
- 代码库风格：纯文字反馈，全 app 无 ProgressBar/spinner/等待光标

### Metis Review
**Identified Gaps**（全部已纳入计划）:
- OCE 被 catch(Exception) 吞掉误报为失败（TranslationFlow.cs:112, App.Translation.cs:85）→ 每个触碰的 catch 必须过滤 OCE（最高风险）
- EnsureModelAsync 无 ct 参数 → 签名变更波及 4 调用点
- 拒绝 vs 失败不可区分（同返回 false）→ 引入 EnsureModelResult 枚举
- Topmost toast 可能遮挡 owner=null 的下载确认 MessageBox → toast 作为 owner / 模态期间降 Topmost
- SwapTranslationAsync 下载进度走托盘 → 通道统一到 toast
- PinWindow.ShowToast 是 private 实例方法 → 必须提取为新共享组件，非字面复用

---

## Work Objectives

### Core Objective
让离线翻译（及 OCR）的每个入口在执行期间都有清晰可见的阶段性 UI 反馈，且长操作可被取消，取消/失败/拒绝三态可区分。

### Concrete Deliverables
- `src/ModernScreenShot.App/Output/ToastWindow.cs` — 共享 toast 组件（新文件）
- `src/ModernScreenShot.App/Translation/TranslationFlow.cs` — EnsureModelAsync 签名 + OCE 过滤 + 结果枚举
- `src/ModernScreenShot.App/App.Translation.cs` — 截图路径 toast + CTS + 通道统一
- `src/ModernScreenShot.App/App.Ocr.cs` — OCR 截图路径 toast
- `src/ModernScreenShot.App/Editor/EditorWindow.xaml.cs` — 编辑器取消按钮
- `src/ModernScreenShot.App/Translation/TranslationResultWindow.xaml.cs` — Swap 防重入
- `src/ModernScreenShot.App/Localization/Strings.{zh-CN,en-US}.xaml` — 新键
- 诊断探针 `--render-translate-toast`

### Definition of Done
- [ ] `dotnet build ModernScreenShot.sln -c Release` → exit 0
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1` → exit 0
- [ ] `ModernScreenShot.App.exe --translate-test` → exit 0（回归）
- [ ] `--render-translate-toast` 输出各阶段快照
- [ ] F1-F4 终审全部 APPROVE

### Must Have
- 截图路径翻译：toast 在 OCR 开始即出现（或延迟 ~300ms），阶段文字随 识别中→下载模型（含%）→加载引擎→翻译中 更新
- 截图路径 OCR：同款 toast 反馈（Ocr.Running）
- 取消能力：toast 取消按钮（仅可取消阶段显示）+ 编辑器翻译按钮变取消；取消后 toast/状态栏显示 Cancelled，**绝不显示 Failed**
- EnsureModelAsync 返回可区分结果（Ready/Declined/Failed/Cancelled）；拒绝下载时截图路径显示"已取消"提示
- Swap 按钮重新翻译期间禁用 + 忙碌文字，finally 恢复
- 新本地化键双语同步，check-i18n 通过
- 每个触碰的 catch(Exception) 过滤 OperationCanceledException

### Must NOT Have (Guardrails)
- ProgressBar / spinner / 等待光标（代码库无此范式）
- PinWindow.ShowToast 现有调用点的任何改动
- 设置页模型下载的取消 parity
- 推理流式/token 级进度
- 托盘气泡在 translate/OCR 截图路径之外的全局替换
- toast 可见性设置开关
- 编辑器 OCR 按钮取消（范围仅翻译按钮；OCR 编辑器路径保持现状）
- 任何测试基础设施搭建

---

## Verification Strategy (MANDATORY)

> **ZERO HUMAN INTERVENTION** - ALL verification is agent-executed.

### Test Decision
- **Infrastructure exists**: NO
- **Automated tests**: None（用户确认）
- **Agent-Executed QA**: ALWAYS — WPF 桌面应用的 QA 通道 = 构建 + 诊断探针快照 + 日志断言

### QA Policy
WPF 无 Playwright 路径。本项目已有的等价机制：
- **诊断探针**：`App.Features.cs` 已有渲染探针分派模式（`--show-ocr`、`case "ocr"/"translate"`，约 :151-173, :364-365）；新增 `--render-translate-toast` 同模式
- **端到端自检**：`--translate-test`（exit 0）、`--smoke`
- **日志断言**：`%LOCALAPPDATA%\Modern-ScreenShot\logs\app.log`（Diagnostic 事件落盘，App.Features.cs:60）
- **快照证据**：探针输出 + 运行截图保存到 `.omo/evidence/`

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1（基础设施，3 个并行）:
├── Task 1: 共享 ToastWindow 组件（新文件）[visual-engineering]
├── Task 2: 本地化新键（双语 + check-i18n）[quick]
└── Task 3: TranslationFlow 改造（ct + 结果枚举 + OCE 过滤）[deep]

Wave 2（接线，5 个并行；依赖 Wave 1）:
├── Task 4: 截图路径翻译 toast 接线（App.Translation.cs）[deep]
├── Task 5: 截图路径 OCR toast 接线（App.Ocr.cs）[unspecified-high]
├── Task 6: 编辑器翻译取消按钮（EditorWindow.xaml.cs）[unspecified-high]
├── Task 7: Swap 按钮防重入（TranslationResultWindow.xaml.cs）[quick]
└── Task 8: 诊断探针 --render-translate-toast [quick]

Wave FINAL（4 个并行审查，然后用户确认）:
├── Task F1: 计划符合性审计（oracle）
├── Task F2: 代码质量审查（unspecified-high）
├── Task F3: 真实手动 QA（unspecified-high）
└── Task F4: 范围保真检查（deep）

Critical Path: Task 1 → Task 4 → F1-F4 → user okay
Max Concurrent: 5（Wave 2）
```

### Dependency Matrix

| Task | Depends On | Blocks | Wave |
|---|---|---|---|
| 1 | — | 4, 5, 8 | 1 |
| 2 | — | 4, 5, 6, 7, 8 | 1 |
| 3 | — | 4, 6 | 1 |
| 4 | 1, 2, 3 | — | 2 |
| 5 | 1, 2 | — | 2 |
| 6 | 2, 3 | — | 2 |
| 7 | 2 | — | 2 |
| 8 | 1, 2 | — | 2 |

### Agent Dispatch Summary
- **Wave 1**: 3 — T1 → `visual-engineering`, T2 → `quick`, T3 → `deep`
- **Wave 2**: 5 — T4 → `deep`, T5 → `unspecified-high`, T6 → `unspecified-high`, T7 → `quick`, T8 → `quick`
- **FINAL**: 4 — F1 → `oracle`, F2 → `unspecified-high`, F3 → `unspecified-high`, F4 → `deep`

---

## TODOs

> Implementation + Verification = ONE Task. EVERY task has QA Scenarios.

- [ ] 1. 共享 ToastWindow 组件（新文件）

  **What to do**:
  - 新建 `src/ModernScreenShot.App/Output/ToastWindow.cs`：从 `PinWindow.ShowToast`（PinWindow.cs:428-489）提取模式为独立共享组件
  - 视觉/动效保持一致：无边框置顶小窗、深色 0xE0 背景、白字、14x8 padding、`UiMotion.FadeSlideIn(content, 0, 8, 150)` 进入、`UiMotion.FadeOut` 退出
  - 与 PinWindow.ShowToast 的差异（必须实现）：
    - 不自动 2s 关闭：持续显示直到调用方显式关闭（进行中状态）；终态（失败/取消）显示 ~3s 后自动 FadeOut 关闭
    - `UpdateText(string)` 原地替换文字（复用 replace-in-place 语义）
    - 可选取消按钮：文字按钮（用 Task 2 的 `Toast.Cancel` 键），点击触发 `Action` 回调；仅在调用方传入回调时显示
    - `ShowActivated = false`，不抢焦点
    - 位置：指定显示器工作区右下角（调用方传入；per-monitor DPI 感知）
    - 提供静态入口（如 `ToastWindow ShowOrUpdate(...)` / 单例管理器），保证"单例 toast 原地替换"语义：已有实例则替换文字与取消回调，无实例则新建
  - 模态共存：暴露方法允许调用方在弹 MessageBox 前将 toast 设为其 owner（或临时 Topmost=false），防止遮挡模态框
  - 应用退出时能正常关闭（ShutdownMode=OnExplicitShutdown 下无 keep-alive 问题，但需确保 Close 可走）

  **Must NOT do**:
  - 不改动 PinWindow.cs 的任何现有代码（ShowToast 保持原样）
  - 不引入 ProgressBar/spinner/图标按钮（纯文字）
  - 不做全局 toast 服务/队列抽象（单例即可）

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: 新建 WPF UI 组件，含动效与视觉一致性要求
  - **Skills**: []
    - 无匹配技能（animate/frontend 技能面向 web；WPF 桌面组件无对应 skill）
  - **Skills Evaluated but Omitted**:
    - `animate`: 面向 web 动画，不适用 WPF
    - `apple-design`: web 手势/材质，不适用

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1（with Tasks 2, 3）
  - **Blocks**: Tasks 4, 5, 8
  - **Blocked By**: None

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/Output/PinWindow.cs:428-489` - ShowToast 完整实现：窗口样式、FadeSlideIn/FadeOut 用法、DispatcherTimer、replace-in-place、工作区锚定 —— 提取的蓝本
  - `src/ModernScreenShot.App/UiMotion.cs` - FadeSlideIn / FadeOut 动效原语签名
  **API/Type References**:
  - `src/ModernScreenShot.App/App.xaml.cs` - ShutdownMode=OnExplicitShutdown 确认 toast 生命周期无 keep-alive 影响
  **WHY Each Reference Matters**:
  - PinWindow.cs:428-489：视觉参数（颜色、padding、圆角）与动效时序必须与现有 toast 完全一致，用户才感知为"同一个 app 的提示"
  - UiMotion.cs：不要自己写 DoubleAnimation，直接调用现有原语

  **Acceptance Criteria**:
  - [ ] `dotnet build ModernScreenShot.sln -c Release` → exit 0
  - [ ] ToastWindow.cs 存在且不引用 PinWindow 的任何 internal/private 成员
  - [ ] PinWindow.cs 零改动（git diff 无该文件）

  **QA Scenarios**:
  ```
  Scenario: 组件可实例化并显示（通过临时探针或后续 Task 8 验证）
    Tool: Bash
    Preconditions: Task 1 完成（本任务的完整可视化验证由 Task 8 探针承担，此处仅验证编译与基本 API 形状）
    Steps:
      1. dotnet build ModernScreenShot.sln -c Release → 断言 exit 0
      2. grep ToastWindow.cs 确认公开 API：ShowOrUpdate / UpdateText / 关闭方法 / 可选取消回调参数
      3. git diff --stat src/ModernScreenShot.App/Output/PinWindow.cs → 断言无输出（零改动）
    Expected Result: 编译通过；API 形状满足 Task 4/5 调用需求；PinWindow.cs 未触碰
    Failure Indicators: 编译错误；PinWindow.cs 出现在 diff 中
    Evidence: .omo/evidence/task-1-build.txt

  Scenario: 无违禁 UI 范式
    Tool: Bash (grep)
    Steps:
      1. grep ToastWindow.cs 中 "ProgressBar|Cursors.Wait|OverrideCursor|Spinner" → 断言零匹配
    Expected Result: 纯文字反馈，无违禁模式
    Evidence: .omo/evidence/task-1-no-forbidden.txt
  ```

  **Commit**: YES
  - Message: `feat(toast): add shared ToastWindow component for async operation feedback`
  - Files: `src/ModernScreenShot.App/Output/ToastWindow.cs`
  - Pre-commit: `dotnet build ModernScreenShot.sln -c Release`

- [ ] 2. 本地化新键（双语 + check-i18n）

  **What to do**:
  - 在 `Strings.zh-CN.xaml` 与 `Strings.en-US.xaml` 同步新增键（两个文件同一 commit）：
    - `Toast.Cancel` — "取消" / "Cancel"
    - `Translate.Toast.Recognizing` — "正在识别文字…" / "Recognizing text…"（可复用 Ocr.Running 语义，若键已存在则直接用 `Ocr.Running`，不重复造键——先检查再决定）
    - `Translate.Toast.LoadingEngine` — "正在加载翻译引擎…（首次约需几秒）" / "Loading translation engine… (first run takes a few seconds)"
    - `Translate.Toast.Translating` — "正在翻译…" / "Translating…"
    - `Translate.Toast.Cancelled` — 若 `Translate.Cancelled` 文案适用则复用，否则新增
    - `Translate.Toast.Resuming` — "从 {0:0}% 继续下载…" / "Resuming download from {0:0}%…"（断点续传提示）
  - 先 grep 两个 Strings 文件确认可复用键（Translate.Running / Ocr.Running / Translate.Cancelled / Translate.Downloading 的现有文案），避免重复键
  - 运行 `tools/check-i18n.ps1` 确认两语言键集合一致

  **Must NOT do**:
  - 不修改任何现有键的文案
  - 不只改一个语言文件

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 两个 XAML 资源文件加键，机械性工作
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1（with Tasks 1, 3）
  - **Blocks**: Tasks 4, 5, 6, 7, 8
  - **Blocked By**: None

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/Localization/Strings.zh-CN.xaml` - 现有 Translate.*（:362 附近）/ Ocr.* / Toast.* 键格式
  - `src/ModernScreenShot.App/Localization/Strings.en-US.xaml` - 同上，英文文案
  **Test References**:
  - `tools/check-i18n.ps1` - 键一致性检查脚本，必须 exit 0

  **Acceptance Criteria**:
  - [ ] 两个 Strings 文件新键完全对称
  - [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1` → exit 0
  - [ ] 无重复键（新增前已 grep 确认）

  **QA Scenarios**:
  ```
  Scenario: i18n 一致性
    Tool: Bash
    Steps:
      1. 运行 check-i18n.ps1 → 断言 exit 0
      2. grep 新键名在两个文件中各出现恰好一次
    Expected Result: 检查通过，键对称
    Failure Indicators: check-i18n 报缺失/多余键
    Evidence: .omo/evidence/task-2-i18n.txt

  Scenario: 无重复键（负面）
    Tool: Bash (grep)
    Steps:
      1. 对每个新键名 grep -c 整个 Localization 目录 → 断言每个键总数 = 2（每语言一次）
    Expected Result: 无重复定义
    Evidence: .omo/evidence/task-2-no-dup.txt
  ```

  **Commit**: YES
  - Message: `feat(i18n): add translation/OCR toast phase and cancel strings (zh-CN, en-US)`
  - Files: `src/ModernScreenShot.App/Localization/Strings.zh-CN.xaml`, `src/ModernScreenShot.App/Localization/Strings.en-US.xaml`
  - Pre-commit: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1`

- [ ] 3. TranslationFlow 改造：EnsureModelAsync 可取消 + 结果可区分 + OCE 过滤

  **What to do**:
  - 修改 `src/ModernScreenShot.App/Translation/TranslationFlow.cs`：
  - 新增枚举（可放同文件或单独文件）：`EnsureModelResult { Ready, Declined, Failed, Cancelled }`
  - `EnsureModelAsync` 签名变更：增加 `CancellationToken ct = default` 参数；返回类型从 `Task<bool>` 改为 `Task<EnsureModelResult>`
    - 引擎缺失 → `Failed`（保留现有 notify）
    - 模型缺失弹 MessageBox，用户拒绝 → `Declined`（当前 :90 静默 return false 的位置）
    - 用户接受 → 下载，把 ct 传给 `TranslationModelDownloader.DownloadAsync(progress, ct)`（TranslationModelDownloader.cs:90 已支持）
    - 下载成功 → `Ready`；下载异常 → `Failed`；`OperationCanceledException` → `Cancelled`
  - **OCE 过滤（最高优先）**：现有 `catch (Exception)`（:112）会把取消误报为 `Translate.DownloadFailed`——必须改为 `catch (OperationCanceledException) → Cancelled` 在前，`catch (Exception) → Failed` 在后
  - 下载进度回调保持 `(title, body)` 形状不变（调用方接 toast 或状态栏由 Task 4/6 决定）；如已有 `InstalledBytes() > 0`（断点续传），进度回调首发时可用 Task 2 的 `Translate.Toast.Resuming` 文案
  - 更新本文件内部对 `TranslateAsync` 的调用，透传 ct（:62-67 已支持 ct）
  - **本任务不改 4 个外部调用点**（App.Translation.cs x2 + EditorWindow.xaml.cs x2）——它们由 Task 4/6 适配；本任务结束后编译会暂时失败是可接受的（Wave 内并行），或在本任务内同步做最小适配让编译通过（推荐：把 4 个调用点的返回值判断从 `if (!ok)` 改为 `if (result != EnsureModelResult.Ready)` 的最小机械适配，取消/拒绝的细粒度 UI 行为仍由 Task 4/6 实现）

  **Must NOT do**:
  - 不改 TranslationService.cs / TranslationModelDownloader.cs / OcrService.cs（Core 层已就绪）
  - 不实现 toast（Task 1 的组件，Task 4/5 才接线）
  - 不改变 MessageBox 的文案与确认逻辑（只接 ct 和结果枚举）

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 签名变更波及多调用点 + OCE 过滤是全局最高风险点，需要仔细追踪所有 catch 路径
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1（with Tasks 1, 2）
  - **Blocks**: Tasks 4, 6
  - **Blocked By**: None

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.Core/Translation/TranslationService.cs:119` - OCE 重抛的先例：`catch (Exception ex) when (ex is not ... and not OperationCanceledException)` —— 本项目的 OCE 过滤范式，照此办理
  **API/Type References**:
  - `src/ModernScreenShot.Core/Translation/TranslationModelDownloader.cs:90` - `DownloadAsync(IProgress<double>, CancellationToken)` 已支持 ct；`:61-66` `InstalledBytes()` 查续传起点
  - `src/ModernScreenShot.App/App.Translation.cs:77,125` - 2 个 EnsureModelAsync 调用点
  - `src/ModernScreenShot.App/Editor/EditorWindow.xaml.cs:1246,1281` - 另 2 个 EnsureModelAsync 调用点
  **WHY Each Reference Matters**:
  - TranslationService.cs:119：项目已确立"OCE 不当普通异常处理"的约定，本任务必须遵守同一约定，否则取消会被误报为失败

  **Acceptance Criteria**:
  - [ ] `EnsureModelAsync` 返回 `EnsureModelResult`，接受 ct 并传入 DownloadAsync
  - [ ] `catch (OperationCanceledException)` 在 `catch (Exception)` 之前
  - [ ] 4 个调用点完成最小适配后 `dotnet build ModernScreenShot.sln -c Release` → exit 0
  - [ ] `--translate-test` → exit 0（引擎链路回归）

  **QA Scenarios**:
  ```
  Scenario: 回归——正常下载/已安装流程不受影响
    Tool: Bash
    Preconditions: 模型已安装（%程序目录%\translate\models\ 有 GGUF）或测试机已装
    Steps:
      1. dotnet build ModernScreenShot.sln -c Release → exit 0
      2. 运行 ModernScreenShot.App.exe --translate-test → 断言 exit 0
      3. 读 %LOCALAPPDATA%\Modern-ScreenShot\logs\translate-test.txt → 断言含翻译结果
    Expected Result: 签名变更未破坏引擎链路
    Failure Indicators: exit 非 0；translate-test.txt 缺失或含异常
    Evidence: .omo/evidence/task-3-translate-test.txt

  Scenario: OCE 过滤静态验证（负面）
    Tool: Bash (grep)
    Steps:
      1. grep TranslationFlow.cs 中所有 catch 块 → 断言存在 catch (OperationCanceledException) 且位于 catch (Exception) 之前
      2. 断言没有任何路径把 OCE 映射为 Failed/DownloadFailed
    Expected Result: 取消路径必然返回 Cancelled
    Failure Indicators: OCE 被 catch(Exception) 吞掉
    Evidence: .omo/evidence/task-3-oce-filter.txt
  ```

  **Commit**: YES
  - Message: `refactor(translation): make EnsureModelAsync cancellable with distinguishable result`
  - Files: `src/ModernScreenShot.App/Translation/TranslationFlow.cs`（+ 4 个调用点的最小机械适配）
  - Pre-commit: `dotnet build ModernScreenShot.sln -c Release` 且 `--translate-test` exit 0

- [ ] 4. 截图路径翻译 toast 接线（App.Translation.cs）

  **What to do**:
  - 修改 `src/ModernScreenShot.App/App.Translation.cs`：
  - `RunTranslateCapture`（:31-65）：
    - 开始即显示 toast（Task 1 组件），文字 = `Ocr.Running` / `Translate.Toast.Recognizing`；考虑 ~300ms 延迟显示避免亚秒操作闪烁（用 DispatcherTimer 或 Task.Delay 门控，操作完成前未触发则完全不显示）
    - OCR 完成进入模型阶段 → `UpdateText` 为下载进度（EnsureModelAsync 的 notify 回调接入 toast 而非托盘 Notify）或 `Translate.Toast.LoadingEngine`
    - 推理阶段 → `Translate.Toast.Translating`
    - 成功 → 立即关闭 toast 并弹结果窗口（结果窗口即完成信号）
  - 取消接线：
    - 本流程创建 `CancellationTokenSource`，cts.Token 传入 OCR（OcrService.cs:91 已支持）、EnsureModelAsync（Task 3 新签名）、TranslateAsync（:81）
    - toast 在可取消阶段（下载/引擎/推理）显示取消按钮 → 点击 `cts.Cancel()`
    - 新操作启动时取消并替换旧 CTS（单例 toast 语义：新操作取代旧操作）
  - 结果分支全部接 Task 3 的 `EnsureModelResult`：
    - `Declined` → toast 显示 `Translate.Cancelled` ~3s 后关闭（修复"拒绝后静默退出"）
    - `Cancelled`（用户点取消）→ toast 显示 `Translate.Cancelled` ~3s 后关闭
    - `Failed` → toast 显示 `Translate.DownloadFailed`/错误 ~3s 后关闭
  - **OCE 过滤**：:85 的 `catch (Exception)` 必须先过滤 `OperationCanceledException` → Cancelled 分支，绝不落入 `Translate.Failed`
  - `TranslateAndShowAsync`（:71-90）与 `SwapTranslationAsync`（:116-139）：
    - SwapTranslationAsync 的下载进度/失败从托盘 Notify（:125）改接 toast（通道一致性）；复用同一 CTS/toast 管理机制
  - MessageBox 共存：EnsureModelAsync 弹下载确认框前，把 toast 设为 MessageBox owner 或临时 Topmost=false（与 Task 3 协作的接线点）
  - 保留托盘 Notify 作为 toast 不可用时的兜底？——不保留：toast 是独立窗口不依赖托盘，始终可用；删除该路径上对 Notify 的"进行中"依赖，错误仍可同时 Notify（保持现有错误通道不变）

  **Must NOT do**:
  - 不改 OverlayWindow.cs / OverlaySession.cs（覆盖层行为不变）
  - 不改 TranslationResultWindow 的 XAML
  - 不动编辑器路径（Task 6）
  - 不引入 ProgressBar/spinner

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 主流程状态机（4 阶段 × 4 结果分支）+ CTS 生命周期 + OCE 三态区分，错误路径密集
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2（with Tasks 5, 6, 7, 8）
  - **Blocks**: None（最终集成由 F3 验证）
  - **Blocked By**: Tasks 1, 2, 3

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/Editor/EditorWindow.xaml.cs:1225-1271` - RunTranslate 已被证明的反馈生命周期（禁用→Running→下载进度→Done/Failed/Cancelled），本任务把同一生命周期搬到 toast
  - `src/ModernScreenShot.App/Output/PinWindow.cs:428-489` - toast 视觉/动效蓝本
  **API/Type References**:
  - `src/ModernScreenShot.App/App.Translation.cs:31-139` - 全部改动点
  - `src/ModernScreenShot.Core/Ocr/OcrService.cs:91` - RecognAsync 的 ct 参数
  - `src/ModernScreenShot.App/Translation/TranslationFlow.cs` - Task 3 后的新签名
  **WHY Each Reference Matters**:
  - EditorWindow.xaml.cs:1225-1271：阶段顺序、文案选择、异常分支划分都应与编辑器路径保持语义一致，避免两套行为

  **Acceptance Criteria**:
  - [ ] `dotnet build` exit 0；`--translate-test` exit 0
  - [ ] grep 确认 :85 附近 catch 过滤 OCE
  - [ ] SwapTranslationAsync 不再用 Notify 传下载进度（通道统一）
  - [ ] Declined/Cancelled/Failed 三态各有独立 toast 文案分支

  **QA Scenarios**:
  ```
  Scenario: 完整流程 toast 阶段连贯（已装模型）
    Tool: Bash + 诊断探针（Task 8）+ 日志
    Preconditions: 模型已安装；Task 8 探针可用
    Steps:
      1. dotnet build → exit 0
      2. 通过探针/运行触发截图路径翻译（或 --translate-test 回归）
      3. 断言日志/探针快照中阶段顺序：Recognizing → LoadingEngine（冷启动时）→ Translating → 结果窗口
    Expected Result: 阶段按序出现，无 Failed 误报
    Failure Indicators: 日志出现 Exception 但 UI 显示 Failed 而非 Cancelled；阶段跳跃
    Evidence: .omo/evidence/task-4-phases.png, .omo/evidence/task-4-log.txt

  Scenario: 取消被正确报告（负面，核心风险）
    Tool: Bash (grep + 日志断言)
    Steps:
      1. grep App.Translation.cs 确认 catch (OperationCanceledException) 在 catch (Exception) 之前
      2. 模拟取消路径（代码审查追踪 cts.Cancel() → OCE → Cancelled 分支）→ 断言不经过 Translate.Failed
    Expected Result: 取消永远显示 Cancelled
    Failure Indicators: OCE 落入 catch(Exception) → Failed 分支
    Evidence: .omo/evidence/task-4-cancel-path.txt

  Scenario: 拒绝下载有提示（负面）
    Tool: 代码审查 + 日志
    Steps:
      1. 追踪 EnsureModelResult.Declined 分支 → 断言 toast 显示 Translate.Cancelled 而非静默 return
    Expected Result: 拒绝后用户看到"已取消"
    Evidence: .omo/evidence/task-4-declined.txt
  ```

  **Commit**: YES
  - Message: `feat(translation): toast feedback + cancellation on capture translate path`
  - Files: `src/ModernScreenShot.App/App.Translation.cs`
  - Pre-commit: `dotnet build` + `--translate-test` exit 0

- [ ] 5. 截图路径 OCR toast 接线（App.Ocr.cs）

  **What to do**:
  - 修改 `src/ModernScreenShot.App/App.Ocr.cs` 的 `RunOcrCapture`（:29-52）：
  - OCR 开始 → toast 显示 `Ocr.Running`（**必须带 ~300ms 延迟显示门控**：热态 OCR 仅 60-400ms，立即显示会闪烁；操作在延迟窗口内完成则 toast 完全不出现）
  - 完成 → 结果窗口弹出前关闭 toast（结果窗口即完成信号）
  - 失败/空结果 → toast 显示 `Ocr.Failed` / `Ocr.Empty` ~3s 后关闭（替代或补充现有托盘 Notify；保持 Notify 作为次要通道不删除，与 Task 4 的策略一致）
  - OCR 阶段**不显示取消按钮**（亚秒操作，取消无意义；与 Defaults Applied 决策一致）
  - 复用 Task 4 建立的 toast 单例管理机制（若 Task 4 提取了共享 helper，直接用；若两任务并行开发，约定 helper 放在 App.Translation.cs 或新文件 App.Toast.cs，后完成者做去重——在任务的 Must NOT do 中约束）

  **Must NOT do**:
  - 不给 OCR 阶段加取消按钮
  - 不改 OcrResultWindow
  - 不改编辑器 OCR 路径（EditorWindow RunOcr 已有状态栏反馈）
  - 不删除现有托盘 Notify 错误通道

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 模式已由 Task 4 确立，但延迟门控与单例协调需要细心
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2（with Tasks 4, 6, 7, 8）
  - **Blocks**: None
  - **Blocked By**: Tasks 1, 2
  - **协调注意**: 与 Task 4 共享 toast helper；若 Task 4 未完成，自行在 `App.Toast.cs`（新文件）实现 helper，Task 4 复用——两任务都**禁止**各自造重复 helper（F4 会检查）

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/App.Translation.cs`（Task 4 产物）- toast 接线模式
  - `src/ModernScreenShot.App/App.Ocr.cs:29-52` - 改动点
  **API/Type References**:
  - `src/ModernScreenShot.Core/Ocr/OcrService.cs:91-92` - RecognizeAsync（ct 已支持但本任务不接取消）

  **Acceptance Criteria**:
  - [ ] `dotnet build` exit 0
  - [ ] 延迟门控实现存在（DispatcherTimer 或等效），阈值 ≈300ms
  - [ ] 失败/空结果经 toast 提示

  **QA Scenarios**:
  ```
  Scenario: 热态 OCR 无 toast 闪烁（负面/性能）
    Tool: 代码审查 + 日志
    Steps:
      1. 审查延迟门控实现 → 断言 300ms 内完成的操作不创建 toast 窗口
      2. 断言窗口创建发生在延迟回调内而非调用处
    Expected Result: 亚秒操作零 UI 噪声
    Failure Indicators: toast 在调用处无条件 Show
    Evidence: .omo/evidence/task-5-delay-gate.txt

  Scenario: 失败路径有 toast 提示
    Tool: 代码审查
    Steps:
      1. 追踪 Ocr.Failed / Ocr.Empty 分支 → 断言 toast 显示对应文案并 ~3s 自动关闭
    Expected Result: 失败不再只有易错过的托盘气泡
    Evidence: .omo/evidence/task-5-failure.txt
  ```

  **Commit**: YES
  - Message: `feat(ocr): toast feedback on capture OCR path`
  - Files: `src/ModernScreenShot.App/App.Ocr.cs`（+ 可能的 `App.Toast.cs`）
  - Pre-commit: `dotnet build ModernScreenShot.sln -c Release`

- [ ] 6. 编辑器翻译按钮变取消（EditorWindow.xaml.cs）

  **What to do**:
  - 修改 `src/ModernScreenShot.App/Editor/EditorWindow.xaml.cs` 的 `RunTranslate`（:1225-1271）与 `SwapInEditorAsync`（:1276+）：
  - 创建 `CancellationTokenSource`（成员字段，如 `_translateCts`），token 传入 EnsureModelAsync（Task 3 新签名）与 TranslateAsync
  - 翻译开始时：`_translateButton` 文本切换为 `Toast.Cancel`、保持 IsEnabled=true、点击行为切换为 `cts.Cancel()`（或新增独立取消行为；实现方式二选一：复用按钮切换 Content+Handler，或按 house 风格改按钮文字+Tag 状态机——选改动最小的）
  - 取消后：状态栏显示 `Translate.Cancelled`（修复现状该键仅表示"拒绝下载"的误称问题：拒绝下载与真取消现在可用 EnsureModelResult.Declined vs Cancelled 区分，两者都可显示 Cancelled 文案，语义正确）
  - 完成/失败/取消后在 finally 中恢复按钮原文（`Translate` 按钮原文字）与原点击行为，dispose CTS
  - **OCE 过滤**：:1267 附近的 catch 必须先过滤 OperationCanceledException → Cancelled，绝不落入 `Translate.Failed`
  - 防重入保持现状语义：运行期间再次点击 = 取消而非二次启动（按钮语义已变为取消，天然防重入）
  - 编辑器窗口关闭时有在途翻译：取消 CTS（Closed 事件里 Cancel+Dispose），避免后台操作写死窗口的状态栏
  - 适配 Task 3 的 EnsureModelResult：Declined → 状态栏 `Translate.Cancelled`；Failed → `Translate.Failed`；Cancelled → `Translate.Cancelled`

  **Must NOT do**:
  - 不改编辑器 OCR 按钮行为（范围外）
  - 不改状态栏 SetStatus 机制本身（:840）
  - 不引入 toast 到编辑器（编辑器有状态栏，house 风格已确立）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: WPF 按钮状态机 + CTS 生命周期 + 窗口关闭边界，模式明确但边界情况多
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2（with Tasks 4, 5, 7, 8）
  - **Blocks**: None
  - **Blocked By**: Tasks 2, 3

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/Editor/EditorWindow.xaml.cs:1225-1299` - RunTranslate / SwapInEditorAsync 现状
  - `src/ModernScreenShot.App/Settings/SettingsWindow.xaml.cs:493-521` - 按钮文字切换 + finally 恢复 + 重试允许的成熟先例
  **API/Type References**:
  - `src/ModernScreenShot.App/Translation/TranslationFlow.cs` - Task 3 后的 EnsureModelResult
  **WHY Each Reference Matters**:
  - SettingsWindow.xaml.cs:493-521：按钮 Content 切换、guard 字段、finally 恢复是本代码库已验证的按钮状态机写法，直接套用

  **Acceptance Criteria**:
  - [ ] `dotnet build` exit 0；`--translate-test` exit 0
  - [ ] 运行期间按钮文字为"取消"，点击触发 Cancel
  - [ ] finally 恢复按钮原文与行为；窗口 Closed 取消 CTS
  - [ ] catch 过滤 OCE

  **QA Scenarios**:
  ```
  Scenario: 取消按钮状态机（静态+日志验证）
    Tool: Bash (grep) + 代码审查
    Steps:
      1. grep EditorWindow.xaml.cs 确认 _translateCts 字段、Cancel 接线、finally 恢复块
      2. 追踪：点击取消 → OCE → 状态栏 Translate.Cancelled（非 Failed）
      3. 追踪：窗口 Closed → cts.Cancel() → 无死窗口写入（现有 _closed guard 模式，参考 :1271）
    Expected Result: 取消语义正确，恢复完备
    Failure Indicators: OCE 落入 Failed；finally 缺失；Closed 无取消
    Evidence: .omo/evidence/task-6-cancel-sm.txt

  Scenario: 防重入（负面）
    Tool: 代码审查
    Steps:
      1. 断言运行期间按钮语义为取消，不存在二次启动路径
    Expected Result: 双击 = 启动+取消，不会并发两个翻译
    Evidence: .omo/evidence/task-6-reentry.txt
  ```

  **Commit**: YES
  - Message: `feat(editor): translate button becomes cancel while running`
  - Files: `src/ModernScreenShot.App/Editor/EditorWindow.xaml.cs`
  - Pre-commit: `dotnet build` + `--translate-test` exit 0

- [ ] 7. Swap 按钮防重入 + 忙碌状态（TranslationResultWindow.xaml.cs）

  **What to do**:
  - 修改 `src/ModernScreenShot.App/Translation/TranslationResultWindow.xaml.cs` 的 `OnSwapClick`（:80 附近）：
  - 点击后：`SwapButton.IsEnabled = false`，`StatusText` 显示 `Translate.Toast.Translating`（复用 Task 2 新键）覆盖原统计行
  - `SwapRequested` 事件保持 `Action` 形状不变（调用方在 App.Translation.cs SwapTranslationAsync / EditorWindow SwapInEditorAsync）；需要让窗口知道重译完成——新增公开方法如 `ShowRetranslationDone(...)` / 复用现有结果更新入口（构造时传入的原文/译文更新路径），在结果更新时恢复按钮与统计行
  - **finally 恢复**：重译失败或窗口中途关闭都要恢复/不再触碰：
    - 结果更新路径里恢复 `IsEnabled = true` + 统计行（`Translate.Status` 格式，:66-67 现有逻辑）
    - 失败路径：调用方（Task 4 的 SwapTranslationAsync 失败分支）通知窗口恢复——若调用方走 toast 提示失败，窗口侧通过同一恢复入口复原
    - 窗口 Closed 后：恢复逻辑必须先检查窗口存活（Dispatcher/IsLoaded guard），不得操作已关闭窗口
  - 双击防护验证：禁用后第二次点击无法进入 handler

  **Must NOT do**:
  - 不改 TranslationResultWindow.xaml 布局（只动 code-behind 状态）
  - 不改变 SwapRequested 事件签名（避免波及两个订阅方之外的代码）
  - 不在窗口内实现翻译逻辑（重译仍在 App.Translation.cs）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单文件 code-behind 状态管理，模式清晰
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2（with Tasks 4, 5, 6, 8）
  - **Blocks**: None
  - **Blocked By**: Task 2（用新键）；与 Task 4 的 SwapTranslationAsync 失败/完成通知有接口约定（QA 集成由 F3 覆盖）

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/Translation/TranslationResultWindow.xaml.cs:42-75` - StatusText 更新与 _copyFlash 计时器模式（临时覆盖文字再恢复）——忙碌状态套用同一机制
  - `src/ModernScreenShot.App/Ocr/OcrResultWindow.xaml.cs:32-58` - 同构窗口的同一模式
  - `src/ModernScreenShot.App/Settings/SettingsWindow.xaml.cs:493-521` - 按钮禁用/恢复先例
  **API/Type References**:
  - `src/ModernScreenShot.App/App.Translation.cs:101,116-139` - SwapRequested 订阅方与重译完成/失败的通知点

  **Acceptance Criteria**:
  - [ ] `dotnet build` exit 0
  - [ ] 点击后按钮禁用 + StatusText 显示 Translating；完成/失败均恢复
  - [ ] 窗口关闭后恢复逻辑有存活 guard

  **QA Scenarios**:
  ```
  Scenario: 防双击重入（核心）
    Tool: 代码审查 + 日志
    Steps:
      1. 审查 OnSwapClick：第一行禁用按钮 → 断言第二次点击无法重入
      2. 追踪恢复路径：结果更新 / 失败 / 窗口关闭 三条路径都覆盖
    Expected Result: 一次点击至多触发一次重译；恢复无遗漏
    Failure Indicators: IsEnabled 恢复缺失于任一路径
    Evidence: .omo/evidence/task-7-double-click.txt

  Scenario: 窗口中途关闭（负面）
    Tool: 代码审查
    Steps:
      1. 追踪 Closed 后重译完成 → 断言恢复代码检查窗口存活，不抛异常
    Expected Result: 无 ObjectDisposed/NullReference 风险
    Evidence: .omo/evidence/task-7-closed-guard.txt
  ```

  **Commit**: YES
  - Message: `fix(translation): disable swap button during retranslation`
  - Files: `src/ModernScreenShot.App/Translation/TranslationResultWindow.xaml.cs`
  - Pre-commit: `dotnet build ModernScreenShot.sln -c Release`

- [ ] 8. 诊断探针 --render-translate-toast

  **What to do**:
  - 按 house 诊断模式（现有渲染探针分派在 `App.Features.cs`：`--show-ocr` 与 `case "ocr"/"translate"`，约 :151-173, :364-365）新增 `--render-translate-toast`：
  - 渲染 ToastWindow 的各阶段状态并输出快照（PNG 到 `out\` 或探针既有输出目录）：
    - 状态 1：识别中（无取消按钮）
    - 状态 2：下载中 42%（含取消按钮）
    - 状态 3：加载引擎中（含取消按钮）
    - 状态 4：翻译中（含取消按钮）
    - 状态 5：已取消（终态）
    - 状态 6：失败（终态）
  - exit 0 表示渲染成功；快照路径打印到 stdout 供证据收集
  - 探针不触发真实翻译/下载，纯 UI 渲染（与现有渲染探针同哲学）
  - 接线点：App.xaml.cs 或 App.Features.cs 的命令行分派处（与现有 --render-* 同位置）

  **Must NOT do**:
  - 不在探针中启动真实引擎/下载
  - 不改变现有渲染探针（--show-ocr 等）行为
  - 不引入新的输出目录约定（跟随现有探针）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 现有探针模式照搬，纯渲染无业务逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2（with Tasks 4, 5, 6, 7）
  - **Blocks**: None（F3 的 QA 依赖它）
  - **Blocked By**: Tasks 1, 2

  **References**:
  **Pattern References**:
  - `src/ModernScreenShot.App/App.Features.cs:151-173,364-365` - 现有渲染探针分派（`--show-ocr`、`case "ocr"/"translate"`，诊断渲染器模式）—— 照搬此模式
  - `src/ModernScreenShot.App/App.Translation.cs:174,216` - --translate-test / --translate-install 探针分派先例
  - `verify_editor_ocr.png` 等仓库根目录快照 - 既有探针快照产物示例
  **API/Type References**:
  - `src/ModernScreenShot.App/Output/ToastWindow.cs`（Task 1 产物）- 被渲染对象

  **Acceptance Criteria**:
  - [ ] `--render-translate-toast` exit 0
  - [ ] 6 个状态快照全部输出，视觉与 PinWindow toast 一致（深色背景/白字/FadeSlideIn 首帧）
  - [ ] stdout 打印快照路径

  **QA Scenarios**:
  ```
  Scenario: 探针渲染全部状态
    Tool: Bash
    Steps:
      1. 运行 --render-translate-toast → 断言 exit 0
      2. 断言 6 个快照文件存在于输出目录
      3. 用 look_at 抽查快照：状态 2 含"取消"按钮与百分比；状态 5 显示已取消
    Expected Result: 全状态可渲染、可快照、内容正确
    Failure Indicators: exit 非 0；快照缺失；取消按钮在状态 1 出现
    Evidence: .omo/evidence/task-8-probe-*.png

  Scenario: 现有探针回归（负面）
    Tool: Bash
    Steps:
      1. --smoke → exit 0；现有渲染探针分派（--show-ocr 等）行为不变（git diff 确认对应 case 块无修改）
    Expected Result: 新探针未破坏既有诊断
    Evidence: .omo/evidence/task-8-regression.txt
  ```

  **Commit**: YES
  - Message: `feat(diagnostics): add --render-translate-toast probe`
  - Files: `src/ModernScreenShot.App/App.Features.cs`（或探针所在文件）
  - Pre-commit: `--render-translate-toast` + `--smoke` exit 0

---

## Final Verification Wave (MANDATORY — after ALL implementation tasks)

> 4 review agents run in PARALLEL. ALL must APPROVE. Present consolidated results to user and get explicit "okay" before completing.
>
> **Do NOT auto-proceed after verification. Wait for user's explicit approval before marking work complete.**
> **Never mark F1-F4 as checked before getting user's okay.** Rejection or user feedback -> fix -> re-run -> present again -> wait for okay.

- [ ] F1. **Plan Compliance Audit** — `oracle`
  通读计划。对每个 Must Have：读文件/跑命令验证实现存在。对每个 Must NOT Have：全库搜索违禁模式（ProgressBar、Cursors.Wait、Mouse.OverrideCursor、spinner、PinWindow.ShowToast 调用点改动、设置页下载取消），发现即 REJECT 并给 file:line。检查 .omo/evidence/ 证据文件齐全。
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review** — `unspecified-high`
  跑 `dotnet build ModernScreenShot.sln -c Release`（exit 0）+ `tools/check-i18n.ps1`（exit 0）+ `--translate-test`（exit 0）+ `--smoke`（exit 0）。审查全部改动文件：每个 catch(Exception) 是否过滤 OCE；空 catch、console 残留、注释掉代码、未用 using；AI slop（过度注释、过度抽象、data/result/temp 等泛名）。
  Output: `Build [PASS/FAIL] | i18n [PASS/FAIL] | translate-test [PASS/FAIL] | OCE filters [N/N] | Files [N clean/N issues] | VERDICT`

- [ ] F3. **Real Manual QA** — `unspecified-high`
  执行每个任务的每个 QA 场景：跑探针、断言日志、验证取消三态（Cancelled 绝不误报 Failed）。跨任务集成：截图路径完整流程（OCR→下载→引擎→推理）toast 阶段连贯；编辑器取消后再次翻译正常；swap 双击只触发一次。证据存 `.omo/evidence/final-qa/`。
  Output: `Scenarios [N/N pass] | Integration [N/N] | Edge Cases [N tested] | VERDICT`

- [ ] F4. **Scope Fidelity Check** — `deep`
  逐任务读 "What to do" 对比实际 diff（git log/diff）：规格内全部实现（无遗漏）、规格外零实现（无蔓延）。检查 "Must NOT do" 遵守。检测跨任务污染（Task N 动了 Task M 的文件）。标记无法归类的改动。
  Output: `Tasks [N/N compliant] | Contamination [CLEAN/N issues] | Unaccounted [CLEAN/N files] | VERDICT`

---

## Commit Strategy

- **Task 1**: `feat(toast): add shared ToastWindow component for async operation feedback` — Output/ToastWindow.cs
- **Task 2**: `feat(i18n): add translation/OCR toast phase and cancel strings (zh-CN, en-US)` — Localization/Strings.*.xaml
- **Task 3**: `refactor(translation): make EnsureModelAsync cancellable with distinguishable result` — Translation/TranslationFlow.cs
- **Task 4**: `feat(translation): toast feedback + cancellation on capture translate path` — App.Translation.cs
- **Task 5**: `feat(ocr): toast feedback on capture OCR path` — App.Ocr.cs
- **Task 6**: `feat(editor): translate button becomes cancel while running` — Editor/EditorWindow.xaml.cs
- **Task 7**: `fix(translation): disable swap button during retranslation` — Translation/TranslationResultWindow.xaml.cs
- **Task 8**: `feat(diagnostics): add --render-translate-toast probe` — App.Features.cs / diagnostics

---

## Success Criteria

### Verification Commands
```powershell
dotnet build ModernScreenShot.sln -c Release  # Expected: exit 0
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1  # Expected: exit 0
src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe --smoke  # Expected: exit 0
src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe --translate-test  # Expected: exit 0
src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe --render-translate-toast  # Expected: exit 0 + 快照输出
```

### Final Checklist
- [ ] 所有 Must Have 存在
- [ ] 所有 Must NOT Have 缺席
- [ ] 全部验证命令 exit 0
- [ ] F1-F4 全部 APPROVE
