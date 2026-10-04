> **状态更新（2026-10-01）：Windows 11 原生标题栏对齐重构（`.omo/plans/titlebar-win11-native.md`，T1–T13）已实现：标题栏 32 DIP、模板化 CaptionButton、Snap Layouts（HTMAXBUTTON）、最大化外扩修复、探针与 fixtures 更新。文档同步：KNOWN_ISSUES.md 新增本改造小节；README.md 未提及标题栏故未动。以下 2026-09-27 记录为历史交接，保留。**
>
> **状态更新（2026-09-27）：T4–T10 已全部完成并逐任务提交（d9cc2b6 → 797d0dd → f1167e0 → db03609 → ce53b13 → 2002f5a + T10 收尾提交）。README.md / KNOWN_ISSUES.md / publish\ 均已就绪，等待用户真机测试。以下为过程交接记录。**

# HANDOFF — Modern-ScreenShot（交接文档）

> 给下一个 Agent：读完本文即可继续开发。计划全稿在 `.omo/plans/modern-screenshot.md`（必读，含全局规则、目录规划、任务拆分）。本文记录**已完成内容、验证方法、关键接口、剩余任务**。

## 0. 环境与硬约束

- 工作目录：`C:\Users\Evan Evan\Documents\Modern-ScreenShot`（Windows 11，PowerShell 5.1，**不支持 `&&`**，用 `;` 分隔命令）
- 只装了 .NET 10 SDK → 所有项目 `net10.0` / `net10.0-windows`。用户机器是 Win11。
- **没有单元测试**。用户原话："直接写，今天晚上弄完。无人值守，测试的话等写完后我亲自测试"。
- 无人值守：**永远不要向用户提问**。拿不准就自行合理决定并记入 `KNOWN_ISSUES.md`。
- i18n：所有界面文字走 `Localization/Strings.zh-CN.xaml` + `Strings.en-US.xaml`，禁止硬编码。每个任务完成后跑一次键完整性检查（App 的 `--smoke` 已内置 `LocalizationService.FindMissingKeys()`，缺键会直接失败）。
- 坐标全部用**物理像素**；overlay 窗口用 `SetWindowPos` 定位，不用 WPF Left/Top。
- 核心算法只写在 Core（纯 net10.0，无 WPF 引用）；编辑器预览必须调用同一套 Core 代码。
- 编译检查命令（每个任务结束必须 0 错误）：
  ```powershell
  dotnet build ModernScreenShot.sln -c Release -nologo
  ```
- 冒烟测试：
  ```powershell
  $p = Start-Process -FilePath "src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe" -ArgumentList "--smoke" -Wait -PassThru -WindowStyle Hidden
  $p.ExitCode   # 必须为 0
  ```
  日志在 `%LOCALAPPDATA%\Modern-ScreenShot\logs\app.log`。
- 算法验证：`dotnet run --project tools/Harness -c Release -- core`（31 项断言全 PASS，输出样图在 `tools/Harness/bin/Release/net10.0-windows/win-x64/out/`）。
- git 已初始化，当前 1 个提交 `1e3bad8`，工作区干净。**每个任务完成后提交一次**（提交信息英文、原子化）。构建通过才算任务完成。

## 1. 已完成（T1–T3）

### T1 脚手架 + Core 数据契约（由主 Agent 手写）
- `ModernScreenShot.sln`：Core（net10.0）、App（net10.0-windows, WinExe, win-x64, PerMonitorV2 manifest, 图标）、Harness（net10.0-windows 控制台）。
- 包：WPF-UI 4.3.0、CommunityToolkit.Mvvm 8.4.2、H.NotifyIcon.Wpf 2.4.1、Microsoft.Extensions.DependencyInjection 10.0.12、SkiaSharp 3.119.4 + NativeAssets.Win32。
- `Directory.Build.props`：Nullable enable、LangVersion latest、ImplicitUsings。
- Core 契约：
  - `Imaging/PixelBuffer.cs`：`PixelBuffer`（直通 alpha BGRA32，`Row/GetPixel/SetPixel/Clone/Crop/Fill/Blit/DrawOver`）、`PixelColor`（A,R,G,B、`ToHex/TryParseHex`）、`PixelRect`（`FromLTRB/Intersect/Union/Contains/Offset/Inflate`）。
  - `Imaging/EffectSettings.cs`：`ShadowOptions`（Enabled/BlurRadius/Spread/Color十六进制/Angle度/Distance/Opacity）、`ReflectionOptions`（Enabled/Height比例/StartOpacity/EndOpacity/Gap/Blur）、`FrameOptions`（CornerRadius/Padding/Background枚举/颜色/GradientAngle/Border）、`EffectSettings`、`EffectPreset`。
  - `Annotation/AnnotationItems.cs`：多态标注模型（JSON `[JsonDerivedType]`）：`RectItem/EllipseItem/LineItem/ArrowItem/PenItem/HighlighterItem/TextItem/StepItem/MosaicItem/SpotlightItem/MagnifierItem`，公共字段 `StrokeColor/StrokeThickness/Opacity/Id`，`GetBounds()/Move()/CloneItem()`。
  - `Annotation/AnnotationDocument.cs`：Items + 非破坏性 `Crop` + `Effects` + `RenumberSteps()`。
  - `Annotation/UndoStack.cs`：快照式 JSON 撤销/重做（`Push/Undo/Redo`，`Serialize/Deserialize`）。
  - `Settings/AppSettings.cs`：`AppSettings`（Language/Theme/StartWithWindows/Hotkeys/Output/Capture/Editor/Effects/UserPresets/HistoryMaxCount）+ `HotkeySettings`（默认键位见下）+ `CaptureMode/AfterCaptureAction/ImageFormat` 枚举。
  - `Settings/BuiltInPresets.cs`：Clean（默认，阴影 blur24/距离8/角度90/透明度0.35）、SoftFloat、Dramatic、Mirror（带反射）、GradientCard、None。
  - `Settings/SettingsStore.cs`：JSON 序列化到 `%APPDATA%\Modern-ScreenShot\settings.json`，损坏时备份并回退默认值（`Normalize` 修补缺字段）；`AppPaths` 常量。
  - `History/HistoryStore.cs`：`%LOCALAPPDATA%\Modern-ScreenShot\History\{id}\` 存 original.png / doc.json / thumb.png / meta.json，`List/Prune/Delete`。
  - `JsonDefaults.Options`：camelCase + 字符串枚举。
- App 脚手架：`App.xaml(.cs)` DI 启动、`--smoke`（解析全部单例、切换中英双语、检查缺失键、枚举虚拟屏）、`App.Features.cs` 三个 partial 钩子（**后续任务只在这类 partial 文件扩展，不要大改 App.xaml.cs**）。

### T2 Core 算法（首个子代理超时 30 分钟无产出 → 主 Agent 手写，全部验证通过）
- `BoxBlur`：3 次 box 近似高斯，行/列滑窗 O(n)，`Parallel.For`，alpha 正确（预乘再还原）。
- `ShadowEffect`：`Apply(src, options, out offsetX, out offsetY)`；基于 alpha 蒙版 → spread 膨胀 → 模糊 → 按角度/距离偏移 → 着色*透明度 → 与原图合成；画布自动扩展不裁剪。`RenderShadowOnly` 只出阴影（供 EffectPipeline 组装用）。
- `ReflectionEffect`：垂直翻转底部片段 + t^1.5 缓出渐隐 + 可选模糊；`RenderReflectionOnly` + `Apply`。
- `FrameEffect`：`RoundCorners`（角部像素级解析覆盖率抗锯齿，不破坏已有透明角）、`DrawInnerBorder`、`ApplyBackground`（None/Solid/Gradient 全画布线性渐变，`FillGradient`）。
- `EffectPipeline.Compose(src, settings)`：顺序 = 圆角+边框 → 阴影（仅图片本体）→ 反射放在图片下方（**不被阴影影响**，画布自动加高）→ 背景/内边距。
- `Mosaic.Pixelate`（按格平均）/ `Mosaic.Blur`（区域内两次重模糊，采样区域外保证边缘平滑）。
- `ScrollStitcher`：逐帧行指纹（亮度量化 0–63 哈希，容忍微噪）+ 静态表头/表尾行检测 + 最大重叠搜索（≥8 匹配行、失配<5%），`AddFrame` 返回 false 表示无滚动。
- `FileNameTemplate`：`{yyyy}{MM}{dd}{HH}{mm}{ss}{fff}{counter}{window}{mode}` token、`Sanitize`（非法字符/保留名/长度）、`GetUniquePath`（冲突加 ` (2)`）。
- `ColorUtil`：HSV↔RGB、Lerp、WithAlpha。
- **验证**：`harness core` 31/31 PASS；4K（3840×2160）Mirror 预设管线 108ms；目检过 `preset_GradientCard.png`（渐变卡片+阴影）和 `preset_Mirror.png`（反射渐隐）样图，效果符合预期。

### T3 App 基础 + Win32 互操作 + 截屏（后台子代理完成，主 Agent 验证）
- `app.manifest`（PerMonitorV2 + supportedOS）、`Assets/app.ico`（16/32/48/256 程序化生成）。
- `Interop/NativeMethods.cs`：user32/gdi32/dwmapi/shcore 全套 P/Invoke（EnumWindows、PrintWindow、BitBlt、DIB、RegisterHotKey、SendInput、EnumDisplayMonitors、DwmGetWindowAttribute、SetWindowPos 等）。
- `Interop/DibSection.cs` + `Interop/BitmapInterop.cs`：PixelBuffer ↔ BitmapSource（Bgra32）、PNG 编解码。
- `Capture/MonitorService.cs`：`MonitorInfo(Handle, Bounds, WorkArea, DpiX, DpiY, IsPrimary, DeviceName)`，`GetMonitors/GetVirtualScreen/FromPoint/GetCursorMonitor`（支持负坐标虚拟屏）。
- `Capture/ScreenCapturer.cs`：`Capture(PixelRect, includeCursor)` → `PixelBuffer`。
- `Capture/WindowEnumerator.cs`：**先 `Refresh()` 拿快照（在 overlay 显示前），再 `HitTest(x, y, includeChildren)`**；排除本进程/被遮蔽/最小化/Progman 等；子窗口按最深最小优先。
- `Capture/WindowCapturer.cs`：`CaptureWindow(hwnd, transparentCorners, out title)` 用 PrintWindow(PW_RENDERFULLCONTENT)，失败回退屏幕 BitBlt；Win11 自动圆角透明遮罩（`CornerRounding.Apply`，半径 8×DPI 缩放）；`CaptureActiveWindow` 跳过自身窗口。`WindowInfo(Handle, Title, ClassName, Bounds, ProcessId, IsChild)`，`CaptureResult(Image, Mode, WindowTitle, SourceRect, Time)`。
- `Localization/`：`LocalizationService`（`Apply(lang)` 运行时切换、`Get(key, args)`、`FindMissingKeys()`、`SupportedLanguages`）、两份 XAML 各 ~14KB 键集（Tray/Mode/Action/Toast/Preset/Tool/Prop/Effects/Settings/History/Overlay/Scroll/Countdown/Editor/FirstRun 全覆盖）。
- `Services/Log.cs`：文件日志（1MB 滚动）。
- **验证**：solution 编译 0 错误 0 警告；`--smoke` 退出码 0（日志确认中英切换、键无缺失、虚拟屏枚举正常）。

## 2. 已提交

`1e3bad8 feat: scaffold, core imaging pipeline, app foundation with capture + i18n`（工作区干净）。

## 3. 剩余任务（按序执行，T4 → T10）

计划详情见 `.omo/plans/modern-screenshot.md` 对应小节，这里只给执行要点：

### T4 区域选择 Overlay（优先做）
- 每显示器一个 overlay 窗口（**先冻结全虚拟屏截图再显示**），`SetWindowPos` 物理像素定位，PerMonitorV2。
- 拖拽框选；悬停高亮窗口/子窗口（用 `WindowEnumerator.Refresh()+HitTest`，overlay 自己必须在 Refresh 后创建以免被选中）；点击即选窗（WindowPick 模式用 `WindowCapturer` 真内容+透明圆角）；方向键微调 1px（Shift=10px）；8 缩放手柄。
- 放大镜（8× 网格 + 坐标 + HEX 颜色，C 复制颜色）、尺寸标签（`Overlay.Size` 键）、工具栏（编辑/复制/保存/贴图/取消）、Enter/双击确认、Esc/右键取消。
- 选区存入 `settings.Capture.LastRegion`（int[]{x,y,w,h} 物理像素）。
- **交互完成后调用处**（暂时写在 `App.Features.cs` 的 partial 里接线）。

### T5 编辑器
- `Editor/EditorWindow`（WPF-UI Fluent）：顶部工具栏、右侧属性面板、底部动作。快捷键见计划（V/R/E/L/A/P/T/N/H/M/B/S/G/C、Ctrl+Z/Y、Del、Ctrl+C/S/P）。
- `Editor/Canvas/AnnotationCanvas`：缩放（Ctrl+滚轮、适应、100%）/平移（空格+拖/中键）、把 `AnnotationItem` 渲染成 WPF 形状、命中测试、8 手柄选中移动缩放、双击文字编辑、置顶置底。
- `RenderFlattened()`：原图 + 标注 + Crop → `RenderTargetBitmap`(96DPI 1:1) → `PixelBuffer`，再走 Core `EffectPipeline`。
- Mosaic/Blur/Spotlight/Magnifier 预览直接调用 Core `Mosaic` / 遮罩合成；每步变更 `UndoStack.Push`。
- 文字用 `FormattedText` 测量，把 MeasuredWidth/Height 写回 `TextItem`。

### T6 效果面板（编辑器右侧或独立面板）
- 预设下拉（内置+用户，保存/删除）、阴影（启用/模糊/扩散/颜色/角度/距离/透明度）、反射（启用/高度/起止透明度/间隙/模糊）、边框（圆角/内边距/背景 None-纯色-渐变+两色+角度）。
- 实时预览：60ms 防抖、后台线程对缩小图（≤1200px）跑同一套 Core 管线；导出走全分辨率。
- 需要一个 `ColorPicker` 控件（HSV 方块+色相条+alpha+HEX）。**这是 UI 工作，用 `visual-engineering` 类别 + `frontend-ui-ux` skill 委派。**

### T7 输出
- `ClipboardService`：PNG + CF_DIBV5 双格式，重试 10×50ms。
- `ImageExporter`：PNG/JPG(质量)/WebP(SkiaSharp)；文件名模板 + 快存目录。
- 编辑器工具栏 Copy/Save/SaveAs/Pin/OpenFolder + Toast；`PinWindow`（最顶、拖拽、滚轮缩放、Ctrl+滚轮透明度、双击关闭、右键菜单）；HistoryWindow（缩略图网格、重开编辑——加载 doc.json、复制、删除、打开目录）；每次确认的截屏入 History 并 `Prune(HistoryMaxCount)`。

### T8 外壳
- 托盘（H.NotifyIcon.Wpf）：左键=区域截屏，菜单含全部模式+延时子菜单(3/5/10s)+历史+设置+语言+退出。
- `HotkeyService`：隐藏 HwndSource 窗口 `RegisterHotKey`，冲突 Toast 提示并在设置页可改绑（默认键位在 `HotkeySettings.CreateDefaults()`：Ctrl+Shift+A/F/W/P/D/R/S/H）。
- 单实例 Mutex + 命名管道转发 `--capture region|fullscreen|all|active|window|delay|last|scroll`。
- `CountdownWindow` 延时气泡；`SettingsWindow` 页签（常规/热键[录制控件]/截屏/输出/效果/关于），中英即时切换；首次运行提示；开机自启（HKCU Run）。
- **只编辑 `App.Features.cs` 或新增 partial 文件接入，别动 App.xaml.cs 主体。**

### T9 滚动长截图
- overlay 选区后：SendInput 滚轮（3 格、350ms 间隔、滚到选区中心）、每帧 `ScreenCapturer.Capture` → `ScrollStitcher.AddFrame`；停止条件：连续帧相同 / 高度到 `ScrollMaxHeight` / Esc 或悬浮"停止"按钮；自动滚不动则退化为手动滚动拼接。结果进编辑器。

### T10 集成收尾
- i18n 键检查通过；`--smoke` 实例化所有窗口（overlay 除外）双语通过；启动 5 秒存活检查；`dotnet publish -c Release -r win-x64 --self-contained false -o publish/`；写 `README.md`（中文：功能、快捷键、构建运行、设置路径）和 `KNOWN_ISSUES.md`；最终提交。

## 4. 已知问题 / 待办备忘

- `KNOWN_ISSUES.md` 尚不存在（T10 创建）。
- WebP 导出（SkiaSharp）尚未实际验证过，T7 里第一次跑通时记入 KNOWN_ISSUES。
- `CaptureSettings.PlaySound`、`EditorSettings.SpotlightDim/MagnifierZoom` 等字段已定义但尚无消费方（T5/T8 用上）。
- 第一个 Core 算法子代理超时教训：**大任务拆小、派生后台代理时用精确签名清单**；主 Agent 手写算法反而更快。
- 双显示器环境在冒烟日志里出现过（4480×1600 / 2 显示器），也出现过 2560×1600 单屏——overlay 的多屏逻辑必须真机验证。

## 5. 关键文件速查

| 文件 | 作用 |
|---|---|
| `.omo/plans/modern-screenshot.md` | 完整工作计划（全局规则、目录、任务） |
| `src/ModernScreenShot.Core/Imaging/*` | 阴影/反射/圆角/管线/马赛克/拼接算法 |
| `src/ModernScreenShot.Core/Annotation/*` | 标注数据模型 + 撤销栈 |
| `src/ModernScreenShot.App/App.xaml.cs` | DI + --smoke（尽量别改） |
| `src/ModernScreenShot.App/App.Features.cs` | **功能接线入口（在这里接）** |
| `src/ModernScreenShot.App/Capture/*` | 截屏/窗口枚举/监视器 |
| `src/ModernScreenShot.App/Localization/*` | 中英文资源（加键要两边都加） |
| `tools/Harness/Program.cs` | 算法断言（`harness core`） |
