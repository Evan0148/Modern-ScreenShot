# KNOWN ISSUES / 备忘
按任务顺序记录无人值守期间自行决定的事项与未验证点。用户手工测试时可对照检查。

## 遗留自 T2/T3

- **WebP 导出未验证**：SkiaSharp WebP 编码计划在 T7 第一次实际导出时验证。
- `CaptureSettings.PlaySound`、`EditorSettings.SpotlightDim/MagnifierZoom` 已定义但尚无消费方（T5/T8 接入）。

## T7 输出（本次）

- **剪贴板现在写入 CF_DIBV5 + "PNG" 双格式**（经 Win32 原生 API），比 WPF SetImage 兼容性更好；未实测 Office/Photoshop/微信等具体消费端，用户测试时可重点确认。
- **WebP 导出已通过 SkiaSharp 实现**（Bgra8888 直拷），未实际生成过 WebP 文件验证；设置里切换格式后快存即可测。
- **DIBV5 采用 BI_BITFIELDS + alpha 掩码**；个别老旧程序若读出异常颜色可反馈（回退方案 BI_RGB）。
- **PinWindow 的滚轮缩放是整体 DIP 缩放**，放大 4 倍以上有轻微模糊，属 WPF 位图缩放正常表现。
- **历史记录在分发阶段写入**（原始图 + 空标注文档 + 缩略图）；编辑器里后续标注不会同步回历史 doc.json，历史"重新编辑"总是从原始图开始。
- **HistoryWindow 缩略图加载失败时显示空白卡片**（文件被手动删除的场景）。
- **效果设置在编辑器关闭时写回 settings.json**（记住上次使用的效果）；编辑中途改的效果不影响其他已开编辑器。

## T8 外壳（子代理完成）

- **H.NotifyIcon 的 `ForceCreate()` 默认开启效率模式，在本机抛 COMException 0x80070001**：已改为 `ForceCreate(enablesEfficiencyMode: false)`。若日后升级 H.NotifyIcon 版本需留意此点。
- **主题切换**使用 WPF-UI `ApplicationThemeManager.Apply`，"跟随系统"在启动时读取一次系统主题，之后不随系统实时变化（改主题需在设置里手动切换）。
- **热键显示名（Ctrl/Shift/F1 等）未本地化**（技术惯例）；格式名 PNG/JPG/WebP 同理。
- **进程被强杀后托盘可能出现 Ghost 图标**，鼠标悬停一次即消失（Explorer 行为，非 bug）。
- 设置窗口在关闭时统一写回 settings.json 并重注册热键；窗口内改动在关闭前不会持久化。
- 开机自启写 `HKCU\...\Run`，指向当前 exe 路径——**移动 exe 位置后需在设置里重新开关一次**。
- 首次运行提示用托盘气泡；气泡失败时降级为日志（不阻塞）。

## T9 滚动长截图（子代理完成）

- **全屏选区时浮动 HUD 可能被截进长图**（所有备选位置都与选区重叠时的兜底行为）。
- **Esc 通过 WH_KEYBOARD_LL 低级钩子捕获并被吞掉**，避免被被截页面响应；若发现个别全屏应用对 Esc 无响应请反馈。
- **自动/手动语义**：至少成功滚动过一轮后，连续 2 帧无新增即判"到底"结束；从未成功滚动（合成滚轮完全无效）才进入手动模式（不再自动滚、不做到底判定），等用户点停止。
- 混合 DPI 多显示器下 HUD 尺寸/位置换算未在真机逐项验证。
- 需要 Edge/Chrome/资源管理器等真实长页面上实测：自动滚动顺滑度、到底判定、手动兜底。

## 代码/UI 审查修复（2026-09-28，aca6f30 + 34a6ddc）

系统性代码 + UI 审查，修复约 45 项确认缺陷，全部验证通过（构建 0/0、harness、i18n 201 键、smoke 0）。要点：

- **崩溃/数据类**：settings.json 中异常效果参数不再溢出崩溃（Normalize 加钳制）；PixelBuffer 大图分配用 long 校验；HistoryStore 改原子写 + IO 兜底，`Prune(0)` 语义改为"不限制"（原会删光历史）。
- **交互失效类**：选区框选后的移动/缩放此前是死分支（完全无效）；放大镜工具（G）此前每次都被丢弃；贴图窗口的适配与滚轮缩放此前被 `SizeToContent` 抵消；三者均已修复。
- **资源泄漏**：HistoryWindow 订阅单例 `HistoryStore.Changed` 不解绑，每次开关窗泄漏整个缩略图树——已解绑；剪贴板 `SetClipboardData` 失败句柄泄漏——已 `GlobalFree`。
- **重入/竞态**：overlay 打开时热键/托盘/管道可重入 `RunCapture` 致第二张烙入第一层 overlay——加进行中守卫；OverlaySession `_finished` 赋值时序可致 UI 永久卡死——已前移 + try/catch。
- **正确性**：DIBV5 `bV5Intent` 写错偏移（104→108）；LastRegion 换显示器后越界黑图——钳制到当前虚拟屏；滚动拼接 OOM 静默损坏——加异常计数；滚动截图后光标不还原——已还原。
- **一致性/日志**：设置里改语言经托盘切换后点 OK 不再被回退；日志一次 IO 失败不再永久禁用（改累计阈值）；单实例管道失败不再热旋刷日志。
- **UI**：编辑器 14 个工具在默认宽度下溢出（后 3 个不可达）→ 改 WrapPanel 两行；亮色主题下画布工作区变纯白 → 深色背景移到 Grid；次级文本/历史卡片边框改用主题资源（暗/亮自适应）；HEX 输入框加宽；属性面板滑杆补数值标签（与效果面板一致）。
- **清理**：删除 5 个未使用本地化键、若干死代码（ToArgb/WithAlpha/CloneItem/SavedPath/HasCropDraft）、移除多余 `AllowUnsafeBlocks`。

诊断开关：新增 `--show-editor` / `--show-history`（配合已有 `--show-settings`，仅供 UI 验证）。

**仍需真机手测**：交互类修复（选区拖拽、放大镜、贴图缩放、热键裸键拒绝、滚动截图光标还原）。未改动的设计行为（编辑器标注不回写历史 doc.json、BoxBlur 区域内存占用）保持原样。

## 全局验收状态（T10，2026-09-27）

- Release 构建 0 错误 0 警告；`--smoke` 退出码 0（发布产物同样验证）。
- `harness core` 31/31 通过；`tools/check-i18n.ps1` 206 键双语齐平（审查后为 201 键，删了 5 个未使用键）。
- 启动 5 秒存活检查通过；`publish/ModernScreenShot.App.exe` 存在。
- **所有交互流程（overlay 框选、编辑器绘图、效果预览、滚动拼接、托盘菜单、热键、设置改绑）未经人工真机测试**，由用户自测；机器级 commit 内存压力（见 T5 节）可能影响大图操作。

## T5 编辑器（本次）

- **系统 commit 内存压力**：测试期间发现机器 commit 配额接近耗尽（48GB 页面文件几乎用满），导致 harness 的 4K 大分配测试间歇性 OOM。已通过在大分配前强制 GC 缓解；若用户测试时仍出现"Out of memory."，请关闭其他大内存程序后重试。这不是应用代码问题。
- **属性面板的自定义颜色目前是 HEX 输入框**：完整的 HSV ColorPicker 控件按计划在 T6 交付。
- **Pin 按钮当前回退为打开编辑器**：PinWindow 在 T7 实现。
- **Save As 对话框暂含 WebP 选项但保存时回退 PNG**（日志有提示）：SkiaSharp WebP 编码在 T7 接入。
- **TextItem 的 MeasuredWidth/Height 在每次编辑后由 UI 层测量写回**；从 JSON 加载历史项时使用已存的测量值（T7 历史功能接入时无需重测）。
- **编辑器工具快捷键在文字输入框聚焦时不生效**（避免输入冲突），点击画布外区域即可恢复。

## T4 区域选择 Overlay（本次）

- **Edit / Pin 工具栏动作是过渡行为**：编辑器（T5）和贴图窗口（T7）尚未实现，当前先回退为"复制到剪贴板"并在日志中说明。T5/T7 完成后由真实功能替换。
- **剪贴板复制是过渡实现**：`App.Features.CopyToClipboard` 目前用 WPF `Clipboard.SetImage`（CF_BITMAP/CF_DIB）。T7 会替换为正式的 `ClipboardService`（PNG + CF_DIBV5 双格式）。
- **快速保存是过渡实现**：`App.Features.QuickSave` 目前只输出 PNG；T7 的 `ImageExporter` 会支持 PNG/JPG/WebP。
- **区域截图的冻结帧不包含鼠标指针**（设计决定）：overlay 交互期间指针会移动，冻结进画面会很怪；指针捕获仅对全屏/整屏/当前窗口等直接模式生效（由 `Capture.CaptureCursor` 控制）。
- **延时模式当前等同立即区域截图**：`CountdownWindow` 在 T8 实现。
- **滚动截图未实现**（T9），托盘/热键尚未接入（T8）；当前可通过 `ModernScreenShot.App.exe --capture region|window|fullscreen|all|active|last` 手动测试对应模式。
- **多显示器逻辑已实现但只能等真机手测**：本机冒烟环境为双显示器（虚拟屏 4480×1600），overlay 每显示器一个窗口、物理像素 SetWindowPos 定位、负坐标虚拟屏均已按设计处理，但交互（跨屏拖拽、放大镜、工具栏归属窗口）未经人工验证。
- **WindowPick 模式的窗口矩形来自枚举快照**：若目标窗口在 overlay 打开期间移动/改变大小，捕获内容（PrintWindow 实时）与记录的 SourceRect 可能不一致；低概率，接受。
- **子窗口高亮使用 DWM extended frame bounds 的相交矩形**，个别自绘子窗口（如 Chromium 内部 HWND）边界可能不准，属 Windows 枚举固有局限。
