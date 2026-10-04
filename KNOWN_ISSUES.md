# KNOWN ISSUES / 备忘
按任务顺序记录无人值守期间自行决定的事项与未验证点。用户手工测试时可对照检查。

## 已执行：翻译引擎换成 Hy-MT2 + llama.cpp（2026-10-04，替换 Argos）

上一条调研的结论已落地。**旧引擎（Argos Translate / OPUS-MT 77M int8 + 173 MB 便携 Python 引擎 + JSON bridge 子进程）已整体删除**，换成腾讯 **Hy-MT2-1.8B**（Apache-2.0）跑在 **llama.cpp**（MIT，纯 CPU）上。

- **集成方式：llama-server 子进程 + OpenAI 兼容 HTTP**，不是进程内 LLamaSharp。理由：**质量数据本来就是在 llama-server 上测的**，用同一路径能原样复现；而且聊天模板/special token 由 llama.cpp 自己套用，C# 侧完全不碰（手工拼 `<｜hy_User｜>` 这类 token 风险太高）。进程长驻、首个请求时启动。
- **删掉的东西**：`tools/argos_bridge.py`、`tools/build-argos-engine.ps1`、`Assets/argos/`（1361 个文件 / 171 MB）、`TranslationPackages.cs`（按语言对下载的 .argosmodel 逻辑）、`ARGOS_*` 环境变量、`--translate-install=from-to` 的按对安装。
- **新增**：`tools/build-llama-runtime.ps1`（取 llama.cpp win-cpu-x64 release，只留 `llama-server.exe` + 21 个 DLL，剔掉其它 CLI 的 `*-impl.dll`，42 MB）、`TranslationLanguages.cs`（36 种语言）、`TranslationModelDownloader.cs`（GGUF 断点续传下载）、重写的 `TranslationService.cs`。
- **包体积反而变小**：publish **447.9 MB / 1644 文件 → 318.9 MB / 306 文件**（−129 MB，文件数少 1338 个），因为 171 MB 的 Python 引擎换成 42 MB 原生运行时。按需下载的模型从 70 MB/方向 变成 1.08 GB（一次，36 语言双向）。
- **实测质量（同一段文字、同一个模型、走应用自身链路）**：
  | | Argos（旧） | Hy-MT2（新） |
  |---|---|---|
  | 习语 the deck is stacked against them | 「**甲板堆积在他们身上**」 | 「处于**极不利的境地**」✓ |
  | Press Enter…or Escape to cancel | 「或取消。」**漏掉 Escape** | 「按 Enter 确认选择，按 Escape 取消。」✓ |
  | …slow inflation **but also** increase unemployment | 「**并**增加失业率」转折丢失 | 「但也会增加失业率」✓ |
  | zh→en 他想了想，**还是把那份合同**放回了抽屉里 | 漏译「那份合同」 | "…still put **the contract** back…"✓ |
  | 对用户标准译文的 chrF | 31.06 | **47.11** |
- **延迟**（本机 24 逻辑核，`-t 12`）：生成 **~21.5 token/秒**；一句话 **0.7–1.0 秒**，用户那段 68 词长段落 **~4 秒**；首次翻译额外付一次模型加载（约 3–5 秒）。旧的 Argos 是 0.26 秒/句，所以**确实慢了 3–4 倍**——这是换质量的代价，已如实记入 README。
- **内存**：模型常驻约 2 GB。对一个托盘常驻工具太重，因此**加了闲置 5 分钟自动卸载引擎**，下次翻译再加载（`TranslationService.StopIfIdle`）。这条是自发加的，未在真机长时间验证。
- **模型下载实测**：ModelScope 源，1.08 GB 用时 **0.8 分钟**（约 24 MB/s）。HuggingFace CDN 同文件只有约 0.3 MB/s，故 ModelScope 为主源、HF 为备源。下载器**按已发布字节数校验**，支持 `.part` 断点续传（服务器不支持 Range 时自动从头开始）。
- **门禁**：build 0 警告 0 错误；i18n 382=382；`--smoke` 退出码 0；`harness core`（66 项断言，含 OCR 行重组与文本重排）与 `harness ocr` 均 ALL PASSED；`--translate-install` 退出码 0；`--translate-test` 双向退出码 0；`--render-translate` / `--render-settings`（新增 `MSS_RENDER_SCROLL=end` 以截到折叠下方）快照目检通过。
- **未验证 / 待真机**：① **叠加层工具条**渲染不了（无头环境限制同前），翻译按钮的接线与图标已在编辑器快照确认，但仍需真机点一次；② 闲置卸载逻辑、断点续传的中途中断-恢复、36 种语言中除中英外的其它语种质量均未测；③ 未在 8 GB 内存机器上验证（模型约 2 GB 常驻）；④ llama.cpp 在老旧 CPU 上会退化到 `ggml-cpu-x64` 档（更慢但可用），未实测。


## 翻译引擎选型调研：有没有比 Argos 更好的离线方案（2026-10-04，待用户决策）

**结论**：有，而且好很多——**腾讯 Hy-MT2（Apache-2.0，专为翻译训练，33 语种）**，用 **llama.cpp（MIT）** 跑 GGUF。1.8B / Q4_K_M 权重 1.08 GB，在本机 CPU 上实测**质量大幅优于 Argos**，同时**基础安装包还能变小**（llama.cpp 最小运行集 ~25 MB，可整体替换现在 171 MB 的 Python 引擎）。仍未改动代码，等用户拍板。

- **需求约束（承接上文）**：完全离线、Windows x64 + **纯 CPU**（不假设 GPU）、可嵌入 .NET WPF、体积/延迟可接受、开源可商用、中英双向。
- **实测（同一批句子、同一套指标；Argos 用的是本应用已调优的 beam 10）**：

  | 引擎 | 对用户标准译文 chrF↑ | 往返 chrF↑ | 单句热延迟 | 内存 | 模型体积 |
  |---|---|---|---|---|---|
  | Argos Translate（现状） | 31.06 | 71.66 | **0.26 s** | ~0.2 GB | 70 MB/方向 |
  | **Hy-MT2-1.8B Q4_K_M** | **47.11** | 69.83 | 0.72 s | ~2.1 GB | 1.08 GB（全 33 语种） |
  | Hy-MT2-7B Q4_K_M | 42.24 | **73.87** | 3.75 s | 6.46 GB | 4.41 GB |

  **两个指标不一致要如实说明**：chrF 是字符 n-gram 重合度，只对一个参考答案算分，换个同义说法就掉分；往返一致性则偏向直译。7B 的句子（「处于劣势」「本该轻松得手的交易」「虽然能抑制…但也会增加」）肉眼更贴标准译文却 chrF 更低，而 1.8B 在某些句子上用词更接近参考。**两者都远胜 Argos，这一点两个指标一致**。
- **肉眼可见的具体差距（en→zh，逐条对照）**：
  - 习语 "the deck is stacked against them"：Argos →「**甲板堆积在他们身上**」（不通）；Hy-MT2 →「自己的处境非常不利 / 形势对他们不利」✓
  - "Press Enter … or Escape to cancel."：Argos →「按 Enter 键确认选择, 或取消 。」**整句丢掉了 Escape**；Hy-MT2 →「按回车键确认选择，或按 ESC 键取消。」✓ 完整且本地化
  - "…slow inflation **but also** increase unemployment."：Argos →「…减缓通货膨胀**并**增加失业率」（**转折关系丢失**，意思反了）；Hy-MT2 →「虽然能抑制通货膨胀，**但也会**增加失业率」✓
  - zh→en「他想了想，**还是把那份合同**放回了抽屉里。」：Argos → "He thought about it and put it back in the drawer."（**漏译「那份合同」**）；Hy-MT2 → "…but still put the contract back…" ✓
  - 标点：Argos 混用半角；Hy-MT2 输出规整全角中文标点。
- **成本与收益**：基础包侧 llama.cpp 最小运行集 **~25 MB**（llama.dll 3.1 + ggml-base 0.8 + ggml-cpu-* 按 CPU 指令集选用），**替换掉现有 171 MB 的 CPython 引擎后基础包反而缩小约 145 MB**；代价是按需下载的模型从 70 MB/方向 变成 1.08 GB（一次性，全语种双向），内存 ~2 GB，CPU 延迟约为原来的 2.8 倍。按应用已有的「内置轻量 + 按需下载重型」分层模式（同 OCR 快速/精确两档），这套完全对得上。
- **已排除的方案及原因**：`opus-mt-tc-big-*-zh` **不存在**（tc-big 只覆盖欧洲语系，已逐一核对 HF）；**NLLB-200** 是 CC-BY-NC-**非商用**，且第三方母语者评测显示 en→zh 还不如 Opus-MT，600M 也要 2.5–3 GB；**NLLB-3.3B** 需 13–16 GB 且 en→zh 差；**MADLAD-400** Apache-2.0 但要 30–40 GB 内存，[另一个项目明确评估后拒绝](https://github.com/kizuna-ai-lab/sokuji/issues/384)（"usable, but worse than what we already have"）；**Tower-7B** 质量最好但 25 GB 内存、CPU 上单条 10 分钟；**Hunyuan-MT-7B / Chimera-7B**（WMT25 31 项第一）体积与 7B 同级，作为 7B 档备选。
- **集成路径（未实施）**：LLamaSharp 0.27（NuGet，147 万下载，MIT）+ llama.cpp(MIT) 原生库；.NET 侧直接进程内推理，**可顺带删掉 Python 引擎、bridge.py 与整套 ARGOS_* 环境变量**。前文所有翻译入口（叠加层/编辑器/设置/反向翻译/`--translate-test`）都收敛在 `TranslationFlow`，替换面很小。
- **证据**：`%TEMP%\llm-test\`（`hymt_greedy.json` / `hymt7b_greedy.json` / `side_by_side.json`）；模型 [tencent/Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)（ModelScope 镜像下载快 8 倍）；[中文母语者评测参考](https://whynothugo.nl/journal/2025/11/02/translation-models-between-english-and-chinese/)。
- **未验证**：只测了 en↔zh 与零星句子；未测长文/多段、未测 1.25bit(440 MB) 与 Q8 量化档、未在低配机器（8 GB 内存）上验证 1.8B 的可用性；llama.cpp 的 AVX 档位在老旧 CPU 上会退化到 ggml-cpu-x64（更慢但可用）。


## 翻译质量调参（2026-10-04，用户要求"微调参数，实在不行就算了"）

**结论先说**：**有可测量的小幅提升，但补不上与"标准译文"的差距**——那个差距是模型容量，不是参数。

- **可用的旋钮只有两个**（查上游源码确认）：`ARGOS_BEAM_SIZE` → `translate_batch(beam_size=max(num_hypotheses, ARGOS_BEAM_SIZE))`；`ARGOS_COMPUTE_TYPE` → `ctranslate2.Translator(compute_type=...)`。**`length_penalty=0.2` 与 `num_hypotheses=4` 是硬编码的**，不改上游源码就动不了（没改，保持与上游一致）。两者都在 import 时读取，所以由 bridge 在导入 argostranslate **之前** 设置。
- **测量方法（两套，互补）**：
  1. **对用户给的标准译文算 chrF**（单句，有参考答案）；
  2. **往返一致性**：en→zh→en 后再对原英文算 chrF（8 句多样语料，不需要参考答案）。用后者是为了**避免只对一句话调参**。
- **结果**：
  | 配置 | 用户那句 chrF↑ | 往返均值 chrF↑ | 往返 16 次翻译耗时 |
  |---|---|---|---|
  | beam 4（原默认） | 28.96 | 68.56 | 3.2 s |
  | **beam 10（已采用）** | 31.06 | **71.66** | 4.6 s |
  | beam 8 | 31.27 | 69.00 | 4.0 s |
  | beam 12 | 31.06 | 71.66 | 5.5 s |
  | beam 14 | 27.95 | 72.37 | 5.5 s |
  | beam 16 | 25.01 | — | — |
  | float32 | 24.52 | 64.14 | 8.2 s |
  | int8_float32 | 28.96 | — | — |
  逐句胜平负（vs beam 4）：beam 10 为 **2 胜 5 平 1 负**，beam 8 为 1 胜 5 平 2 负。
- **采用 beam 10**：往返均值比默认高 **+4.5%**、用户那句也更好，而 beam≥14 在用户那句上反而明显退化（过度搜索），float32 全面更差（还会把 "Jason and Lucia" 整段留成英文）——说明该模型是 int8 量化训练/发布的，反量化反而伤质量。代价约 0.29 s/句（默认 0.25 s），相对模型加载可忽略。
- **实际效果（用户那句，en→zh）**：调参前「…一直知道 甲板堆积在他们身上…犯罪阴谋中…」→ 调参后「…**一直都知道** 甲板堆积在他们身上…犯罪阴谋**之中**…」——"都知道"、"之中" 与标准译文更接近，但 **"the deck is stacked against them" 仍被直译成"甲板堆积在他们身上"**（标准译文的"处于不利地位"），"an easy score" 仍译成"容易的分数"。
- **诚实结论**：调参只能带来这一量级的改善。**余下的差距属模型容量**——Argos 的 en↔zh 是 ~77M 参数、78 MB 的 OPUS-MT int8 模型，而标准译文大概率来自大得多的商用模型。要接近它需要换模型（更重的依赖与体积），已超出"微调参数"的范围，故按用户"实在不行就算了"在此收手。
- **顺带修复**：桥接进程的 stderr（引擎横幅、解码参数、告警）此前**从未接入应用日志**——现已路由到 `app.log` 的 `translate[engine]:` 前缀，日志里可直接看到 `decoding: beam_size=10 compute_type=auto ...`，这次调参就是靠它确认参数真正生效的。同时修掉一个噪声：关机应答也带 id=0，原逻辑会把它误报成"engine reported a startup problem"。
- **未做**：`length_penalty` / `num_hypotheses` 需改上游源码，未改；未在低配机器上测 beam 10 的耗时上限。


## 修复：翻译逐词输出、无法成句（2026-10-04，用户报告）

**症状**：用户截图（GTA6 海报风大字排版）后点翻译，原文框里每个单词各占一行，译文是「副总统 / 更多 / 超过 / 永远 / 页1 …」这样的孤立单词堆叠。

- **根因（两层，缺一不可）**：
  1. **PP-OCRv5 检测的是"框"，不是"行"**。字距/词距稍大（海报、标题、任何带字间距的排版）时它会**一个单词吐一个框**；而 `OcrService.ExtractLines` 把每个框当成一行、`Text` 用换行拼接 → 文本变成每词一行。实测：把一段 4 行英文渲染成图，OCR 返回 **36 个"行"**（修复后 4 行）。
  2. **Argos 把每个换行都当作一个段落**：`split_into_paragraphs` 就是 `input_text.split("\n")`，逐个翻译。于是 36 行 = 36 次独立翻译 = 逐词垃圾。来源已核对上游源码（`argostranslate/translate.py`）。
- **修复 1（OCR 层，`Core/Ocr/OcrTextLayout.cs`）**：`OcrLine` 新增 `Bounds`（在 `ExtractLines` 里从 `TextBlock.BoxPoints` 取包围盒），新增 `OcrTextLayout.Assemble`：**垂直重叠 ≥ 50% 的碎片归为同一视觉行**，行内按 X 排序，拉丁碎片用空格连接、**CJK 碎片直接相连**（两个汉字之间插空格是错的）。无几何信息或只有单个碎片时原样返回——**整行检测的常规情况完全不受影响**（harness 有断言）。OCR 结果窗与译文现在都按真实行显示。
- **修复 2（翻译层，`Core/Translation/TextReflow.cs`）**：翻译前把硬换行的屏幕文本**还原成段落**——空行 / 句末标点（`.!?;。！？；…`，含尾随引号括号）/ 列表标记（•·-*、`1.`、`a)`）才另起段；行尾连字符按断词处理（`exam-`+`ple` → `example`）；CJK 边界不加空格。**视觉换行对翻译没有意义**，故不保留。`TranslationFlow.TranslateAsync` 统一走这条路，捕捉/编辑器/反向翻译/`--translate-test` 四个入口一致。
- **差分验证**：修复前 `harness ocr` 对同一张 4 行海报图返回 36 行；修复后 4 行。端到端（图 → OCR → 翻译）：输入 3 行硬换行英文，`source=` 显示已合并的**单个段落**，`result=` 为完整中文句子（不再是单词堆叠）。
- **回归门禁**：harness 新增 **13 条断言**（`OcrLayoutChecks` / `TextReflowChecks`：同行合并、跨行不合并、CJK 不加空格、无几何直通、空行分段、句末分段、连字符重连、列表分段、空输入安全），`harness core` 与 `harness ocr` 均 ALL PASSED；build 0/0；i18n 385=385；`--smoke` 退出码 0。
- **已知取舍**：① 同一视觉行上的**多列排版**（如"标签 … 值"）现在会被合并成一行并用空格分隔——对阅读与翻译通常更合理，但列结构信息丢失；② 不以标点结尾的**短标题**会与紧随的正文合成一段（启发式未做行长判断，宁可少切不可多切——多切正是本次 bug 的来源）。以上两点均未做真机长尾测试。


## 翻译功能（Argos Translate，2026-10-03）

**需求**：在 OCR 按钮右边加翻译功能，采用 Argos Translate 开源项目（从 GitHub 取），UI 仿照 OCR。

- **三个入口**：截图叠加层工具条 OCR 右边新增**翻译**按钮（`OverlayIntent.Translate` → `AfterCaptureAction.TranslateText`）；编辑器底栏「翻译」（在「文字识别」右边）；**设置 → 截图后动作** 下拉新增「翻译」（枚举末尾追加，索引与下拉项 1:1，旧配置不受影响）。三者都走 `RunTranslateCapture` / `EditorWindow.RunTranslate`：先 OCR 取字，再翻译。
- **引擎形态（关键决策）**：Argos Translate 是 Python 库（CTranslate2 + SentencePiece），.NET 无可用绑定（NuGet 上只有 Whisper 专用包装）。故**打包一个便携版 CPython 3.14.6 + 依赖**放在程序目录 `translate\`，由 `tools\build-argos-engine.ps1` 生成（约 173 MB / 1489 文件，**已加入 .gitignore**，不入库）；.NET 侧用**长驻子进程 + JSON 行协议**驱动（`tools/argos_bridge.py` ↔ `Core/Translation/TranslationService.cs`），进程常驻以保住热态（冷加载模型数十秒，热态 0.1–0.5 秒）。
- **不装 spacy / stanza**：`argostranslate` 的 `pip` 依赖里 `spacy`、`stanza` 很重（stanza 会拖进 PyTorch ~1 GB），但 GitHub master 的 `sbd.py` 把两者**都包在 try/except ImportError 里**，无 stanza 时自动回退 MiniSBD。**注意 PyPI 上 1.11.0 的 wheel 与 master 不同**（wheel 里 `import stanza` 未加保护，直接 ImportError 崩溃）——因此构建脚本**从 GitHub codeload 取 master 源码**而非 pip 安装 argostranslate，这也正好符合"自己去 github pull"的要求。
- **语言包按需下载**：`.argosmodel`（zip，内含 `metadata.json` + CT2 模型 + sentencepiece 模型）下载到 `%LOCALAPPDATA%\Modern-ScreenShot\translate\packages\`。C# 侧自行解压（等价于 argostranslate 的 `install_from_path`），下载先落 `.part`、**解压前校验 zip 可读且 metadata 的 from/to 与请求一致**——截断文件或错误页不可能被当成语言包装上。目录来自 argospm-index（带本地缓存，离线时回退内置 en↔zh 两条）。
- **CDN 403 坑**：`argos-net.com` 的 CDN 对 **Python 默认 UA（`Python-urllib/x.y`）返回 403**。C# 下载器显式带 `ModernScreenShot/1.0`（实测放行），bridge 里若用 urllib 也需自定 UA。
- **MiniSBD 句子模型已内置**：否则首次翻译会去 GitHub 下载每语言一个小 ONNX 模型，使"离线"功能首次仍需联网。构建脚本内置 `en.onnx` / `zh-hans.onnx`（共 0.77 MB），bridge 在导入后把它们播种到 Argos 缓存目录（实测清空缓存后能自动补齐）。
- **自动识别原文语言**（设置项，默认开）：按识别文字的书写系统判断（`Core/Translation/LanguageDetector`：CJK 字符占比）——中文截图自动 zh→en、英文截图自动 en→zh，检测方向未装语言包时回退到设置方向，并用 `RememberLastPair` 记住实际用过的方向。
- **结果窗口** `Translation/TranslationResultWindow`：与 `OcrResultWindow` 同构（相同 `AppTitleBar`、相同只读文本框与底部状态栏/复制按钮语义），上方原文、下方译文，另有 `⇄ 反向翻译`（同一段文字反向重译）。**踩坑**：`Style="{StaticResource RowSubtitle}"` 是 SettingsWindow 的**窗口级**资源，独立窗口取不到，会让 XAML 加载抛 `Cannot find resource named 'RowSubtitle'`——已改为内联 FontSize/Foreground。该缺陷正是被 `--smoke` 拦下的（smoke 会实例化 TranslationResultWindow）。
- **客观门禁（全部通过）**：build 0 警告 0 错误；i18n 385=385；`--smoke` 退出码 0（含新窗口实例化 + 引擎存在性日志）；`harness ocr` 全 PASS（OCR 无回归）；`--translate-install=en-zh` / `=zh-en` 退出码 0（走真实 CDN 下载，各约 70 MB）；`--translate-test` 退出码 0，en→zh 与 zh→en 双向均正确，热态 442–624 ms；`--render-translate` 生成的窗口快照经目检（`tools/verify_out/ui/translate.png`）；编辑器快照确认「翻译」按钮出现在「文字识别」右边（`editor.png`）。
- **新增自检开关**：`--translate-test[=文本]`（端到端翻译，退出码 0 为通过，结果写 `%LOCALAPPDATA%\Modern-ScreenShot\logs\translate-test.txt`）、`--translate-install[=from-to]`（真实下载并安装语言包）、`--render-translate`（渲染结果窗）。三者都在 shell 启动前执行，不依赖托盘。
- **未验证 / 待真机手测**：
  - **叠加层工具条**渲染不了（无头环境，与既有 overlay 验证限制相同）。翻译按钮的图标与文案已在编辑器快照中确认渲染正确，工具条一行是紧挨 OCR 按钮的同构调用，但**"按钮确实出现在 OCR 右边且点击后走通流程"仍需真机点一次**。
  - 首次翻译冷启动耗时较长（本机实测首次 42 s、之后同进程 442 ms）——主要是一次性的模型加载/首次落盘，非每次开销；**未在低配机器上测过上限**。
  - 语言包下载失败/中断、设置页删除语言包后的引擎重启、自动识别在**中英混排**下的取舍（当前阈值 CJK×2 ≥ 拉丁）只做了代码审查与上述用例，未做长尾测试。
- **翻译质量是模型本身的限制，不是集成缺陷**：zh→en 对「截图工具支持区域截图、窗口截图和滚动长截图。」会得到 "The amplogram tool supports regional amplograms…"（「截图」被译成 amplogram）。**差分实验证明**：绕开本项目的 bridge 与 .NET、直接用上游 argostranslate 跑同一句，输出**逐字相同**；而单词「截图」→ "Screenshot"、「截图工具」→ "Screenshot Tool" 均正确，属 OPUS-MT 模型在长句上下文下的固有问题，本项目不做改写（保持与上游一致）。


## 叠加层操作提示面板（F1）+ 精确操作（Snipaste 风格，2026-10-01，未提交）

- **已实现**：帮助卡片**会话开始即在激活显示器左下角自动显示**（用户反馈"Snipaste 直接放左下角了你还按 F1"后从"仅 F1 呼出、底部居中"改为自动显示 + 左下角；F1 仍可开关；首次点击任意处收起；工具栏出现时卡片自动上移避让；Esc **不再**关卡片、始终直接取消截图——自动显示后"Esc 先关面板"会变成按两次才能取消的陷阱）；WASD 移动真实光标 1px（SetCursorPos + 走 OnMove 同一路径，悬停/拖拽/放大镜同步跟随）；Tab 本会话内切换 窗口/元素 检测（顶部 1.5s 提示）；1/2 选择 上层/下层 界面元素（复用 WindowEnumerator DWM 边界管线）；Ctrl+A 选区=当前整屏；Shift+R 使用上次截图区域（**R 已被矩形工具占用，只绑 Shift+R**）；F5 重新截取冻结帧；` / ! 显示/隐藏 冻结帧中的鼠标指针（按当前「捕获光标」设置取反）。
- **实现要点**：F5/`/! 刷新前**先隐藏全部叠加层窗口再等 140ms**（与黑帧重读同策略）——否则重截会把暗色遮罩/选框/工具栏烙进新冻结帧；会话冻结帧可换（Frozen/FrozenSource private set），渲染器 `Reslice` 重建每屏切片并清放大镜缓存；马赛克裁剪缓存失效重算。帮助卡片行按会话能力过滤（放大镜关→无 C 行；无上次区域→无 Shift+R 行；诊断场景未接刷新委托→无 F5/`/! 行），**卡片永远只列出真实绑定的键**；滚动截图（自动确认会话）不自动显示卡片。WASD 中 **A 在已选区且未拖拽时仍是箭头工具**（保留既有键位，卡片内工具行如实列出 A=箭头）。
- **键位冲突核查**（改前逐一验证 `OverlaySession.OnKey` + `OverlayWindow.OnPreviewKeyDown`）：R=矩形工具（选区内）→ 只绑 Shift+R；A=箭头工具（选区内）→ A 仅在框选前/拖拽中移动指针；W/S/D、Tab、1、2、F5、`、!、Ctrl+A 改前均未绑定。Ctrl+Shift+R（全局「上次区域」热键）不受影响（Shift+R 处理器精确校验修饰键==Shift）。
- **推迟未做**：`,` / `.` 浏览历史截图作为冻结背景——需要历史图像加载 + 信箱式缩放 + 坐标重映射，超出本轮范围。
- **2026-10-02 补充修复（用户报「鼠标移到第二屏 WCAN 无法吸附到整个屏幕」）**：活窗口探针实证第二屏 64 采样点中 28 个为裸桌面（仅 图片查看器 620×881 + QQ 1315×881 两窗，未铺满），悬停裸桌面时 HitTest 返回 null → 无高亮、点击无反应；根因是 2026-10-01「HitTest 移除桌面回退」把桌面从吸附目标中**整个**删除（当时"与 Snipaste 语义一致"的判断有误——Snipaste 悬停桌面会高亮整屏、单击截整屏）。现按 Snipaste 语义恢复：**悬停裸桌面=高亮整个显示器（带尺寸标签）、单击=选中整屏**（普通区域捕获路径，非 PrintWindow；确认时 FindByBounds 排除桌面故不会误走 mac 阴影窗口路径，若该屏恰有等大最大化窗口则与"点击吸附最大化窗口"行为一致）；拖框画自定义区域完全不受影响（2026-10-01 用户抱怨的"一点即全屏"场景现在有前置高亮预告，且拖拽仍是区域）。同批修复：①**Ctrl+A 用光标所在显示器**（原实现用接收键盘焦点的窗口的显示器——焦点不随鼠标跨屏移动，鼠标在第二屏按 Ctrl+A 会选中第一屏）；②Tab 的"检测窗口"模式现在真的是窗口级吸附（HitTest includeChildren:false），此前该模式实际是"完全不检测"却标注"检测窗口"；③Shift+R/悬停解析统一走 MonitorBoundsAt 帮助方法。渲染器脏区签名纳入 HoverMonitorBounds（跨屏离开时旧高亮不残留）。
- **未验证（无头环境渲染不了 overlay）**：所有按键交互（WASD 拖拽跟随、F5 刷新闪烁观感、帮助卡片布局与遮挡、1/2 层级导航、`/!` 光标显隐）仅经代码审查 + build 0/0 + smoke + harness 验证，**待真机手测**。

## 自定义标题栏 Win11 原生对齐重构（2026-10-01，Wave 3 收尾，未提交）

把共享 `Controls/AppTitleBar`（WPF `WindowChrome`）从 36 DIP / 40×28 手绘按钮重构为 Windows 11 原生规格，并补齐 **Snap Layouts 贴靠浮窗**。本节取代下文「统一自定义标题栏」各节（2026-09-29）里的 36 DIP 描述（历史保留）。证据包在 `.omo/evidence/`。

- **尺寸**：标题栏高 **32 DIP**（本机原生实机实测 **31 DIP**，±1 容差）；标题钮 **46×32**（原生 45×31）；字形 **10 DIP**。全部来自单点常量 `TitleBarMetrics`（`CaptionHeight`/`ButtonWidth`/`ButtonHeight`/`GlyphSize`），6 窗口与探针共用同一来源。原生对照见 `.omo/evidence/native-caption-spec.md`（Win11 25H2 build 26200，OS 自绘标题栏 + 记事本交叉参考）。
- **状态机（残留根因修法）**：新 `Controls/CaptionButton` 用 `ControlTemplate` 触发器驱动 rest / hover / pressed / disabled / **非激活** 五态；非激活由窗口 `Activated`/`Deactivated` 切换（非激活标题用 `TextFillColorTertiary`；pressed 用 `SubtleFillColorTertiary`）。**不再用 ad-hoc `MouseEnter`/`MouseLeave` 直接改 `Background`**（旧实现的残留根因）。
- **分隔线**：去掉底部 1px 分隔线（原生标题栏无）。
- **最大化外扩修复**：`AppTitleBar.MaximizeTrim=1`——`WindowChrome` 下最大化窗口会被 Windows 按 `GlassFrameThickness=0` 的整窗客户端**每侧外扩约 11px**（超出显示器右缘，侧屏时落到邻屏）。`WM_GETMINMAXINFO` 对 `ptMaxSize` 减、`ptMaxPosition` 加 `MaximizeTrim` 抵消。实测 `clientRight == monitor right`，150%（144 DPI）与 100%（96 DPI）两档均在 0–1px 内（`.omo/evidence/titlebar-maxfix.txt`）。
- **Snap Layouts（`Interop/TitleBarSnapHook`）**：`WM_NCHITTEST` 在最大化钮矩形内返回 `HTMAXBUTTON(9)` + `handled=true`，短路 `WindowChrome` 自身命中测试。做法是在 `WindowChrome.SetWindowChrome` **之后**于 `SourceInitialized` 装 `HwndSource` hook——`HwndSource.PublicHooksFilterMessage` **倒序调用**（后添加先执行），已实证（`.omo/evidence/hook-order-proof.md`）。**门控**：仅当点落在最大化钮活动矩形内 **且** 窗口为前台 **且** 具备 `WS_MAXIMIZEBOX` **且** 按钮可见；其余点原样透传（空白 caption 仍 `HTCAPTION`、resize 边仍 `HTLEFT/…`、min/close 仍走 WPF 客户端按钮）。
- **NC 交互语义**：`WM_NCLBUTTONDOWN(HTMAXBUTTON)` 只置 pressed + `handled`（抑制默认）；**最大化/还原在 `WM_NCLBUTTONUP` 切换**（原生按钮语义，与 PSAppDeployToolkit `FluenceWindow` 先例一致）。**与任务原文「在 DOWN 切换」的偏差已记录**：若在 DOWN 切换，合成 pressed 像素探针按下瞬间窗口即被最大化，会打坏 state 场景，故改 release-toggle，state 像素矩阵保持全绿。`WM_NCMOUSEMOVE` 带真实位移守卫驱动 hover（防 snap 浮窗轮询的同像素重放复活已清除的 hover）；`WM_NCMOUSELEAVE` / `WM_NCACTIVATE(0)` 清除 hover+pressed。`CaptionButton.IsNcMode` 时关掉指针式 hover（NC 面上 WPF `IsMouseOver` 恒真会留残影），最大化钮填充单由 `IsNcHover`/`IsNcPressed` 驱动；`AppTitleBar.Attach` 公开签名不变，`_maxButton.Click` 路径保留（键盘/自动化 Invoke 仍可切换）。
- **降级**：Windows 10（build < 22000）`Install()` 直接返回，不装 hook，最大化走原 `WindowChrome` 路径不受影响；本机为 Win11-only，降级由分支守卫覆盖、未在真 Win10 执行。
- **HotkeyEditDialog**：修复 `outer` Grid 无 `RowDefinitions` 却 `Grid.SetRow(rows,1)` 导致标题栏被竖向居中、与内容重叠（改为 `Auto` + `*` 两行）；**20 DIP 透明阴影让位保留**（自绘卡片阴影设计，用户 2026-10-01 拍板）。圆角 radius 7 clip 完整包含 32 DIP 关闭钮（`.omo/evidence/hotkey-dialog-layout.txt`，520×294 两轮布局稳定、无增长循环）。
- **标题字体**：12 DIP Regular `"Segoe UI Variable Text, Segoe UI"`（原生测量只量了几何/颜色，未量字重——字号沿用原控件，字重取 Regular）。
- **语言热切刷新**：语言切换时刷新标题文字与三钮 `AutomationProperties.Name`（`LocalizationService.LanguageChanged` 事件），修复旧版「运行时语言切换不刷新」限制（本文 §「自定义标题栏 5 轮审查」末条的已知低价值限制）。**注**：由代码 `Text=` 直接设的标题为 best-effort——动态资源路径自动跟随，代码设值路径以刷新回调兜底。
- **探针用法**：
  - `tools/titlebar_probe.ps1`：`-Scenario geometry|state|hit|residue|all`（默认 all）——几何（bar 32 DIP、内容顶偏移 32、关闭钮右缘对齐可见物理边缘）、状态像素（hover/pressed/inactive/close-hover 色与对比度）、真实 HWND `WM_NCHITTEST` 命中、残留（失焦/模态/`WM_NCMOUSELEAVE`）。全 PASS = exit 0。
  - `tools/titlebar_native_measure.ps1`：本机原生标题栏实机对照测量（写 `tools/verify_out/titlebar/native_spec.json`）。
  - `tools/hook_order_probe.ps1`：HwndSource hook 倒序 + `WM_NCHITTEST` 基线实证。
  - fixtures 已从 `x36` 更新为 `x32`（`phase2_ui_probe.ps1` 断言 + `verify_*.tree.txt` 全部重生成，见 `.omo/evidence/phase2-after.txt`）。
- **已知限制**：
  - **OOBE 与快捷键编辑对话框不可贴靠**（`NoResize`，无 `WS_MAXIMIZEBOX`）：不装 snap 门控、不返回 `HTMAXBUTTON`（几何/状态/残留仍覆盖）。
  - `tools/verify_theme.ps1` 的 **A3 暗色浅色补丁失败为既有问题，本轮未碰**（改前后同为 Dark/System 各 4 块，无新增回归；`.omo/evidence/verify-theme-after.txt`）。
  - fixtures 里仍存 **2 处 `x36` 子串**，均为无关的 OCR 光标框 `532x365`（含子串 `x36`）；属真实渲染输出，不伪造/删除（`.omo/evidence/phase2-after.txt`）。
- **门禁**：build 0/0、smoke 0、harness ALL PASSED、i18n 齐平、`titlebar_probe.ps1` ALL PASS、`phase2_ui_probe.ps1` ALL PASS（`x32` 断言生效）、`verify_theme.ps1` A1/A2 PASS（A3 既有失败除外）。
- **待真机确认**：hover/pressed 手感与 snap 贴靠浮窗观感、暗色关闭钮 hover 深红可读性、多屏最大化钉屏与边缘（本环境无 visual-judge / 无图片输入，以上为真机视觉项）。

## Goal 模式收尾：死设置修复 + 捕获失败可见化（2026-10-01，未提交）

阶段 1 报告中「待定夺」的两项，按「代码对齐 UI 承诺」原则落地：

- **「截图后动作」死设置（原 C10）已修**：overlay 确认手势（Enter/双击/点击吸附确认/窗口点选）此前硬编码 `OverlayIntent.Edit`，导致 `AfterRegionCapture` 设置（复制/保存/贴图/浮动缩略图/文字识别）对区域和窗口截图永远不生效。新增 `OverlayIntent.Default`（确认手势=无显式选择），`MapIntent(Default)` 返回 null 回退到用户配置；工具栏五个按钮（复制/保存/贴图/编辑/文字识别）仍是显式意图不受影响。**连带修**：`ShowToolbar` 落到分发 `default:` 分支会误执行「复制到剪贴板」——现显式路由到编辑器（overlay 工具栏已显示并被无显式选择的确认关闭=标准编辑结果；直出模式下编辑器即后续界面）。默认值 `ShowToolbar` 的用户行为不变（确认→编辑器）；只有改过设置的用户会看到 Enter 现在执行其配置的动作。滚动截图的自动确认 overlay 只读 Region，不受影响。
- **捕获失败可见化**：`CaptureService.Capture` 原先吞掉一切异常返回 null，真实失败（GDI/BitBlt/显示器错误）与用户按 Esc 不可区分、只有日志。移除内层 catch——null=用户取消（静默），异常传播到 `RunCaptureCore` 的 catch（原有「截图失败」toast + Log.Error）。确认过唯一调用方就是 RunCaptureCore；overlay `Show()` 自带窗口级失败守卫（EndSession+null）。
- **门禁**：build 0/0、smoke 0、harness ALL PASSED、i18n 317=317、`--probe-oobe` PASS、phase2_ui_probe ALL PASS。
- **真机自测**：①设置→输出→截图后动作选「复制到剪贴板」→区域截图按 Enter 应直接复制（不再开编辑器）；工具栏按钮行为不变；②选「浮动缩略图」同验证；③其余场景（全屏等）默认行为不变。

## Goal 模式阶段 3：观感提升（2026-10-01，未提交）

- **文字工具输入框底色**（overlay 与编辑器两处）：原为白色 75% 半透明面板——用户选白色描边时文字在白底上完全不可见。改为共享的透明棋盘格刷（`AnnotationRenderer.CheckerboardBrush`，浅灰格 #E2E2E6/#C2C2C8）：任何描边色（白/黑/彩）都可读，且与画布「透明表面」语义一致（画布、效果预览、输入框同一视觉语言）。文字提交坐标逻辑不变。
- **判定说明**：本项目已经历多轮系统性观感工程（UiMotion 全局动效令牌 13 动画点、设置页导航+卡片重构、统一自定义标题栏、OOBE 选框动画、历史卡片几何统一、色板弹层/贴图 Snipaste 化），本轮阶段 3 按小步原则只做上述一致性补齐；更大的重设计（间距/字号/圆角体系重排）会与并行进行中的 OCR 会话冲突且无真机视觉反馈，未动。
- **门禁**：build 0/0、smoke 0、harness ALL PASSED、i18n 317=317（含并行 OCR 会话新增键）、`--probe-oobe` PASS、phase2_ui_probe ALL PASS、verify_theme 三主题 PASS。
- **待真机**：overlay/编辑器文字输入框在任意描边色下的可读性。

## 离线 OCR 文字识别（用户指定「速度快、精度高、能识别标点、能选精度」，2026-10-01，未提交）

- **选型**（Github/NuGet 实测调研后定）：**RapidOcrNet 4.2.0**（PP-OCRv5 ONNX，ONNX Runtime + SkiaSharp）——与项目既有 SkiaSharp 3.119.4 兼容、不引入 OpenCV/PaddleInference 原生库；随包带 v5 检测 + 方向分类 + latin 识别模型。中文识别模型与字典（16.6 MB + 74 KB，Apache-2.0，RapidOCR/ModelScope `v3.9.2`）已 vendor 进仓库 `src/ModernScreenShot.Core/Assets/ocr/`，构建时落到输出目录 `models/v5/`。**注意：RapidOcrNet 的模型复制是包内 build targets，不随 ProjectReference 传递**——App/Harness 各自直接引用了该包，否则发布产物会缺模型。落选：Sdcb.PaddleOCR（+OpenCvSharp 38 MB、mkl 原生解压 278 MB）、PaddleOCRSharp（原生解压 314 MB、核心 DLL 闭源）、Tesseract（中文与标点弱）、Windows.Media.Ocr（要改 TFM 且中文标点不可靠、无精度档）。
- **两档精度**：快速 = 内置 mobile 模型（热态 **~57–66 ms**/1080p 级截图）；精确 = PP-OCRv5 server det+rec（164.7 MB 按需下载到 `%LOCALAPPDATA%\Modern-ScreenShot\models\ocr\`，热态 ~0.43 s；未下载时自动回退快速档，结果窗会注明）。下载器按字节数校验、先写 `.part` 再改名，错误页/截断不会伪装成模型；已实测下载 6.9 s 完成并识别通过。
- **集成**：Core `OcrService`（引擎懒加载——托盘闲置不占内存、按档位重建引擎、lock 串行化、透明窗口图先合成到白底再识别）；App：叠加层工具条 OCR 按钮（框选→识别→自动复制 + 结果窗）、编辑器底栏「文字识别」按钮（识别当前成品图，结果窗归属编辑器）、设置「截图后动作」新增「文字识别」（枚举尾部追加，ComboBox 索引保持稳定）、设置页 OCR 卡片（档位 / 精确模型下载+进度 / 识别后自动复制）、独立结果窗（只读文本可选中、复制有 1.6 s 反馈、Esc 关闭）。i18n +25 键。
- **关键性能发现**：ONNX Runtime 默认线程池（=逻辑核数）在 20+ 核机器上对这些小模型**慢约 10 倍**（warm ~670 ms vs 固定 4–6 线程 ~60 ms；harness 里做了 1/2/4/6/8/0 线程扫档实验）。生产默认 `Clamp(ProcessorCount/2, 2, 6)`。
- **验证**：harness 新增 `ocr`（合成「中文+全角标点+英文数字」图 → 断言识别出「你好，世界！」「Hello」「2026」；传图片路径可再识别真实截图并输出 `out/ocr-file.txt`，4480×1080 实测 ~0.39 s）与 `ocr-download`；`--render-ocr` 诊断渲染结果窗（树转储含 CopyButton/Close/StatusText/TextBox 与 1 行文本）；smoke 增加 OCR 结果窗实例化 + 模型存在性硬断言（缺模型即失败）；Debug 输出与 `publish/` 均确认含 `models/v5` 六件模型与 onnxruntime.dll；i18n 317=317。
- **已知限制**：①PP-OCRv5 字典不含 `‘`（U+2018）；中英混排里的 ASCII 逗号可能被识别为全角「，」（模型偏置，未做启发式后处理）；②首张识别含引擎加载 ~0.4–0.5 s（之后热态）；③精确档引擎驻留内存较重（切换档位会重建并释放旧引擎）；④**叠加层 OCR 按钮的视觉/交互未经真机验证**（无头环境渲染不了 overlay；它复用与既有工具按钮完全相同的元素图标按钮路径）；⑤设置里的「截图后动作 → 文字识别」对**全屏/多屏/当前窗口/上次区域/滚动**生效；区域/窗口截图沿用既有已知问题①（overlay 恒带 Intent，默认动作不可达）——区域场景请直接用叠加层 OCR 按钮。
- **真机自测建议**：框选一段中英混合文字 → 点叠加层 OCR 图标，应在剪贴板拿到文字并弹出结果窗；编辑器里点「文字识别」；设置 → 截图 → OCR 切换到「精确」并下载模型后重复上面流程对比识别质量。
- **用户报告「OCR 窗口右上角按钮显示有问题」的排查（2026-10-01，两轮）**：用户两张截图（首轮 VS F5 调试；次轮 Snipaste 直截）右上角都有两个「浅底 #F0F0F0 + 粗黑字形」方块，且我们的白色 `□`/`✕` 字形不可见。逐像素分析（`user_ocr_buttons_10x.png` / `verify_user2.png`）：那是一块 ~52px 宽、被 2px 分隔线分成两格的**浅色胶囊**，右缘离窗口边还有空隙——与应用标题栏按钮（40×28 DIP、相邻紧贴右缘、白色细线）几何与画法全对不上；次轮截图里那张「窗口」仅 135×111px，标题条占比 31%（应用窗口为 7.7%），比例对不上且 135px 宽低于 MinWidth=380，判定它**不是 OCR 窗口本体**（钉住的局部截图/缩略图一类）。**复现矩阵（全部正常）**：`--render-ocr` RTB、`--show-ocr` 屏拍、`--render-ocr` 探针 + 外部进程屏拍（PerMonitorV2 DPI 感知、SetWindowPos 置顶）的 plain/悬停最大化/悬停关闭三态——`— □ ✕` 与 hover 高亮全部正确（`verify_rf/w_*_cap.png`）。应用 18:10–18:11 的真实 OCR 流程日志（热键→框选→OCR 按钮→识别→复制）全部正常。综合判定仍是 **VS 调试叠加 UI**（XAML in-app toolbar / 热重载徽章盖在右上角；两张截图来自同一调试会话，Snipaste 只是截屏工具换了）。**请用户做一次无 VS 的对照实验**：完全关闭 VS → 直接运行 `publish\ModernScreenShot.App.exe --show-ocr` → 截整屏（不要裁剪）发来；若仍异常按应用缺陷深挖，若消失则在 VS 工具 → 选项 → 调试 → 常规取消「为 XAML 启用 UI 调试工具」。调试期注意：该叠加层还会挡住右上角的点击。

## Goal 模式阶段 1：代码全面体检（2026-10-01，未提交）

4 个只读审查子代理串行覆盖全部 66 个源文件（编辑器 / Overlay+Capture / Output+Shell / Settings+Core），合计 **P1×2、P2×8、P3×29**；本轮修复 P1×2、P2×7、P3×17，全部门禁绿（build 0/0、harness ALL PASSED【新增 12 条断言】、smoke 0、i18n 292=292、`--probe-oobe` PASS、`--render-oobe` 退 0）。

- **P1 捕获光标从未生效**：`ScreenCapturer.Capture` 先 `ToPixelBuffer` 提取像素、后 `DrawCursor` 画进即将销毁的 DIB——“捕获光标”设置在全屏/所有显示器/上次区域三类模式下静默无效。已改为画光标后再提取；重读日志保持画光标前采样（语义不变）。
- **P1 文字工具多屏幻影文本框**：`TextEditRequested` 扇出给每个显示器的 overlay 窗口，双屏时第二个窗口在钳制后的角落创建幻影 TextBox 并抢走键盘焦点，文字提交到错误（可能裁掉）的位置。已加显示器归属守卫（点击点不在本屏即返回）。
- **P2 编辑器 undo 链污染**：滑杆/裁剪草稿的 `_pendingSnapshot` 活跃期间（`_mode==Idle`）Ctrl+Z 可执行，快照残留下次提交时入栈 → 撤销链跳回刚撤销的状态。`DoUndo/DoRedo` 现在拒绝在快照未决时执行。
- **P2 损坏 doc.json null 洞**：`LoadDocument` 只防了 0×0 尺寸；`"effects":null`/`"items":null`/null 条目会让预览定时器 60ms 抛 NRE（全局 handler 吞掉=永不刷新的预览+日志刷屏）或编辑器静默打不开。现在归一化 Items/Effects/嵌套 Options，镜像 SettingsStore.Normalize。
- **P2 overlay 工具栏吞掉拖拽释放**：Move/Resize 拖拽自然结束在可见工具栏上时，`OnPreviewLeftButtonUp` 的 `IsOverToolbar` 早退吞掉释放 → 选区粘住光标。现在持有鼠标捕获时必先完成拖拽。
- **P2 马赛克选区拖动每帧全量重算**：`MoveSelectionTo` 直接 `RecomputeMosaic()`（全 crop 复制+像素化+BitmapSource.Create≈55MB/帧）；改走 40ms 节流 + 拖拽结束精确重算。
- **P2 OOBE 看门狗自愈无效**：`SnapToSettled` 只 `SetValue`，停摆但挂着的 HoldEnd 时钟继续压住动画值——正是看门狗要治的场景。新增 `SetFinal`（先 `BeginAnimation(null)` 再落值）。
- **P2 settings.json null 绑定致启动崩溃**：`Normalize` 不查值，`"Region": null` 使 `HotkeyService.ReRegister` NRE → `Shutdown(1)` 且无法自愈。已修复并顺带补 Palette/UserPresets 的 null 修复（连修效果预设 null Settings NRE）。
- **P2 「0 表示不保存历史」未实现**：UI 文案承诺 0=禁用，代码却是 0=无限保留。`HistoryRecorder.Record` 现在为 0 时跳过。
- **P3 修复 17 项**：托盘延时截图倒计时纳入捕获守卫（倒计时中可重入 Region 且倒计时结束后被静默丢弃）；WindowPick 区域钳制到虚拟屏；马赛克/聚光灯拖画中无反馈（加虚线框预览）；LineItem N/S 死手柄（拖了没动还推一条撤销）；放大镜提交阈值 2px→4px 对齐渲染下限（隐形幽灵项）；工具栏缩放锚点未计内容居中间隙（跳变）；文字覆盖层字号随缩放刷新；PinWindow `#` 路径、保存对话框属主（置顶贴图盖住对话框）、主屏居中 NaN 死代码、替换后期望黑占比过期（PresentationGuard 改惰性 provider）；HistoryStore List 捕获 UnauthorizedAccessException、Clear 批量 Changed（O(n²) 刷新风暴）、孤儿目录 10 分钟清扫、缩略图失败不再丢条目；ColorPickerButton 弹层硬编码白底（暗色主题白字白底）；小键盘运算键名（Num1-6→Num*/+/-/./）；UiMotion FadeOut 代数条目泄漏+无停摆兜底（时钟停摆=窗口永不关闭）；ClipboardService 异常路径 GlobalFree 缺口；设置窗语言热切后两处提示未刷新；ScreenCapturer 黑帧探针就地采样（省 ~30MB/次捕获的全幅复制）。
- **未修（需用户定夺/低价值，非回归）**：①`AfterRegionCapture` 设置对区域/窗口截图实际不生效（overlay 确认恒带 `Intent=Edit`，`??` 兜底不可达；「浮动缩略图」等选择无效）——改语义涉及交互设计（无工具栏操作时如何应用默认动作），**待用户定夺**；②捕获真实失败无 toast（与用户取消不可区分，需改 CaptureService 返回契约）；③ClipboardService 重试在 UI 线程 Sleep（剪贴板被占用时最长 ~1s 假死，需异步化）；④mac 阴影开启时 CaptureService 重复跑一遍效果管线只为取偏移（Core API 加 out 参数可消除）；⑤OverlayRenderer.DrawAnnotations 每帧分配（GC churn，无泄漏）；⑥`{counter:D3}` 命名格式静默退化为 0（文档化即可）。
- **真机自测建议**：开“捕获光标”全屏截图应含指针；双屏下文字工具在副屏选区内点击应就地出输入框；设置历史数为 0 后截图不再入历史；暗色主题打开自定义颜色弹层应为主题底色。

## 「窗口吸附一点就是全屏」修复（用户报「窗口获取逻辑不咋地，一截图只能截全屏」，2026-10-01，未提交）

- **日志取证**（app.log 15:24–15:31）：用户连续 5 次 Region 会话取消 + 第 6 次 Region 结果恰为 `{0,0,2562×1600}`（整主屏）且 `window=0x0`——签名是**点击吸附命中了整屏大小的"窗口"**。根因：`WindowEnumerator.HitTest` 在没有命中顶层窗口时**回退到桌面外壳（Progman/WorkerW）**，其 bounds=整个显示器 → 鼠标悬停桌面=整屏蓝框、单击桌面=选中整屏作选区（15:31 用户关掉设置后桌面在眼前，一点即全屏，连 Esc 5 次就是这个）。**修复：HitTest 移除桌面回退**——桌面不是吸附目标，裸桌面返回 null（悬停无高亮、单击无选区、拖框照常），与 Snipaste 语义一致；`ConfirmWindowPick` 对 null 已有守卫（点桌面=不确认）。
- **独立探针实证**（临时 csproj 引用 App 工程，活窗口枚举）：修复后 96 个裸桌面网格点全部返回 null、0 个命中等于桌面矩形/类名、真实窗口 10/10 命中 → PROBE PASS。
- **顺带核对的用户感知点（非缺陷）**：①12:39/15:15 的"当前窗口"结果 2679×1719 = 2561×1601 全屏前台窗口 + 118px mac 阴影边距——前台窗口本身是最大化/全屏（等面积正确，想截窗口内容而非整屏时请用区域拖框或窗口吸附）；②15:15:06 'ClassIn X' 781×781 = 前轮黑图整治的 PrintWindow 黑占比判废→屏幕兜底正常工作（该窗口 PrintWindow 内容 75% 黑，旧逻辑会烘进黑图）；③`window=0x0` 日志只反映 WindowPick 模式的 WindowHandle，Region 吸附路径恒为 0x0，勿误读。
- 门禁：build 0/0、smoke 0、probe-oobe PASS。**待真机**：鼠标悬停桌面应无蓝框、点桌面后可直接拖框；悬停真实窗口仍整窗高亮。

## 贴图 Snipaste 化：阴影 + 完整右键菜单（用户指定，2026-10-01，未提交）

- **窗口阴影**：原 PinWindow 的 DropShadowEffect 被窗口边界裁剪（窗口=图片大小，模糊没地方画）所以看不见。现窗口四周留 20 DIP 边距（`ShadowMargin`），BlurRadius 24 / Depth 0 / Opacity 0.55 的柔和阴影有空间渲染；右键菜单新增 **✓窗口阴影** 可切换（`AppSettings.PinShadow` 默认 true，JSON 缺键保留初始化值，切换即落盘）。
- **右键菜单**（对标 Snipaste 截图，保留适用项）：复制图像 / 图像另存为... / ─ / ✓窗口阴影 / 缩放▸(放大·缩小·重置·适应屏幕) / 背景模式▸(原始·白色·灰色·黑色，透明 PNG 的底色) / 图像处理▸(旋转90°·水平翻转·复位) / ─ / 粘贴(剪贴板图片替换贴图) / 替换为文件... / 打开文件夹 / ─ / 尺寸项(禁用态显示当前 W×H，子菜单复制尺寸) / ─ / 关闭。Snipaste 的移动到分组/分配到桌面/工具条/销毁无对应功能，未做。
- **实现**：`_image` 改可变；旋转/翻转为 `LayoutTransform`（RotateTransform+ScaleTransform(-1,1)），窗口尺寸=显示尺寸×zoom+阴影边距（`SizeWindow()` 统一）；粘贴/替换走 `FormatConvertedBitmap→Bgra32→CopyPixels→PixelBuffer`；替换后重置方向并重算适应缩放。`PinWindow` 构造新增可选 `SettingsStore store` 参数（App.Features 两处调用点传入；smoke 新增 PinWindow 实例化覆盖菜单/阴影构造）。i18n +21 键 **292=292 齐平**（Pin.CopyImage…Pin.PasteEmpty）。
- **门禁**：build 0/0、smoke 0（含 PinWindow）、probe PASS、harness ALL PASS、i18n 292=292。
- **待真机确认**：阴影观感（透明 PNG 的阴影形状、边距区域可拖动）、菜单各项、旋转后窗口宽高互换。边距区域属于贴图窗口（可拖动），不做点击穿透——与 Snipaste 行为可能略有差异。

## OOBE 配置面板对齐（两次修复，2026-10-01，未提交）

- **第一次修错**：只把控制列从 Auto 改固定 190 并给开关 MinWidth=190——树转储显示控件槽位 [307,190] 以为好了，**用户截图打脸：开关视觉上仍黏在标签后**。根因：WPF-UI ToggleSwitch 模板把轨道画在控件内的自然宽度、贴左——MinWidth 拉宽的是布局槽，轨道不动。**教训：树转储量的是布局槽，模板控件的视觉可以和槽位脱节——涉及模板控件的视觉断言必须做像素级验证（扫 PNG 亮像素），布局槽对≠画面对。**
- **真修**：开关不再给 MinWidth，用**自然宽度 + HorizontalAlignment=Right** 落在固定 190 列的右缘——轨道跟着控件走，右缘与下拉框对齐。
- **像素级验证**：渲染 PNG（MSS_RENDER_OOBE_CONFIG=1 钩子）扫描开关行亮像素：轨道 x 686..745 物理 → 右缘 **496.7 DIP = 下拉框右缘 497** ✓；布局槽 [457,347 40x20] ✓。四控件（语言/主题/开关/截图后）右缘全部成列。
- **门禁**：build 0/0、probe PASS、smoke 0、harness ALL PASS、i18n 齐平。

## 黑窗/黑图全面整治(Goal 5 轮迭代,2026-10-01,未提交)

用户报图:一个 720×560 圆角深色窗口内容整片纯黑(即 OOBE 欢迎窗:AppTitleBar+关闭钮+白分隔线特征吻合)。5 轮"审查→修复→门禁"全绿(build 0/0、harness ALL PASSED、smoke 0、`--probe-oobe` 双模式 PASS、i18n 271=271)。

- **取证**:①对用户截图逐像素采样——内容区与标题条都是纯 RGB(0,0,0),而正常暗色主题标题条是 #202020,分隔线/字形白 F0 可见 → "整面呈现为黑"而非"内容没画";②日志实锤当晚 22:19/22:33 两次"调试分类打开 OOBE 预览";③23:23 用 PrintWindow 抓同一活窗——视觉树/呈现已正常(该窗还暴露了**旧构建的活路径词组右偏+右缘裁切**,当前源码居中断言通过,属旧构建缺陷);④结论=两类洞:**WPF 呈现层可能整面黑而视觉树健康**(watchdog/数值 probe 全看不见),**真实可见窗口从未被任何门禁看过一眼**(probe 用 Opacity 0、render 走 Suppress)。
- **轮 1(OOBE)**:`OobeWindow` 增加呈现自检+自愈 `EnsurePresented`——3.6s 采样自己窗口的屏幕像素(GDI BitBlt 自身矩形),黑占比 ≥98.5% 而视觉树已揭示 → 踢呈现管线(梯度:SWP_FRAMECHANGED → 1px 尺寸往返 → Hide/Show,至多 3 次),窗口从此不可能停在黑面;`DiagnosticLayoutValid` 扩为 **不重叠+词组居中±8px+不出界**,新增 `DiagnosticLayoutDescription` 失败详情。**新门禁 `MSS_PROBE_VISIBLE=1` + `--probe-oobe`**:真窗口(Topmost 不抢焦点)跑完动画后直接断言屏幕像素(black 0.0% 语义),补上"呈现层"盲区。
- **轮 2(消费链,串行子代理审查)**:位图步长/解码失败/JPG 白底/DIBV5 偏移/历史占位全部核验干净;唯一洞=**PinWindow/FloatingThumbnailWindow 无呈现守卫**(都是 AllowsTransparency+GPU DropShadow,比 OOBE 更高危)。新增共享 `Services/PresentationGuard.cs`:按窗武装 DispatcherTimer(1.5s 首查,踢后 0.7s 重查,≤3 次),用**内容自身黑占比交叉验证**——用户真贴了一张黑图时守卫自动解除(绝不"修复"真实内容);BitmapSource 过载供缩略图用。
- **轮 3(编辑器/Overlay,串行子代理审查,4 缺陷)**:①`AnnotationCanvas.LoadDocument` 盲信 doc 尺寸——0×0 的 doc.json 使画布 0×0、编辑器只剩 #202022 平板黑工作台,现回退基图尺寸+告警;②`UiMotion.AnimateDouble` 只有 Completed 提交终值(项目已实证时钟可停摆)——加**挂钟提交兜底**(2×时长+150ms,代数计数器防超写新动画);③`ScreenCapturer` 全黑帧(显示器状态切换期 BitBlt 可成功返回全黑)**120ms 重读一次**,真黑屏照常保留绝不拒绝;④历史"重新编辑"打开失败从静默改为弹窗提示(新键 `History.OpenFailed`,271/271 齐平)。
- **轮 4(统一+固化)**:5 处重复的黑占比实现收口为 `Core/Imaging/BlackFrame.cs`(近黑阈值 r+g+b<30 单点定义,#202020 背景不算黑),WindowCapturer/ScreenCapturer/PresentationGuard/OobeWindow 全部改调;`WindowCapturer` 梯子改为 **黑占比>60% 判废→60ms 重试一次→屏幕兜底**(旧 IsBlank 只拒 100% 黑,"标题条画出+客户区全黑"的 GPU 窗口照样漏进贴图/剪贴板;屏幕兜底改为保留地面真相并记日志);Harness 新增 6 条 BlackFrame 数值断言。
- **轮 5(新鲜眼攻击本轮改动)**:修 2 项——①ScreenCapturer 重读用了 `finally` 已 Release 的 DC(use-after-release,真黑屏反而抛异常,与意图相反),重读移回 DC 存活期内;②UiMotion 挂钟兜底会掐死 `FadeOut`(其手写 timeline 不经 AnimateDouble、没注册代数;Esc 在淡入的 410–550ms 窗口按下时 OOBE 窗口弹回不透明且永不关闭),提取 `TouchGeneration` 供 FadeOut 注册。nit:OOBE 踢次数对齐为 3。
- **遗留/待真机**:①用户黑窗当时的具体构建已不可考(其 Debug 实例 23:17 重启后同窗正常)——当前构建双模式 probe 均绿,真机复测"设置→调试→播放预览"即可确认;②PresentationGuard 实际踢中场景无法离线复现,依赖日志观察(`Presentation guard:` 前缀);③OobeWindow 内联守卫与 PresentationGuard 待合并(该文件当时用户在并行编辑,刻意保持独立);④Pin/Ctrl+滚轮降透明期间守卫跳过(Opacity<0.99),属预期。

## OOBE 配置面板对齐修复（用户截图报「修复」，2026-09-30，未提交）

- **症状**：配置面板里"开机自动启动"的开关贴在标签后面，与三个下拉框（右缘对齐）不成列。
- **根因**：`MakeConfigRow` 每行是独立 Grid，控制列宽 `Auto`——下拉框行被 MinWidth=190 撑到 190，开关行只有开关自身宽 ~55，各行右缘参差。
- **修复**：控制列改**共享固定宽 `ConfigControlWidth=190`**（所有行同一列宽 → 右缘必然对齐），非拉伸控件（开关）在列内 `HorizontalAlignment=Right`（其 MinWidth=190 实际拉伸成 190 宽的长开关，Win11 设置风格）。
- **验证工具化**：新增渲染钩子 `MSS_RENDER_OOBE_CONFIG=1`（配合 `--render-oobe`）——SnapToSettled 里直接切到配置面板，让**离屏渲染可以树转储验证配置面板布局**（此前面板只能交互后出现，渲染探针够不到）。实测四控件全部 `[307, y 190]`，右缘 497 成列，行距均匀 20px。门禁全绿（build 0/0、probe PASS、smoke 0、harness ALL、i18n 270=270）。

## OOBE 回归：拖拽框画到左上角（用户截图报「改坏代码」，2026-09-30，已修，未提交）

- **根因**：R2 把矩形重构为"预置全尺寸 + ScaleTransform origin(0,0)"时**删掉了 `Canvas.SetLeft/SetTop` 定位**——框画到画布 (0,0)，而光标按正确坐标（框槽位右下角）动画，两者分离；词标不受影响所以 3.2s 探针仍 PASS。**教训：把"逐帧设宽高"改成"scale 预置尺寸"时，原帧回调里顺带做的 Canvas 定位是隐性依赖，重构必须逐行核对被删代码的所有副作用。**
- **修复**：`BuildSelectionScene` 创建矩形时即 `Canvas.SetLeft/SetTop(BoxLeft, BoxTop)`（静态位置，一次设置）。
- **门禁加固**：`--probe-oobe` 新增 **1s 拖拽期断言 `DiagnosticBoxCentered`**——框完全长出时其视觉中心必须等于 host 中心（±2px），失败立即 exit 1。此前探针只查 3.2s 的文字透明度/词几何，拖拽期框位置是盲区，这正是本次回归溜过去的原因。
- **验证**：build 0/0、probe PASS（日志含 "box centred during drag" + 全链 PASS）、smoke 0、render 0。

## OOBE 动画/UI 五轮审查（review/improve-animations 技能，2026-09-30，未提交）

五轮差异化透镜（全量十条标准→修复→边界路径→新鲜眼→终审台账），每轮 build 0/0 + probe PASS + smoke 0，共修 **9 项**：

- **R1 全量审查 6 项**：①拖拽帧回调逐帧改 Width/Height（布局属性，60fps measure/arrange）→ 两个矩形预置全尺寸 + `ScaleTransform 0→1` origin(0,0)，纯 GPU 合成；②光标跟随也逐帧 → 改同拍 `StartAnim` X/Y；③尺寸标签每帧改文字 → 100ms 低频定时器读 scale 当前值（拖完即停）；④选框淡出用 `CubicInOut` → 退出元素改 `CubicOut`；⑤**配置切换词标跳位 ~47px**（intro 区 192px ↔ 配置区 ~280px 高度差，居中列整体位移）→ FLIP：切换前记词行窗口位置、切换后按 delta 种子 260ms 滑回 0；⑥配置面板 `FadeIn` 无位移 → `UiMotion.FadeSlideIn(0,10,220)` + Done 按钮 Focus（与设置页切换动效一致 + 键盘 a11y）。
- **R3 边界 2 项**：①`_afterAction` 字段默认 ShowToolbar 不读当前设置 → 用户改语言点完成后已有截图后动作被静默覆写 → 初始化 `TryReadCurrent`；②改了截图后动作后 Esc/X 关闭 → 语言/主题/自启已生效但该项丢失 → 统一 `PersistChoices()` 从 CompleteOobe/Finish/OnClosing 三路径调用。
- **R4 新鲜眼 1 项**：`PersistChoices` 在未进配置面板时也会写盘（smoke 构造即 Close 会重写用户 settings.json）→ 加 `_configShown` 守卫，intro 阶段 Skip 不碰设置文件。
- **R5 回归台账**：9/9 项 grep 验证在位（帧回调 0 残留、scale 动画 2 处、labelTimer 拖完即停、FLIP 测量 2 处、FadeSlideIn+Focus、afterAction 初始化、PersistChoices 3 调用点、_configShown 守卫）。
- **判定（review-animations 标准表）**：900ms 拖拽/460ms glide 超 300ms 属合理（首启引导=罕见/首次频率档可加时长）；全部动画 transform/opacity（GPU）；easing 全部 ease-out 族；Enter/Exit 对称处不存在（退出均即时或更短）；reduced-motion/Suppress 走 SnapToSettled 终帧。**Approve**。
- **门禁**：build 0/0、probe PASS、smoke 0、harness ALL PASS、i18n 270=270、EN/ZH `--render-oobe` 退 0 且词面几何不变（Modern[160..320]/ScreenShot[333..560]）。动画实时观感与配置面板真机交互仍待用户自测。

## OOBE 增加配置步骤（用户指出"只有动画没有配置内容"，2026-09-30，未提交）

- **新增**：欢迎动画结束后点"开始使用"不再直接关闭，而是**淡入偏好设置面板**（对标参考项目的多步 OOBE）：**语言**（跟随系统/中文/English，`SetResourceReference` 动态资源 → 切换后全窗文字即时换语言）、**主题**（跟随系统/浅色/深色，`App.ApplyTheme` 即时预览）、**开机自启**（ToggleSwitch，立即写 HKCU Run 注册表，失败自动回弹）、**截图后动作**（与设置页同枚举 AfterCaptureAction 的 6 项）。底部"完成"按钮一次性 `store.Save()` 落盘（语言/主题/自启在改动时已即时生效，此处统一持久化）。
- **预览安全**：`OobeWindow(onCompleted, persistSettings)` 新增参数——真实首启 true；**调试预览 false：配置面板照常显示、可选可看，但任何改动都不应用也不落盘**（语言/主题/注册表/设置文件全部不碰），防止预览污染用户配置。
- **实现要点**：全部 OOBE 文本从 `L.Get` 硬编码改为 `SetResourceReference(TextBlock.TextProperty/ContentControl.ContentProperty, key)`（DynamicResource 语义，语言热切自动跟随）；配置控件初值从 `App.Services` 解析 SettingsStore 读取；`_configLoading` 守卫防构造期 SelectedIndex 触发处理器；i18n +6 键 **264→270 齐平**（Oobe.ConfigTitle/.ConfigSub/.LangLabel/.ThemeLabel/.AfterLabel/.Done，语言/主题/截图后选项复用既有 Settings.LangSystem、Tray.Lang*、Settings.Theme*、Settings.After.* 键）。
- **验证**：build 0/0、`--probe-oobe` PASS、smoke 0、harness ALL PASS、check-i18n 270=270、`--render-oobe` 终帧正常（配置面板 Collapsed 不影响欢迎帧）。
- **遗留**：配置面板真实交互（切语言即时刷新、切主题即时换肤、注册表写入）未经真机人工确认——本环境无法注入 UI 交互，由用户自测：重启后 设置→连点关于5次→调试→播放预览 可看面板（改动不生效属预期）；真实体验需 `--reset-oobe` 后重启应用。

## OOBE 布局终版：整块文字居中 + 间距统一（用户三轮反馈，2026-09-30，未提交）

- **用户反馈链**：①去掉黑色遮罩（完成）；②"所有字整体来看都在中间"——第二版（舞台单独居中+提示区锚底+词上抬48px）顶部大空白被否定，第三版（单一居中列+拖框种子下移）居中了但**词→标语间距 ~78px**（舞台 150px 带内词居中留下的死空间）与全页 12–28px 节奏不一致，用户报"间距太奇怪"；③终版修法 = **选框画布与文字列拆成两层**：选框 band（560×150）独立居中覆盖整个内容区（拖框天然在窗口正中，无需任何偏移 trick），文字列只含「词行(51px) + 提示区」紧凑居中（词→标语间距 27px，与全页节奏一致）。
- **morph 上升**：`ComputeRiseSeed()` 量"框中心 − 词行中心"（host 坐标，`TransformToVisual`，约 110px），morph 时给词行 TranslateTransform 种子该值并以 460ms ExpOut 滑回 0——文字从框里**平滑上移约 110px** 归位，与 "ScreenShot" 滑动、"Modern" 滑入同拍。旧 `ChromeHalfShift`/`StageRiseY`/`_stageTranslate` 全部删除。
- **验证**：build 0/0、`--probe-oobe` PASS、smoke 0；终帧树转储：词 y=147..198、标语 225（间距 27）、提示 269/299/329、按钮 387..418，整块中点 282.5 = 内容区中心 282（精确居中）；选框 band 中心 282 = 窗口中心（拖框正中）。动画实时观感待用户真机确认。

## OOBE 入场动画重做为"截图选框"主题（用户指定，2026-09-30，未提交）

- **改动**：`Shell/OobeWindow.cs` 入场动画从"方块掉落→翻转成字母"整体替换为**呼应本应用核心动作的截图选框动画**：鼠标光标（白色箭头 Path）拖出一个蓝色选区框（复用 `Overlay/OverlayRenderer` 的配色/尺寸——`#0A84FF` 2px 边框、`#59000000` 暗色遮罩、`#260A84FF` 半透明填充、10px 白色四角把手、`#E01C1C1E` 尺寸读数标签）→ 框定后把手/标签弹出并短暂"定格"→ 选框淡出缩小、渐变词 "ScreenShot" 从中长出（crossfade + BackEase 放大）→ "Modern" 从左位移淡入 → 标语/三提示/按钮错峰浮现。
- **技术要点**：①拖拽阶段用单个 `CompositionTarget.Rendering` 帧回调同步驱动框宽高、暗色遮罩挖洞（`GeometryGroup` EvenOdd 每帧重建）、光标跟随右下角、尺寸标签文字——因为 Canvas 矩形的 Width/Height 与 Geometry 不能用 transform 动画，必须帧驱动；`_renderHandler` 被 `Finish`/`OnClosing`/`SnapToSettled` 显式 detach 防泄漏。②其余全是合成动画（Translate/Scale/opacity），静态刷/笔全 Freeze 共享。③保留上一轮的所有健壮性机制：`IsVisibleChanged`+`Loaded` 双触发幂等起播、`BeginTime` 只在 >0 时设置（null=永不开始的坑）、5s 看门狗 `EnsureSettled` 强制终帧、`SnapToSettled` 供 Suppress/减弱动效、`--probe-oobe` 实时路径门禁。
- **验证**：build 0/0、`--probe-oobe` **PASS**（日志链 drag→box settled(+0.9s)→morph(+0.5s)→reveal Modern(+0.5s)→chrome→PASS，无 stalled）、smoke 0、harness ALL PASS、i18n 264=264；`--render-oobe` 终帧树转储 "Modern"[160..320] / "ScreenShot"[333..560] 间隙 13px 居中无重叠，选框 Canvas 已淡出。
- **遗留**：动画实时观感（拖拽流畅度、morph 过渡、尺寸标签数字）需用户真机确认（重启后 设置→连点关于5次→调试→播放预览）；visual-judge/图片输入不可用，本轮用实时探针+终帧几何替代。尺寸标签显示的是"框 DIP×4"的伪像素数（纯装饰，非真实分辨率）。

## OOBE 预览真根因修复（`BeginTime=null` + 预布局测量，2026-09-30，未提交）

上一节（预览空窗修复）的两条"根因"经本轮复核均为**误诊**，真根因如下，已修：

- **真根因 1（致命）**：`StartAnim` 里 `BeginTime = beginMs > 0 ? ... : null`——**WPF 中 `Timeline.BeginTime = null` 意味着时间线永不开始**。所有 `beginMs==0` 的动画（遮罩缩放/淡入、收拢、面板位移、chrome 揭示）从未运行：`_intro`/`_chrome` 永远停在 Opacity 0 = 空窗。上一节观察到的"letters=1.00 而 intro=0.00/chrome=0.00"正是此签名——`beginMs>0` 的字母动画正常启动并完成，证明 `Completed` 本身**可靠**，"Completed 不可靠"是误诊（挂钟兜底/看门狗是在治症状）。
- **修复 1**：`BeginTime` 只在 `>0` 时设置（默认 `TimeSpan.Zero` 即立即开始）；**删除挂钟兜底**（Completed 在动画真正启动时可靠触发，兜底反而在字母表上引入 ~50 个多余定时器）；`IsVisibleChanged` 双触发与 5s 看门狗保留（防御层，幂等无副作用）。
- **真根因 2（终帧重叠）**：`SnapToSettled` 在 `Show()` 内同步执行（首布局未完成），`_slideWord.ActualWidth` 读到 **0** → 侧并排目标按 0 宽计算，而真实布局按 160px 排列 → 终帧 "Modern" 与 "ScreenShot" 重叠 ~75px。此前 `--render-*` 的树转储里 Modern[160..320] vs 字母[245..] 的重叠被漏判。日志插桩实锤：`slideW=0.0 ... panel.X=6.0`。
- **修复 2**：①宽度测量改为 **`FormattedText`（不依赖布局进度）**，`ActualWidth<1` 时回退；②侧并排定位改用**字母盒几何**（`_wordBoxLeftLocal`/`_wordBoxWidth`，盒=40px 槽位，字形居中其内），动画路径与终帧路径共用同一 `ComputeRevealTargets()`；③删除参照系混乱的 halfSlack 中间 nudges。
- **新门禁 `--probe-oobe`**：以 Opacity 0 显示欢迎窗（不可见但合成照跑、动画时钟照走），**3.2s（动画链自然结束 ~2.6s 之后、5s 看门狗之前）**断言 ①intro/chrome/双词/全部字母经真实动画管线到达全显 ②两词并排无重叠（盒边距 ≥2px）。Exit 0/1。**这补上了门禁盲区：`--render-*` 走 Suppress 直落终帧，永远测不到真实动画路径的回归**——本次空窗正是从这条盲区溜进的。
- **验证**：build 0/0、smoke 0、harness ALL PASS、i18n 264=264；`--probe-oobe` **PASS（exit 0）**，日志链 start via visible → intro start(+0.15s) → reveal(+1.4s) → tips(+2.1s) → PASS(+3.2s)，无 stalled 告警；`--render-oobe` 终帧树转储实证 **Modern[151..311] / ScreenShot[326..572]，间隙 15px，无重叠，组合中心 361.5≈360 居中**（修复前重叠 75px）。
- **遗留**：动画实时观感仍需用户真机确认（重启应用后：设置→连点"关于"5 次→调试→播放预览）。

## OOBE 预览空窗修复（用户报「调试菜单用不了」，2026-09-30，未提交）

- **症状**：设置→调试→播放预览打开的欢迎窗口内容全空（截图：仅标题栏+一个空按钮轮廓），+19s 仍空白。日志证实预览打开无异常、无崩溃。
- **根因 1（致命）**：`Window.Loaded` 在该窗口上**从未触发**（进程活着、窗口渲染着、无异常，但 Loaded 静默不来——首帧布局管线未走完的路径不引发它）→ 动画链从未启动 → 全部内容停在透明初值 = 空窗。此前所有验证都走 `--render-*`（UiMotion.Suppress → SnapToSettled 直落终帧，不经动画链），所以从未暴露。
- **修复 1**：启动触发改 `IsVisibleChanged`（HWND 可见必发，探针实证）为主 + `Loaded` 并行，`StartAnimationOnce` 幂等收口。
- **根因 2（插桩发现）**：`DoubleAnimation.Completed` 在"首帧前启动的动画"上**不可靠**——实测有时钟推进到终值但 Completed 不发（加 `Timeline.SetDesiredFrameRate(60)` 时完成回调还会整体拖慢/更乱），导致 `_intro`/`_chrome` 容器 Opacity 终值永远不落（watchdog 实测 intro=0.00 chrome=0.00 letters=1.00），子树整体不可见。
- **修复 2**：`StartAnim` 增加**挂钟提交兜底**——每个动画 `After(beginMs+durationMs+120)` 由 Send 优先级一次性定时器执行与 Completed 相同的落终值+摘时钟（幂等，双保险）；已验证 Completed 不可靠时提交照样落位（跑 8s 无 stalled 告警，链路 reveal+1.4s / tips+2.1s 准时）。
- **顺带加固**：阶段序列 `After()` 定时器从 Background 提到 **Send 优先级**（持续输入下 Background 会被饿死数秒）；`EnsureSettled` 看门狗（5s/8s 两次）度量 intro/chrome/slide 透明度，未收敛则 `DetachAllAnimations`+`SnapToSettled` 强制终帧并告警——**窗口从此不可能停在空白态**；`DetachAllAnimations` 清掉全部 ~40 条时钟防 HoldEnd 覆盖。
- **验证**：Release build 0/0、smoke 0、harness ALL PASS、i18n 264=264、`--render-oobe` 退 0；真机直跑 `--show-oobe` 日志链完整（start via visible → intro start → reveal → tips → **无 stalled**）。已重启用户实例（PID 98000，Release 修复版）。**动画实时观感仍待用户自测**（VS 里跑需自行重新 F5 构建 Debug 版）。
- **方法论沉淀**：①离屏渲染验证（Suppress 路径）与真实动画路径是两条完全不同的代码路径，UI 功能必须至少跑一次真实启动路径；②WPF `Completed` 事件不可作为终值提交的唯一机制，挂钟定时器兜底幂等提交是可靠模式；③"窗口可见但 Loaded 不来"在本机真实存在，可见性触发用 `IsVisibleChanged`。

## OOBE 动画流畅度优化（Goal 模式 5 轮，2026-09-30，未提交）

对 `Shell/OobeWindow.cs` 入场动画做 5 轮差异化优化，每轮 build 0/0 + `--render-oobe` 终帧几何回归：

- **轮 1（最大卡顿源：逐帧布局动画）**：原实现用 `MinWidth 40→0` 动画收拢 10 个字母 → WPF 每帧跑一次全量 measure+arrange 布局 pass 持续 700ms，无法 GPU 合成、必掉帧。改为**每字母 `TranslateTransform.X`（纯合成，零布局）**：新增 `ComputeCollapseOffsets()` 用 `FormattedText` 量字形前进宽度，算出每字母从固定 46px 槽位平移到自然字距的偏移；面板整体再补一个居中平移。布局全程静止，只有 transform 动。
- **轮 2（并发时间线/时钟不脱离/时序）**：`StartAnim` 的 `Completed` 落终值 + `BeginAnimation(prop,null)` 脱离时钟（对齐 UiMotion 纪律，`_finished` 守卫防退场淡出被覆盖），避免几十条完成的逐字母动画作为 HoldEnd 时钟常驻加每帧开销；叠加时间线削减：**遮罩缩放 3000ms→1500ms、翻转前停顿 750ms→420ms、错峰跨度 500→420ms**（都是 dead-time 大头，拖慢观感且长时间线与全程并发）。
- **轮 3（GPU 合成/冻结）**：静态画刷全部 `Freeze()` 并共享（`AccentBrush`/`BlockBrush`/`GradientBrush`，去掉每字母 new 渐变刷）；方块 `CacheMode=BitmapCache`（只 transform/opacity 动 → 缓存为 GPU 位图，每帧合成纹理而非重绘圆角矩形）；动画 `Timeline.SetDesiredFrameRate(60)`。
- **轮 4（首帧闪烁/起播时机/减弱动效奇偶性）**：起播从"80ms 定时器"改为 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded)`——恰在首帧就绪时起播，无静止空白帧、无定时器粒度 hitch；`_finished` 守卫防退场时起播。`SnapToSettled` 复用 `ComputeCollapseOffsets` 保证减弱动效/诊断渲染终帧与动画路径逐像素一致（跨 5 轮 render 均确认相同）。
- **轮 5（终审+回归台账+门禁）**：新鲜眼复查揪出 1 项（tip 图标仍用未冻结 `new SolidColorBrush` → 改用 `AccentBrush`）；全门禁绿（build 0/0、smoke 0、harness ALL PASS、check-i18n 264=264）；EN/ZH 双语 `--render-oobe` 均退 0，终帧几何（"Modern" 左 x=160 + "ScreenShot" 渐变字母右 x=219+ 自然字距）逐轮稳定。
- **WPF 动画性能纪律沉淀**（进 [[gotchas-csharp-wpf]] 候选）：①绝不动画 `MinWidth`/`Width`/`Spacing` 等布局属性，改 `TranslateTransform`/`ScaleTransform` 合成；②HoldEnd 动画完成要落终值+脱离时钟，别让几十条时钟常驻；③静态画刷 Freeze 共享；④纯 transform/opacity 元素上 `BitmapCache`；⑤起播用 `DispatcherPriority.Loaded` 而非任意 ms 定时器。
- **验收局限**：动画实时流畅度（帧率、是否还有肉眼可感 hitch）本环境无法眼见——visual-judge 供应商不可用 + 当前模型不支持图片输入，只能用终帧几何 + 构建门禁 + WPF 性能原理替代。**流畅度改善需用户真机（设置→调试→播放预览）确认。**

## 设置隐藏调试分类（暗格，2026-09-30，未提交）

- **新增功能**：设置窗左侧导航栏**连点"关于"5 次**解锁隐藏的"调试"分类（默认 `Visibility=Collapsed` 的第 7 个导航项 `NavDebugItem` + 页面 `PageDebug`）；解锁后弹一次托盘提示，本窗生命周期内保持显示。调试页含两张卡片：①"预览欢迎动画"（Primary 按钮，打开 `OobeWindow` 播放入场动画，**不改 `OobeCompleted` 标记**）；②"重置 OOBE 标记"（与关于页同款重置）。
- **实现要点**：`NavAboutItem` 挂 `PreviewMouseLeftButtonDown="OnAboutNavClicked"`（用 Preview 事件所以点击选中的同时也计数）；`_debugUnlockClicks` 计数到 `DebugUnlockClickCount=5` 后显示 `NavDebugItem`；`OnNavSelectionChanged` 的 `pages` 数组扩到 7 项（含 `PageDebug`）；`SettingsWindow` 构造新增可选 `Action? onPreviewOobe`（App.Features.OpenSettings 传入 `PreviewOobe`，预览用 no-op 完成回调不落盘）；smoke 的 5 参构造调用因新参可选保持兼容。i18n +6 键 **258→264 齐平**（Settings.Debug/.Sub/.Unlocked/.PreviewOobe/.Sub/.Button）。
- **验证**：build 0/0、smoke 0、harness ALL PASS、check-i18n 264=264；`--render-settings MSS_RENDER_TAB=6` + 可视树证实调试页两卡片 + Play 按钮渲染，`NavDebugItem` 未解锁时 0×0 Collapsed、`SelectPage(6)` 仍可选中（导航项可见性不影响页面选择）。**5 次点击解锁的实时交互未真机人验**（离屏渲染直接 SelectPage 到调试页），由用户自测。

## OOBE 首次使用引导（2026-09-30，未提交）

- **新增功能**：首次启动显示 `Shell/OobeWindow`（欢迎界面 + 入场动画），完成/跳过后写入 `AppSettings.OobeCompleted` 并保存；之后启动跳过 OOBE，仅在 `FirstRunShown` 未置位时弹一次托盘提示。
- **入场动画来源**：照搬参考项目 CSD（WinUI 3）`Views/InitializationWindow.xaml.cs` 的"方块掉落 → 翻转成字母"序列——正弦缓动错峰（`sin(((i+2)/(n+2))·π/2)·50` ms）、每字母时长 = 错峰值×9、掉落后停顿 750ms、强调块放大 X→2.17、随后字母间距收拢、渐变词从右滑入。时长/延迟/缓动/错峰公式逐值照搬。
- **技术栈适配（差异说明）**：参考项目用 WinUI `PlaneProjection.RotationX` 做卡片翻转，**WPF 无 PlaneProjection**，改用 `ScaleTransform.ScaleY`（方块 1→0 / 字母 0→1，绕竖直中心）等价复刻翻转观感；其余 `DoubleAnimation` + `CubicEase`/`ExponentialEase`/`QuadraticEase` 均为 WPF 原生同名类型，直接沿用。词面用本应用名 "Modern ScreenShot" 取代 "Classworks Desktop"。
- **动画健壮性**：走项目既有约定——`UiMotion.Suppress`（`--render-*` 诊断路径）与系统"减少动态效果"（`ClientAreaAnimation==false`）时直接跳到 `SnapToSettled()` 终帧，不播放；窗口关闭时 `StopTimers()` 清理所有 `DispatcherTimer`；完成回调 `FireCompleted()` 幂等，Skip/开始使用/Esc/标题栏关闭四条路径都只落一次盘。
- **调试/重置**：设置→关于→"重新显示欢迎界面"清除 `OobeCompleted`；命令行 `--reset-oobe`（清标记后退出）、`--show-oobe`（强制打开，不看标记）、`--render-oobe`（诊断快照）。
- **验证**：Release 全解 0 错 0 警；`--smoke` 退出 0（含 OobeWindow 双语实例化 + 全键双语齐平校验）；`harness core` 通过；`check-i18n.ps1` 258/258 双语齐平；`--render-oobe` 双语快照 + 可视树校验证实词面、标语、三条提示、Skip/开始使用按钮均居中不溢出不重叠、方块与字母共用竖直中心带。
- **未经真机人工测试**：入场动画的实时观感、快速点击/中途 Esc/减少动态效果开关下的表现由用户自测（本环境 visual-judge 与图像输入均不可用，已用可视树几何 + 构建/冒烟/i18n 门禁替代）。

## 全局动效系统 UiMotion（2026-09-29，4 个串行子代理，未提交）

按 Emil Kowalski 动画哲学跑「全 UI 审计（门禁筛选）→ 实现」流程，新增 `src/ModernScreenShot.App/UiMotion.cs` 共享动效令牌（冻结 KeySpline 强 ease-out `(0.23,1,0.32,1)`；`Suppress` 全局开关供诊断渲染确定性；减弱动效=丢弃 transform 保留 opacity，读 `SystemParameters.ClientAreaAnimation`；一律 SplineDoubleKeyFrame+SnapshotAndReplace，Completed 落终值不留半途）。8 个过门禁的动画点：

| 位置 | 动效 | 门禁理由 |
| --- | --- | --- |
| overlay 工具栏（OverlayWindow.UpdateToolbar） | Opacity 0→1 + 内层 Y+6→0，150ms | 选区完成唯一一次硬 pop；反馈+空间一致性；热键本体/选区拖拽**不**动画 |
| 编辑器面板互斥切换（EditorWindow.ToggleEffects） | 入场面板 dy8 180ms，出场即时 | 防跳变；`_ready` 守卫 |
| 编辑器窗口首次显示 | Window.Opacity 150ms | 几十次/天取短档 |
| 设置/历史窗口首次显示 | Window.Opacity 200ms | 偶尔档 |
| 设置页导航切换 | 新页 FadeIn 180ms 无位移 | 页面平级不滑动；构造期/诊断 SelectPage 不动画 |
| 色板弹层（ColorPickerButton） | PopIn 0.96→1 origin(0.5,0) 150ms | 从触发钮方向弹出，绝不从 scale(0) |
| Pin 窗体弹出 | PopIn 0.96 150ms | 同上 |
| Pin 成功提示条（ShowToast） | 入 dy8 150ms；仅 2s 超时路径 FadeOut 120ms→Close | 用户主动关闭保持即时 |
| 悬浮缩略图（收编存量） | FadeIn 220 + Y24 260ms | 参数与原实现逐位一致，改走令牌 |

**明确否决**（审计 Part 2）：overlay 遮罩/本体入场（热键 100+/天+遮罩需当帧就位）、AnnotationCanvas 选中/绘制动效（1:1 操作精度+OnRender 热路径）、效果预览 crossfade（双大位图 OOM 风险）、历史卡片 stagger（后台 Refresh 会整墙重放）、倒计时数字逐秒弹跳（用户正在读的数据）。overlay 帧循环 `OnRenderingFrame` 零新增分配（工具栏动画走 BeginAnimation 合成时钟）。

门禁全绿：build 0/0、harness ALL PASSED、smoke 0、i18n 236=236（无新增键）。**待真机观感确认**：①编辑器窗淡入与 OS 激活动画的叠加；②缩略图换曲线后是否仍贴 macOS 原味；③overlay 工具栏同会话反复 隐藏→显示 的 150ms 重播是否烦人（若烦可加"只播首显"开关）；④色板弹层打开动画与点击外部关闭的竞态观感；⑤Pin PopIn 幅度。

### 5 轮 review-animations 审查循环（2026-09-29，同日串行子代理）

按 review-animations（十条标准）+ improve-animations（自包含修复计划）跑 5 轮「审查→修复」，每轮修后 build 0/0：

- **轮 1（全量十条标准）**：Approve 零缺陷（ease-in/scale(0)/布局动画/键盘高频/每帧分配全部 repo 级 grep 实证排除）。
- **轮 2（边界路径追踪）**：修 1 项——`FadeSlideIn` 的 `delayMs>0` 时 opacity 腿未在延迟窗口播种起始姿态（transform 腿有、opacity 腿无，启用 stagger 即"满透明度悬停偏移位后眨黑"）。修复=UiMotion.cs 播种块（先清 held animation 再落 0）；现网无 delayMs 调用点，属休眠契约修复，零行为差。
- **轮 3（性能/内存/内聚）**：修 1 项——PinWindow 连续 ShowToast（复制后 2s 内保存）两条 toast 同坐标叠印鬼影。改为**单条可复用 toast**：可见时就地换文案+计时重置（不重播入场）；字段在 Tick 置空，淡出途中来的新提示走新建路径（与 120ms 残影共存，不可察）。
- **轮 4（新鲜眼重扫）**：修 2 项——①toast 定位只在 Loaded 做一次，SizeToContent 窗口换长文案（保存路径）朝右/下长出屏幕右缘约 250 DIP：新增 `PlaceToast()`（含 Math.Max 钳制）挂 Loaded+**SizeChanged**（布局通道内渲染前触发，无错误位置帧）；②toast 恒用主屏 WorkArea 而 pin 已跨屏定位：改 `_toastWorkArea`（构造时按 pin 所在屏换算 DIP），反馈跟随用户视线所在屏。
- **轮 5（终审回归）**：Approve 零新缺陷；3 组修复台账逐项核验闭合（复用分支×SizeChanged 重锚定×FadeOut 字段置空的状态序列全覆盖推演）。

轮 5 产出最终动效清单（13 项含既有吸附缓动与"倒计时无动效"决定）；已核清的可接受裁量：缩略图入场 dy24 的 HWND 底缘裁切在 opacity≈0.65 前不可察（且其 DropShadowEffect 本就静态被裁——静态外观问题另记）、overlay 跨屏把手抖动的工具条复淡（触发面极窄，若被报加一行容差带）、AllowsTransparency 表面入场瞬态整面重渲（平台代价，仅创建时一次）。

### 后续：设置页切换动画升级（2026-09-29，用户反馈「切换分类没有动画」）

用户在跑 16:15 构建（含 180ms 纯淡入）仍感知不到——纯透明度淡入在暗色面板上不可感，反馈成立。升级两处：①页内容切换 `FadeIn(180)` → **`FadeSlideIn(dy:10, 220ms)`**（淡入+上滑，FadeSlideIn 强制 from-0 每次重播）；②左侧导航选中反馈：**`UiMotion.GrowY`（新工厂，ScaleY 0→1 强制 from）** 让选中项的 3px accent 竖条从下缘向上生长 160ms（XAML Bar 加 `RenderTransformOrigin="0.5,1"`，代码经 ItemContainerGenerator+Template.FindName 取条）。门禁语义不变：Suppress 落终值（--render-settings 确定性）、减弱动效无位移（页纯淡入/竖条直接满高）、SnapshotAndReplace 连点可中断。构建 0/0、smoke 0，已重启用户实例（PID 77820，--show-settings 直开设置页）。

### PowerToys 风格快捷键编辑（2026-09-29，用户指图 1/图 2 点名复刻）

参照 microsoft/PowerToys `src/settings-ui/Settings.UI/SettingsXAML/Controls/ShortcutControl/`（ShortcutControl + ShortcutDialogContentControl，源码已存临时目录研究；采纳其呈现/交互模型，未引入其 WinUI 低级键盘钩子）：**①快捷键页行**=图 2 式「动作名 + 键帽 chips + 铅笔」（新 `Shell/HotkeyChips.cs`：accent 蓝底圆角 chips、Win=\uE782/方向键/Enter 字形映射、空绑定=占位「设置快捷键」、两档尺寸），行级「已被占用」标记保留；**②编辑对话框**（新 `Settings/HotkeyEditDialog.cs`）=图 1 式：提示行「快捷键应以 Windows 键、Ctrl、Alt 或 Shift 开头」→ 大圆角捕获区实时 chips → 重置（恢复默认绑定）/清除 → AltGr（Ctrl+Alt 无 Win）与应用内冲突警告行 → 保存（accent，仅有效组合可用）/取消。校验规则逐条对齐 PowerToys：仅修饰键=等待不算错、裸键=红错误禁保存、冲突仅警告。**连带修复隐患**：RegisterHotKey 系统级吞键——旧 HotkeyRecorder 录制时已绑定的组合键根本到不了控件（会直接触发全局热键）；新对话框开= `HotkeyService.Suspend()`（新增公开方法，=UnregisterAll），任何出口 finally `Resume()`。**解绑能力保留**（对齐 PowerToys 的清除语义）：清除→保存空绑定=解绑（ReRegister 跳过空绑定），带黄色提示行；旧版 Esc 清空行为由此迁移。i18n +8 键（243→244 双语齐平，复用 Action.Save/Cancel）。HotkeyRecorder.cs 已删（0 引用）。**已知限制**：录制中按 Win 仍会弹开始菜单（屏蔽需低级钩子，未引入）；改键在设置窗关闭时统一落盘生效（既有单一保存点设计）；被系统占用的组合只能在保存后经 FailedActions 行标记+toast 感知（应用内重复已在对话框内实时警告）。门禁：build 0/0、i18n 244=244、smoke 0、harness ALL PASSED、--render-settings 明暗两主题渲染通过。

### PowerToys 风格快捷键编辑·视觉回归本应用风格（2026-09-29，用户反馈「太丑、和原来的差别太大」）

用户否定第一版视觉（PowerToys 满屏 accent 蓝实心键帽与本应用克制的 Fluent 卡片风格冲突）。**交互模型（chips+编辑对话框+入口）保留，视觉全部回归本应用语言**：①键帽=Win11 设置风描边键帽——`SubtleFillColorSecondaryBrush` 底+1px `CardStrokeColorDefaultBrush`+`TextFillColorPrimaryBrush` SemiBold（错误态=Critical 文字描边、保持 subtle 底），紧凑档 30 宽/12 号；②快捷键页行=与同页其他卡片**同构的 `ui:CardControl`**（62 DIP 行高经 MSS_DUMP_TREE 探针与对照卡一致），右侧 subtle chips+透明外观铅笔钮；③对话框=捕获区 subtle+CardStroke 边框、重置/清除次要色 hover 变 Primary、保存钮照抄设置窗底部 Primary 写法（accent 只在这）、右对齐底栏（取消/保存）、520 宽。探针自证：渲染明暗两主题整页内容区 accent 蓝像素=0、键帽描边/填充/文字像素带齐全、直方图为描边键帽灰阶特征；WPF-UI 4.3.0 双主题资源键在位（无需硬编码红）。**待真机确认**：对话框本体观感（诊断渲染点不开对话框）、错误/占位态可读性、hover 手感。门禁：build 0/0、i18n 244=244、smoke 0。渲染证据 `tools/verify_out/ui/hotkeys_run_*`。

### 统一自定义标题栏（2026-09-29，用户要求）

新共享控件 `Controls/AppTitleBar.cs`（纯 C#，Border 派生，零 WPF-UI 子类化）：36px 高、左侧窗口标题（复用现有 i18n 键）、右侧 最小化/最大化/关闭 三钮（40×28、Segoe 字形 \uE921/\uE923(还原\uE922)/\uE8BB，hover=SubtleFillColorSecondary、关闭钮 hover=SystemFillColorCritical 底），标题区拖动 DragMove（try/catch）+双击最大化，全部 DynamicResource 随主题。接线走 `System.Windows.Shell.WindowChrome`（CaptionHeight=36/ResizeBorder=6/GlassFrame=0/UseAeroCaptionButtons=False）——保住 Win11 圆角/贴靠/系统阴影，按钮标 IsHitTestVisibleInChrome。**覆盖 4 窗口**：设置（Min+Max+Close）、编辑器（同，标题栏独立行不挤 780 最小宽的动作栏）、历史（同）、快捷键编辑对话框（**仅 Close+可拖动**——顺带补上了它此前没有关闭钮的缺口，PopIn 动画保留）。探针自证：MSS_DUMP_TREE 亮暗 6 次渲染（bar 880×36 在位、内容区整体下移 36、按钮 40×28）、像素探针（标题带=ApplicationBackgroundBrush 随主题翻转、y=35 有 1px CardStroke 底边、标题文字/按钮字形像素齐全）、对话框 STA harness 两轮布局稳定 520×262（WindowChrome+AllowsTransparency+SizeToContent=Height 无增长循环）、verify_theme.ps1 A1/A3 过（A2 失败为既有问题——断言指向已删除的 HotkeyRecorder）。**待真机确认**：拖动/双击最大化/贴靠/圆角阴影、关闭钮红底观感、对话框拖动与 PopIn 叠加。门禁：build 0/0、i18n 244=244、smoke 0、harness ALL PASSED。

### 自定义标题栏·圆角修复（2026-09-29，用户报「特么没圆角的」）

`WindowChrome` 替换标准标题栏后 **Win11 DWM 不会自动保留圆角**（丢了标准非客户区即按方角处理——首版报告的"理论保留"是错的）。修法：`AppTitleBar.Attach` 在 `SourceInitialized` 时显式 `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_ROUND=2)`（build<22000 跳过、try/catch 兜底）。验证：DwmGetWindowAttribute 回读运行中设置窗 = CORNER_PREFERENCE 2 / HR 0。**教训：凡是声称"WindowChrome 保留系统圆角/阴影"的说法必须 DwmGetWindowAttribute 回读实证，不能靠推断。**快捷键编辑对话框走 AllowsTransparency 分层窗（DWM 圆角不适用），其圆角由自绘卡片圆弧承担，无需此修。

### 自定义标题栏·最大化字形反了（2026-09-29，用户报「做反了」）

Segoe MDL2/Fluent 码位记错：**\uE922=ChromeMaximize（单框）、\uE923=ChromeRestore（双框叠放）**，首版把两个常量标反，还原态显示双框、最大化态显示单框。已交换码位（状态机逻辑本就正确：Maximized→显示 Restore 字形）。验证字形码位别靠记忆——用 PowerShell 渲染单字形或对照系统标题栏截图核对。

### 自定义标题栏 5 轮审查循环（2026-09-29，Goal 模式）

对 `Controls/AppTitleBar.cs` + 4 接线点跑 5 轮差异化透镜「审查→修复」，每轮修后 build 0/0：

- **轮 1（状态机/生命周期/事件）**：修 2——①P2 死按钮：`UpdateButtons` 只看 ShowXxx，ResizeMode=NoResize 时 min/max 可见却点击被守卫吞（死按钮），改为可见性与 ResizeMode 一致（HookWindow 拿到 _window 后重算）；②P3 历史窗双关闭入口（工具栏 close + 标题栏 ×），删工具栏 close。
- **轮 2（WindowChrome/DWM/几何）**：修 1 P1 真 bug——**WindowChrome 不处理 WM_GETMINMAXINFO，最大化窗口外扩 ~8px、region 裁掉最外圈内容 → 右上角关闭钮被切**（即用户之前反馈的"最大化黑边"根因）。加 MaximizeHook（MonitorFromWindow+GetMonitorInfo 把 ptMaxSize/Position/TrackSize 钉在工作区，物理像素、mixed-DPI 安全）。附带：分层窗（对话框）跳过 DWM 圆角死调用。
- **轮 3（主题/无障碍/视觉）**：修 3——①P2 关闭钮 hover 用主题 SystemFillColorCriticalBrush，暗色主题是浅红 #FF99A4、白字对比度仅 1.9:1（低于 3:1），改固定深红 #C42B1C（Win11 值）；②三按钮加 AutomationProperties.Name（+Action.Minimize/Maximize/Restore 键，i18n 244→247）；③对话框圆角红块溢出：`outer` 加 ClipToBounds+RectangleGeometry(radius 7) 裁子内容。
- **轮 4（新鲜眼+攻击前三轮修复）**：修 1 Low——MaximizeHook 误给不可最大化的对话框（NoResize+SizeToContent）钉 ptMaxTrackSize，小屏/高 DPI 会截断对话框底部；改为 `ResizeMode is NoResize or CanMinimize` 时跳过 AddHook；并把对话框 ResizeMode 赋值挪到 Attach 之前（稳健性）。攻击核实前三轮其余修复全对（MINMAXINFO 字段序、非泄漏、圆角 clip）。
- **轮 5（终审回归）**：10 项台账逐项 grep 核验闭合，Approve 零功能缺陷（仅清一条陈旧文档注释）。

渲染探针（MSS_DUMP_TREE）确认标题栏 `AppTitleBar [0,0 880x36]` 在位、内容区 `[0,36 880x604]` 正确下移。**待真机确认**：最大化后关闭钮完整可点无裁切（P1 修复效果）、暗色关闭钮 hover 深红白字可读、对话框圆角处红块不溢出、多屏最大化钉对屏。门禁：build 0/0、i18n 247=247、smoke 0、harness ALL PASSED。**已知低价值限制**：标题栏三按钮无障碍名一次性设、运行时语言切换不刷新（历史窗标题同）。

### 工具栏选择图标"没显示全"（2026-09-30，用户截图指认）

用户对照工具栏截图问"鼠标指针是不是没显示全"——**实锤：overlay 的 `SelectIcon` 多边形少了闭合边**（编辑器版 `EditorIcons.Select` 有 `LineTo(4,2)` 闭合，overlay 版没有），细线描边渲染成"竖线+断翼"两截。修复：①两处统一改为 **Snipaste 风格填充光标**（`Fill=White` 实心箭头，几何相同，工具栏词汇表一致）；②连带修真实光标形状 bug——`ApplyCursorShape` 在鼠标位于工具栏上时落到 `_ => Cross` 兜底（选了标注工具后更全程 Cross），工具栏空白区显示十字线；现在工具栏可见且鼠标悬停其上时 `Cursor=Arrow`（按钮自身仍是 Hand）。截图底部/顶部的"碎片"经查是透过 92% 不透明工具栏看到的任务栏内容（右上角 06:54 即任务栏时钟），非缺陷。build 0/0、smoke 0。

## Mac 窗口阴影观感重调（2026-09-29 凌晨，用户报「阴影像几何图形、不像 Windows 阴影」）

用户发来的捕获样张像素剖面证实：阴影管线本身工作正常（模糊真实生效、alpha 随形状衰减），但 `MacShadow` 预设参数失调——**Opacity=0.20 + BlurRadius=52** 把阴影摊成 80px 宽、贴边峰值仅 ~25/255（10%）的极淡一圈；再叠 `Frame.Padding=56`（模糊边距已含 66-94px/侧），四周总留白达 **122~150px**，窗口只占截图 73%——观感即「窗口贴在一张透明大卡片上，卡片边界是个几何图形」。重调（`BuiltInPresets.MacShadow`）：

- BlurRadius 52→**38**（box≈σ19，~60px 内衰减完，比 OS 略宽的 mac 柔和衰减；首版 26 偏紧，按用户反馈放大）
- Opacity 0.20→**0.40**（贴边 ~47/255=18%，底边正下方 ~40%，任何背景可见）
- Distance 14→**14**；Padding 56→**0**（RenderShadowOnly 的模糊边距就是留白，额外 padding 只会放大「卡片感」）
- 四周留白 272px→**118px**（左右 59 / 上 45 / 下 73）

harness 新增数值断言防回归（tools/Harness core）：留白=118px、贴边 α≥25、单调外向衰减、55px 处 ≤12、**镂空源阴影衰减 ≥2×（证明阴影随窗口形状计算而非矩形贴片）**、Compose 不额外加边距；样图 `out/mac_on_white.png` / `mac_on_dark.png` 明暗双背景目检通过。三条烘焙路径（窗口捕获/吸附区域/活动窗口）都走同一预设，一次覆盖。**仍需真机复测**：用户实际窗口截图观感。

## 编辑器 UI 三轮「修复→检查」（2026-09-28 深夜）

针对用户报告的「元素重叠、元素显示不全」对 EditorWindow 做三轮渲染矩阵检查（`--render-editor` + 新增 MSS_RENDER_W/H、MSS_RENDER_EDITOR_SELECT/-TOOL/-FX 探针；脚本 `tools/_render_editor_state.ps1`，产物在 `tools/verify_out/r*`），共修复 8 项，门禁通过（构建 0/0、harness ALL PASS、smoke 0、i18n 207 键齐平）：

1. **P1 效果面板与属性面板重叠**：两者同处一个 Grid 单元格，`ToggleEffects` 只切换效果面板可见性、从不隐藏属性面板——打开效果后两套控件相互叠印。现在互斥切换（`_propertiesScroll`）。
2. **P1 底部动作栏最小宽度溢出**：左行动作是横向 StackPanel，MinWidth=780 时「另存为/贴到屏幕/效果/打开文件夹」整体被裁掉不可达。改 WrapPanel，窄窗口自动换行（浅/暗、中/英均验证）。
3. **P2 效果面板预设行裁切**：「删除预设」按钮被 254px 面板列宽裁掉。预设行改 WrapPanel。
4. **P2 序号工具属性面板空白**：Step 工具/选中 StepItem 时除颜色外无任何控件。补「半径」滑杆（复用 `Prop.Radius` 键，键数不变）。
5. **P2 文字项选中手柄塌陷**：`MeasuredWidth=0` 的文档（程序创建/旧存档）选中框退化到文字原点的小方块（命中测试/擦除/移动同步失真）。`LoadDocument` 对未测量文字项补测，已存测量值不动。
6. **P3 选择虚线低对比**：选中虚线纯白，浅色截图上不可见。改黑衬底+白虚线双描边。
7. **P3 诊断渲染进程偶发挂起**：优雅 Shutdown 与在途预览 compose 竞态，`--render-editor` 写完 PNG 后进程不退。诊断路径改 `Environment.Exit`（一次性探针进程，无清理负担）。
8. 探针时序：快照与 `DiagnosticPrepare` 同 tick 同步执行，60ms 防抖预览永远赶不上快照 → 打开效果面板时快照前加 900ms settle。

覆盖状态：暗/亮主题 × 中/英 × 默认/最小(780×520) × 属性/效果面板 × 15 工具面板 × 选中项(矩形/文字/序号) × 阴影开/关 × 渐变/反射展开。**仍需真机手测**：画布拖拽/缩放/文字内联编辑、裁剪草稿、橡皮擦手势、Pin/贴图窗口。

## 编辑器棋盘格背景 + PrintWindow 黑边备忘（2026-09-28 晚，5a06c1f / db8de87 之后）

- **用户报"编辑器窗口显示有问题"**：mac 阴影截图在编辑器里显示成一块平板灰卡。根因 = `AnnotationCanvas.OnRender` 用纯 `Brushes.DimGray` 做画布背景，带 alpha 的烘焙截图（透明四周 + 阴影）叠在上面完全看不出透明与阴影。已改为**透明棋盘格**（`AnnotationRenderer.CheckerboardBrush`，16px 屏幕空间纹理，与效果预览同一画刷，Frozen 共享单例）；真机 E2E 断言 checker 像素 >5800、DimGray=0。**编辑器布局本身正常**（工具条/属性面板/动作栏齐全，`--render-editor` 渲染可证）——用户截图里"工具条消失"是 Snipaste 截图区域裁掉了窗口顶部、且暗色主题下工具条与画布底色接近所致。
- **PrintWindow 未绘制边缘烘成黑边（既有限制，本次测试中显形）**：`TryPrintWindow` 的 DIB 尺寸=窗口矩形，个别窗口 WM_PRINT 只绘制部分区域（DPI 不感知宿主、部分 GPU/UWP 内容面），未绘制像素为黑，`forceOpaque` 把它抬成不透明黑边烘进内容右/下边缘。真实应用（Chrome/Office/Explorer 等 DPI 感知窗口）未见此问题；测试用的 DPI 不感知 WinForms 宿主会复现（右 ~14px/下 ~11px 黑边）。若真机遇到可反馈，方向是按 client rect 二次裁剪或检测全黑行列收缩。

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
