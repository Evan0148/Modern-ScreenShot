# KNOWN ISSUES / 备忘

按任务顺序记录无人值守期间自行决定的事项与未验证点。用户手工测试时可对照检查。

## 遗留自 T2/T3

- **WebP 导出未验证**：SkiaSharp WebP 编码计划在 T7 第一次实际导出时验证。
- `CaptureSettings.PlaySound`、`EditorSettings.SpotlightDim/MagnifierZoom` 已定义但尚无消费方（T5/T8 接入）。

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
