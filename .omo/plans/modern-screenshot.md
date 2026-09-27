# Plan: Modern-ScreenShot

Windows 10/11 screenshot app in C# WPF. The target is `net10.0-windows`, because only the .NET 10 SDK is installed. The user wants it finished in one unattended night and will test it themselves. **No unit test projects.** Each task is verified by a build, a `--smoke` run, and throwaway harnesses.

Root: `C:\Users\Evan Evan\Documents\Modern-ScreenShot`

## Global rules (every task)
- Executors run UNATTENDED. Never ask questions. When unsure, choose a sensible default, note it in `KNOWN_ISSUES.md`, and keep going.
- `dotnet build ModernScreenShot.sln -c Release` MUST finish with 0 errors at the end of every task. Never leave the tree broken.
- Coordinates are **physical pixels** everywhere in capture code and overlay placement. Convert to DIPs only inside a window, using that window's DPI. Place overlay windows with `SetWindowPos` in physical px. Don't use WPF Left/Top for them.
- The manifest declares PerMonitorV2 DPI awareness.
- Every user-facing string goes through i18n resources, in both zh-CN and en-US. No hardcoded UI text.
- Don't use `as any`-style suppressions and don't leave empty catch blocks. Log errors to `%LOCALAPPDATA%\Modern-ScreenShot\logs\app.log` through a simple `Log` static class.
- Out of scope: cloud upload, OCR, video recording, plugins, auto-updater, and test projects.
- Effects and pixel algorithms live ONLY in Core, operating on `PixelBuffer`. The editor preview calls the same Core code. Don't write a separate WPF-only version.
- The final render path also goes through Core. Annotation rendering is WPF `RenderTargetBitmap` into a PixelBuffer, then the Core effects pipeline runs on it.

## Solution layout
```
ModernScreenShot.sln
Directory.Build.props         (Nullable enable, LangVersion latest, ImplicitUsings)
src/ModernScreenShot.Core/    net10.0 - pure logic, no WPF/Win32
  Imaging/PixelBuffer.cs       BGRA32 premultiplied=false, Width, Height, Stride, byte[] Data, Crop, Clone
  Imaging/ShadowEffect.cs      ShadowOptions{Enabled, BlurRadius, Spread, Color(ARGB), Angle deg, Distance, Opacity 0-1}
  Imaging/ReflectionEffect.cs  ReflectionOptions{Enabled, Height 0-1 fraction, StartOpacity, EndOpacity, Gap px, Blur}
  Imaging/FrameEffect.cs       CornerRadius, Padding, Background (None/Solid/Gradient2 with angle), InnerBorder
  Imaging/EffectPipeline.cs    Compose(src, EffectSettings) -> PixelBuffer, order: round corners -> shadow -> reflection -> padding/background
  Imaging/BoxBlur.cs           3-pass box blur approximating gaussian, separable, alpha-aware
  Imaging/Mosaic.cs            Pixelate(buffer, rect, cellSize); Blur(buffer, rect, radius)
  Imaging/ScrollStitcher.cs    Incremental stitcher: AddFrame(PixelBuffer) finds vertical overlap via row hashing, ignores fixed header/footer rows
  Imaging/ColorUtil.cs
  Output/FileNameTemplate.cs   tokens {yyyy}{MM}{dd}{HH}{mm}{ss}{fff}{counter}{window}{mode}; sanitize; collision _(n)
  Settings/AppSettings.cs      versioned; Language, Hotkeys, Output (dir, format, jpgQuality, autoSave, autoCopy, template), Effects (EffectSettings + named presets), Capture (delaySeconds, showMagnifier, captureCursor, windowTransparentCorners), Editor (default colors/stroke/font), History(max count)
  Settings/SettingsStore.cs    System.Text.Json, %APPDATA%\Modern-ScreenShot\settings.json, tolerant load (defaults on failure, backup corrupt file)
  Annotation/*.cs              serializable model: AnnotationDocument{BaseImagePath/size, List<AnnotationItem>, Crop rect?, EffectSettings}; item kinds: Rect, Ellipse, Line, Arrow, Pen(points), Text, Step(number), Highlighter(points), Mosaic(rect, mode, strength), Spotlight(rect/ellipse, dim opacity), Magnifier(sourceRect, target center, zoom); common Stroke color, thickness, fill, opacity, Id
  Annotation/UndoStack.cs      snapshot-based (serialize document JSON) undo/redo, limit 100
  History/HistoryStore.cs      %LOCALAPPDATA%\Modern-ScreenShot\History\{id}\ original.png, doc.json, thumb.png, meta; prune to max
src/ModernScreenShot.App/     net10.0-windows WPF, WinExe, RuntimeIdentifier win-x64, app.manifest PerMonitorV2, icon
  Interop/Native*.cs           P/Invoke (user32, gdi32, dwmapi, shcore, kernel32)
  Capture/MonitorService.cs    EnumDisplayMonitors + GetMonitorInfo + GetDpiForMonitor -> MonitorInfo{Bounds px, WorkArea, Dpi, IsPrimary}; VirtualScreen bounds (may be negative)
  Capture/ScreenCapturer.cs    BitBlt from screen DC (CAPTUREBLT) any physical rect -> PixelBuffer; optional cursor draw
  Capture/WindowEnumerator.cs  visible, non-cloaked, non-iconic, not own process; z-order; DWMWA_EXTENDED_FRAME_BOUNDS; child windows for snapping
  Capture/WindowCapturer.cs    PrintWindow(PW_RENDERFULLCONTENT) into DIB; fallback screen BitBlt; Win11 rounded corner alpha mask (radius 8*scale unless DWMWCP_DONOTROUND); foreground-window capture
  Capture/CaptureService.cs    orchestrates modes: Region, Fullscreen(current monitor), AllMonitors, ActiveWindow, WindowPick, LastRegion, Scrolling; delay countdown; returns CaptureResult{PixelBuffer, Mode, WindowTitle, SourceRect}
  Capture/ScrollingCaptureService.cs
  Overlay/RegionOverlayWindow  one per monitor, shows frozen screenshot, dim outside selection, size label, magnifier + RGB/HEX, hover window snap highlight, handles, Enter/double-click confirm, Esc cancel, right-click cancel, toolbar after selection (Edit, Copy, Save, Pin, Cancel)
  Overlay/CountdownWindow      delay countdown bubble
  Editor/EditorWindow          Fluent window: top toolbar tools, left/right property panel, canvas (zoom/pan), Effects panel, bottom actions
  Editor/Canvas/*              AnnotationCanvas rendering items as WPF visuals, selection adorners, move/resize, hit test
  Editor/Tools/*               one tool class per kind
  Effects/EffectsPanel         sliders + color pickers, live preview via Core pipeline (throttled, downscaled preview)
  Output/ClipboardService      PNG + CF_DIBV5 + Bitmap; retry 10x50ms
  Output/ImageExporter         PNG/JPG via WPF encoders, WebP via SkiaSharp
  Pin/PinWindow                topmost, borderless, drag, wheel zoom, opacity (Ctrl+wheel), double-click close, context menu (copy, save, edit, close)
  History/HistoryWindow        grid of thumbnails, open in editor, copy, delete, open folder
  Shell/TrayService            H.NotifyIcon tray icon + menu for all modes
  Shell/HotkeyService          RegisterHotKey on hidden HwndSource window; report failures
  Shell/SingleInstance         named Mutex + named pipe forwarding args
  Settings/SettingsWindow      tabs: General(language, startup with Windows via HKCU Run), Hotkeys (recorder control), Capture, Output, Effects defaults/presets, About
  Localization/Strings.zh-CN.xaml, Strings.en-US.xaml (ResourceDictionary of sys:String), LocalizationService swaps merged dictionary at runtime; DynamicResource everywhere; L.Get(key) for code
  App.xaml(.cs)                DI container (Microsoft.Extensions.DependencyInjection), Fluent theme, startup args: --smoke, --capture <mode>
tools/Harness/                 net10.0-windows console, references Core (+App's capture where needed). Throwaway verification, kept for the user
tools/check-i18n.ps1           compares keys in both xaml files
KNOWN_ISSUES.md, README.md (zh-CN)
```

## Packages (pin, verify restore; fallback if incompatible)
- WPF-UI 4.x (`WPF-UI`). Fallback: a hand-written light/dark Fluent-ish style dictionary.
- CommunityToolkit.Mvvm 8.x
- Microsoft.Extensions.DependencyInjection 9.x/10.x
- H.NotifyIcon.Wpf 2.x. Fallback: `UseWindowsForms` NotifyIcon.
- SkiaSharp 3.x + SkiaSharp.NativeAssets.Win32. Fallback: disable WebP and note it.

## Default hotkeys
| Action | Hotkey |
|---|---|
| Region | Ctrl+Shift+A |
| Fullscreen (current monitor) | Ctrl+Shift+F |
| Active window | Ctrl+Shift+W |
| Window pick | Ctrl+Shift+P |
| Delay region | Ctrl+Shift+D |
| Repeat last region | Ctrl+Shift+R |
| Scrolling | Ctrl+Shift+S |
| All monitors | none |
| Open history | Ctrl+Shift+H |

Everything is rebindable, and binding conflicts show up in the Settings window. Optional: bind PrtSc to Region (off by default).

## Behaviors
- **After capture:** the default is the overlay toolbar for region/window pick, and the editor for the other modes. Configurable: open editor / copy only / save only / pin.
- **Auto actions:** if autoCopy/autoSave are on, they run after the user confirms. Every capture is added to history.
- **Editor shortcuts:**

  | Tool / action | Key |
  |---|---|
  | Select | V |
  | Rect | R |
  | Ellipse | E |
  | Line | L |
  | Arrow | A |
  | Pen | P |
  | Text | T |
  | Step | N |
  | Highlighter | H |
  | Mosaic | M |
  | Blur | B |
  | Spotlight | S |
  | Magnifier | G |
  | Crop | C |
  | Undo / Redo | Ctrl+Z / Ctrl+Y (also Ctrl+Shift+Z) |
  | Delete | Del |
  | Copy result | Ctrl+C |
  | Save / Save as | Ctrl+S / Ctrl+Shift+S |
  | Pin | Ctrl+P |

  In the editor, Shift constrains shapes (square, 45° lines).
- **Step numbers:** auto-increment in document order, and renumber when a step is deleted.
- **Effects:** default preset is "Clean": shadow on (blur 24, distance 8, angle 90° meaning downward, color black, opacity 0.35), reflection off, padding 32, background transparent. Built-in presets: Clean, Soft Float, Dramatic, Mirror (reflection on), Gradient Card (gradient background + shadow + rounded corners 12), None. Users can save their own presets.
- **Shadow geometry:** the output canvas grows so the blur is never clipped. Offset is `dx = cos(angle)*distance`, `dy = sin(angle)*distance`, with screen y pointing down. The shadow comes from the alpha mask (so it follows rounded corners), is expanded by spread, box-blurred, and tinted with color * opacity. The image is composited on top of it.
- **Reflection:** a vertically flipped copy of the bottom `Height` fraction, placed `Gap` px below, with a linear alpha gradient from StartOpacity to EndOpacity and optional blur. The shadow does not reflect. The reflection is computed on the rounded image, before the shadow is added.

---

## Tasks

### T1 Scaffold + contracts
- git init; add a .gitignore (bin/obj/.vs/.omo/drafts).
- Create the sln, Core, App and Harness projects, plus Directory.Build.props.
- Add the packages above and confirm they restore.
- Add all Core contract types listed above, with full public shapes. Stubs are allowed only for algorithm bodies, which T2 fills in.
- App gets:
  - DI bootstrapping
  - the manifest
  - the LocalizationService with both string dictionaries (seed the keys)
  - a `Log` class
  - a `--smoke` handler that resolves registered services and exits 0
  - a placeholder MainWindow that stays hidden (the app is tray-first)
- Generate an app icon (.ico) programmatically in the Harness, or create a simple one.
- Verify: build passes, and `ModernScreenShot.App.exe --smoke` gives exit code 0.
- Commit "chore: scaffold".

### T2 Core algorithms
- Implement the PixelBuffer ops, BoxBlur, ShadowEffect, ReflectionEffect, FrameEffect (rounded corners with anti-aliased edges, padding, solid/gradient background), EffectPipeline, Mosaic (pixelate + blur region), ScrollStitcher, FileNameTemplate, SettingsStore, UndoStack, HistoryStore and the annotation JSON (polymorphic via `JsonDerivedType`).
- Harness command `harness core` writes sample outputs to `tools/Harness/out/` and asserts:
  - the shadow canvas is larger than the source
  - the pixel at the shadow offset has alpha > 0
  - the far corner is transparent
  - reflection output height ≈ h*(1+Height)+Gap
  - stitching two overlapping synthetic frames gives the expected height
  - template sanitization works
  - settings and annotations survive a JSON round-trip
- Harness output is non-zero on failure.
- Commit.

### T3 Interop + capture
- Implement MonitorService, ScreenCapturer, WindowEnumerator, WindowCapturer, the active-window capture and the HotkeyService core.
- WPF interop helpers:
  - PixelBuffer ↔ BitmapSource (Bgra32)
  - physical ↔ DIP conversion per monitor
- Harness command `harness capture` checks:
  - the virtual-screen capture size equals the virtual-screen bounds
  - it lists windows with titles
  - it captures the foreground window, and the PNG it writes is non-empty
- Commit.

### T4 Region overlay
- Freeze the full virtual screen first, then show one overlay per monitor, each positioned in physical px.
- Selection behavior:
  - dragging selects a region
  - hovering highlights the window or child window under the cursor, and a click selects it
  - arrow keys nudge the selection by 1px (Shift = 10px)
  - resize handles
- Show the magnifier (8x zoom grid, cursor coordinates, HEX color). C copies the color.
- Show a size label.
- The toolbar offers Edit / Copy / Save / Pin / Cancel.
- Window pick mode uses the same overlay but snaps to windows only, and captures with WindowCapturer so it gets the true window content with transparent corners.
- Store the last region in settings.
- The overlay must exclude the app's own windows from snapping.
- Commit.

### T5 Editor
- EditorWindow uses the Fluent style. The canvas supports zoom (Ctrl+wheel, fit, 100%) and pan (space+drag or middle mouse).
- Implement every tool from the model, with a property panel for color palette + custom color, thickness, fill toggle, font size, opacity, and mosaic mode/strength.
- Mosaic and blur preview live: render the processed region from the base image using Core Mosaic.
- Spotlight dims everything outside its shapes (multiple spotlights union).
- Magnifier shows a zoomed source rect in a circle with a connector line.
- Select tool:
  - move/resize/delete
  - double-click text to edit
  - z-order front/back
- Crop is non-destructive (stored in the document) and applied at export.
- Undo/redo covers every mutation.
- Implement `RenderFlattened()`: base image + annotations + crop → PixelBuffer, rendered at 1:1 physical pixels via RenderTargetBitmap at 96 DPI mapping.
- Commit.

### T6 Effects
- An Effects side panel in the editor with:
  - preset dropdown (built-in + user presets), save/delete preset
  - shadow controls: enable, blur, spread, color (with alpha), angle dial or slider 0-360, distance, opacity
  - reflection controls: enable, height, start/end opacity, gap, blur
  - frame controls: corner radius, padding, background none/solid/gradient (2 colors + angle)
- A checkerboard shows through transparency.
- Live preview is debounced to 60ms and runs on a background Task over a downscaled flattened image (max 1200px). Export uses full resolution.
- Toggle "apply effects on export" and remember the last used settings.
- Add a simple reusable ColorPicker control (HSV square + hue slider + alpha + hex).
- Commit.

### T7 Output
- ClipboardService puts PNG + DIBV5 on the clipboard.
- ImageExporter handles PNG, JPG (quality setting), and WebP via Skia.
- Save dialog, plus quick save to the configured folder using the template.
- The editor toolbar gets Copy / Save / Save As / Pin / Open Folder, with toast feedback (WPF-UI Snackbar or a small custom toast).
- PinWindow as specified. Pinning also works directly from the overlay toolbar.
- HistoryStore integration: every confirmed capture is saved (the original plus doc.json). The HistoryWindow can reopen an item re-editable (loads doc.json).
- Commit.

### T8 Shell
- Tray-first startup, with a tray menu listing every capture mode, delay submenu (3/5/10s), history, settings, language switch and exit.
- Left-click the tray icon runs the Region capture.
- HotkeyService registers the hotkeys from settings; conflicts are toasted and shown in settings.
- SingleInstance, with `--capture region|fullscreen|all|active|window|delay|last|scroll` args forwarded from a second instance.
- The CountdownWindow is shown for delayed captures.
- SettingsWindow with every tab, a hotkey recorder control, and a startup-with-Windows toggle.
- Language switching applies live.
- A first-run balloon/toast explains the hotkeys.
- Delay applies to any mode (the delay mode prompts region after the countdown; the fullscreen/active variants follow the settings default).
- Commit.

### T9 Scrolling capture
- The user selects a region with the overlay, then capture starts.
- The app sends mouse wheel scrolls (SendInput, 3 notches, via WM_MOUSEWHEEL at the region center) every 350ms, captures the region each step, and feeds ScrollStitcher.
- It stops when two consecutive frames are identical, when height reaches 20000px, or when the user presses Esc or clicks a small floating "Stop" button.
- Manual fallback: if auto-scroll produces no change, keep capturing while the user scrolls manually, until Esc or Stop.
- The result goes to the editor.
- Commit.

### T10 Integration & polish
- Run `tools/check-i18n.ps1` and make sure it passes.
- `--smoke` must instantiate every window off-screen (overlay excluded) in both languages.
- Launch check: start the exe, wait 5s, confirm the process is alive, then kill it.
- Release publish: `dotnet publish -c Release -r win-x64 --self-contained false -o publish/`.
- Write README.md in Chinese covering features, hotkeys, build/run, and the settings path.
- Write KNOWN_ISSUES.md listing anything untested or deferred.
- Final commit.

## Acceptance (agent-verifiable)
- Release build has 0 errors.
- `--smoke` exits 0.
- `harness core` and `harness capture` exit 0.
- The i18n check passes.
- The process survives a 5s launch check.
- `publish/ModernScreenShot.App.exe` exists.
