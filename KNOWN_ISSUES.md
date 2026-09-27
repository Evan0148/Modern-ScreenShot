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
