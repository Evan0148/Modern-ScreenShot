# Windows 11 原生标题栏对齐重构（Modern-ScreenShot）

## TL;DR

> **Quick Summary**: 把共享自定义标题栏 `AppTitleBar`（WPF `WindowChrome`）从 36 DIP / 40×28 手绘按钮重构为 Win11 原生规格（32 DIP、原生尺寸按钮、模板化交互状态含按下/非激活/失焦清除），补齐 **Snap Layouts 贴靠浮窗**（HTMAXBUTTON 命中），修复拖动/贴靠手感、hover 残留、关闭钮右侧空隙，迁移全部 6 个窗口，并新增标题栏专项探针（原生实机对照测量 + 几何 + 状态像素 + 真实 HWND 命中测试 + 残留测试）。
>
> **Deliverables**:
> - 新 `Controls/TitleBarMetrics.cs`（单点尺寸常量）与 `Controls/CaptionButton.cs`（模板化状态按钮）
> - 重写 `Controls/AppTitleBar.cs`（32 DIP、合成 CaptionButton、标题规格、去分隔线、非激活 + 残留消除、语言热切刷新）
> - 新 `Interop/TitleBarSnapHook.cs`（HTMAXBUTTON + NC 鼠标消息 + 门控 + Win10 优雅降级）
> - 6 窗口接线适配（设置 / 编辑器 / 历史 / OCR 结果 / OOBE / 快捷键编辑对话框）
> - 新探针 `tools/titlebar_probe.ps1` + 原生对照测量脚本 + 证据包（`.omo/evidence/`）
> - 更新 `tools/phase2_ui_probe.ps1`、`verify_*.tree.txt` fixtures、`tools/verify_theme.ps1` 标题断言、`KNOWN_ISSUES.md`
>
> **Estimated Effort**: Large
> **Parallel Execution**: YES - 3 个实现 Wave + 最终验证 Wave
> **Critical Path**: T1（原生实测）→ T4（metrics/button）→ T6（AppTitleBar 重写）→ T10（snap 集成）→ T13（E2E 矩阵）→ F1–F4；并行关键支线 T2（hook 实证）→ T9（snap hook）→ T10

---

## Context

### Original Request
「优化项目窗口的自定义标题栏，使它的稳定性和 UI 效果更靠近 Windows 11 原生。」用户后续确认：现有实现按钮整体尺寸与原生不符（关闭按钮右侧有一段空白）；拖动/贴靠不顺手；悬停/状态有残留。

### Interview Summary
**Key Discussions / 用户已确认决策**：
- **标题栏高度**：对齐原生 **32 DIP**（当前 36；6 个窗口内容区随之 -4px）
- **UI 对齐重点**：① Snap Layouts 贴靠浮窗 ② 尺寸/字形逐像素对齐 ③ 交互状态补齐（按下/非激活）④ 标题文字规格
- **Mica 云母材质：明确不做**（Out of scope）
- **范围**：6 个使用 `AppTitleBar` 的窗口全部统一处理
- **验证**：沿用项目既有门禁 + 新增标题栏专项探针
- **子代理**：用户确认现已可并行调用
- **稳定性痛点**：拖动/贴靠不顺手；悬停/状态残留；按钮尺寸与原生不符（关闭钮右侧空白）

**Research Findings（三路并行子代理）**：
- **原生规格**：Win11 标准标题栏高 32 DIP（Tall 48）；WinUI 3 TitleBar 资源：图标 16×16 边距 (16,0,16,0)、标题边距 (0,0,8,2)；hover=`SubtleFillColorSecondary`（浅 #09000000 / 深 #0FFFFFFF）、pressed=`SubtleFillColorTertiary`（浅 #06000000 / 深 #0AFFFFFF）；非激活标题=`TextFillColorTertiary`；关闭钮 hover/pressed #C42B1C；按钮前景扁值 浅 #191919/pressed #606060/inactive #9B9B9B、暗 #FFFFFF/#CFCFCF/#717171。**存在两套口径冲突**（legacy UWP 9%/20% vs Fluent2 token 3.7%/5.9%），必须本机像素实测定案。本机为 **Win11 25H2 build 26200**；`GetSystemMetrics` 经典度量（96 DPI：SM_CYCAPTION=23 / SM_CXSIZE=36）**不等于**可见标题栏尺寸。
- **Snap Layouts**：官方指引 WM_NCHITTEST 在最大化按钮区返回 `HTMAXBUTTON`。**WPF `HwndSource` 的 hook 倒序调用**（后添加先执行）→ 应用在 WindowChrome 之后加 hook 可抢先返回并短路 WindowChrome。先例：lepoco/wpfui、PSAppDeployToolkit `FluenceWindow`、Gh61/wpf-custom-window-snap、ControlzEx（`HitTestResult="MAXBUTTON"`）。坑：需 `WS_MAXIMIZEBOX`、需前台窗口才弹浮窗、双击/拖动回归、Win10 无 snap。
- **接线/几何**：6 窗口全部用**默认 captionHeight**（单点改动）；客户端坐标系按钮**已贴右**（树转储实证）→ 右侧空隙来自非客户区/对话框 20px 阴影让位；`HotkeyEditDialog.cs:236` 有 20 DIP 透明阴影让位（确定性成因，属设计取舍待定）；`HotkeyEditDialog` `outer` Grid 未声明 RowDefinitions 却 `Grid.SetRow(rows,1)`（待核）。

### Metis Review
**Identified Gaps（已纳入本计划）**：
- **只有 4/6 窗口可贴靠**（设置/编辑器/历史/OCR 可调整尺寸；OOBE 与快捷键对话框 `NoResize` → 不做 snap，但仍需几何/状态/残留）
- **状态机是 ad-hoc**（`AppTitleBar.cs:380-396` 直接改 `Background`），必须改为**模板触发器 + `Activated/Deactivated`**，这是"残留"的根因修法
- **hook 倒序是承重假设**，必须**先实证**再据此设计 snap hook；失败回退方案已列
- **必须同步更新** `phase2_ui_probe.ps1:118`（断言 `x36`）、`verify_*.tree.txt` fixtures、`verify_theme.ps1` 标题断言
- 不得创建任何"需用户肉眼确认"的验收标准（全部 agent 可执行）
- 不得顺手加图标 / 换字体族 / 做系统菜单 / 修 `verify_theme.ps1` 既有 A2 失败

---

## Work Objectives

### Core Objective
将 `AppTitleBar` 重构为 Windows 11 原生规格的标题栏（尺寸/字形/状态/命中测试），在 4 个可调整尺寸窗口上支持 Snap Layouts，并消除拖动/贴靠、hover 残留、右侧空隙问题，统一 6 个窗口，Windows 10 优雅降级，不触碰业务逻辑。

### Concrete Deliverables
- `src/ModernScreenShot.App/Controls/TitleBarMetrics.cs`（新）
- `src/ModernScreenShot.App/Controls/CaptionButton.cs`（新）
- `src/ModernScreenShot.App/Controls/AppTitleBar.cs`（重写）
- `src/ModernScreenShot.App/Interop/TitleBarSnapHook.cs`（新）
- 6 窗口接线适配（`Settings/`、`Editor/`、`Output/`、`Ocr/`、`Shell/`、`Settings/HotkeyEditDialog.cs`）
- `tools/titlebar_probe.ps1`（新）、`tools/titlebar_native_measure.ps1`（新）、`tools/hook_order_probe.ps1`（新）
- 更新：`tools/phase2_ui_probe.ps1`、`verify_*.tree.txt`、`tools/verify_theme.ps1`、`KNOWN_ISSUES.md`
- 证据包：`.omo/evidence/native-caption-spec.md`、`.omo/evidence/hook-order-proof.md`、`.omo/evidence/titlebar-geometry-audit.md`、`.omo/evidence/titlebar-*.png/txt`

### Definition of Done
- [ ] `dotnet build ModernScreenShot.sln -c Release` → 0 错误 0 警告
- [ ] `ModernScreenShot.App.exe --smoke` → 退出码 0
- [ ] `dotnet run --project tools/Harness -c Release -- core` → ALL PASSED
- [ ] `tools/check-i18n.ps1` → 双语齐平（与改动前计数一致）
- [ ] `tools/titlebar_probe.ps1` → 全断言 PASS（几何/状态/命中/残留）
- [ ] `tools/phase2_ui_probe.ps1` → ALL PASS（`x32` 断言生效）
- [ ] `tools/verify_theme.ps1` → A1/A3 PASS（A2 既有失败保持不处理）

### Must Have
- 标题栏高 **32 DIP**，由**单一常量**驱动（`TitleBarMetrics`），所有窗口与探针引用同一来源
- 按钮采用 **Win11 原生尺寸/字形/位置**；关闭钮 hover/pressed 必须到达**物理窗口**右上角，**普通与最大化两态均无边框/非客户留白**，由本机原生实测校准（不是凭文档猜测）
- 交互状态为 **模板驱动**：rest / hover / pressed / disabled / **非激活**；窗口失焦/模态打开/`WM_NCMOUSELEAVE` 时**无残留高亮**
- 4 个可调整尺寸窗口支持 **Snap Layouts**（悬停最大化按钮出贴靠浮窗）；命中测试契约由真实 HWND 探针验证
- 标题文字规格（字体/字号/粗细/颜色）**对齐原生**（以实测为准）
- Windows 10（build < 22000）**优雅降级**：不启用 snap，最大化/状态仍正确
- 所有既有门禁继续全绿，且探针/fixtures 同步更新

### Must NOT Have (Guardrails)
- **禁止**凭文档/记忆断言原生像素值——每个像素声明必须有**本机测量产物**（沿用 `KNOWN_ISSUES.md:229` 的"DwmGetWindowAttribute 回读实证"教训）
- **禁止**在 `AppTitleBar` 内再次用 `MouseEnter/MouseLeave` 直接改属性（必须模板触发器 + 激活事件）
- **禁止**改动窗口内容布局（除 32 DIP 的自然位移）；不动业务逻辑；不重构内容区 XAML
- **禁止**引入 Mica/backdrop
- **禁止**顺手加：标题图标、字体族全局迁移、系统菜单（右键/Alt+Space）、修 `verify_theme.ps1` 的 A2 既有失败、a11y 语言刷新以外的"整理"
- **禁止**让 `32`/`40`/`28` 散落在多处（必须单点常量）
- **禁止**在非合格窗口（OOBE / 快捷键对话框，`NoResize`）上启用 snap 命中
- **禁止** `git add -A`（工作树脏 + 并行会话）；只 `git add <明确文件>`
- **禁止**创建需"用户肉眼确认"的验收条目（须 agent 可执行）

### 已确认决策（2026-10-01 用户拍板）
| 决策 | 确认值 |
|---|---|
| 应用图标 | **不加**（纯文字标题） |
| 标题字体 | **对齐本机原生实测** |
| NC 命中范围 | **仅最大化钮**（HTMAXBUTTON）；min/close 保留 WPF 客户端按钮 |
| 底部 1px 分隔线 | **去掉**（对齐原生） |
| 快捷键对话框 20px 阴影让位 | **保留**（自绘卡片阴影设计） |
| Windows 10 | **保留优雅降级** |
| **右侧空白范围（关键结论）** | 用户确认**常规窗口 / OOBE / 快捷键对话框、普通状态与最大化状态都能看到** → 非单一成因，必须做**物理窗口边缘**级修复（不能只满足客户端坐标贴边） |

---

## Verification Strategy (MANDATORY)

> **ZERO HUMAN INTERVENTION** — 所有验收均由 agent 执行命令/探针完成，无任何"用户手动确认"条目。

### Test Decision
- **Infrastructure exists**: YES（`tools/Harness`、`--smoke`、`--render-*`、`MSS_DUMP_TREE`、`tools/verify_theme.ps1`、`tools/phase2_ui_probe.ps1`）
- **Automated tests**: 探针式（probe-based, tests-after）；不引入单元测试框架（遵循仓库惯例）
- **Framework**: 自定义 Harness（Core 数值断言）+ PowerShell 探针（UI 几何/像素）+ 真实 HWND 消息探针
- **无 TDD**：本任务为 UI 重构，行为由像素/几何断言锁定，非逻辑单测

### QA Policy
每个任务必须包含 agent 可执行 QA 场景（下方 TODO 模板）。证据写入 `.omo/evidence/`。要点：
- **几何**：`MSS_DUMP_TREE` 逐窗口断言 bar 高==32、内容顶偏移==32；按钮右缘与**可见物理窗口右缘**差 ≤1px（分层对话框以卡片边缘计）
- **状态色**：对渲染 PNG 采样像素断言 hover/pressed/inactive/close-hover 的精确色与对比度
- **命中测试**：真实 HWND 上 `SendMessage(hwnd, WM_NCHITTEST, 0, MAKELPARAM(客户区最大化钮中心))` 返回 `HTMAXBUTTON(9)`；空白 caption 点返回 `HTCAPTION(2)`；`NoResize` 窗口返回非 HTMAXBUTTON
- **残留**：hover → 失焦 / 打开模态 / `WM_NCMOUSELEAVE` → 断背景回透明
- **原生对照**：本机原生窗口（记事本/资源管理器）测量产物与本控件常量一致性断言
- **前端/UI**：Playwright 不适用（桌面 WPF）；使用 repo 探针 + 像素采样
- **CLI**：`interactive_bash`(tmux) 不适用；用 Bash 直跑 exe + 探针脚本
- **API/后端**：不适用
- **库/模块**：Harness 数值断言

---

## Execution Strategy

### 环境约束（来自 .zcode 记忆，执行时必须遵守）
- **工作树脏**（含未提交改动 + 可能有并行会话）：提交必须精确 `git add <文件>`；改 `KNOWN_ISSUES.md` 前**先重读**（并行会话会改它）
- **无图片输入 / visual-judge 不可用**：视觉验证靠像素探针 + 用户真机；视觉改造必须拆成可断言的资源键/像素特征
- **渲染探针 PNG 尺寸随 DPI 不恒定（96/150）**：像素断言必须按 `bitmapWidth / 窗口DIP宽` 换算，禁止硬编码 96 DPI 坐标
- **文字像素仅占 ~3%**：对比度断言用"极端尾部均值"（暗色取最亮 0.5%、亮色取最暗 0.5%）与中位数相减
- **单实例转发**：探针前先杀残留 `ModernScreenShot.App` 实例，否则参数被转发到旧实例
- **PowerShell AMSI 间歇崩溃**：探针脚本失败先重试；大批量拆小步
- **子代理**：现已可并行；但文件级冲突须先规划（同一文件单属主）

### Parallel Execution Waves

```
Wave 1 (Start Immediately — 基础测量与构造，5 任务，全部新文件/只读):
├── T1: 原生标题栏实机对照测量（新脚本 + 证据产物）           [deep]
├── T2: HwndSource hook 顺序 + WM_NCHITTEST 实证（新脚本+证据） [deep]
├── T3: 几何/右侧空隙/边距审计确认（只读 → 证据）              [unspecified-high]
├── T4: TitleBarMetrics.cs + CaptionButton.cs（新文件，冻结 API）[visual-engineering]
└── T5: tools/titlebar_probe.ps1 + 诊断钩子（新脚本 + App.Features）[unspecified-high]

Wave 2 (After Wave 1 — 4 任务，文件级不冲突):
├── T6: 重写 AppTitleBar.cs（32 DIP、合成 CaptionButton、标题规格、去分隔线、非激活+残留、语言刷新）[visual-engineering]
├── T7: OobeWindow.cs 适配（32 高、仅关闭、无 snap、拖动）      [visual-engineering]
├── T8: HotkeyEditDialog.cs 适配（32 高、阴影边距、圆角 clip、拖动/hover）[visual-engineering]
└── T9: Interop/TitleBarSnapHook.cs（HTMAXBUTTON + NC 鼠标 + 门控 + Win10 降级）[deep]

Wave 3 (After Wave 2 — 4 任务):
├── T10: snap 集成进 AppTitleBar.Attach + 三钮/悬停回归        [deep]
├── T11: 探针与 fixtures 更新（phase2_ui_probe / verify_*.tree.txt / verify_theme）[unspecified-high]
├── T12: 文档更新（KNOWN_ISSUES / README / HANDOFF）           [writing]
└── T13: 端到端探针矩阵 + 证据包                               [unspecified-high]

Wave FINAL (After ALL tasks — 4 并行审查，随后用户 okay):
├── F1: Plan compliance audit                                   (oracle)
├── F2: Code quality review                                     (unspecified-high)
├── F3: Real manual QA / probe execution                        (unspecified-high)
└── F4: Scope fidelity check                                    (deep)
→ 汇总结果 → 获取用户显式 okay

Critical Path: T1 → T4 → T6 → T10 → T13 → F1–F4 → user okay
Max Concurrent: 5 (Wave 1)
```

### Dependency Matrix

| Task | Depends On | Blocks | Wave |
|---|---|---|---|
| T1 | — | T4, T6 | 1 |
| T2 | — | T9 | 1 |
| T3 | — | T6, T8, T13 | 1 |
| T4 | — | T6, T7, T8 | 1 |
| T5 | — | T11, T13 | 1 |
| T6 | T1, T4 | T10, T11, T12, T13 | 2 |
| T7 | T4 | T12, T13 | 2 |
| T8 | T3, T4 | T12, T13 | 2 |
| T9 | T2 | T10 | 2 |
| T10 | T6, T9 | T11, T12, T13 | 3 |
| T11 | T5, T6, T10 | T13 | 3 |
| T12 | T6–T11 | — | 3 |
| T13 | T3, T5, T10, T11 | F1–F4 | 3 |
| F1–F4 | T13 | user okay | FINAL |

### Agent Dispatch Summary

| Wave | Tasks | Dispatch |
|---|---|---|
| 1 | 5 | T1 → `deep`；T2 → `deep`；T3 → `unspecified-high`；T4 → `visual-engineering`；T5 → `unspecified-high` |
| 2 | 4 | T6 → `visual-engineering`；T7 → `visual-engineering`；T8 → `visual-engineering`；T9 → `deep` |
| 3 | 4 | T10 → `deep`；T11 → `unspecified-high`；T12 → `writing`；T13 → `unspecified-high` |
| FINAL | 4 | F1 → `oracle`；F2 → `unspecified-high`；F3 → `unspecified-high`；F4 → `deep` |

---

## TODOs

> Implementation + Test = ONE task. 每个任务必须含：推荐 Agent Profile + 并行信息 + 可执行验收/QA 场景。
> 标签格式：任务用裸数字 `1.` `2.`；最终验证用 `F1.` `F2.`。

- [x] 1. 原生标题栏实测对照（本机 Win11）

  **What to do**:
  - 新建 `tools/titlebar_native_measure.ps1`：启动/前台化一个**真原生窗口**（记事本或资源管理器；**不得**用本应用窗口），按当前 DPI 用 BitBlt/PrintWindow 抓标题栏到 PNG
  - 测量并写入 `tools/verify_out/titlebar/native_spec.json`：标题栏高、三按钮各宽/高、字形包围盒、标题文字左内边距
  - 用 `SetCursorPos` 悬停/按下最大化与关闭按钮，抓 rest/hover/pressed 背景像素并解析 hex（含 alpha）
  - 切换前台窗口抓非激活标题/按钮像素
  - 输出 `.omo/evidence/native-caption-spec.md`（数值表 + 与 WinUI 文档 32/46×32 的异同 + 容差）
  - 在 100% 与 150%（可切换时）各跑一次，记录 DPI 缩放

  **Must NOT do**:
  - 不得用文档/记忆值当结论；不得硬编码 96 DPI 坐标；不得用本应用窗口当参照

  **Recommended Agent Profile**:
  - **Category**: `deep` — 真实窗口交互 + 像素解析 + 容差判断
  - **Skills**: 无（repo 自有 PowerShell/像素探针惯例）
  - **Skills Evaluated but Omitted**: `visual-qa`（面向"自建 UI 的视觉 QA"，此处是与原生对照测量）

  **Parallelization**: Can Run In Parallel: YES | Wave 1 | Blocks: T4, T6 | Blocked By: None

  **References**:
  - `tools/verify_theme.ps1` — 像素扫描 + DPI 换算惯例（**WHY**：像素断言必须按 bitmapWidth/窗口DIP宽 换算）
  - `tools/phase2_ui_probe.ps1` — 探针脚本结构与退出码约定
  - `KNOWN_ISSUES.md:229` — "系统视觉承诺必须回读实证"纪律
  - 记忆：文字像素仅 ~3% → 对比度用极端尾部均值

  **Acceptance Criteria**:
  - [ ] `tools/verify_out/titlebar/native_spec.json` 存在且含 `captionHeight/buttonW/buttonH/glyphBox/hoverHex/pressedHex/closeHoverHex/inactiveTitleHex`
  - [ ] 脚本可重跑，两次 `captionHeight` 一致（±1px）
  - [ ] `.omo/evidence/native-caption-spec.md` 记录每个值的来源与容差

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 采到原生标题栏几何与状态色
    Tool: Bash (powershell -File tools/titlebar_native_measure.ps1)
    Steps: 1. 先杀残留 ModernScreenShot 实例 2. 跑脚本 3. 读 native_spec.json 4. 断言 captionHeight∈[28,40]、buttonW∈[40,52]、hoverHex≠pressedHex
    Expected: json 字段齐全、数值在容差内、PNG 证据存在
    Failure Indicators: 抓图全黑/空白（锁屏或未前台化）、字段缺失、hover==rest
    Evidence: .omo/evidence/titlebar-native-rest.png / -hover.png / -pressed.png
  Scenario: 非 100% DPI 不误用固定坐标（边界）
    Tool: Bash (同上，150% 输出)
    Steps: 1. 150% 会话渲染 2. 断言换算后 DIP 值 ≈ 100% 值（±1 DIP）
    Expected: DIP 归一后跨 DPI 一致
    Evidence: .omo/evidence/titlebar-native-150.png
  ```

  **Evidence to Capture**: `.omo/evidence/native-caption-spec.md`、`tools/verify_out/titlebar/native_spec.json`、PNG
  **Commit**: YES — group with Wave 1

- [x] 2. HwndSource hook 顺序 + WM_NCHITTEST 实证

  **What to do**:
  - 新建 `tools/hook_order_probe.ps1`：最小 WPF 诊断进程上 `AddHook` 两个带序号日志的 hook，触发消息并记录调用顺序 → 判定是否"后添加先执行"
  - 对应用真实窗口查询 WM_NCHITTEST 返回值：空白 caption 点、最大化钮中心点、边缘 resize 点（含当前 `IsHitTestVisibleInChrome=true` 下的结果）
  - 输出 `.omo/evidence/hook-order-proof.md`：结论（倒序成立 / 需回退）+ 原始日志 + 命中基线
  - 若倒序不成立：记录回退方案（ControlzEx `HitTestResult="MAXBUTTON"` 或 `CaptionHeight=0` 自管 hit test）并据此调整 T9

  **Must NOT do**: 不得以文档断言代替运行证据；不得改生产行为（仅诊断）

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: 无
  - **Omitted**: `debugging`（非运行时缺陷排查）

  **Parallelization**: YES | Wave 1 | Blocks: T9 | Blocked By: None

  **References**:
  - WPF 源码 `HwndSource.PublicHooksFilterMessage`（倒序调用；**WHY**：这是 snap 方案承重假设）
  - `Controls/AppTitleBar.cs:207-235` — WindowChrome 安装点与 SourceInitialized 时序
  - `Interop/NativeMethods.cs` — P/Invoke 惯例
  - `KNOWN_ISSUES.md:229` — 回读实证纪律

  **Acceptance Criteria**:
  - [ ] `.omo/evidence/hook-order-proof.md` 含两次 hook 的原始调用序日志
  - [ ] 明确"倒序成立/不成立"及对应 T9 策略
  - [ ] 记录三点 WM_NCHITTEST 基线返回值

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 证明后添加的 hook 先执行
    Tool: Bash (powershell -File tools/hook_order_probe.ps1)
    Steps: 1. 跑探针 2. 断言日志首个执行者为"后添加的 hook#2"
    Expected: #2 先于 #1；证据文件产出
    Failure Indicators: 日志缺失、进程未起（单实例转发/锁屏）
    Evidence: .omo/evidence/hook-order-proof.md
  Scenario: 命中测试基线可复现（边界）
    Tool: Bash (SendMessage WM_NCHITTEST 探针)
    Steps: 1. 对真实窗口三点发 WM_NCHITTEST 2. 断言返回值稳定可复现
    Expected: 三点返回值一致复现
    Evidence: .omo/evidence/titlebar-nchittest-baseline.txt
  ```

  **Evidence to Capture**: `.omo/evidence/hook-order-proof.md`、`.omo/evidence/titlebar-nchittest-baseline.txt`
  **Commit**: YES — group with Wave 1

- [x] 3. 几何 / 右侧空隙 / 边距审计确认

  **What to do**:
  - 只读审计 6 窗口：根容器 Margin/Padding、bar 摆放、窗口样式（WindowStyle/ResizeMode/AllowsTransparency/SizeToContent）、`Grid.SetRow` 异常
  - **物理几何实测（关键）**：对每个窗口在**普通 + 最大化**两态测量 `GetWindowRect` / `GetClientRect(MapWindowPoints)` / `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`，算出「客户区右缘 vs **可见窗口右缘**」的实际像素差——这是用户"普通状态也能看到空隙"的定案依据
  - 全库枚举硬编码 `36/40/28` 与 `CaptionHeight` 引用；枚举受影响的探针/fixtures（`tools/*.ps1`、`verify_*.tree.txt`）
  - 分类"关闭钮右侧空隙"成因（resize border 6 / DWM 扩展边框 / `HotkeyEditDialog` 20px 阴影让位），**逐窗口、逐状态（普通/最大化）**给处置建议（修 / 保留为设计取舍）
  - 核 `HotkeyEditDialog` `outer` 无 RowDefinitions 却 `SetRow(rows,1)` 是否导致重叠
  - 输出 `.omo/evidence/titlebar-geometry-audit.md`

  **Must NOT do**: 不得改任何源码（只读）；不得把"树转储存量坐标"当屏上物理坐标结论

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: 无
  - **Omitted**: `explore`（已完成一轮接线审计；本任务为确认+分类+变更清单）

  **Parallelization**: YES | Wave 1 | Blocks: T6, T8, T13 | Blocked By: None

  **References**:
  - 上轮 explore 审计结论（6 窗口接线点/行号）
  - `Controls/AppTitleBar.cs` — 尺寸常量、resize border、按钮布局
  - `Settings/HotkeyEditDialog.cs:143-236` — 20px 让位、rowless grid、radius 7 clip
  - `Interop/NativeMethods.cs:288-295` — `DWMWA_EXTENDED_FRAME_BOUNDS`
  - `tools/phase2_ui_probe.ps1:117-118`、`verify_*.tree.txt`

  **Acceptance Criteria**:
  - [ ] 文档含 6 窗口逐项表（文件:行、Margin/Padding、窗口样式、Attach 参）
  - [ ] 硬编码 36/40/28 与 `CaptionHeight` 引用清单完整（file:line）
  - [ ] 右侧空隙逐窗口判定（修/保留）+ 依据
  - [ ] fixtures/probe 更新清单 + 重生成步骤

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 审计清单完整
    Tool: Read (.omo/evidence/titlebar-geometry-audit.md) + Grep 交叉核对
    Steps: 1. grep '\b36\b|CaptionHeight' 于 src 与 tools 2. 断言文档覆盖每个命中
    Expected: 清单与 grep 结果一一对应，无遗漏
    Failure Indicators: 缺探针/fixture 条目、缺某窗口
    Evidence: .omo/evidence/titlebar-geometry-audit.md
  Scenario: 右侧空隙成因分类有据（边界）
    Tool: Read
    Steps: 1. 断言文档对每个窗口给出成因与处置
    Expected: 每个窗口都有明确成因标注
    Evidence: 同上
  ```

  **Evidence to Capture**: `.omo/evidence/titlebar-geometry-audit.md`
  **Commit**: YES — group with Wave 1

- [x] 4. `TitleBarMetrics.cs` + `CaptionButton.cs`（新文件，冻结 API）

  **What to do**:
  - 新建 `Controls/TitleBarMetrics.cs`：单点常量 `CaptionHeight=32`、`ButtonWidth/ButtonHeight`（原生值，默认 46/32，待 T1 校准）、`GlyphSize`、`IconBox`、`TitleMargin`、`TitleFontSize`、资源键名常量
  - 新建 `Controls/CaptionButton.cs`：`ControlTemplate` 触发器驱动的 caption 按钮——rest=透明、hover=`SubtleFillColorSecondaryBrush`、pressed=`SubtleFillColorTertiaryBrush`、disabled、**非激活**；close 变体 hover/pressed 固定 #C42B1C + 白字形；`AutomationProperties.Name`；`IsHitTestVisibleInChrome` 可选开关
  - 暴露 `Configure(string glyph, bool isClose)`（或构造参数）；**不引用 AppTitleBar**
  - 字形字体保留 `Segoe Fluent Icons, Segoe MDL2 Assets` 回退链

  **Must NOT do**:
  - 不得用 `MouseEnter/Leave` 直接改 `Background`；不得硬编码主题色（close 红除外）；不得改 `AppTitleBar.cs`

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering` — 控件模板/状态/主题资源
  - **Skills**: 无
  - **Omitted**: `animate`（无动画需求，状态切换即时）

  **Parallelization**: YES | Wave 1 | Blocks: T6, T7, T8 | Blocked By: None

  **References**:
  - `Controls/AppTitleBar.cs:349-411` — 现有 MakeCaptionButton/MakeFlatCaptionTemplate（将被替换）
  - WPF-UI 资源键：`SubtleFillColorSecondaryBrush`/`SubtleFillColorTertiaryBrush`/`TextFillColorPrimaryBrush`/`TextFillColorTertiaryBrush`
  - `KNOWN_ISSUES.md:229` — 关闭钮固定深红 #C42B1C 的对比度教训
  - 记忆：WPF-UI 控件子类必须 `SetResourceReference(StyleProperty, typeof(Button))`（隐式样式按运行时类型解析）

  **Acceptance Criteria**:
  - [ ] `dotnet build -c Release` 0/0（新文件编译通过）
  - [ ] `TitleBarMetrics.CaptionHeight == 32` 且是**全仓唯一**标题栏高度常量来源
  - [ ] CaptionButton 的 normal/hover/pressed 由模板触发器实现（grep 确认无 `MouseEnter`/`MouseLeave` 改 `Background`）

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 状态色由主题资源驱动（明/暗两主题）
    Tool: Bash (探针渲染 CaptionButton + 像素采样)
    Steps: 1. 渲染 hover 态按钮 2. 采样背景 hex 3. 断言 == 当前主题 SubtleFillColorSecondary 解析值
    Expected: 明/暗均解析到对应 token，非硬编码
    Failure Indicators: 背景恒透明/恒白、取值等于硬编码
    Evidence: .omo/evidence/titlebar-captionbutton-states.png
  Scenario: 关闭钮 hover 可读（边界）
    Tool: 像素采样
    Steps: 1. hover 关闭钮 2. 断言背景 #C42B1C、字形白、对比度 ≥3:1
    Expected: 深红 + 白字形，对比度达标
    Evidence: .omo/evidence/titlebar-captionbutton-close-hover.png
  ```

  **Evidence to Capture**: `.omo/evidence/titlebar-captionbutton-states.png`、`.omo/evidence/titlebar-captionbutton-close-hover.png`
  **Commit**: YES — group with Wave 1

- [x] 5. `tools/titlebar_probe.ps1` + 诊断钩子

  **What to do**:
  - 新建 `tools/titlebar_probe.ps1`，含子场景（每项独立退出码）：
    (a) **几何**：`MSS_DUMP_TREE` 逐窗口断言 bar 高==32、内容顶偏移==32；并断言按钮右缘与**可见物理窗口右缘**差 ≤1px（非仅客户端坐标；分层对话框以卡片边缘计）
    (b) **状态像素**：采样 hover/pressed/inactive/close-hover 精确色与对比度（极端尾部均值法）
    (c) **命中测试**：真实 HWND 上 `SendMessage(WM_NCHITTEST)` 于最大化钮中心返回 `HTMAXBUTTON(9)`、空白 caption 点返回 `HTCAPTION(2)`、`NoResize` 窗口返回非 HTMAXBUTTON
    (d) **残留**：hover → 失焦 / 打开模态 / `WM_NCMOUSELEAVE` → 断言背景回透明
  - 在 `App.Features.cs` 增加最小诊断入口（如 `--titlebar-probe`）：前台化窗口、输出几何/hit 值（复用既有 `--render-*`/MSS_DUMP_TREE 设施）
  - 探针前先杀残留实例

  **Must NOT do**: 不得改生产行为（仅诊断）；不得硬编码 96 DPI 坐标；不得用分位数抓文字像素

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: 无
  - **Omitted**: `playwright`（桌面 WPF，不适用浏览器自动化）

  **Parallelization**: YES | Wave 1 | Blocks: T11, T13 | Blocked By: None

  **References**:
  - `tools/phase2_ui_probe.ps1` — 探针结构、退出码、MSS_DUMP_TREE 用法
  - `App.Features.cs` — 诊断开关接线惯例（`--render-*` 在 single-instance 握手之前）
  - 记忆：PNG 尺寸随 DPI 不恒定按宽换算；文字用极端尾部均值；AMSI 间歇崩溃需重试；单实例转发先杀实例

  **Acceptance Criteria**:
  - [ ] `powershell -File tools/titlebar_probe.ps1` 可跑，断言失败时退出码非 0
  - [ ] 四个子场景各有证据产物落到 `.omo/evidence/`
  - [ ] 脚本含 DPI 归一换算（不硬编码 96 DPI）

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 探针能抓到已知缺陷（自检）
    Tool: Bash (powershell -File tools/titlebar_probe.ps1)
    Steps: 1. 先对当前（未重构）构建跑一次 2. 断言几何场景对 bar 高!=32 报 FAIL
    Expected: 探针在旧构建上正确失败（证明断言有效，非恒 PASS）
    Failure Indicators: 旧构建也全 PASS（断言无效）
    Evidence: .omo/evidence/titlebar-probe-selfcheck.txt
  Scenario: hit-test 与残留场景可执行（边界）
    Tool: Bash
    Steps: 1. 真实窗口发 WM_NCHITTEST 2. hover 后失焦 3. 断言返回值与背景透明
    Expected: 命中值与残留断言均产出结果
    Evidence: .omo/evidence/titlebar-hitresidue.txt
  ```

  **Evidence to Capture**: `.omo/evidence/titlebar-probe-selfcheck.txt`、`.omo/evidence/titlebar-hitresidue.txt`
  **Commit**: YES — group with Wave 1

- [x] 6. 重写 `Controls/AppTitleBar.cs`

  **What to do**:
  - 高度改用 `TitleBarMetrics.CaptionHeight`（32），按钮改用 `CaptionButton`；标题文字规格按 T1 实测校准（字体/字号/粗细/颜色/边距）
  - 去掉底部 1px 分隔线（默认决策）；标题非激活态用 `TextFillColorTertiary`
  - **残留消除**：状态交给 CaptionButton 模板触发器；窗口 `Activated/Deactivated` 时清除/切换状态；不再用 `MouseEnter/Leave` 改属性
  - **语言热切刷新**：标题与三钮 a11y 名在语言切换时刷新（DynamicResource 或监听语言变更），修复 `KNOWN_ISSUES.md:245` 的既有低价值限制
  - 保留 `Attach(Window, captionHeight=…, resizeBorderThickness=…)` 签名（冻结）、`Text/ShowMin/ShowMax/ShowClose` 属性、`StateChanged` 字形切换、`WM_GETMINMAXINFO` 钩子、DWM 圆角
  - **不**在此任务接入 snap（T10 负责）

  **Must NOT do**: 不得改 `Attach` 签名/公开属性；不得用 `MouseEnter/Leave`；不得改其他窗口文件；不得接 snap

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering` — 控件重写 + 状态 + 主题
  - **Skills**: 无
  - **Omitted**: `animate`（无动画）

  **Parallelization**: YES | Wave 2 | Blocks: T10, T11, T12, T13 | Blocked By: T1, T4

  **References**:
  - `Controls/AppTitleBar.cs` 全文（现有实现与各注释约定）
  - `Controls/CaptionButton.cs`、`Controls/TitleBarMetrics.cs`（T4）
  - `.omo/evidence/native-caption-spec.md`（T1，用于校准常量）
  - `KNOWN_ISSUES.md:223-245`（历史轮次与教训）
  - Metis 指令：ad-hoc 状态是残留根因，必须模板触发器 + 激活事件

  **Acceptance Criteria**:
  - [ ] `dotnet build -c Release` 0/0；`--smoke` 0
  - [ ] `MSS_DUMP_TREE` 断言 6 窗口 bar 高==32、内容顶偏移==32
  - [ ] **非分层 5 窗口**（设置/编辑器/历史/OCR/OOBE）在**普通 + 最大化**两态，按钮右缘与**可见（物理）窗口右缘**差 ≤1px；**分层对话框**以卡片右缘计（保留 20px 阴影让位，见 T8）
  - [ ] grep 确认 `AppTitleBar.cs` 内无 `MouseEnter`/`MouseLeave` 改 `Background`
  - [ ] 常量 `32` 只来自 `TitleBarMetrics`（grep 无其它硬编码标题栏高度）

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 高度与几何迁移（明/暗）
    Tool: Bash (--render-settings|editor|history|ocr + MSS_DUMP_TREE)
    Steps: 1. 渲染 4 窗口 2. 断言 AppTitleBar 高 32、内容 y=32、按钮右缘==窗口右缘
    Expected: 全窗口几何达标
    Failure Indicators: 任一窗口 bar 高!=32、内容未整体上移
    Evidence: .omo/evidence/titlebar-geometry-<window>.tree.txt
  Scenario: 残留消除（边界）
    Tool: Bash (titlebar_probe 残留子场景)
    Steps: 1. hover 按钮 2. 失焦/开模态/WM_NCMOUSELEAVE 3. 断言背景回透明
    Expected: 三种触发下均无残留高亮
    Failure Indicators: 高亮卡住
    Evidence: .omo/evidence/titlebar-residue.txt
  ```

  **Evidence to Capture**: 上列 tree.txt 与 residue 证据
  **Commit**: NO — group with Wave 2

- [x] 7. `Shell/OobeWindow.cs` 适配

  **What to do**:
  - 标题栏随新控件变为 32 高；仅关闭钮；`NoResize`（不接 snap）；拖动/关闭行为保持
  - 校验入场动画与终帧布局不受高度变化破坏（`--probe-oobe` 通过、`DiagnosticLayoutValid` 仍成立）
  - 底部/内容偏移自然跟随，无需硬编码

  **Must NOT do**: 不得改动画逻辑/时序；不得让 OOBE 变成可贴靠；不得改 `Attach` 调用（仍 `resizeBorderThickness:0`）

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
  - **Skills**: 无
  - **Omitted**: `animate`（仅高度适配，不动动画）

  **Parallelization**: YES | Wave 2 | Blocks: T12, T13 | Blocked By: T4

  **References**:
  - `Shell/OobeWindow.cs:107-141`（构建与 Attach）
  - `--probe-oobe` / `DiagnosticLayoutValid`（既有门禁）
  - 记忆：`BeginTime=null` 陷阱、`IsVisibleChanged` 触发、挂钟兜底——不得回归

  **Acceptance Criteria**:
  - [ ] `--probe-oobe` PASS（动画链与终帧几何不回归）
  - [ ] `--render-oobe` 终帧树转储 bar 高==32

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: OOBE 高度适配不破坏动画门禁
    Tool: Bash (exe --probe-oobe)
    Steps: 1. 跑 probe-oobe 2. 断言 exit 0 且日志无 stalled
    Expected: PASS
    Failure Indicators: 有 stalled / 词面重叠 / 非 0 退出
    Evidence: .omo/evidence/oobe-probe.txt
  Scenario: 终帧几何（语言×2）
    Tool: Bash (--render-oobe EN/ZH)
    Steps: 1. 渲染中英终帧 2. 断言 bar 32 且词面居中无重叠
    Expected: 两语言均达标
    Evidence: .omo/evidence/oobe-render-<lang>.tree.txt
  ```

  **Evidence to Capture**: `.omo/evidence/oobe-probe.txt`、渲染树转储
  **Commit**: NO — group with Wave 2

- [x] 8. `Settings/HotkeyEditDialog.cs` 适配

  **What to do**:
  - 标题栏随新控件变为 32 高；分层窗（`AllowsTransparency`）/`NoResize` 行为保持；拖动/hover/关闭正确
  - 按 T3 结论处置 20px 透明阴影让位（默认保留）；确认圆角 clip（radius 7）仍完整包含满高关闭钮
  - 若 T3 确认 `outer` 无 RowDefinitions 却 `SetRow(rows,1)` 导致重叠 → 修正 RowDefinitions
  - 复核 `SizeToContent=Height` 在新高度下无布局增长循环

  **Must NOT do**: 不得去掉卡片阴影/圆角设计；不得改用例为主窗；不得改 `Attach(resizeBorderThickness:0)`

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
  - **Skills**: 无
  - **Omitted**: `apple-design`（无手势/材质需求）

  **Parallelization**: YES | Wave 2 | Blocks: T12, T13 | Blocked By: T3, T4

  **References**:
  - `Settings/HotkeyEditDialog.cs:143-236`（窗口样式、20px 让位、radius 7 clip、rowless grid）
  - `.omo/evidence/titlebar-geometry-audit.md`（T3 结论）
  - `KNOWN_ISSUES.md:225`（STA harness 两轮布局稳定性验证惯例）
  - 记忆：`Border` CornerRadius 不裁子级要显式 Clip

  **Acceptance Criteria**:
  - [ ] `--smoke` 0（含对话框实例化）
  - [ ] STA 两轮布局稳定（520×262±，无增长循环）
  - [ ] 树转储 bar 高==32；关闭钮完整在圆角裁剪内

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 分层对话框高度适配且布局稳定
    Tool: Bash (smoke + STA harness 两轮布局)
    Steps: 1. 实例化对话框 2. 两轮布局 3. 断言尺寸稳定、bar 32
    Expected: 无增长循环、几何达标
    Failure Indicators: 尺寸每轮增长、关闭钮被裁
    Evidence: .omo/evidence/hotkey-dialog-layout.txt
  Scenario: 右侧空隙处置符合决策（边界）
    Tool: Read 树转储
    Steps: 1. 断言关闭钮右缘与窗口右缘的距离等于既定处置（保留=~21 DIP；若改=≤1 DIP）
    Expected: 与决策一致
    Evidence: .omo/evidence/hotkey-dialog-tree.txt
  ```

  **Evidence to Capture**: `.omo/evidence/hotkey-dialog-layout.txt`、`.omo/evidence/hotkey-dialog-tree.txt`
  **Commit**: NO — group with Wave 2

- [x] 9. `Interop/TitleBarSnapHook.cs`（Snap Layouts 命中）

  **What to do**:
  - 新建**自包含** hook 类：在 WindowChrome 之后安装 HwndSource hook（依据 T2 结论），实现：
    - `WM_NCHITTEST`：当点落在最大化钮矩形内 **且** 窗口为前台 **且** 具备 `WS_MAXIMIZEBOX` **且** 最大化钮可见 → 返回 `HTMAXBUTTON(9)` 并 `handled=true` 短路 WindowChrome
    - `WM_NCLBUTTONDOWN(HTMAXBUTTON)`：执行最大化/还原切换并 `handled=true`（避免默认行为）
    - `WM_NCLBUTTONUP` / `WM_NCMOUSEMOVE`：维持按钮 hover/pressed 视觉；`WM_NCMOUSELEAVE`：清除
  - 暴露 `IsOverMaxButton` 供上层驱动 `CaptionButton` 状态；**不负责**按钮外观
  - Windows 10（build < 22000）：不安装（或不产生 snap 语义），保证最大化不受影响
  - 若 T2 证明倒序不成立：改用记录的回退方案（ControlzEx 或自管 hit test）

  **Must NOT do**: 不得实现按钮视觉；不得影响 min/close；不得在 `NoResize/CanMinimize` 窗口安装；不得凭文档代替 T2 结论

  **Recommended Agent Profile**:
  - **Category**: `deep` — Win32 消息 + 命中测试 + 时序
  - **Skills**: 无
  - **Omitted**: `playwright`（不适用）

  **Parallelization**: YES | Wave 2 | Blocks: T10 | Blocked By: T2

  **References**:
  - `.omo/evidence/hook-order-proof.md`（T2 结论；**WHY**：决定 hook 安装方式）
  - 官方指引：learn.microsoft.com/windows/apps/desktop/modernize/ui/apply-snap-layout-menu
  - 先例：lepoco/wpfui TitleBar/TitleBarButton、PSAppDeployToolkit `FluenceWindow`、Gh61/wpf-custom-window-snap、ControlzEx `NonClientControlProperties.HitTestResult`
  - `Controls/AppTitleBar.cs:229`（现有 `NoResize/CanMinimize` 跳过 `MaximizeHook` 的判据，需对齐）
  - `Interop/NativeMethods.cs`

  **Acceptance Criteria**:
  - [ ] `dotnet build -c Release` 0/0
  - [ ] 真实 HWND 上 `WM_NCHITTEST`(最大化钮中心) == `HTMAXBUTTON`；空白 caption 点 == `HTCAPTION`；`NoResize` 窗口 != `HTMAXBUTTON`
  - [ ] 窗口非前台时不返回 `HTMAXBUTTON`
  - [ ] Win10 路径（`build<22000`）不启用 snap 且最大化正常

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 最大化钮区域返回 HTMAXBUTTON
    Tool: Bash (SendMessage WM_NCHITTEST 探针，真实前台窗口)
    Steps: 1. 取最大化钮客户区中心换算为屏幕坐标 2. SendMessage(WM_NCHITTEST, MAKELPARAM) 3. 断言返回 9
    Expected: 返回 HTMAXBUTTON(9)
    Failure Indicators: 返回 HTCLIENT(1)/HTCAPTION(2)（说明 hook 未抢先或门控过严）
    Evidence: .omo/evidence/titlebar-hitmaxbutton.txt
  Scenario: 门控与边界（NoResize / 非前台）
    Tool: Bash
    Steps: 1. 对 OOBE/对话框窗口同点发 WM_NCHITTEST 2. 断言 != 9；3. 窗口失焦后对主窗同点发 4. 断言 != 9
    Expected: 均不返回 HTMAXBUTTON
    Evidence: .omo/evidence/titlebar-hitgating.txt
  ```

  **Evidence to Capture**: `.omo/evidence/titlebar-hitmaxbutton.txt`、`.omo/evidence/titlebar-hitgating.txt`
  **Commit**: NO — group with Wave 2

- [x] 10. Snap 集成进 `AppTitleBar.Attach` + 回归

  **What to do**:
  - 在 `AppTitleBar.Attach` 于 WindowChrome 安装后接入 `TitleBarSnapHook`，**仅可调整尺寸窗口**（对齐 `:229` 的 `NoResize/CanMinimize` 跳过逻辑）
  - 让最大化钮状态由 NC 消息驱动（`IsOverMaxButton`），确保 hover/pressed 正确；确认 `_maxButton.Click` 旧路径不再重复触发
  - 回归：空白 caption 拖动、双击最大化、Win+方向贴靠、min/close 点击、`StateChanged` 字形切换全部正常

  **Must NOT do**: 不得改 `Attach` 公开签名；不得在 `NoResize` 窗口接 snap；不得破坏 min/close 的 WPF 客户端交互

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: 无
  - **Omitted**: `git-master`（不涉及 git 操作）

  **Parallelization**: YES | Wave 3 | Blocks: T11, T12, T13 | Blocked By: T6, T9

  **References**:
  - `Controls/AppTitleBar.cs:199-255`（Attach 与 MaximizeHook 判据）
  - `Interop/TitleBarSnapHook.cs`（T9）
  - `Controls/CaptionButton.cs`（状态驱动）
  - WPF-UI/PSADT/Gh61 先例的消息处理顺序

  **Acceptance Criteria**:
  - [ ] `--smoke` 0；`titlebar_probe` 命中测试子场景 PASS
  - [ ] 最大化/还原经 NC 点击可切换；`StateChanged` 字形正确
  - [ ] 空白 caption 双击最大化、拖动、Win+方向贴靠均正常
  - [ ] `NoResize` 窗口仍不返回 `HTMAXBUTTON`

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 经 NC 命中切换最大化并同步字形
    Tool: Bash (模拟 NC 点击 + 读取 StateChanged 结果)
    Steps: 1. 在最大化钮中心发 WM_NCLBUTTONDOWN/UP 2. 断言 WindowState 变化 3. 断言字形从 ChromeMaximize 切到 ChromeRestore
    Expected: 状态与字形一致切换
    Failure Indicators: 点击无反应、字形不变、双击最大化失效
    Evidence: .omo/evidence/titlebar-maxclick.txt
  Scenario: 拖动/贴靠不回归（边界）
    Tool: Bash (WM_NCHITTEST 空白点 + 拖动模拟)
    Steps: 1. 空白 caption 点发 WM_NCHITTEST 断言 HTCAPTION 2. 模拟拖动/双击
    Expected: 拖动与双击最大化正常
    Evidence: .omo/evidence/titlebar-drag.txt
  ```

  **Evidence to Capture**: 上列证据
  **Commit**: NO — group with Wave 3

- [x] 11. 探针与 fixtures 更新

  **What to do**:
  - `tools/phase2_ui_probe.ps1`：把标题栏断言 `x36` 改为 `x32`（并修正注释/其它几何假设）
  - 按 T3 记录的流程**重生成** `verify_settings_ocr.png.tree.txt` / `verify_editor_ocr.png.tree.txt` / `verify_ocr.png.tree.txt` 等 fixtures
  - `tools/verify_theme.ps1`：更新标题栏相关断言（A1/A3）；**不动既有 A2 失败**
  - 全库搜残留 `x36` 并清零

  **Must NOT do**: 不得修改 `verify_theme.ps1` 的 A2 既有失败；不得手工伪造 fixtures（必须由渲染流程生成）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: 无
  - **Omitted**: `writing`（是探针代码不是文档）

  **Parallelization**: YES | Wave 3 | Blocks: T13 | Blocked By: T5, T6, T10

  **References**:
  - `.omo/evidence/titlebar-geometry-audit.md`（T3 的 fixtures 清单与重生成步骤）
  - `tools/phase2_ui_probe.ps1:117-118`
  - `tools/verify_theme.ps1`
  - 记忆：渲染 PNG 尺寸随 DPI 不恒定；AMSI 重试

  **Acceptance Criteria**:
  - [ ] `tools/phase2_ui_probe.ps1` ALL PASS（含新的 `x32` 断言）
  - [ ] 所有 `verify_*.tree.txt` 不再含 `x36`（grep 为 0）
  - [ ] `tools/verify_theme.ps1` A1/A3 PASS（A2 维持既有失败，不新增回归）

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 探针断言随新几何更新
    Tool: Bash (phase2_ui_probe.ps1)
    Steps: 1. 跑探针 2. 断言 ALL PASS 3. grep fixtures 无 'x36'
    Expected: 全 PASS 且无陈旧断言
    Failure Indicators: 仍断言 x36 而失败、fixtures 未重生成
    Evidence: .omo/evidence/phase2-after.txt
  Scenario: 主题探针不回归（边界）
    Tool: Bash (verify_theme.ps1)
    Steps: 1. 跑 verify_theme 2. 断言 A1/A3 PASS、A2 状态与改动前一致
    Expected: 无新增回归
    Evidence: .omo/evidence/verify-theme-after.txt
  ```

  **Evidence to Capture**: 上列证据
  **Commit**: NO — group with Wave 3

- [x] 12. 文档更新

  **What to do**:
  - `KNOWN_ISSUES.md`：新增本次改造小节（尺寸/状态/snap/命中门控/降级/探针用法/已知限制/待真机确认项）——**编辑前先重读**（并行会话会改）
  - `README.md`：若提及标题栏则同步（否则不动）
  - `HANDOFF.md`：更新状态

  **Must NOT do**: 不得改代码；不得 `git add -A`；不得顺手重写历史小节

  **Recommended Agent Profile**:
  - **Category**: `writing`
  - **Skills**: 无
  - **Omitted**: `deep`（文档整理非深度推理）

  **Parallelization**: YES | Wave 3 | Blocks: None | Blocked By: T6–T11

  **References**:
  - `KNOWN_ISSUES.md:223-245`（标题栏历史小节格式与教训）
  - `.omo/evidence/`（本次全部证据）
  - 记忆：并行会话会改 `KNOWN_ISSUES.md`，Edit 前必重读

  **Acceptance Criteria**:
  - [ ] `KNOWN_ISSUES.md` 新增小节条目齐全（变更/门控/降级/探针/限制）
  - [ ] 文档中无陈旧的 `36` 标题栏描述（除非标注为历史）
  - [ ] 只精确改动本任务文件

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 文档与实现一致
    Tool: Read + Grep
    Steps: 1. grep '标题栏' 于 KNOWN_ISSUES 2. 断言新小节含 32/状态/snap/门控/降级/探针
    Expected: 条目齐全、无陈旧 36
    Evidence: .omo/evidence/docs-check.txt
  Scenario: 未污染并行会话改动（边界）
    Tool: Bash (git diff --stat)
    Steps: 1. 断言本任务仅改预期文件
    Expected: 文件集最小
    Evidence: .omo/evidence/docs-gitdiff.txt
  ```

  **Evidence to Capture**: `.omo/evidence/docs-check.txt`、`.omo/evidence/docs-gitdiff.txt`
  **Commit**: NO — group with Wave 3

- [x] 13. 端到端探针矩阵 + 证据包

  **What to do**:
  - 跑全矩阵：6 窗口 × 明/暗 × 正常/最大化 × 100%/150%；执行 `titlebar_probe` 全部子场景
  - 做**原生对照 diff**（T1 的 native_spec.json vs 本控件常量/实测像素），输出偏差表
  - 汇总 `.omo/evidence/titlebar-e2e-report.md`：矩阵结果、命中测试、残留、几何、原生对照、已知偏差与理由
  - 全门禁复跑（build 0/0、smoke 0、harness ALL、i18n 齐平、phase2 ALL、verify_theme A1/A3）

  **Must NOT do**: 不得用离屏渲染冒充真实 HWND 验证 snap 命中；不得跳过 `NoResize`/分层窗口场景

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high` — 大规模探针矩阵执行与汇总
  - **Skills**: 无
  - **Omitted**: `visual-qa`（若可用可作为补充，但一致性以 repo 探针为准）

  **Parallelization**: YES | Wave 3 | Blocks: F1–F4 | Blocked By: T3, T5, T10, T11

  **References**:
  - `.omo/evidence/`（全部前置产物）
  - `tools/titlebar_probe.ps1`（T5）、`tools/titlebar_native_measure.ps1`（T1）
  - `tools/render_ui.ps1`（既有渲染矩阵惯例）
  - 记忆：单实例先杀；AMSI 重试；PNG DPI 换算

  **Acceptance Criteria**:
  - [ ] 矩阵全场景产出证据；`titlebar_probe` 全 PASS
  - [ ] `.omo/evidence/titlebar-e2e-report.md` 含矩阵表 + 原生对照偏差表
  - [ ] 全门禁复跑绿色（A2 既有失败除外）

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: 全矩阵通过
    Tool: Bash (titlebar_probe.ps1 + render_ui 矩阵)
    Steps: 1. 跑全矩阵 2. 断言每个组合 PASS
    Expected: 全绿
    Failure Indicators: 某 DPI/主题/最大化组合失败
    Evidence: .omo/evidence/titlebar-e2e-report.md
  Scenario: 原生对照偏差在容差内（边界）
    Tool: Bash + Read
    Steps: 1. 比对 native_spec.json 与本控件实测 2. 断言偏差 ≤ 容差（尺寸 ±1px、色差 ΔE 阈值）
    Expected: 偏差表全部在容差内或已注明理由
    Evidence: .omo/evidence/titlebar-native-diff.md
  ```

  **Evidence to Capture**: `.omo/evidence/titlebar-e2e-report.md`、`.omo/evidence/titlebar-native-diff.md`
  **Commit**: YES — group with Wave 3（本计划主提交）

---

## Final Verification Wave (MANDATORY — after ALL implementation tasks)

> 4 个审查代理**并行**运行，全部 APPROVE 后把汇总结果交用户，取得显式 okay 才算完成。
> **不得自动结束**：未获用户 okay 前不得勾选 F1–F4；被打回则修复 → 重跑 → 再次呈报。

- [x] F1. **Plan Compliance Audit** — `oracle`
  通读本计划。逐条核对 "Must Have"：读文件/跑命令/发消息验证实现存在。逐条核对 "Must NOT Have"：全库搜索禁止模式（`MouseEnter`/`MouseLeave` 直接改属性的残留、散落 `36`、Mica、git add -A 等），发现即带 file:line 驳回。核对 `.omo/evidence/` 证据文件存在。对比交付物清单。
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [x] F2. **Code Quality Review** — `unspecified-high`
  跑 `dotnet build -c Release`（0/0）+ `--smoke`（0）+ `harness core`（ALL PASSED）+ `check-i18n`。审查所有改动文件：`as any`/`@ts-ignore` 等价物、空 catch、生产 console、注释掉的代码、未用 import、AI slop（过度注释/过度抽象/泛化命名 data/result/item/temp）。检查 `TitleBarMetrics` 单点常量是否真的单点。
  Output: `Build [PASS/FAIL] | Smoke [PASS/FAIL] | Harness [N/N] | i18n [N/N] | Files [N clean/N issues] | VERDICT`

- [x] F3. **Real Manual QA（探针执行）** — `unspecified-high`（可加载 `visual-qa` skill 辅助像素核对）
  从干净状态起：杀残留实例 → 跑 `tools/titlebar_probe.ps1` 全场景（6 窗口 × 明暗 × 正常/最大化 × 100%/150%）。逐条执行每个任务的 QA 场景：几何、状态像素、hit-test、残留、原生对照 diff。跨任务集成（拖动/双击/贴靠/最大化联动）。边界：`NoResize` 窗口、分层对话框、混合 DPI 副屏。
  Output: `Scenarios [N/N pass] | Integration [N/N] | Edge [N tested] | VERDICT`

- [x] F4. **Scope Fidelity Check** — `deep`
  逐任务：读 "What to do" 与实际 diff（git diff）。验证 1:1（规格内全做了、规格外没做）。核对 "Must NOT do"。检测跨任务污染（T6 是否改了 T7/T8 的文件等）。标记未入账改动。
  Output: `Tasks [N/N compliant] | Contamination [CLEAN/N issues] | Unaccounted [CLEAN/N files] | VERDICT`

---

## Commit Strategy

- 每个 Wave 结束（门禁全绿后）一个提交；只 `git add` 明确文件
- Wave 1: `feat(ui): title bar metrics + template caption button + native/titlebar probes`
- Wave 2: `refactor(ui): rebuild AppTitleBar to Win11 native specs; adapt OOBE/hotkey dialog`
- Wave 3: `feat(ui): Win11 Snap Layouts support for custom title bar; update probes/fixtures/docs`
- 提交前先 `git diff <file>` 逐 hunk 核对；不得夹带并行会话改动

## Success Criteria

### Verification Commands
```bash
dotnet build ModernScreenShot.sln -c Release        # Expected: 0 errors, 0 warnings
src/ModernScreenShot.App/bin/Release/net10.0-windows/win-x64/ModernScreenShot.App.exe --smoke   # Expected: exit 0
dotnet run --project tools/Harness -c Release -- core   # Expected: ALL PASSED
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1   # Expected: parity
powershell -NoProfile -ExecutionPolicy Bypass -File tools/titlebar_probe.ps1   # Expected: ALL PASS
powershell -NoProfile -ExecutionPolicy Bypass -File tools/phase2_ui_probe.ps1   # Expected: ALL PASS
```

### Final Checklist
- [ ] All "Must Have" present
- [ ] All "Must NOT Have" absent
- [ ] All probes pass; fixtures regenerated
- [ ] 用户真机确认（补充性，非验收门槛）
