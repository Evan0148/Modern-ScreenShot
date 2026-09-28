# KNOWN ISSUES / 备忘
按任务顺序记录无人值守期间自行决定的事项与未验证点。用户手工测试时可对照检查。

## Goal 模式三阶段复查（2026-09-28 晚，提交 7ea9ead / ab5f875 / 21444de）

对 1f62468..42dbe56 新增功能做第二轮审查（两个串行审查代理），**12 项修复已实施，全部门禁通过（构建 0/0、harness、smoke 0、i18n 207 键）**：

- **P1 马赛克越界崩溃**：叠加层里马赛克矩形拖出选区边界（或选区缩小后）每帧渲染抛 ArgumentException——`AnnotationRenderer.DrawMosaic` 此前按 baseImage（整显示器尺寸）裁剪、却从 mosaicSource（仅选区尺寸）裁剪；已改为对 mosaicSource 自身裁剪。
- **P2 编辑器橡皮擦撤销丢失**：擦除拖动中按 Esc / 切工具会丢弃已删除项的撤销快照——`AbortInteraction`/`CommitPreview` 在 `_erasedAny` 时改为提交撤销；`FinishErase` 消费后复位 `_erasedAny` 防止残留标记污染下一步撤销。
- **P2 橡皮擦命中改为几何判定**：此前按包围盒命中（斜线/锯齿画笔的包围盒远大于笔迹，点空白处也会删）——新增 `AnnotationRenderer.HitsForErase`（线段距离/圆/包围盒），叠加层与编辑器共用，容差 max(6, 粗细/2+4)。
- **P2 双击误确认**：标注工具激活时快速双击选区会直接进编辑器（序号工具双击即触）——`OnDoubleClick` 在工具激活且点击在选区内时改走普通按下路径。
- **P2 内联文本框点击被吞**：点击文本框内部（移动光标/选字）会被"点击他处提交文本"分支吞掉——现在框内点击穿透到 TextBox，仅框外点击提交。
- **P2 浮动缩略图菜单期间自落地**：右键菜单打开时悬停暂停失效（菜单在独立弹窗 HWND，卡片立即 MouseLeave），6 秒倒计时会关掉菜单并执行落地动作、丢弃用户菜单选择——菜单打开期间暂停倒计时、菜单关闭后重新武装，`OnTimerTick` 双保险。
- **P3 五项**：烘焙过 mac 阴影的文档再开效果面板不会二次合成（`RenderFlattened`/预览尊重 `EffectsBaked` 契约）；透明图直存 JPG 先铺白底（原黑底）；文本工具提交位置与可见输入框对齐（应用同样钳制偏移）；擦除中忽略工具切换（`Tool` setter 加 `_erasing`）；编辑器高亮笔对齐叠加层语义（尊重粗细预设，不再固定 18px）。

**未修复（待确认）**：重画选区后撤销，标注会以旧选区坐标系恢复到新选区（可能落在选区外）。修复方向有两个（重画时清空撤销栈 vs 快照携带选区并重映射），涉及交互语义取舍，等用户定夺。

### UI 检查与观感（阶段 2/3）

- **诊断设施**：补回 `--render-settings|--render-editor|--render-history`（verify_theme.ps1 引用的开关此前从未入库）+ `MSS_RENDER_OUT`/`MSS_RENDER_WAIT_MS`/`MSS_RENDER_TAB`；新增 `tools/render_ui.ps1` 一键渲染 3 窗口 × 2 主题到 `tools/verify_out/ui/`。注意 RTB 必须渲染 Window 视觉本身（渲染 Content 不含窗口背景，会得到透明底假象）。
- **渲染检查结论**：设置全部 6 页签 × 明暗两主题、编辑器、历史均布局完整、对比度可读；快捷键框暗色正常（515b10a 修复的视觉复核通过）。
- **历史网格统一**：缩略图改为固定 112px 高视口等比内嵌（原纵横比自由伸展导致行高参差）；缩略图缺失时显示图片占位符图标（原空白卡片）；卡片加悬停底色反馈。
- **设置输出页**：保存位置为空时在下方显示"留空时保存到默认目录：{路径}"（新增 i18n 键 `Settings.SaveDirDefault`，键数 206→207）。
- **诊断编辑器**：`--show-editor`/`--render-editor` 的合成文档现在带矩形/画笔/文字/序号样例标注，UI 验证不再看空画布。

**仍需真机手测**（静态渲染覆盖不到）：overlay 各工具选项条与橡皮擦交互、双击修复的手感、文本框内点击编辑、浮动缩略图右键菜单期间不落地、多显示器定位、PinWindow、键盘焦点/Tab 序。

## 功能完善（2026-09-28，1f62468 / a6c2ba0 / bd82ea9 / 515b10a）

本轮四项，均通过客观门禁（构建 0/0、harness 31/31、i18n 206 键齐平、--smoke 0 含 publish 产物）。

- **每工具二级选项条 + 橡皮擦（1f62468）**：overlay 主工具条下方按当前工具浮出选项行（粗细/填充/虚线/加粗预设、字号、序号半径、马赛克模式与强度、调色板）；选择/橡皮擦不显示。橡皮擦（`X`）在 overlay 与编辑器均可用，拖过标注即删、一次拖动一步撤销。选项写入 `EditorSettings`（新增 FillShape/DashedLine/FontBold/StepRadius/MosaicPixelate 字段，向后兼容默认值，StepRadius 在 Normalize 钳制），确认/关闭时保存，"记住上次样式"。**订正**：下方"Snipaste 式内联标注 v1 限制"里的"无橡皮擦/单项删除"已不再成立。
- **仿 macOS 窗口阴影（a6c2ba0，同日两次修正）**：新增捕获设置 `MacStyleWindowShadow`（默认关，**勾选即时保存生效**）。开启后三种方式生效：「当前窗口」「选择窗口」、以及**区域截图中选区与快照窗口边界完全重合时**（点击吸附或精确框选——用户日常框窗口的习惯流程；`WindowEnumerator.FindByBounds` 精确匹配 DWM frame bounds，子窗口优先，排除桌面窗口）。效果**在截图瞬间烘焙进像素**——编辑器画布/贴图/复制/保存/历史全部所见即所得；带内联标注的吸附选区会把标注平移进加边后的画布（偏移按 Compose 同样方式重算：圆角→阴影边距→对称 padding）。初版只把效果挂在 `doc.Effects` 导出时合成（编辑器画布看不到）、区域截图完全不覆盖（用户复测用 Region 热键框窗口 → `window=0x0` 不走窗口分支）——两者叠加构成"开关开了没效果"。修正链：烘焙 + 即时保存（16d7be9）+ 吸附区域覆盖；`AnnotationDocument.EffectsBaked` 防导出二次合成、编辑器关闭不回写禁用态（不污染全局效果偏好）；原 `CaptureResult.BakeEffectsOnDirectOutput` 已删除。
- **仿 macOS 浮动缩略图（bd82ea9）**：新增截图后动作 `FloatingThumbnail`（枚举末尾追加，区域/其他两处 ComboBox 各加第 6 项）。截图后右下角滑入小卡片，点击进编辑器、拖动移位、右键复制/保存/贴图/关闭、悬停暂停约 6 秒自动消失倒计时，超时按 AutoSave/AutoCopy 落地。`FloatingThumbnailWindow` 是纯视图（终端动作全为注入回调），加入 `EditorWindows` 复用存活机制，Closed 里解绑全部事件。
- **主题 UI 缺陷修复（515b10a）**：用户报"跟随系统下快捷键框无法正常显示"。**根因**：`HotkeyRecorder : Button`，WPF 隐式样式按精确运行时类型解析，Button 子类拿不到 WPF-UI 主题化 Button 样式、回退到 OS 默认浅灰 Aero 底，在暗色设置窗口上撞色不可读。**修复**：构造函数 `SetResourceReference(StyleProperty, typeof(Button))` 拉入主题样式并随运行时切主题实时更新。用应用内 RenderTargetBitmap 做过对比度断言（禁用修复→暗色下浅灰、67 光斑=FAIL；恢复→暗色 38、0 光斑=PASS）。

**仍需真机手测**（本次开发环境为锁屏，键盘无法送达 overlay，交互类断言被阻塞；鼠标注入 + PrintWindow + 应用内渲染验证均可用）：
- overlay 各工具选项条的显隐/换行、橡皮擦擦除、选项持久化；
- 窗口 Mac 阴影的真机透明+投影观感、透明 PNG 进剪贴板在各消费端的表现（DIBV5 alpha 兼容性，见 T7 节）；
- 浮动缩略图的多显示器/混合 DPI 右下角定位、hover 暂停、超时落地；
- 三主题（Light/Dark/System）下设置窗口整体观感（快捷键框修复已数值验证）。

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

## Snipaste 式内联标注（2026-09-28）

叠加层工具条改为 Snipaste 风格：框选后直接在冻结画面上标注。实现要点与 v1 限制：

- 工具：选择/矩形/椭圆/直线/箭头/画笔/荧光笔/文字/序号/马赛克（像素化）；快捷键 V/R/E/L/A/P/H/T/N/M，Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z。
- **编辑器仍可二次编辑**：直接复制/保存/贴图会把标注烘焙进像素；点「编辑」（或 Enter/双击）则以干净底图 + 可编辑标注文档进入编辑器；历史记录同样保存矢量文档。
- 选区移动/缩放/微调时标注跟随选区左上角平移；重画选区会清空标注（可撤销）。
- v1 限制：叠加层内暂无颜色选择（用设置里的默认画笔色）、无橡皮擦/单项删除（用撤销）、无标注项选中移动、马赛克仅像素化、荧光笔固定 18px/45% 不透明度、文字不加粗。
- 已知小限制：工具条按钮仅图标（悬停有本地化提示）；文字工具点击后出现输入框，`Enter` 提交、`Esc` 取消、点击其他处提交。

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
