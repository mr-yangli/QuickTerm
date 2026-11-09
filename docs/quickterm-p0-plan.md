# QuickTerm — P0 计划（三周闸门）

**QuickTerm** 是 QuickNet 第六家的目标形态：Windows / Linux / macOS 三平台的终端仿真器 + SSH 客户端 + SFTP 文件传输，对标 Termora（Kotlin/JVM + Compose Desktop，XTerm 控制序列的**子集**实现）。

这份文档只解决一件事：**在写第二行代码之前，把"P0 到底要回答什么问题、用什么读数判它真假"钉死**。功能清单往后放，因为这类工具的失败模式从来不是"少做了个功能"，而是"滚动到第三屏开始掉帧、vim 里出现残影、CJK 对不齐"——这些只能在闸门读数里现形，在功能清单里永远看不见。

---

## 0. P0 要回答的唯一问题

> .NET 10 + 自绘 GUI，能不能在**真实靶子**上做出一个不掉帧、不吃内存、跑得动 vim/htop/tmux 的终端？

答"能"需要证据，证据 = 第 3 节那三颗闸门的读数。P0 不做 SSH——SSH 是网络与协议活，与本问题无关，而它一旦进来就会盖住渲染层的真实成本（网速天然限速，掉帧永远看不出来）。

**P0 的合法结局有两种**：读数达标继续投入，或读数不达标改选技术栈（Rust/C 内核 + C# 壳）。"做完了但没人量过"不是合法结局。

---

## 1. 范围

**P0 内**：本地 PTY（ConPTY / openpty）∶ VT100/XTerm 子集仿真 ∶ 单控件自绘渲染 + 键盘输入 ∶ CJK 宽字符与 DEC 特殊图形 ∶ 回滚缓冲 ∶ 选择与复制 ∶ 离线测试册 + 吞吐/帧时基准。

**P0 外**（明确不偷偷做，各自记账）：SSH / SFTP / 端口转发 / 隧道 ∶ 多标签与分屏 ∶ 主题与配置 UI ∶ 密钥管理器 ∶ 六格以上快速命令 ∶ 云端同步 ∶ sixel / kitty 图形协议 ∶ Windows 老式 console API 回退（ConPTY 要 Win10 1809+，更老直接判不支持——目标用户是开发机，不是 Win7 兼容层）。

---

## 2. 选型与依据

| 层 | 选型 | 依据 | 退路 |
| --- | --- | --- | --- |
| GUI | Avalonia 12.1.5 | 本机 NuGet 实测可达（12 系列已 GA），Skia 后端三平台一致；MAUI 的桌面档与 WinForms/WPF 的单平台都不合格 | 无（这一层不换，换=重写） |
| 渲染 | 一个自定义 `Control` + Avalonia 自带 Skia，不引 SkiaSharp 第二份 | 一格一控件的 XAML 树是这类工具的头号死因；两份 Skia 会撞原生库版本 | W2 读数若不满意 → 换 SkiaSharp 直取字形图集 |
| PTY | 直接 P/Invoke `CreatePseudoConsole` / libc，**不引 Pty.Net** | 家规：内核零 NuGet 依赖；且托管封装 fork 的 Unix 侧不可靠 | W1 决定是否为 Unix 自带 20 行 native forkpty helper |
| 状态机 | 自研 per-byte 表驱动 parser | .NET 生态没有可引用的高性能终端仿真核（Terminal.Gui 是 TUI 套件，不是仿真器） | 无——这一颗就是 P0 的主要工作量 |
| SSH（P1） | 候选 Tmds.Ssh ∶ SSH.NET ∶ libssh2 P/Invoke ∶ 自有 transport（2026-11-07 包事实与排序见 `quickterm-notes.md` 的 SSH 候选评估节） | **P0 不给结论**，W3 用一份 200 MB SFTP 原型基准逼出数字。AOT 定为目标后判据换形：AOT 判死的是反射式托管件，**不是原生库**（P/Invoke 与 AOT 相性良好），所以托管候选（`Tmds.Ssh`/`SSH.NET`）要先自证裁剪/AOT 干净度，`libssh2` 的代价落在多 RID 原生件打包，自研 transport 是唯一两头都干净也最贵的一档 | 四选一在 W3 末定档，不在 P0 中途改 |

家规落法：`QuickTerm.Terminal` 与 `QuickTerm.Pty` 两颗零 NuGet 依赖、可 AOT；Avalonia 只出现在 `QuickTerm.App`（`IsPackable=false`，永不发包）。这条坚持把"渲染"与"文档模型"分家。

---

## 3. 三周拆解

### W1 — 内核（无 GUI）

任务：per-byte parser 状态机（Ground/Escape/CSI/OSC/DCS + UTF-8 增量解码）∶ cell 网格与滚动区 ∶ damage 行区间 ∶ 宽字符与挂起换行(pending wrap) ∶ 回滚环形缓冲 + 行对象池 ∶ ConPTY 读循环 ∶ 离线测试册。

闸门（**全过才进 W2**，不过就在此改判技术栈）：

1. **写放大**：`yes` 全速 5 分钟——CPU ≤ 1 核、内存不涨。yes 的字节全落同一行，专门咬"每字节重画整屏"这类错。
2. **真靶子**：`htop`、`vim`（一个 2000 行文件 + 拖选 + 滚动）、`tmux` 新会话、`less` 一个 200 MB 日志——各 60 s，**无残影**。残影是 damage 漏标唯一能被肉眼抓住的症状，比任何单元测试都诚实。
3. **吞吐**：解析基准 ≥ 150 MB/s。**（这一格 2026-11-05 判定为钉错，已由 W2 的 FPS 判据取代，只留作回归哨兵——实测 104.7 MiB/s 不再算不达标。）**
4. **分配**：≤ **64 KB / MB 输入** 的 GC 分配（行池与栈上参数的直接推论）。
5. **册**：离线 ≥ 60 颗全绿，且逐颗能说出"摘掉哪一行会红"——按 QuickSql 的 A/B 反验口径。

### W2 — 渲染与输入（**2026-11-05 定：做到能日用**）

范围（"能日用"的具体清单，缺一项就不算收口）：`TerminalControl` 单控件自绘（脏行范围重绘 **已落 2026-11-05** ∶ 字形图集缓存**未做** ∶ 光标叠层已落、选区叠层未做）∶ 键盘到字节（DECCKM、应用 keypad、Ctrl/Alt、功能键、括号粘贴）∶ **鼠标上报**（1000/1002/1003/1006 的模式位已存，字节还没发出去）∶ **选择与复制**（含按词/按行扩展）∶ **OSC 52 落地**（解 base64、写剪贴板、以及 xterm-paste 那套查询）∶ **滚轮回滚缓冲区**（alt screen 下按 `?1049` 与 `MouseAlternateScroll` 的规矩改发方向键）∶ **调色板归属**（16 色 + 256 色 + `OSC 4/10/11` 改写与 `OSC 104` 复位，现在引擎里根本没有调色板）∶ **字体与主题设置**（含 CJK 界面字体回退链，与终端字体分两件事）∶ IME 组合输入 ∶ resize 与 DPI ∶ **DEC 2026 合帧**（今天只存位）∶ 中英两档的资源表。

闸门（**判据换形，2026-11-05 定**）：

1. **FPS 是唯一判据**：1920×1080、`cat` 一个 200 MB 文本下 FPS ≥ 55、CPU ≤ 25%（5700G 档），帧时 P95 ≤ 8 ms。
2. 观感三件：CJK + emoji + `ESC ( 0` 盒线三种对齐靶子零错位；IME 组合不落残字；DPI 150% / 200% 无半像素偏移。
3. **解析吞吐 104.7 MiB/s 降为参考读数，不再是闸门**。这一格要老实写着：W1 当时把 150 MiB/s 钉成闸门是**钉错了**——用户能感知的是帧时与掉帧，不是每秒解析多少字节，而吞吐高到 150 也救不了一个每帧重画整屏的渲染层。留它的唯一用途是**回归哨兵**（大跌说明状态机被改坏了），达标与否不再决定收口。

决策点（**2026-11-05 按"性能优先、不满足者自研"定方向，同日 W2-c 落了前半截并有数**）：字形缓存与 damage-rect 两格都判为"现成形状不达标"→ **自研**——控件自持 render target，脏行区间补画，字形走自带图集（一次烘焙、按 (字形,字号) 取，不再每帧新建 `FormattedText`）。

**已落地的一半（2026-11-05，离屏读数）**：`TerminalView` 自持一块 `RenderTargetBitmap`，`Render` 按需选三档——整面重建 ∶ 只补引擎报的脏行 ∶ 无损伤时只 blit；光标与 fps 叠层画在 blit 之上不进面。原计划的"脏行区间**拷贝**回贴"被 12 的 `CreateDrawingContext(clear: false)`（原地补画，文档明写保留上帧内容，实测复核过）顶掉，因此**不需要双面包屑**，每帧少一次整面拷贝、也少一块面（1280×720 省 3.7 MB，4K 省 33 MB）。1280×720 @1x、200 帧档的数（两跑 ±5%）：进度条一档 **~11.9 → 0.48 ms/帧（24×）**、零格变化一档 0.40 ms（= 每帧固定底价，其中 blit 占 0.31–0.35 ms）、整屏 flood ~11.9 ms ≈ **84 fps 上限**、~975 KiB/帧。**未落地的字形图集（#16）在 2026-11-06 换了一次判法**：先按"离屏对照表"选了行内容缓存，实现完再按**控件内分段计时**复测——烘焙一行 0.52 ms、直接画一行 0.28 ms，**缓存比它要省的事更贵**，于是整块撤掉（三种形状：无缓存 11.08 ms ∶ 共享图集 12.77 ms ∶ 每行一块位图 13.90 ms；命中率 18/42 是真的，省不下来也是真的）。对照表那两版模型误差也记在 notes：第一版把命中率设成 41/42（真实 flood 每帧 24 行新内容），第二版的 bake 少画了底色与 floor。**剩下的成本形状很清楚**：整屏 11.08 ms 里 **write 10.23（93%）· blit 0.46（4%）**，全部出自每 run 现场 `new FormattedText`。所以 #16 的正解不是缓存，是**换提交方式**——Skia 一次成批字形（`DrawGlyphs`/glyph id 数组，一行一次调用）或把排版挪出 UI 线程（W3）；这两条各带一个依赖决定（直接依赖 SkiaSharp ∶ 自定义合成视觉，后者还能顺手把真窗口那 ~9 ms 的**面→GPU 上传**一起吃掉——注意它不是合成器的固定税，离屏同一次拷贝只 0.46 ms）。定档之前不写半套。**2026-11-06 桌面第三跑的闸门算术**：真窗口同一档叠层读到 **21.00 ms / rows 42**，比离屏 11.08 ms 贵约 1.9×，多出来的 ~9 ms 是**我们自己的面→GPU 上传**（离屏把同一次拷贝单独计时只有 0.46 ms，所以它不是合成器的固定税——能不能吃掉归自定义合成视觉那一档答）；叠层那个数是 EMA 不是 P95。按 21 ms/帧算整屏重画上限约 **48 fps < 55**，要 P95 ≤ 8 ms 就得把 write 从 10.23 ms 压到 ~3 ms，即 **3.4×**。⚠ **这段当晚即作废**，两处方法错：21 ms 是被启动帧污染的 EMA（同一控件单帧 write 实测 2.0 ms），而离屏那 10-12 ms 走的是 Skia **CPU raster** 后端、比真窗口的 GPU 面慢 5 倍，所以"上限证据"这个说法不成立。叠层已改成报 **med/p95/max（最近 128 帧）**，#16 是否要做由那一行 `ms p95` 定：≤8 ms 就关掉这个任务、不引 SkiaSharp 依赖；15-20 ms 再开做。parser 挪出 UI 线程（双缓冲 damage 合并）值不值仍等复测比值；逐颗反验清单（17）要不要补仍未定。

### W3 — 判生死

任务：把 parser 挪出 UI 线程（双缓冲 damage 合并），与 W2 同靶复测并给出比值 ∶ SSH 三候选的 200 MB 传输原型 ∶ 打包面（Win 单文件、Linux self-contained、macOS `.app` + 签名）。

闸门：三平台各起一次，W1/W2 读数复现 ±10%；交出一份**书面结论**——继续投，或改 Rust 内核。

---

## 4. 已知缺口与债（P0 不遮丑，逐条给后果与还债窗口）

- **resize 不 re-flow**：~~软换行的行不重接，缩窗口会截字~~ **已还（2026-11-06）**：`Terminal.ReflowMain` 按 `Wrapped` 位接回逻辑行再按新宽重切，alt 屏与自定义滚动区按设计不重切；实测代价 = 满环历史时每次 15.77 ms（拖动每帧一次，能跟手但变沉），压法与三条边界记在 `quickterm-notes.md` 的 reflow 一节。
- **组合字符直接丢**：重音符、变体选择符不叠加。后果：越南语/部分 emoji 观感错。W2 之后，与选区逻辑一起做（要先定 grapheme 边界策略）。
- **DCS / sixel 吞掉不报**：图像类程序显示空白块。P1 定档（要么支持，要么显式拒绝并报一行诊断）。
- **鼠标上报未接**：1000/1002/1003/1006 的模式位已存，字节能不能发出去是 W2 的活。
- **OSC 52 只发事件**：base64 解码与写剪贴板未接——对 ssh 用户是显性缺失，W2 补。
- **DEC 2026（同步输出）只存位**：没有"等一帧再画"，TUI 撕裂的最优解还欠着；W2 与帧合并一起做。
- **ambiguous 宽度固定按窄**：与 `wcwidth` 默认一致，简中场景要配置项，P1。
- **宽表是手写子集**：`UnicodeWidth` 的 W/F 区间是人肉挑的，不是 EastAsianWidth.txt 生成的。W1 第一颗待办就是换成生成物 + 逐区间回归。
- **Unix PTY 借 `script -qfc` 包裹**：SIGWINCH 尺寸语义与退出码有折损。W1 末决定要不要自带 native helper。

---

## 5. 本轮记账（2026-11-05 · W1 那一周落地了什么）

已落地并过闸：

- `term/Directory.Build.props` + `term/Directory.Packages.props`（第六家口径；内核零 NuGet 依赖写成家规）
- `term/src/QuickTerm.Terminal/`：`VtParser.cs`（per-byte 状态机：UTF-8 增量解码、ESC/CSI/OSC/DCS/APC、SGR 含冒号子参数、DEC 特殊图形）、`Terminal.cs`（引擎与公开面）、`Screen.cs`（网格/滚动区/行池）、`TermLine.cs`、`TermCell.cs`、`TerminalColor.cs`、`TerminalDamage.cs`、`UnicodeWidth.cs`
- `term/src/QuickTerm.Pty/`：`ConPtySession.cs`（`CreatePseudoConsole` 直连）、`IPtySession.cs`、`PtySession.cs`（工厂；**Unix 侧只有决策没有实现，调用即抛**）
- `term/tests/QuickTerm.Tests/`：**163 颗，本机 162 绿 + 1 红**（W1 落的 88 颗 + 输入编码 64 颗 + 中英资源契约 10 颗 + 新加的 ConPTY 可用性计数读数 1 颗）。⚠ **那颗红是有意的**：那 5 颗活体 ConPTY 的门原先写成 `if (!PtyAvailable()) return;`，而 xunit v2 没有运行时 skip——`return` 记通过，所以它们从落下那天起就是**空跑**，本机与 testhost 都生不出 console 孩子。新增的那颗把"能不能生孩子"变成看得见的一行：它红的时候，五颗活体一律按未跑读。要在能生孩子的上下文（VS 测试资源管理器 / 桌面会话）里跑一次才算收这一格。
- `term/src/QuickTerm.App/`：纯代码无 XAML 的宿主 `Program`/`App`/`MainWindow`/`TerminalView`，中英资源表 `UiStrings.resx` + `UiStrings.zh-Hans.resx`，接内核新增的 `TerminalInputEncoder`
- `term/src/QuickTerm.App/RenderBench.cs` + `QuickTerm --bench`（W2-c，2026-11-05）：离屏帧成本测量路——`SetupWithoutStarting` 后 `Measure/Arrange` 出控件、喂 `Inject()` 的字节流、画进一块 scratch `RenderTargetBitmap`，三档（flood ∶ 单行 churn ∶ 只有光标）各给 paint/parse/脏行数/分配，并复核两条厂商承诺（嵌套 batch 200 轮、`clear:false` 是否真保留上帧）。**这条是挖出 `ForegroundOf` 死循环的那把铲子**，不是补数的装饰：没有它，那一格要等他在桌面卡死才现形
- `term/benchmarks/QuickTerm.Benchmarks/`：W1 读数的出处（`parse` / `churn` / `pty` 三档），已挂进 `Quick.slnx`
- 整仓 `dotnet build Quick.slnx`：**0 错 0 警**

W1 闸门读数（本机 Release，细账与测法陷阱见 `quickterm-notes.md`）：解析 **104.7 MiB/s**（这一格 2026-11-05 判定为钉错、已由 W2 的 FPS 判据取代，只留作状态机的回归哨兵）∶ 稳态分配 **0.00 KiB/MiB**（闸门 ≤64，达标）∶ 单行重画 **1.00 行/帧**（写放大为零）∶ 册 162 颗（≥60 达标）且做过反验。

W2 剩下没落地的：**调色板与主题（引擎现在根本没有 palette，16/256 色的 rgb 硬编码在控件里）、设置界面、字体与回退、IME 组合输入、字形提交方式（每 run 现建 `FormattedText`；行内容缓存 2026-11-06 试过又撤，见 §3 的 #16 算术）、DEC 2026 合帧、真窗口 FPS**。W2-d 那半格 2026-11-06 已收：**拖选/词选/行选、高亮进绘制、Ctrl+Shift+C 复制、Ctrl+Shift+V 括号粘贴、OSC 52 写剪贴板**都落了，并且用 `--bench select` 在本机判过（交回的字符串把软换行重接成一行，PNG 里高亮落在该落的列上）——还欠的三格记在账本 W2-d 一节末尾。已经收掉的两格别重复计：damage 现在**既决定要不要重绘也决定重画哪几行**（W2-c），spawn 失败**已在网格里说人话**（`PtyStartFailed`，中英各一份）。本机证不了的格子写明白是未知：窗口观感、**真窗口的 FPS 与面→GPU 上传成本**、DPI 150%/200% 的半像素——离屏那张表是这些读数的上限证据，不是达标证明；控件叠层现在印 `fps · ms · write · blit · rows · 输入链 · sb/off/top · sel/clip · 列×行`，分段读数直接在屏上就能看。ConPTY 活体读数依赖能生 console 孩子的上下文：**`dotnet test` 的 testhost 与管道会话起的 exe 都生不出来**（孩子挂到继承的 std 句柄上，pty 管道只收到 16 字节起手式），探针里连"完全不带 pty 的普通 `cmd.exe`"那一档都卡在 `CreateProcessW` 不返回——所以这一格现在由 `ConPty_Is_Available_In_This_Context` 那颗计数读数钉住（本机红），不再靠 `return` 蒙绿。**2026-11-06 上午收窄了这一格的范围**：同一颗 exe 用 `Start-Process` 脱离管道起，pty 正常交付 215 字节，`--pty-capture` + `--replay` 因此成了本机能抓真流、能离线回放的口子（"显示不完整"那颗就是靠它分家成"引擎没错、画错了位置"的）。2026-11-06 凌晨修掉的正是这一路的根因：`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 要用 **HPCON 本身**当 lpValue（文档样例与 Windows Terminal 同形），我们原先传的是 `ref`（托管变量的地址），孩子一律 `0xC0000142`；修后他桌面那颗实例里 `cmd.exe`（PID 43964，父 = QuickTerm 35732）活着、窗口标题被孩子用 OSC 改掉，宿主也收到了 ConPTY 的起手式字节。

---

## 6. 要你拍的几件事

1. ~~**平台最低线**~~ **已定（2026-11-05）**：不设兼容层。取各自当前的现代档——Windows 只走 ConPTY（Win10 1809+ 硬线，**不实现老式 console API 回退**）∶ Linux/macOS 只走 `openpty`/`posix_spawn` 这一档正路，**不用 `script -qfc` 这类"能跑但语义打折"的包裹**∶ P/Invoke 优先 `LibraryImport` + `fixed` 直读而非 `DllImport` + 数组拷贝∶ 内存换速度的结构（行池、回滚环）不为"省内存模式"加开关。理由：**为低平台或低配置写的每一条回退，都要在主路径上付一次分派或拷贝的税**，而这类工具的失败模式恰恰是主路径掉帧。副作用要说全：这条不等于不做性能优化，也不等于不测老机器——它只是说**老环境是"不支持"，不是"跑得差一点"**。
2. ~~**是否要 AOT / 单文件自包含**~~ **已定（2026-11-05）：以 AOT 为目标形态**。据此已经动的手：两颗内核件开 `IsAotCompatible` + `TreatWarningsAsErrors`（照 sql 家的形状），整仓 0 错 0 警、零条 IL 警告；`PublishTrimmed -p:TrimMode=full` 实发一次，20 MB 产物**跑得出读数**。还证不了的：原生 AOT 那层——本机缺 VS 的 C++ 工作负载（link.exe），`PublishAot` 直接报 `Platform linker not found`，所以"出货体积与冷启动"这一档读数等工具链就位再取。**同时更正一处我早先说反的前提**：AOT 不否定 libssh2（P/Invoke 与 AOT 不打架），AOT 判死的是反射式托管件——SSH 选型因此改成"先证托管候选自己 AOT 干净"，而不是"避开原生库"。详见 `quickterm-notes.md` 的 AOT 一节。
3. ~~**仓库位置**~~ **已定（2026-11-05）：暂作 QuickNet 第六家，后期再迁**。本轮实查过耦合面，结论是**只有一处跨边界的构建依赖**：`term/Directory.Build.props` 里那句 `<Import Project="..\Directory.Build.props" />`（取作者/许可/工程页/仓库地址四颗）。除此之外，`term/` 内对另外五族**零 `ProjectReference`、零包耦合**——全树只剩一条注释提到别的族（`同 QuickSql 活库档无靶即退`），是散文不是依赖。**迁移要做的事就五条，逐条可判做没做**：① `Directory.Build.props` 的 Import 换成把四颗属性内联（并把 `RepositoryUrl`/`PackageProjectUrl` 改到新地址）；② 复制一份根 `LICENSE`（MIT，作者署名口径不变）；③ 复制一份根 `.gitattributes`（`* text=auto eol=lf` 这条必须跟着走，否则新仓默认 `core.autocrlf=true` 会把按字节比对的文本件改成 CRLF——根文件头注已经把理由写过一遍，这里不重述）；④ 从 `Quick.slnx` 摘掉三颗（`/src` 两颗、`/tests` 一颗、`/benchmarks` 一颗）及其注释；⑤ README：本仓删第六行占位（现在还没加，等于免费）、新仓起一份。以上没有一条需要改代码——**这条决定所以现在不做任何预备动作**（不预先放 `.gitattributes`、不预先内联属性），放了就变成两处要同步维护的重复文件。
4. ~~**i18n 是否第一天留位**~~ **已定（2026-11-05）：要，第一天就留位**（Termora 现有简中/英/葡三档）。W2 落地按这条写：UI 字符串一律进资源、不落字面量；`NeutralResourcesLanguage` 要标（不标则每次查找先探一遍中性卫星）；**终端网格里的内容不翻译**——那是 shell 的字节流，翻它是错的；CJK 界面文字需要字体回退链，`WithInterFont()` 不覆盖汉字，且这与"终端字体"是两件独立的事；语言档 = 跟随系统 + 手动覆写。

   **这条路本机已经实测（2026-11-05，探针在 %TEMP%，不进仓）**：`ResourceManager` + 卫星程序集在 `IsAotCompatible` 下 **0 条 IL 警告**；`TrimMode=full` 发布后 `zh-Hans/probe.resources.dll` 仍在产物里，裁过的二进制运行交回中文串（编码无关复核命中 `新建连接`、`第 3 / 12 个文件`），publish 20 MB。三个坑都是实测到的，不是推测：① 清单资源名带根命名空间前缀——写 `"Resources.Ui"` 直接 `MissingManifestResourceException`，正确是 `"probe.Resources.Ui"`；② `EnableDefaultResxSourceGeneration=true` 在本机 10.0.403 SDK 上**什么也没生成**（`obj/` 里找不到生成物），所以"强类型访问器"那格现在是**未知**，不能当既定事实写进设计，要另查开关名/是否需引 `Microsoft.CodeAnalysis.ResxSourceGenerator`；③ 控制台按活跃代码页（本机 936）重编码 CJK，**测试断言不能拿 stdout 字节当 UTF-8 的证据**——这一条差点让我把"命中 false"记成失败。

   **语言档已定（2026-11-05）：先中英两档，后期再加**。落法：English 作**中性/源语言**（`Ui.resx`），简中走卫星（`Ui.zh-Hans.resx`，`zh` 够了，别一档多放一份）；默认跟随系统、设置里可手动覆写。**"后期再加"的实际成本 = 一份 resx + 一行清单**（语言选择器读一个显式的 `["en","zh-Hans"]`，**不做运行时自动发现可用文化**——那要枚举卫星、与 AOT/裁剪的取向相冲，且为两档写它是投机机械）。另两处只有两档时可以先不背、但要知道在哪：英文侧的**复数**（`1 file` / `3 files`，中文无复数，所以这条只在 en 那一份里要处理，别指望格式串两档通用）、以及 `zh-Hant` 要不要跟着来（跟随系统时 `zh-Hant` 会落到中性档=英文，这是**故意的**，不是 bug：没资源就不要假装翻译过）。

5. **分发口径（2026-11-05 定：本族不上 NuGet）**。QuickTerm 走仓库源码 + 自包含产物，不建公开包：两颗内核件 `IsPackable=false` 是**终态**而非 P0 临时；`<Version>` 只作程序集身份，不进另五族那条"改版本要同步 N 处"的发包链路，也不挂 `PackageReadmeFile`/usage.md。**这条不改另一条**——"内核零第三方依赖"照旧，它与发不发包无关。README 表格第六行的 `NuGet` 那格因此写"—（本族不上 NuGet）"并标明开发中（已加）。留一个口子：将来迁独立仓库时若改走内网源/私有包，发包面要整重做（迁移五步清单见第 3 条）。顺带一格免费了：`QuickTerm.*` 三个包名有没有被占用**不用再核**。

6. **连接与设置的存储（2026-11-06 逐条拍定）**。
   - **格式与路径：定了，JSON + `System.Text.Json` 源生成器**，落 `%APPDATA%\QuickTerm\`。选它的三条理由：不撞"内核零第三方依赖"（toml 要么自写解析要么引包，而源生成路径本族已在 AOT 那一档实测过）；主机表初期就是几百条，可读性用导出补。
   - **密码：定了，存**——他的理由一句话："不存的每次都要输入密码"。落法取**存密文不存明文**：字段里放 DPAPI（当前用户作用域）保护后的 base64，读写都过一层 `CryptProtectData`/`CryptUnprotectData` 的 P/Invoke（不引 `System.Security.Cryptography.ProtectedData` 那颗包，形状与 `ConPtySession` 的直连一致）。**这样"不用每次输入"和"明文不落盘"两条同时成立。** 代价要说全：DPAPI 绑用户+机器，**把 `hosts.json` 拷到另一台机器，密码不跟过去**（其余字段照用）。真要跨机同步，将来加一条"用口令导出"的路，不预先做。
   - **全局与每主机：定了，同目录两份文件**——`settings.json` 全局一份，`hosts.json` 每台可盖一层（参照图里"终端"是主机的属性页之一，而多数人只想改一次全局）。

7. **RDP：定了，做，走 Windows 自带的 RDP**（他 2026-11-06 拍）。**不自研协议**，这一条把 §7 里"RDP 不是终端、要另一套图形面"那格直接消掉——图形面由系统客户端出，我们只做发起与归档。本机已核：`C:\Windows\System32\mstsc.exe`（1.04 MiB）与 `mstscax.dll`（8.75 MiB）都在。两种形状，成本差一个量级：
   - **A 启动器（先做这个）**：主机树里一条 RDP 记录 → 起 `mstsc.exe /v:<host>[:<port>]`，远端桌面在系统窗口里，QuickTerm 的标签只负责"我发起过"。成本近乎为零。**免密这一格有个硬事实：`mstsc` 命令行不接受密码**（设计如此），要免输入只能让 mstsc 自己去 Windows 凭据管理器读 `TERMSRV\<host>` 通用凭据——于是第 6 条的密码落法在这里要加一条分支：**RDP 类型的密码不进 `hosts.json`，而是写入凭据管理器**（那里本来就不落明文盘），其余类型走 DPAPI 密文字段。
   - **B 嵌入（先不做）**：`mstscax.dll` 的 `MsTscAx` COM 控件 + Avalonia `NativeControlHost` 的 HWND 子窗口，桌面进标签。代价是手写 COM 接口定义（AOT 下无套打件可依赖）、消息循环、DPI、断线重连与缩放。**等他真的想要"RDP 在标签里"而不是"从标签里开"时再启**——那是一次形状选择，不是缺口。

---

## 7. 连接管理的面形状（2026-11-06 他给的参照：Termora 的"新建主机"）

参照读出来的六格，逐条按"QuickTerm 现在有什么 / 差什么 / 谁欠"记，不当既定承诺：

| 参照里的格 | 内容 | 现状 | 差的东西 |
| --- | --- | --- | --- |
| 左侧主机树 | 分组（内部/客户/内网/云厂商）+ 组内主机 + 双击开新标签 | **已有**（2026-11-06）：`我的主机` → 分组 → 主机，单击开标签；数据来自 `hosts.json` | 还欠：分组折叠状态持久化、右键菜单（改名/删除）、图标（现在用 `ssh/rdp/tel/wsl/loc` 前缀代替） |
| 标签条 | 一条会话一个标签，`+` 新开 | **已有**（2026-11-06）：多会话并存、`×` 关标签即终止子进程、`+` 走 Local 页 | 还欠：拖拽排序、标签改名、会话恢复 |
| 右上有排图标 | 终端/目录/信息/工具/主题/设置 | **故意没摆** | 对应的功能一件都没落地，先摆就是假按钮；等设置面做出来再上 |
| 类型页签 SSH/Local/RDP/Telnet/WSL | 一台主机一种会话 | Local 已可用（ConPTY 起 `COMSPEC`/`SHELL`）| SSH 是 P1/W3 三候选未定；WSL = 本地起 `wsl.exe`，走同一套 ConPTY，**几乎免费**（但本机无靶：`wsl -l -q` 空、`--status` 只回"默认版本: 2"，没装发行版）；Telnet = 裸 TCP + 一小段协商，无依赖可自研；**RDP 已定：走系统 `mstsc.exe` 启动器，不嵌入、不自研**（见 §6 第 7 条） |
| 属性页 | 名称/主机/端口/用户名/认证类型/密码/备注 | 无 | 主机存储（格式与路径 = 待拍，见 §6 第 6 条）；认证类型至少 Password/Key/Agent 三档 |
| 代理 / 隧道 / 跳板机 | HTTP/SOCKS 代理、本地/远程端口转发、JumpServer 链 | 无 | 全在 SSH 传输之上，**P1 之后再排**；跳板机是"再开一条 SSH 套一层"，不是新协议 |
| 终端页 | 字体/字号/调色板/回滚行数/光标形状 | 引擎侧全有（`CursorStyle`、`TermPalette`、`ScrollbackLines`、`GridFontFamily`），**但没有归属** | 就是 §6 第 6 条那个待拍：存哪、什么格式 |
| 传输页 | ZmodiX/LRZ 之类文件传输 | 无 | 依赖 7 位/1k 协议解析，属 P1 的 SSH 侧，不单独提前做 |
| 测试连接 | 建连前的探测 | 无 | 需要"连一下就走"的会话构造，SSH 选型定了才有意义 |

**这一格现在不写代码**，理由与家规一致：没有靶就不假装打过——传输层未选、存储格式未定、宿主外壳（标签+侧栏）一行没有，先画对话框只会变成要重做的皮。**能先做的只有两格**：WSL（免费，走现成 ConPTY）与 Local 的"选哪个 shell、从哪个目录起"，两格都不依赖任何未定项。**2026-11-06 更新**：存储三小格与 RDP 已在 §6 第 6/7 条拍定，RDP 那一行按"走系统 `mstsc`，不嵌入"读；**Local 页已落地**（顶栏一条 + 二字段模态框 + 写进 `hosts.json` + `Restart` 当前会话，形状与"哪一格不可离线测"记在 `quickterm-notes.md`），所以"不写代码"这句现在只对 SSH/隧道/传输三格成立，外加"标签条与侧栏树"这一层外壳。
