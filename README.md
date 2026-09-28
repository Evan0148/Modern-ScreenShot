# Modern ScreenShot

简洁现代的 Windows 截图工具。区域/窗口/滚动长截图，内置标注编辑器、效果合成（阴影/倒影/圆角卡片）、贴图与历史记录，托盘常驻 + 全局热键。

## 功能

### 截图模式（托盘左键 = 区域截图；右键菜单含全部模式）
| 模式 | 默认快捷键 |
|---|---|
| 区域截图 | `Ctrl+Shift+A` |
| 全屏（当前显示器） | `Ctrl+Shift+F` |
| 当前窗口 | `Ctrl+Shift+W` |
| 选择窗口 | `Ctrl+Shift+P` |
| 延时截图（区域） | `Ctrl+Shift+D` |
| 重复上次区域 | `Ctrl+Shift+R` |
| 滚动长截图 | `Ctrl+Shift+S` |
| 打开历史记录 | `Ctrl+Shift+H` |
| 所有显示器 | 未绑定（可在设置中自定义） |

- 快捷键全部可在 **设置 → 快捷键** 中改绑；冲突会标红并在保存时提示。
- **仿 macOS 窗口阴影**：在 **设置 → 截图** 勾选后（**即时生效，无需重启或关闭设置窗口**），以下三种方式截图都会保留窗口圆角、四周透明并套上柔和投影（⌘⇧4 空格点窗口的效果）：「当前窗口」(`Ctrl+Shift+W`)、「选择窗口」(`Ctrl+Shift+P`)、以及**区域截图中选区与某窗口完全重合时**（点击吸附或精确框选——日常框窗口的习惯流程直接生效）。效果在截图瞬间直接烘焙进图像——编辑器画布、贴图、复制、保存、历史看到的都是带阴影的成品。选区与窗口不完全重合（自定义区域）则保持普通区域截图。
- 第二次启动会自动把命令转发给已运行实例（单实例）。
- 区域截图：拖拽框选，悬停自动高亮窗口/子窗口（单击即选），8 向缩放手柄、方向键微调（Shift=10px）、8× 放大镜（坐标 + HEX 取色，按 `C` 复制颜色）、`Enter`/双击确认、`Esc`/右键取消。
- **Snipaste 式内联标注**：框选后直接在叠加层上标注，无需打开编辑器。工具条提供 选择/矩形/椭圆/直线/箭头/画笔/荧光笔/文字/序号/马赛克/橡皮擦（快捷键 V/R/E/L/A/P/H/T/N/M/X），`Ctrl+Z`/`Ctrl+Y` 撤销重做；点「编辑」或 `Enter` 进入编辑器可继续修改这些标注（矢量保留）。
- **每工具二级选项条**：选中某个标注工具后，主工具条下方浮出该工具专属选项——线条粗细预设、填充/虚线/加粗开关、字号与序号半径预设、马赛克模式与强度、以及取色调色板；所选样式记入设置，下次截图沿用。
- **橡皮擦**：选橡皮擦（`X`）后在选区内拖过某条标注即可删除它，一次拖动 = 一步撤销。

### 编辑器（截图后默认进入）
- 工具：选择(V) 矩形(R) 椭圆(E) 直线(L) 箭头(A) 画笔(P) 文字(T) 序号(N) 荧光笔(H) 马赛克(M) 模糊(B) 聚光灯(S) 放大镜(G) 裁剪(C) 橡皮擦(X)。
- `Ctrl+Z` / `Ctrl+Y` 撤销重做；`Del` 删除；橡皮擦拖过标注即删；`Ctrl+C` 复制成品；`Ctrl+S` 保存；`Ctrl+Shift+S` 另存为；`Ctrl+P` 贴到屏幕。
- 马赛克/模糊实时预览（直接调用 Core 像素算法）；聚光灯多区域联合打光；放大镜圆形标注。
- 裁剪为非破坏性，导出时生效。
- 缩放：`Ctrl+滚轮`（以光标为中心），空格/中键拖拽平移，工具栏有适应窗口/100%。

### 效果（编辑器底栏“效果”按钮）
- 预设：简洁（默认）、轻盈悬浮、浓重立体、镜面倒影、渐变卡片、无效果；可保存/删除自己的预设。
- 阴影（模糊/扩展/颜色/角度/距离/不透明度）、倒影（高度/起止透明度/间距/模糊）、边框（圆角/内边距/透明-纯色-渐变背景/内描边）。
- 60ms 防抖的实时缩略预览（棋盘格显示透明区）；导出时按全分辨率重新合成。

### 输出
- 复制：剪贴板同时写入 PNG 流 + DIBV5（兼容现代与经典应用，保留透明）。
- 保存：PNG / JPG（可调质量）/ WebP（SkiaSharp）；文件名模板 `{yyyy}{MM}{dd}{HH}{mm}{ss}{fff}{counter}{window}{mode}`，冲突自动加 `(2)`。
- 贴图（Pin）：置顶悬浮，拖拽移动、滚轮缩放、`Ctrl+滚轮` 调透明度、双击关闭、右键菜单（复制/保存/编辑/关闭）。
- **浮动缩略图（仿 macOS）**：可在 **设置 → 输出 → 截图后动作** 选「浮动缩略图」——截图后右下角滑入一张小卡片，点击进编辑器、拖动移位、右键复制/保存/贴图/关闭；悬停暂停约 6 秒的自动消失倒计时，超时按“自动保存/自动复制”设置落地。
- 历史：每次确认的截图自动入历史（缩略图网格），双击可重新编辑，支持复制/删除/打开目录。

### 滚动长截图
选区后自动在选区中心发滚轮并逐帧拼接（`ScrollStitcher` 行指纹匹配，容错微噪与固定表头）；滚到底自动结束；不支持自动滚动的页面会提示手动滚动；随时 `Esc` 或点浮动"停止"结束。

## 构建与运行

```
dotnet build ModernScreenShot.sln -c Release
# 运行
src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe
# 冒烟自检（退出码 0 为通过）
ModernScreenShot.App.exe --smoke
# 指定模式启动
ModernScreenShot.App.exe --capture region|fullscreen|all|active|window|last|scroll|delay
```

- 依赖：.NET 10 Desktop Runtime（framework-dependent 发布）。`publish\ModernScreenShot.App.exe` 为发布产物。
- 算法自检：`dotnet run --project tools/Harness -c Release -- core`（31 项断言）。
- 多语言检查：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1`。

## 文件位置

| 内容 | 路径 |
|---|---|
| 设置 | `%APPDATA%\Modern-ScreenShot\settings.json` |
| 历史记录 | `%LOCALAPPDATA%\Modern-ScreenShot\History\` |
| 日志 | `%LOCALAPPDATA%\Modern-ScreenShot\logs\app.log` |

## 已知问题

见 `KNOWN_ISSUES.md`。
