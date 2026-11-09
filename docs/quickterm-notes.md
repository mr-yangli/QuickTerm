# QuickTerm 注意事项归档

> 2026-11-05 term 子树落笔（P0 的 W1 那一周）。代码里只留能自立的简短 `///`；"展开才说得清"的坑位、边界、实测读数与判据记在这里，与 `sql/docs/quicksql/notes.md` 同一口径。锚 = `文件相对路径 + 成员名`。

## 实测读数（本机：5700G / 16 逻辑核 / 32 GB，Win11 26300；SDK 10.0.403，运行时 10.0.14；`-c Release`）

- `term/benchmarks/QuickTerm.Benchmarks -- parse`：8 MiB 混合输出（每行 46 字节的文本 + CRLF，每 500 行插一句 `CSI 1;38;5;196mERROR`）进 120×40 面、回滚 1000 行——**稳态 104.7 MiB/s**（78 ms/8 MiB），**稳态分配 0.00 KiB/MiB**（热身后测，见下条），`gen0=1`。
- **热身读数不能当数**：同一循环第一趟 33.5 MiB/s，是 tier0 未上优化，不是引擎成本；册里所有读数都取第二趟。同理 `alloc` 必须先把回滚环喂出来再取基线——环的 1000 行 × 120 列 × 16 字节 = 1.9 MiB 是**一次性建表**，按每 MiB 输入摊会得到 240 KiB/MiB 这种谎话（本条就是先得到过这个假数才回头改的测法）。
- `-- churn`：单行原地重画（`CR` + 整行 + `CSI 2K`，即 `yes`/进度bar 的形状）——**868,840 frame/s** 的伤害记账，**rows dirtied per frame = 1.00**（闸门要的正是这一格：写放大一处错，这格就变成 40）。
- 离线册：`term/tests/QuickTerm.Tests` **88 颗 / 5 s 全绿**。⚠ 这一行的"含 5 颗活体 ConPTY"后来判定为**空跑**（门写成了 `return`，xunit 记通过；见"真 bug"一节第一颗与门那一颗）——那 5 颗从来没起过孩子，所以 W1 的活体读数当时一个都没有。整仓 `dotnet build Quick.slnx` **0 错 0 警**。
- **渲染层离屏读数（2026-11-05，W2-c 落地时取；`QuickTerm --bench 1280 720 200`，面 160×45 格、200 帧/档）**：
  | 档 |  paint | parse | 重画行/帧 | 分配 |
  | --- | --- | --- | --- | --- |
  | 整面拷贝（= 每帧那一次 blit 的下限） | 0.31–0.35 ms | — | — | 0.7 KiB |
  | flood：每帧一整屏新行（滚动 → 全区脏） | **11.6–12.2 ms** | 0.28 ms | 42 | **~975 KiB** |
  | churn：每帧一行进度条（CR + 行 + `EL 2`，不滚动） | **0.48–0.49 ms** | 0.01 ms | 1 | 12–13 KiB |
  | quiet：只有光标移动（零格变化） | 0.40 ms | 0.00 ms | 0 | 6 KiB |

  两档独立复核（同尺寸 200 帧跑两次）差 ±5% 内；640×400/200 帧另给一列稳定的小面读数：blit 0.09 ∶ flood 6.62–6.67（23 行）∶ churn 0.30–0.31 ∶ quiet 0.20–0.21 ms。
  读数怎么读：脏行路径在进度条那一档买到 **24 倍**（11.9 → 0.48 ms），而 0.48 里有 0.31–0.35 是 blit——"重画一行"本身的成本已经掉到 blit 之下；零格变化那一档 0.40 ms 是**每帧固定底价**（blit + 叠层），帧率天花板由它托底。
  仍未达判据的一格也写明白：**flood ~11.9 ms ≈ 84 fps 上限**（还没算合成与 vsync，只会更低），且 **~975 KiB/帧**——两笔都出自同一处：每帧给每个字形 run 新建 `FormattedText`。这一度被写成"下一档做字形图集"，**2026-11-06 已按这条试过行内容缓存并否掉**（烘焙比直接画更贵），正解改成换提交方式，见"#16 行内容缓存"一节。
- **短档读数不能当数（本轮实测到的测法陷阱，与 `-- parse` 那条热身坑同一族）**：默认 60 帧/档时，同一段代码的 churn 两次跑出 **0.57 与 1.02 ms**、flood 跑出 19.9 与 21.6 ms（差 60–80%），第一次跑还混进 tier0 未上优化的 43 ms 首帧；把档拉到 200 帧再跑两次，churn 收到 0.48/0.49、flood 收到 11.57/12.19（±5%）。**结论：这张表只收 200 帧档的数**，且任何"倍数"都要在同一档位数下算——60 帧档得出的 37.8× 是噪声放大，200 帧档的 24× 才是能复现的比值。
- 测法边界（别把这张表读成 FPS）：这是**离屏**——`SetupWithoutStarting` 后把控件 `Measure/Arrange` 出来，画进一块 `RenderTargetBitmap`；没有真窗口就没有合成、vsync 与 GPU 后端自己的拷贝，也没有 `RenderScaling`（脱离树的视觉节点报 1，所以 150%/200% DPI 那一档仍是桌面读数）。所以这张表是 FPS 闸门的**上限证据**，不是达标证明。
- 反验（摘一行会不会红）：本轮把 `Terminal.PutRune` 末尾的 `Damage.Mark(row)` 摘掉跑过一次，88 颗里 **62 红**——伤害记账的覆盖面是被验过的，不是数出来的。口径要说清：那一次 `ESC [` 的入边也还缺着，所以 62 是"依赖 damage 标记的颗数下界"，不是精确归因。另两处修复（IL/DL 的行池别名、`4:3` 的冒号归属）各自先红后绿，红的读数记在上节，等价于反验。
- 计划 §3 的 W1 闸门四条现状：写放大与分配两格达标并有数；**吞吐那一格（≥150 MiB/s）2026-11-05 判定为钉错并已撤销**——实测 104.7 MiB/s 不再算不达标，理由与替代判据（FPS 唯一）记在计划文档 W2 一节，这里只留一句：**闸门要钉在用户能感知的量上**，每秒解析多少字节救不了一个每帧重画整屏的渲染层；104.7 这个数只留作状态机的回归哨兵。

## 这一轮挖出来的真 bug（每条：症状 → 根因 → 现在靠什么钉住）

- `src/QuickTerm.Pty/ConPtySession.cs + StartProcess`：**`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 的传法差一层间接，孩子从来就没活过**。写的是 `UpdateProcThreadAttribute(..., ref pseudoConsoleHandle, sizeof(IntPtr), ...)`——`ref` 交出去的是**托管变量的地址**，内核把这个地址当 HPCON 用，于是孩子的 console 握手拿到无效句柄，`cmd.exe` 在 DLL init 就死，交回 `0xC0000142`（= 无符号 `0xC0000142`，他截图里那行 `-1073741502` 与那个"应用程序无法正常启动"框都是它）。正解按厂商文档样例与 Windows Terminal 的同一形状：**lpValue 直接就是 HPCON 本身**、`dwSize = sizeof(HPCON)`。同轮把管道也换成文档样例的**不可继承**（`CreatePipe(..., NULL, 0)`）。
  证据链（同一条路径、同一台机器，前后对得上）：修前 `--pty-probe` 的 `real path` 行是 `bytes=0 exited=True`；修后先收到 16 字节 ConPTY 自己的起手式（`ESC[?9001h`、`ESC[?1004h`——之前一个字节都没有）；他 00:28 从 Debug 起的那颗实例现在 **`cmd.exe` PID 43964 活着、父进程就是 QuickTerm PID 35732**，且窗口标题被孩子用 OSC 改成了 `C:\WINDOWS\system32\cmd.exe`——标题能改说明字节进了引擎、解析器认了 OSC。
  顺带更正一处**我自己早先记错的结论**：先前"lpDesktop 留空导致 0xC0000142，显式给 `winsta0\default` 后修好"不成立——在那种传法下任何孩子都不可能活着，那次"修好"是把**空跑**读成了通过。`lpDesktop` 那一行留着（与 Windows Terminal 同形、且他截图里能弹交互式桌面的硬错误框，说明窗口站这条路径确实走通了），但它的必要性**未证**，别再当既定事实引用。
- `tests/QuickTerm.Tests/ConPtyTests.cs + 五颗活体的门`：**"起不来就站下"实现成了 `return`，而 xunit v2 没有运行时 skip——`return` 就是记通过**。所以"活体 ConPTY 5 颗全绿"从落下的那天起就是**空跑**，它既没证过 lpDesktop，也没证过退出码 7、打字回显、滚动出屏的行。现在补了一颗**计数读数** `ConPty_Is_Available_In_This_Context`：它断言 preflight 为真，红就红——红的时候上面那五颗一律按"未跑"读。本机现状：**这颗红**（testhost 与本会话都生不出 console 孩子，探针里连"完全不带 pty 的普通 `cmd.exe`"那一档都直接卡在 `CreateProcessW` 不返回），所以本机上任何"活体 ConPTY 通过"的说法都不成立，要收这一格得在能生孩子的上下文里跑（VS 测试资源管理器 / 桌面会话）。
- `src/QuickTerm.App/Program.cs + Main`：**孩子初始化失败会弹系统硬错误框抢焦点**（他第二张截图就是那个框）。错误模式由孩子继承，所以宿主在 `Main` 开头 `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX)` 就够了：孩子死了只走退出码 → 网格里那行中文，不再弹窗。终端宿主不该让子进程的 UI 挡住会话，这条与上面那颗 pty bug 无关，是独立的观感缺陷。

- `src/QuickTerm.Terminal/VtParser.cs + Escape`：**`ESC [` 的入边整条丢了**（重写状态机时删了 `case (byte)'[': BeginCsi()`），后果不是崩溃而是**静默**——CSI 全部不进状态机，光标不动、颜色不上、`vim` 起不来，而 88 颗里只有 26 颗红（因为另外 62 颗只吃 C0 与 ESC 单字符，压根不依赖 CSI）。现在由 `Cursor_Addressing_Accepts_The_Variants`（5 档）钉住入边；"与 5 颗活体测试各钉一条"那句作废——那五颗是空跑（见本节首条与门那一颗）。教训直接进闸门：**状态机每条入边都要有一颗哨兵**，"全绿"不是证据。
- `src/QuickTerm.Terminal/Screen.cs + InsertLines / DeleteLines`：**驱逐行提前还池 → 同一对象被两行共享**。`_pool.Return(...)` 后再 `_pool.Rent(...)`，池反手就把刚还的那颗发回来并 `Initialize` 清零，于是 `Array.Copy` 搬过去的那一行和新建的空白行是**同一个引用**（症状：`IL` 后区域中间一行变成空白，`Expected ROW3 / Actual ""`）。判据是顺序：**先把被逐行摘出来存数组 → 搬 → Rent 新行 → 最后还池**，中间不能让池碰到在用的对象。`ScrollUp/ScrollDown/Resize/Reset` 逐条对着这条判据复查过，都不越界。
- `src/QuickTerm.Terminal/VtParser.cs + ColonSeparated`：冒号子参数的归属记反了。`_separators[i]` 存的是**闭合第 i 颗**的那个分隔符，所以"`4:3` 里的 3 是 4 的子参数"要看 `_separators[index - 1]`；写成 `_separators[index]` 时 `4:3` 拆成两条独立 SGR，第 2 条 `3` 落进 italic（症状：`Expected CurlyUnderline / Actual Italic | Underline`）。`38:2::r:g:b` 的空色域 id 那一支同样靠这一格判。
- `src/QuickTerm.Pty/ConPtySession.cs + DisposeCore`：**关句柄的顺序会死锁**。`FileStream`/`SafeHandle` 的关闭要等一个**被阻塞的同步 `ReadFile`** 落地，而能放它的只有 `ClosePseudoConsole`——所以顺序必须是「先 `TerminateProcess` 孩子 → 再 `ClosePseudoConsole`（这一步解开阻塞的读）→ 才关管道与句柄」。写反时整轮 `dotnet test` 挂 400 s 不返回（第一次遇到时我误判成"测试超时设得太宽"）。
- `src/QuickTerm.Pty/ConPtySession.cs + StartProcess`：`lpDesktop` **要显式给 `winsta0\default`**。留空时孩子在 console 初始化阶段就死，`GetExitCodeProcess` 交回 `0xC0000142`（`STATUS_DLL_INIT_FAILED`），`bytes=0`——`cmd /c echo` 与 `cmd /c exit 7` 同症状，与管道、与是否 drain 输出都无关（这两条都单独探过）。
- `src/QuickTerm.Terminal/Terminal.cs + PutRune`：挂起换行(pending wrap)的判据被列钳制吃掉。原来先 `CursorColumn = min(next, Columns - 1)` 再 `_wrapPending = CursorColumn >= Columns`，后者永远为假 → 四列面写第五个字符是**覆写第四列**而不是换行。改成先用未钳制的 `next` 判定、再钳位。同一条里另外定死一格：`EL 0`/`ED 0` 在挂起换行 armed 时起点取 `Columns`（"光标右边界外一格"），与 `RowText` 那套"钳位后报最后一列"的读法刻意分家——`cmd.exe` 每行铺满 80 列再 `\r` 回零列，正是这条规则在兜。
- `src/QuickTerm.Terminal/Terminal.cs + SaveCursor / SetAltScreen`：`ESC 3;5H` 之类把光标**停在最后一列**时 armed 的挂起换行，`DECSC` 存的是钳位后的坐标。故 alt-screen 退出回到的是最后一列而非"右边界外一格"，`CSI 6 n` 在这种位置报的数比 xterm 少一列。后果仅影响逐列不逐行的边角（实测靶子：`less` 长行、`vim` 的 `c` 模式）。
- `src/QuickTerm.App/TerminalView.cs + PaintRow` 的字形 run 循环：**带颜色的文字一出现就 100% 占核死循环**（不是掉帧，是那一帧永远不结束）。`ForegroundOf()` 每个格子 `new SolidColorBrush(...)`，而 run 的合并判据写的是 `ForegroundOf(cell) == foreground`——`SolidColorBrush` 不重写 `Equals`，这是**引用相等**，于是同一格的第二次比较必为假；内层 run 循环跑零次，`builder.Length == 0 → continue` 又不推进 `index`，外层原地转圈。默认色恰好命中 `return _foreground`（缓存字段，引用相等成立）所以一直"看起来正常"——**这条从 W1 第一版就埋着，只有非默认前景能引爆**，而 `cmd.exe` 的输出几乎全是默认色，所以桌面首跑也没撞上；把它挖出来的是离屏基准喂的第一条 `ESC [32m`。修法两件事一起做：run 的键改成 **rgb 数值**（`ForegroundKey/BackgroundKey` 返回 `uint`，`NoBackground = uint.MaxValue` 当"本格无底色"哨兵），画刷进 `Dictionary<uint, IBrush>` 缓存（顺带把每帧上百颗画刷的税免了），而 run 循环用 `do/while` **先吃当前格再判下一个**——"没推进就 continue"这个形状本身不留。诊断路径也记一笔：这不是 Avalonia 的锁问题——先用 200 轮"外层 batch 未关时开内层 batch"的探针排掉了它（全绿），再 `dotnet-dump collect -p` + `clrstack -all` 直接看到主线程停在 `ForegroundOf`；**能拿到栈的时候不要猜**。
- `src/QuickTerm.App/TerminalView.cs + 三处 API 形状`（Avalonia 12.1.5 与 11 的差别，编译期撞上的，记下来免得下次再撞）：① `RenderTargetBitmap.Render(Action<DrawingContext>)` **在 12 里没有了**，剩下的 `Render(Visual)` 不是同一个东西；画布要从 `CreateDrawingContext()` / `CreateDrawingContext(bool clear)` 拿。② `clear` 那一参正是脏行重绘的地基：**`clear: false` 保留上帧内容**（厂商文档原话 "leaves the image data unchanged"），所以一块自持面就能原地补画，不需要双面包屑拷贝——文档之外另加了一次实测（`--bench` 第一行 `retains: yes`），因为若它其实清了，症状是"除了改动那一行其余全白"，比崩还难查。③ `RenderScaling` 不在 `Visual` 上了，取法是 `(VisualRoot as TopLevel)?.RenderScaling ?? 1`；脱离树的节点没有根，所以离屏档只能测 @1x。④ `Bitmap.Save(Stream, int?)` 已过时（CS0618 会红门禁），改用 `Save(stream, PngBitmapEncoderOptions.Default)`；`BitmapEncoderOptions` 本身是抽象类，不能 `new`。
- `src/QuickTerm.App/TerminalView.cs + 尺寸同步`：原来靠 `SizeChanged` 事件驱动 `SyncGridFromBounds()`。现在改成 `Render` 开头自己调（两次数除一下，幂等），因为**离屏宿主不会触发那个事件**——不挂树就没有尺寸变更路由，基准里量好的 `Measure/Arrange` 会白做，面还是 80×24。顺带一条测法：`Measure`/`Arrange` 对脱离树的控件是可用的，这是"本机证不了真窗口"时唯一能把渲染成本量出来的口子。

## 测试写法（一条会反复咬人的）

- `tests/QuickTerm.Tests/*.cs`：C# 的 `\x` 转义**贪婪吃最多四位十六进制数字**，所以 `"\x1bE"` 不是 ESC+'E' 而是 U+01BE，`"\x1b7"`/`"\x1bc"`/`"\x1bu"` 同理全错。册内一律 `\u001b`（定长四位，后面跟谁都安全）。这一条改完同时**消掉 6 颗假红、暴露 4 颗真红**（`ESC c` 硬重置、IND/NEL、`ESC 7/8`、`CSI s/u` 四条之前根本没进状态机）。另外 `term.Send([.. bytes])` 会绑到 `Send(string)` 重载而不是 `Write(ReadOnlySpan<byte>)`，字节流要用 `Write`。
- `tests/QuickTerm.Tests/ConPtyTests.cs + LivePump`：引擎按契约是**单线程**的（一个写者、一个读者，且期望同一线程），所以后台读线程只准攒字节、由测试线程喂 `Terminal`；让后台线程直接 `term.Write()` 会撕出与时序相关的假红。
- `tests/QuickTerm.Tests/ConPtyTests.cs + PtyAvailable`：ConPTY 的孩子需要**能生 console 孩子的上下文**。实测边界（2026-11-06 校正）：从 Git Bash 这类管道会话起的 exe、以及 `dotnet test` 的 testhost，**都生不出来**——探针里连"完全不带 pty、只重定向管道的普通 `cmd.exe`"那一档都卡在 `CreateProcessW` 不返回。先前那句"`dotnet test`（testhost 起了真桌面）下正常"是**错的**，它来自把空跑读成通过；现在这一格由 `ConPty_Is_Available_In_This_Context` 那颗计数读数钉住，本机红。口径同 QuickSql 的活库档无靶即退，但**退要看得见**：xunit v2 没有运行时 skip，`return` 等于记通过，所以不能靠 `return` 表达"退"。

## AOT 这一档（2026-11-05 定为目标形态，现状分三层，别混着说）

- **分析器层（已过）**：两颗内核件开 `IsAotCompatible` + `TreatWarningsAsErrors`（照 sql 家的形状，只开在源码颗上——测试与基准那颗不开，xunit v2 自己就不干净）。整仓 `dotnet build Quick.slnx` **0 错 0 警**，零条 IL2xxx/IL3xxx。
- **裁剪层（已过，且是真跑过）**：`dotnet publish -r win-x64 --self-contained -p:PublishTrimmed=true -p:TrimMode=full` → 还原 `Microsoft.DotNet.ILCompiler` 之外的部分全绿，**0 条 IL 警告**，publish 目录 **20 MB**，产物 `QuickTerm.Benchmarks.exe churn` **能跑并交出读数**（926,665 frame/s、脏行 1.00/帧；与 JIT 那次的 868,840 差约 6%，两次 run 条件不同，**不作优劣判据**，只作"裁完还能跑"的证据）。
- **原生层（未过，卡在工具链不在代码）**：`-p:PublishAot=true` 报 `Microsoft.NETCore.Native.Windows.targets(142,5): error : Platform linker not found`——ILCompiler 包本身还原成功（10.0.14 已在本地 NuGet 缓存），缺的是 **VS 的"使用 C++ 的桌面开发"工作负载**（link.exe/lib.exe）。所以本机现在能证"裁剪干净"，还不能证"原生 AOT 出货体积与冷启动"。
- **语言档（2026-11-05 定）：先中英两档**。English 为中性/源语言、简中走 `zh-Hans` 卫星；选择器读一份显式清单，**不做运行时自动发现可用文化**（要枚举卫星，与 AOT/裁剪取向相冲，且为两档写它是投机机械）。"后期再加"的成本钉死在**一份 resx + 一行清单**。两个已知边界：英文侧复数不能靠格式串两档通用（中文无复数）；`zh-Hant` 用户会落到中性档即英文，**这是有意的**——没资源就不要假装翻译过。
- **多语言这一档（2026-11-05 定：第一天留位）本机已证的部分**：`ResourceManager` + 卫星程序集在 `IsAotCompatible` 下 **0 条 IL 警告**；`TrimMode=full` 发布后 `zh-Hans/*.resources.dll` 仍在产物内，**裁过的二进制运行交回中文串**（publish 20 MB）。两个坑都是实测的：清单资源名带根命名空间前缀（`<rootns>.Resources.Ui`，少前缀直接 `MissingManifestResourceException`）；`EnableDefaultResxSourceGeneration=true` 在 10.0.403 上**什么也没生成**（`obj/` 无生成物），所以"强类型访问器"是未知项不是既定事实，要另查开关名或是否得引 `Microsoft.CodeAnalysis.ResxSourceGenerator`。一个测法陷阱：控制台按活跃代码页（本机 936）重编码 CJK，拿 stdout 字节断言 UTF-8 会把**成功读成失败**——复核得用编码无关的方式（本次用 `TextDecoder("gbk")` 解回 `新建连接` 才算数）。UI 侧的约束（终端内容不翻译、`NeutralResourcesLanguage` 要标、CJK 字体回退与终端字体是两件事）记在计划文档 §6 第 4 条。
- **一条前提要更正（我早先说反了）**：AOT 的目标**不否定 libssh2**——P/Invoke 是 AOT 友好的（原生层不需要反射）。AOT 真正判死的是"依赖反射/emit 的托管件"。所以 SSH 三候选的重心变化是：`SSH.NET` 要先证它自己的 AOT/裁剪干净度（历史上它带反射式算法注册，这条不能默认通过）；`libssh2` 的代价落在**每次发布备齐多 RID 原生件**，与 AOT 无关；自研 transport 是唯一"两头都干净"但最贵的一档。
- **GUI 层的依据（有出处，未在本机验）**：Avalonia 官方有 Native AOT 部署文档，且 12 系列明确接 .NET 10 的 AOT/裁剪增强；11.2.4 及更早存在"AOT/裁剪下 Windows Narrator 可访问性异常"的已知问题，11.3+ 修复。W2 落地时把 `BuiltInComInteropSupport=false` 与 AOT 开关一起钉进 `QuickTerm.App`，并在真机验一次 Narrator（这条是本机给不了的读数之一）。

## SSH 候选评估（2026-11-07 离线研究，#29；**未实测，定档在 W3**）

把上一节那句"SSH.NET 要先证它自己 AOT/裁剪干净度"往前推一格：这一轮把候选的**包事实**核到 NuGet 一手来源，并给出排序与判据。**这是研究账不是定档**——传输层一行没写、一个包没引（无靶就不假装打过，家规照旧）。

**已核（2026-11-07，来源 = NuGet flatcontainer 的 `.nuspec`，一手）**：

| 候选 | 最新版 | 许可 | 目标框架 | net10 运行时依赖 |
| --- | --- | --- | --- | --- |
| **Tmds.Ssh** | 0.26.0（2026-10） | MIT | net8/net9/net10/net11 | `BouncyCastle.Cryptography` 2.7.0 + `Microsoft.Extensions.Logging.Abstractions` 10.0.12 |
| **SSH.NET** | 2026.0.0（2026） | MIT | net8/net9/net10/netstandard2.0/net462 | `BouncyCastle.Cryptography` 2.7.0 + `Microsoft.Extensions.Logging.Abstractions` 8.0.3 |
| **libssh2**（P/Invoke） | 原生库，无托管 nuspec | BSD 系 | 多 RID 原生件 | 原生 `.so/.dylib/.dll`，每次发布按 RID 备齐 |
| **自研 transport** | — | 自有 | 随本族 | TLS 走 BCL、KEX/SFTP 包层自写 |

两件事顺手核清：① 两家托管候选**共用同一颗加密依赖** `BouncyCastle.Cryptography` 2.7.0——选谁都要背它，这不是区分项；② Tmds.Ssh nuspec 里那颗 `Microsoft.CodeAnalysis.PublicApiAnalyzers` 是 `exclude="Build,Analyzers"`，**只是开发期分析器，不进运行时**，别把它算进依赖树。

**未核（别当事实写）**：上一轮研究读到两家仓库 csproj 标 `IsAotCompatible=true`，**本轮未能复核**——`gh` 未装、raw.githubusercontent 与 github.com blob 都 fetch failed（本机网络对 GitHub 不通，NuGet 通），作者 docs 站（tmds.github.io/Tmds.Ssh，可达）首页与 .NET Tools 节也**没有** AOT/裁剪声明。所以"AOT 干净"现在记为**作者声明 + 待本机发布复核**，不是已证。真正的判据是我们自己那次 `PublishTrimmed`/`PublishAot`（见下），不是包页徽章。

**排序与理由（研究倾向，非定档）**：`Tmds.Ssh` ＞ `SSH.NET` ＞ `libssh2 P/Invoke` ＞ `自研`。
- **Tmds.Ssh 领先**：为现代 .NET 新写的异步 API、明确目标 net10/net11、依赖树小而干净、作者以 AOT/裁剪友好为卖点、MIT。代价是相对年轻、活体证据要靠我们自己的原型补。
- **SSH.NET 次之**：最成熟、用得最广，但历史上带反射式算法注册（上一节已点），AOT/裁剪干净度不能默认通过；成熟度是它的长处，反射包袱是它的疑点。
- **libssh2 第三**：与 AOT 不打架（P/Invoke 原生层不需要反射），代价落在**每次发布备齐多 RID 原生件**的打包面，且算法/协议更新跟着原生库走。
- **自研垫底但保留**：唯一"托管与原生两头都干净"的一档，也最贵（几人月量级，安全原语自研的审查成本归他）。它是"任一托管候选过不了闸"时的硬退路，不是首选。

**决策门（与计划 §2/§6 第 2 条、本文件家规节一致，不另立）**：W3 那份 **200 MB SFTP 原型基准** + 一次 **`PublishTrimmed -p:TrimMode=full` 与 `PublishAot`** 实发——托管候选**任一不过就自研 transport**。AOT 原生层本身还卡在 C++ 工作负载（#9），所以这一门要等工具链就位才收得了。

**家规落点**：任何 NuGet SSH 件**只准进 `QuickTerm.App`**（`IsPackable=false`），两颗内核件零 NuGet 依赖不动——传输层是网络/协议活，不进可被无头基准与别人宿主直接引用的内核。这与计划 §2 那条家规是同一根绳子。

**不做什么（2026-11-07 当时）**：不引包、不写传输层、不做"测试连接"按钮。**2026-11-08 更新**：他拍"把 ssh 打通"，传输层已落地（见下节），引包与写传输两条作废；"测试连接"按钮仍不做（要"连一下就走"的会话构造，另排）。

## SSH 传输层落地（2026-11-08，他拍"把 ssh 打通"；**活体未验，等他登录**）

- **选型落地**：`Tmds.Ssh` 0.26.0（上节评估领先的一颗），只进 `QuickTerm.App`（`Directory.Packages.props` 钉版本）；内核两颗零 NuGet 不动。API 一手来源 = 包内 `Tmds.Ssh.xml`（GitHub 本机不通，NuGet 通）：`SshClient.ConnectAsync` → `ExecuteShellAsync(ExecuteOptions{AllocateTerminal,TerminalType,TerminalSize})` → `RemoteProcess`（`StandardInputStream` 写、`ReadAsStream(StderrHandler.Ignore)` 读、`SetTerminalSize` 改尺寸、`GetExitStatusAsync` 取退出码）。
- **适配器**：`SshSession : IPtySession`（App 层）。Tmds.Ssh 是异步的，而 `IPtySession.Read` 按约定阻塞，故一条后台 pump 把输出搬进 `BlockingCollection`，读侧阻塞取；写走 `StandardInputStream`；resize 走 `SetTerminalSize`；退出经 `Exited` 事件回到 UI。这样终端的 reader 线程/resize/退出通知**一行不改**就接上 SSH（`TerminalView.StartSession(IPtySession)` 从 `Start` 里抽出来共用）。
- **凭据**：Password → `PasswordCredential`（先用 hosts.json 的 DPAPI 密文解出；没有则模态问一次，值不落盘不进日志）；PrivateKey → `PrivateKeyCredential(path, passphrase)`；Agent → 默认凭据（含 agent）。**凭据不进对话**：真登录由他在界面里做。
- **主机密钥**：TOFU。`KnownHostResult.Trusted` 直接过；`Unknown` 弹模态给 SHA256 指纹让他确认（`UpdateKnownHostsFileAfterAuthentication=true` 写回 known_hosts）；`Changed`/`Revoked` 一律拒，不弹框（防 MITM）。
- **离线证到的**：build 0 警 0 错；册 +2 绿（拒绝连接抛异常不挂死、`SshSession` 满足 `IPtySession`），整册 256 绿 + 1 既定红。**没证到的**：真服务器握手/认证/shell/resize/退出——本机无靶、也不拿他的凭据试，等他开一台 SSH 主机回图才算收口。
- **一个上游漏子记一笔**：Tmds.Ssh 把开发期 `PublicApiAnalyzers` 漏给消费者（nuspec 标了 `exclude="Build,Analyzers"` 仍流进 csc；`ExcludeAssets=analyzers/all`、显式引用排除都实测压不住），凡引用 App 的颗被刷 RS0016/RS0037。本族不维护 PublicAPI.txt，在 `term/Directory.Build.props` 定向 `NoWarn` 这两条（其余分析器照旧），上游修好后删。这是静音不是修复，账记在这里。

## SFTP 文件传输第一版（2026-11-08；**活体未验，等他对 228 试**）

- **复用连接**：`SshSession.OpenSftpAsync()` → `SshClient.OpenSftpClientAsync`，SFTP 走同一条已认证连接，不二次登录。
- **窗口**（`SftpWindow`，顶栏文件夹图标打开，仅对活动 SSH 会话有效，否则提示 `SftpNeedSsh`）：**Termora 式双栏 + 队列**——左本机 / 右远端两个列表格（文件名/类型/大小/修改时间/权限，目录优先），各带路径框 + 上级/刷新；双击目录进入，双击远端文件=下载、双击本机文件=上传；底部共享队列表（名称/状态/进度/大小/源/目标）。**仍是独立窗口**；嵌成主窗"传输"标签页要动会话/标签机制，列下一步。
- **API 一手**（包内 xml）：`GetDirectoryEntriesAsync<T>(path, ref-transform, EnumerationOptions)`（transform 是 `ref SftpFileEntry`，`FileName` 要 `.ToString()`，`WorkingDirectory` 是 `SftpDirectory` 取 `.Path`）；`UploadFileAsync(local,remote,overwrite,perms,progress,ct)` / `DownloadFileAsync(remote,local,overwrite,progress,ct)`。第一版 progress 传 null（只报"传输中/完成/失败"），不做进度条。
- **不做什么（第一版）**：拖拽、断点续传、目录递归上传/下载、远端删除/改名/新建。各自记账，后续切。
- **离线证到的**：build 0 警 0 错；册 268 绿 + 1 既定红。**没证到的**：真服务器列目录/上传/下载——等他开 228 点文件夹图标试，回结果才收口。

## W2 第一颗（App）在本会话能证到哪一格（2026-11-05）

- **可证**：`QuickTerm.App` 编译 **0 错 0 警**；册 **162 颗全绿**（152 + 10 颗中英资源契约）；整仓 `dotnet build Quick.slnx` **0 错 0 警**。
- **实测到的运行事实**：直接跑 `QuickTerm.exe`，进程活着 12 秒以上、`tasklist` 显示它在 **Session 1、Console 类型、工作集约 130 MB**、stderr 空——即 **Avalonia 起来了也没崩**。这一条**收窄了**前面那格结论：先前"ConPTY 要交互窗口站"是从管道 shell 里跑 exe 观察到的（孩子一律 `0xC0000142`）；GUI 进程自己不吃这个限制。
- **没证到的（别当成证过）**：① 窗口是否真可见、内容是否正确——本会话看不到屏幕；② **没看到 `cmd.exe` 孩子出现**，所以"shell 真被生出来并渲染"这一格是**未知**不是通过；③ FPS/帧时读数——控件里已画 `fps · ms · 列×行` 叠层，数字要他在桌面读到并回贴。
- **下次动这颗要先处理的形状缺陷**：~~`MainWindow.OnOpened` 里 `Start()` 抛出的异常现在会直接崩宿主~~ **已修（2026-11-05 W2-c 同轮）**：spawn 失败改为往网格里写一行 `PtyStartFailed`（中英各一份，资源键已进册），宿主不崩、用户看得到原因。"起不来说人话"这一格不再是欠账。
- **没有 XAML 的原因记一笔**：这颗一个 `.axaml` 都没有——控件全自绘、主题在代码里加，少一层 markup 编译，也与 AOT/裁剪取向对齐。

## W2-c（控件自持渲染面）落地形状与还能证到哪一格（2026-11-05）

- **已落的形状**：`TerminalView` 自持一块 `RenderTargetBitmap`（按 `Bounds × scaling` 取设备像素），每帧三档工作按需选一——**整面重建**（`_fullRepaint` ∶ 引擎 `IsFull` ∶ 用户滚回历史，因为行的映射整体挪了）∶ **脏行补画**（引擎报的 `[Top..Bottom]` 映射到面里的槽位，其余一格不动）∶ **只 blit**（无损伤，光标移动就是这档）。光标与 fps 叠层画在 blit 之上、不进面：光标每次跳格都要重画整行的话，"零格变化"那一档就不可能只有 0.48 ms。`damage` 改由 `Render` 消费而不是 `Drain` 消费——Avalonia 会把多个 tick 合成一帧，在 pump 时取走的区间会漏掉下一个 tick 刚弄脏的行。
- **为什么是一块面而不是两块**：落第一版时按"每趟给新层"的旧事实设计成双面包屑（后面包从前面包拷贝再补脏行），查到 12 的 `CreateDrawingContext(bool clear)` 之后改成单面原地补画——**省掉一次整面拷贝，也省掉第二块面**（1280×720 是 3.7 MB，4K 是 33 MB）。这条不是抠内存：blit 已经是每帧固定底价 0.31–0.35 ms，再叠一次同尺寸拷贝等于把底价翻倍。保留的两处证据：厂商文档原话 + `--bench` 首行的实测 `retains: yes`。
- **还能证的**：`QuickTerm --bench [w] [h] [frames]` 离屏跑通并给出上面那张表 ∶ 200 轮嵌套 batch 探针全过（外层 batch 未关时开内层是安全的，控件每帧都这么干）∶ 整仓 0 错 0 警 ∶ 册 **162 颗全绿**。
- **还不能证的（别当成证过）**：① 真窗口的 FPS 与合成成本——离屏没有 vsync、没有 GPU 后端的拷贝，表里的 paint 是上限证据；② DPI 150%/200% 的半像素那一格（脱离树拿不到 `RenderScaling`，只测了 @1x）；③ 窗口观感与"shell 真被生出来并渲染"照旧未知，本会话看不到屏幕、也没看到 `cmd.exe` 孩子；④ 面是 CPU 侧 `RenderTargetBitmap`，在真窗口下是否走同一块 Skia surface、有无额外上传，要等桌面读数。
- **下一档的量化判据**（不是审美）：flood 那一档 **~11.9 ms/帧 ≈ 84 fps 上限**、**~975 KiB/帧**，两笔同源——每个字形 run 现场 `new FormattedText`。**原以为"把行烘焙成图集"就是这一档的做法，2026-11-06 实测否掉了**（烘焙一行 0.52 ms > 直接画 0.28 ms，整块撤），换成按分段计时定出的新判据：write 10.23 ms 要压到 ~3 ms，靠的是**换提交方式**而不是换缓存。细账见"#16 行内容缓存"一节。

## 桌面首跑（他回贴的截图，2026-11-05 深夜 → 2026-11-06 凌晨收口）

- **渲染层在真窗口里是活的**：面 **128×41**、中文行 `进程已退出，代码 -1073741502` 字形与间距正常（字体回退链在真窗口下成立，这条离屏证不了）、叠层画在左下角。**先前那格"窗口是否真可见"从未知改成已证。**
- **"起不来说人话"这条按设计生效**：孩子没生出来时用户看到的是网格里的一行中文，不是崩溃对话框——这条路径第一次在真窗口里跑到。
- **第一条真窗口帧时读数**：叠层显示 **13.56 ms / rows 41**（整屏重画那一档）。⚠ 当时据此写的"与离屏 11.9 ms 只差约 14%、离屏表可直接当上限证据"**已被下一张截图校正**：同档叠层读到 **21.00 ms / rows 42**，真窗口比离屏贵约 **1.75×**（叠层那个数是 **EMA（0.8/0.2）不是单帧 P95**，静止画面 `0 fps` 也正常（那一格数的是过去一秒内 `Render` 被调了几次）。且离屏没有 present 腿）——细账与对闸门的影响见"桌面第三跑"一节。
- **同一张截图也报了一条硬缺口**：`-1073741502` = `0xC0000142` = `STATUS_DLL_INIT_FAILED`，即 **CreateProcess 成功了、孩子死在 console/DLL 初始化**。这与本机管道会话的老症状同号，但**本机不能替他定性**：新加的 `QuickTerm --pty-probe`（逐档换 spawn 形状、写 `%TEMP%\quickterm-pty-probe.log`）在本机 GUI-parent 下跑出同样的 `0xC0000142`，而**控制档（完全不带 pty、只重定向管道的普通 `cmd.exe` 孩子）在本机直接卡在 `CreateProcessW` 里不返回**——说明这个会话上下文连普通 console 孩子都生不出来，它给出的任何读数都不是桌面证据。册内 5 颗活体 ConPTY 在 `dotnet test`（testhost 有真桌面）下照旧全绿，所以**同一段代码在两种上下文里一个能跑一个不能**，问题在上下文还是在我们的形状，要在他那边判。
- **要在他那边跑的判别实验**（一条命令，一份日志）：从**资源管理器/Win+R**启动 `QuickTerm.exe --pty-probe`（不要从任何终端里起），然后把 `%TEMP%\quickterm-pty-probe.log` 贴回。读法：`real path` 是出货路径；`desktop set` 与 `desktop null` 差的是 `lpDesktop`；`no pty (redirect)` 是控制档——**若控制档在他那边能跑而 pty 档全灭，就是我们的 spawn 形状有 bug**；若两档同症状，则是他启动 QuickTerm 的那个父进程本身不让生 console 孩子（嵌套 ConPTY 的老账）。

## 桌面第二跑（同一晚，硬错误框）把上面那条实验的结论直接给了出来

- 他贴的第二张图是系统框 `cmd.exe - Application Error / 应用程序无法正常启动(0xc0000142)`。**框能弹在交互桌面上**这一格顺手排掉了窗口站与 `lpDesktop` 那一支：孩子确实到了 `WinSta0\default`，失败在 console 握手。
- 由此定位到真正的根因（`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 差一层间接，见"真 bug"一节首条），修完之后的三条读数：
  - `--pty-probe` 的 `real path` 行：修前 `bytes=0`，修后 **`bytes=16`，内容是 ConPTY 自己的起手式 `ESC[?9001h`、`ESC[?1004h`**——之前一个字节都没到过宿主。
  - 他 00:28 起的实例（`bin/Debug`，PID 35732）：**窗口标题被孩子用 OSC 改成 `C:\WINDOWS\system32\cmd.exe`**——标题能改，说明字节进了引擎且解析器认了 OSC。
  - 同一时刻进程表：**`cmd.exe` PID 43964 活着，父进程 = 35732**。这颗终端里第一次真的跑着 shell。
- **仍待他确认的**：网格里看到的是不是可用的提示符（我只能证字节与父子进程，看不到屏幕）；以及 `ConPty_Is_Available_In_This_Context` 那颗计数读数——本机仍红，要在能生孩子的上下文（VS 测试资源管理器 / 桌面会话）里跑一次，那五颗活体才算真跑过。

## 桌面第三跑：提示符可用，同时把上一条"离屏≈真窗口"的比值校正掉

- **W2 "能日用"的第一道门槛过了**：他贴的第三张图里 `Microsoft Windows [版本 10.0.26300.9818]` / `(c) Microsoft Corporation。 保留所有权利。` / `E:\Resource\QuickNet\term\src\QuickTerm.App>` 三行齐、块光标在提示符末、窗口标题由孩子的 OSC 设置。**CJK 与 shell 输出在同一条路上都对了**，这条不再是未知。
- **校正一条我自己写早了的结论**：上一节说"真窗口 13.56 ms 与离屏 11.9 ms 差约 14%，所以离屏表可直接当上限证据"。第三张图的叠层是 **21.00 ms / rows 42**（同尺寸档，42 行整屏重画）——**真窗口比离屏贵约 1.75×，不是 14%**。两处原因，都记明白：① 叠层里那个数是 `_frameMs` 的 **EMA（0.8/0.2）**，不是单帧 P95，启动那一串整屏帧会把均值抬高，13.56 与 21.00 是同一颗叠层在不同时刻的读数，别当成两次测量；② 离屏那一档画进的是另一块 CPU 侧 `RenderTargetBitmap`，**没有真窗口的 present 腿**（上传 + 合成 + vsync），这一腿现在量出来约 +9 ms。⚠ 这半句的措辞后来还要再校一次：离屏把 `blit` 单独计时是 **0.46 ms**，量的正是"面→另一块 CPU 位图"这同一次拷贝——所以真窗口那 ~9 ms 不是合成器的固定税，而是**我们自己的面在过 GPU 上传**。能不能压掉归自定义合成视觉那一档答（见"#16 行内容缓存"一节末），别再说成"不可控"。
- **对闸门的直接影响（这才是重点）**：W2 闸门要 `1920×1080 下 FPS ≥ 55、帧时 P95 ≤ 8 ms`。按真窗口整屏重画 ~21 ms/帧，**上限约 48 fps，已经低于 55**；P95 ≤ 8 ms 需要再快约 **2.6×**。⚠ 这一整段后来作废，别按它做决定：那个 21 ms 是**被启动帧污染的 EMA**，同一控件的单帧 write 实测 2.0 ms，而离屏 10-12 ms 是 **CPU raster 后端**的数、比真窗口慢 5 倍——详见"帧时读数的方法学塌方"一节。闸门现在等的是真窗口的 `ms p95`，不是任何均值。所以**字形图集（任务 #16）从"下一档"升格为"闸门必需"**——它要压的正是那两笔同源成本（每 run 现建 `FormattedText` 的 11.9 ms 离屏部分与 ~975 KiB/帧），而 present 腿那 ~9 ms 不在我们手里，得先把它从图集收益里扣掉再谈达标。1920×1080 那一档还没读数（行数更多、面更大），下一轮读数要按尺寸分档记，别再混成一个数。

## #16 行内容缓存：试了、量了、撤了（2026-11-06），以及闸门现在落在哪

- **试的形状**：每行按格子的内容烘焙成一小块位图、按键复用；滚动时"脏"的 42 行里有 18 行只是往上挪了 y，内容一字未变 → 命中就是一次贴图。命中率是真数：`--bench` 报 **18 hits / 24 bakes**，与模型算出来的比例对上。
- **撤的理由是成本，不是没命中**：把一帧拆成三段分别计时之后（`--bench` 现在直接印 `write / blit` 两段，控件叠层也印），烘焙一行 **0.52 ms**，而同一行直接画进面里只花 **0.28 ms**——**缓存比它想省的那件事更贵**。整屏 flood 三种形状实测：无缓存 **11.08 ms**（write 10.23 · blit 0.46）∶ 共享图集 **12.77 ms** ∶ 每行一块位图 **13.90 ms**。churn/quiet 两档基本没动（0.45 / 0.38 ms），因为它们本来就只有 0-1 行要画。所以缓存整体撤掉，代码不留半套。
- **我自己两次把模型当读数，都记下来**：
  - 第一版 `--bench paths` 用"每帧只推进 1 行"的滚动模型，报行条带 0.49 ms 对现状 10.30 ms（21×）。真实 flood 是每帧 24 行新内容 → 只有 18/42 是重复，改成这个模型后同一档变成 **3.97 对 9.61**（2.4×）。**倍数缩水 8 倍，纯粹是我把命中率设错了**。
  - 缩水之后仍然选了 C，因为那张表里 C 的 bake 只画一颗 `FormattedText`——**没有行底色、没有 floor、没有 run 切分**，而控件的 `PaintRowInto` 三件都做。这一格差异把 0.14 ms/行 说成了 0.52 ms/行，我照着它写了一版实现。**教训：离屏对照表只能比"同一种画法的不同提交方式"，不能比"画法不同的两条路"**；能替代它的只有把计时器插进控件本身（现在就在 `LastPhaseMilliseconds` 里）。
- **闸门现在落在哪**：真窗口整屏重画 21.00 ms，离屏同档 11.08 ms，其中 **write 10.23 ms 占 93%、blit 0.46 ms 占 4%**。要 P95 ≤ 8 ms，就得把"画一行文字"本身压到 ~3 ms——**这条不是缓存能给的，是提交方式的**：每 run 现场 `new FormattedText` 换成一次成批的字形提交（Skia `DrawGlyphs`/glyph id 数组，一行一次调用），或者把解析+排版挪出 UI 线程（W3 那一格）。
- **因此欠你一个依赖决定**：走 Skia 字形直提要碰 `ISkiaSharpApiLease`（`Avalonia.Skia` 已在依赖图里，但那是**直接依赖 SkiaSharp**，本族第一次），或者走自定义合成视觉（`CompositionCustomVisualHandler`，能顺手把 present 腿那 ~9 ms 也吃掉，但形状更重）。两条都不改内核两颗的零依赖家规（只动 `QuickTerm.App`）。**没定之前我不写半套**——同 Unix PTY 那条口径。

## 帧时读数的方法学塌方：EMA 与离屏 raster 都不能当闸门的数（2026-11-06）

⚠ **本节里"真窗口单帧 write 2.0 ms"与"离屏比真窗口慢 5 倍"两句已被第四张截图作废**（`w 22.7`、`med = p95 = max = 22.8` 只采到一帧），读的时候按本节末尾"第四张截图"那一段为准；保留原文是因为它记的是我犯错的过程，不是读数。

同一晚上连着三次把"读数"说错，根因是**测的是均值、测的是 CPU 后端**，而闸门写的是 P95。逐条摆开：

- **EMA 冒充帧时**：叠层里那个 `_frameMs` 是 0.8/0.2 的指数均值。启动那一串整屏帧（字体首次成型、行池与回滚环建表）会把均值顶到 **21.00 ms**，而同一颗控件的**单帧** write 只有 **2.0 ms**。我据此写过"真窗口上限 ~48 fps < 闸门 55"，那句作废——**它拿的是被启动帧污染的均值**。
- **离屏 harness 比真窗口慢 5 倍，方向是反的**：`--bench` 画进的是 `RenderTargetBitmap`，脱离窗口系统时 Skia 走 **raster（CPU）**；真窗口的面是 GPU-backed。同一件事（41-42 行整屏重画）离屏 write **10.23-12.68 ms**、真窗口 **2.0 ms**。**离屏表从此只能读"相对比值"（脏行买到多少倍、分配多少），不能读绝对帧时**，更不能当"上限证据"——它连上下界都算不上，它比真窗口慢一个数量级。这条把上一节"#16 行内容缓存"里那句"write 10.23 ms 占 93%"的推论一并降级：那个 93% 是 CPU 后端的比例。
- **现在改测什么**：叠层换成**分布**——`ms med / p95 / max` 取最近 128 帧的环形样本，`w`/`b` 是最后一帧的分段，`keys / txt / sent / err` 是输入链。理由同上：闸门写的是 P95 ≤ 8 ms，只有分布能回答它；均值与末帧都不行。
- **#16 的前提因此变成未知**：真窗口稳态 ~2 ms/整屏帧的话，Skia 成批字形要打的靶子（write 10.23 ms）在 GPU 后端上根本不存在，**做它是负收益**（多一层依赖换一点不存在的成本）。**定档之前先要那一行 `ms p95`**：在真窗口里 `type` 一个 200 MB 文件跑满几秒，读 p95。p95 若 ≤8 ms，#16 直接关掉，SkiaSharp 那层依赖不引；p95 若 15-20 ms，再按成批字形开做。
- **顺带把输入链也钉住**：他说"无法输入"。静态排掉了编码器嫌疑（`EncodeKey` 在 `Key == TermKey.None` 那支会 `input.Char.ToString()` 兜底，普通字母走 `WriteUtf8`，`InputEncodingTests` 64 颗覆盖这条）。剩下的四种成因在屏上长得一模一样：控件没拿到焦点（keys=0）∶ Avalonia 没发 `TextInput`（keys>0、txt=0、sent=0）∶ 编码器返回 0（txt>0、sent=0）∶ 写管道抛异常被吞（sent=0、err>0）。叠层那四个计数就是为一次截图能分辨这四种而加的——**键盘输入是本机唯一证不了的一格**（管道会话生不出 console 孩子，活体测试在本机红）。

- **p95 那一行还没读到，输入这一环先塌了**：他回贴的第四张图叠层是 `ms med 22.8 p95 22.8 max 22.8 | w 22.7 b 0.0 r 42 | keys 0 txt 0 sent 0 err 0 | foc 0`。两格同时有数：
  - **`keys 0 txt 0 sent 0 err 0`** —— 键盘事件**根本没进控件**，与 pty、与编码器都无关（那两条路都在 `Send` 之后）。`b 0.0` 顺带说明**面→层的 blit 不是那 9 ms**，成本全在 `w`。
  - **`med = p95 = max = 22.8`** —— 一个分布三个分位数一模一样，说明**只采到一帧**（启动那一帧），我上一轮说"21 ms 是被启动帧污染的 EMA"这句**方向对、数值仍然没落地**：真窗口的整屏重画可能就是 22.8 ms，也可能稳态是 2 ms——**没跑起来就不知道**，别拿一帧的样本当分布。
- **静态依据（这次有出处）**：`TopLevel` 只重写 `OnOpened/OnClosed`，`Window` 没有 `OnActivated` 之类的钩子可挂；而"把控件当 `Window.Content` 交上去"并不会自动把焦点给它——树里唯一 `Focusable` 的就是它，也照样可能一次都不聚焦。**这就是"打开正常、打字无声"的形状**。
- **改了三处，都带得上数的理由**：① `TerminalView` 在 `AttachedToVisualTree` 自己 `Focus()`（不靠宿主记得调，也不靠事件时序）∶ ② `MainWindow.OnOpened` 把 `Focus()` 提到 `base.OnOpened` **之前**、`Start()` 之后（顺序写反过一次，就是这次的症状）∶ ③ 叠层加 `foc 0/1`（`GotFocus/LostFocus` 记账），一次截图就能分"没焦点"与"有焦点但没事件"。
- **`RoutingStrategies` 那一格记下来别再撞**：`AddHandler(RoutedEvent, handler, RoutingStrategies)` 这个重载在 12.1.5 上**编译不过**（`RoutingStrategies` 在包内文档里查不到公开类型），焦点改由控件自己在 attach 时申请，不需要隧道处理器。

## 键盘输入断在哪：`foc 1 / keys 0 / txt 0`，以及我没能自己验出来的那格

- **读数**（他回贴的第五张图）：`keys 0 txt 0 sent 0 err 0 foc 1`。焦点确实在控件（`foc 1` 是 `GotFocus` 记账，不是猜的），**但一次按键事件都没进控件**——所以断的是 Avalonia 的事件投递，不是编码器、不是管道、不是 pty。
- **机制（12.1.5 上的静态依据，不是猜测）**：`Avalonia.Base.dll` 的类型表里有 `TextInputMethodClient`、`ITextInputMethodImpl`、`TextInputMethodClientRequestedEvent`、`TextBoxTextInputMethodClient`，**没有** `Control` 上的可重写成员，也**没有** `TextInputOptions` 那种"每控件设一次"的附加属性。也就是说：**文本输入只交给"自带客户端的元素"**——`TextBox` 有 `TextBoxTextInputMethodClient`，一个自绘的 `Control` 没有，于是它拿到焦点也拿不到字符。终端正好是后者。
- **已做的两件事**：① `MainWindow` 处理 `TextInputMethodClientRequestedEvent` 交回一个无状态客户端（[TerminalTextInputClient.cs](../src/QuickTerm.App/TerminalTextInputClient.cs)：`SupportsPreedit=false`、`SupportsSurroundingText=false`、空 `SetPreeditText`——终端没有 preedit 可画，组合输入将来直接当普通 `TextInput` 字节进 pty）∶ ② 叠层把输入链摊成 `keys / txt/empty / enc0 / sent / err / foc / np / ex`，六种成因一次截图可分。
- **本机为什么证不了这条**：`--bench threads` 那次负对照顺手证明了另一件事——**后台线程连 `FillRectangle` 都过不去**（`Dispatcher.VerifyAccess`：对象被别的线程拥有）。Avalonia 的对象（含 `FocusManager`、输入投递）绑死在 UI 线程上，而本会话没有 UI 线程可驱动。**所以这一格只能他在桌面验**，我不写"大概能通"的话。
- **顺带否掉一个我自己的假设**："文本工作跨线程串行（每实例锁）→ 22 ms 帧 + 输入被吞"。`--bench threads` 直接抛 `VerifyAccess`，**根本不存在第二个线程在画**，假设作废；那 22 ms 的成因回到"每帧每 run 现建 `FormattedText`"本身，#16 的判据仍然是他桌面那一行 `ms p95`。

## 输入通了，画面没动：断点从投递挪到渲染（2026-11-06 第六张图）

- **读数**：`keys 47 txt 3/0 enc0 0 sent 47 err 0 foc 1 ptr 5 last ptr 698,244`，而屏幕仍停在 banner，且 `r 0 · w 0.0 · med 0.0 p95 25.0 max 25.0 · 0 fps`。
- **这组数说明什么**：键盘这一路**已经通了**——47 次按键进控件、编码器一次都没返回 0（`enc0 0`）、47 字节写进 pty 且无异常（`err 0`）。上一节的 `TextInputMethodClient` 那条修的是真问题（自绘 `Control` 不带客户端就拿不到文本输入），但它不是"无法输入"的全部：**字节出去了，屏上不动**。
- **`r 0 / w 0.0` 是这次的关键**：这一帧**什么都没画**。配合 `med 0.0`（样本几乎全是零）与 `0 fps`，形状是"启动那几帧之后，渲染器基本不再跑"。两种解释在截图上长得一模一样：① `InvalidateVisual()` 没被调（pty 读线程没交回字节 → pump 不触发）；② 调了但合成器不回调 `Render`。
- **本机为什么分不了这两种**：管道会话生不出 console 孩子（连不带 pty 的普通 `cmd.exe` 都卡在 `CreateProcessW`），所以"读线程有没有把字节交回来"这一格在本机永远测不到。
- **因此加了两个每帧计数器**（`Drain` 里累加、`Render` 开头消费）：`iv` = 本帧之前请求过几次重绘，`pk` = pump 这几毫秒里喂进引擎的数据块数。下一次的读法写死在这里：
  - `pk 0` → 读线程没交回字节，断在 pty 读侧（与显示无关）；
  - `pk > 0 iv 0` → 字节进来了但没请求重绘，断在 damage/合并逻辑；
  - `iv > 0` 而屏仍不动 → 请求发了、合成器没回调，断在渲染触发（下一步就是 `RequestAnimationFrame` 那一档）。
- **顺带把"帧"的定义钉住**：`CountFrame()` 在 `Render` 开头无条件跑，**一帧什么都不画也算一帧**——之前 `RecordFrame` 挂在绘制路径末尾，静止时样本堆不满，`med/p95` 读出来是启动帧的残影，容易把"渲染器在跑但没内容"误读成"渲染器没跑"。

## 第七张图定案：`pk 0 rd 0/0B eof 0` —— 断在 pty 读侧，机制是管道缓冲

- **读数**：`keys 29 txt 0/0 enc0 0 sent 28 err 0 foc 1 ptr 7 iv 0 pk 0 last key LeftAlt/None`，而 `2 fps · med 0.3 p95 18.7 max 21.7 · r 0`。上一轮写死的三种读法直接命中第一条：**`pk 0` = 读线程一个字节都没交回来**（不是渲染没跑——`2 fps` 说明渲染器在跑，只是无内容可画）。
- **机制（静态推定，本机证不了）**：`CreatePipe` 之前传的是 `nSize = 0`，即默认缓冲。默认值比一屏 VT 输出还小（128×42 纯文本就 ~5.4 KB，还没算属性序列），而 ConPTY 服务端一次要写完一整屏：**写不进缓冲就拆分并阻塞等宿主排空**。我们只在 `Read` 里等，没人排空 → 它永远等不到 → 输出零。**这同时解释"启动 banner 进得来"（一次性小写）与"之后再也不进"**，也解释为什么 `err 0`：我们这侧的写没失败，失败在它的写。
- **改法**：`ConPtySession.PipeBufferSize = 64 KiB`（一屏最宽输出 + 周围转义序列的余量），两侧管道都按这个尺寸建。**这一格本机无法验证**（生不出 console 孩子），判据在他桌面：`rd` 跟着涨、`pk > 0`、屏上出回显。
- **新加的三个数**（读线程自己报，`Render` 里印）：`rd 次数/字节`、`eof`（读线程是否已退出循环）。`eof 1` 而字节为 0 会是另一种病（管道被提前关闭），与 `rd 0` 完全不同，所以分开数。
- **我这轮自己犯的一次**：把 `private const` 写进了构造函数体内 → 77 条 CS1001/CS1002 语法错。错误码本身指向的是"成员声明位置不对"，读第一行就够定位，别再往下猜。

## W2-d 的内核半格：选择模型与 OSC 52 解码（2026-11-06，他休息时做的）

- **落的东西**：`term/src/QuickTerm.Terminal/TermSelection.cs`（新文件）——`TermPosition` / `TermRange` / `TermSelectMode` / `TermSelection`，加在 `Terminal` 上的绝对行空间 `TotalRows` ∶ `AbsoluteRow` ∶ `AbsoluteRowWrapped`（回滚行在前、可见网格在后）。OSC 52 的 base64+UTF-8 解码收进 `VtParser.DecodeClipboard`，`ClipboardRequested` 现在交回**解好的文本**，解不出来交回 `null`（xterm 对畸形 payload 就是忽略，把字面量贴出去是错的）。
- **为什么不缓存选中的格子**：`TermSelection` 只存两个端点与模式，取文本时现读网格。快照式的选择会把程序已经覆盖掉的文字继续留在剪贴板里——那种错是用户会直接感觉到的。
- **端点约定只有一条**：两端列号都是**独占**（单击一格 = `(r,c)` 到 `(r,c+1)`）。我一开始写成"钳制后再看要不要 +1"，两套约定同时存在，第一次跑就红了 7 颗。规则只有一条才不需要在两个地方保持一致。
- **空行的处理定死了**：选中区域覆盖到真实格子但没字 → 算一个空格（那就是你指的东西）；整段选择拖到屏幕最后一行的空白 → 那一行就是被选中了，`"one\ntwo\nthree\n "` 带一个空行是**正确行为**，我一开始想把它当"拖拽的终点不该多带一行"来特判，那条规则依赖"拖拽停在哪一行"，不成立，已按行为改测试而不是按测试改行为。
- **一次红→绿的完整过程**：17 颗新测试首跑 7 红（约定不一致）→ 统一独占端点 → 2 红（空行特判互相打架）→ 去掉特判 → 1 红（我把 `"   "` 当预期，实际正确行为是尾随空白被裁，与"选 `three` 不该带 9 个空格"是同一条规则）→ 全绿。**册面 181 绿 + 1 颗有意的红（ConPTY 可用性）**，term 子树 build 0 错 0 警。
- **还没接的**：App 侧的鼠标拖选、Ctrl+Shift+C 复制、OSC 52 写剪贴板——这三样都要真窗口才能验，按"没靶就不假装打过"的口径**这轮没写**，等 pty 那一格通了再接（否则接上去也测不出对错）。

## 第八张图：`rd 3/225B` 与 `sent 4` 同时成立 → 根因是 `FileStream` 的写缓冲

- **读数**：`keys 5 txt 0/0 enc0 0 sent 4 err 0 foc 1 iv 0 pk 0 rd 3/225B eof 0`。两条线索合起来才看得见：**输出侧回来了 225 字节**（banner + 提示符，正好是启动量），**输入侧我们"发了" 4 字节**，而之后一个字节的输出都没有。
- **根因**：`ConPtySession._input` 是 `new FileStream(handle, FileAccess.Write)`，默认**内部缓冲 4096 字节**。一次按键是 1–4 字节，全部留在我们这侧的缓冲里，管道另一端的 ConPTY 服务端**从来没读到过**——所以 shell 不回显、不执行、不输出。`sent` 数的是"交给 `FileStream` 的字节"，不是"进管道的字节"，这就是它一直显示 4 的原因。
- **改法**：`Write` 里 `Flush()`。这不是防御性代码，是这条路径的语义要求：终端每 keystroke 都必须立刻进管道，攒到 4 KiB 等于永远不送。
- **上一轮的 64 KiB 管道缓冲不算白做，但它不是这次的因**：225 B < 4 KiB，说明卡住的位置在更上游（我们的写缓冲），不在管道尺寸。那一改动仍然合理（一屏输出确实大于默认缓冲），只是别把它当根因记着。
- **我自己的仪器在说谎，也记一条**：`iv`/`pk` 是"本帧之前请求过几次重绘 / pump 喂了几块"，但 `CountFrame()` 在 `Render` 开头**先清零、再被同一帧的叠层打印**——所以它们恒为 0，`pk 0` 差点把我引向"读线程没干活"。改成先结转再清零（`_shownInvalidates`/`_shownPumped`），打印的是上一帧窗口的真实值。**教训：计数器"先清后读"是仪器自身的 bug，读数会自证清白**；这次是 `rd 3/225B`（累计值，不清零）与 `pk 0` 互相矛盾才暴露的。
- **另一格待查**：`txt 0/0`——按键走了 `OnKeyDown`（`keys 5`）但 `OnTextInput` 一次没进。ASCII 靠 KeyDown 路径已能出字节，所以不影响这次；但 IME/中文输入必须走 TextInput，那一格（W2 清单里的 IME）现在知道它还没通。

## 第九张图：flush 之后 `rd` 一字节没多，而且 `eof 0` —— 先证"这段代码跑过没有"

- **读数**：`keys 17 sent 15 rd 3/225B eof 0`，`txt 0/0`、`pk 0`、`iv 0` 照旧。三条各自都 informative：
  - `sent 4 → 15`：输入侧在动，flush 那轮改动**没有改变输出侧任何东西**；
  - `rd 3/225B` 一字节未增：管道里从启动之后就没来过任何东西；
  - **`eof 0`**：读线程还阻塞在 `Read` 里——它没退出。若是"我们把输出写端句柄提前关了"这类死管道，`Read` 会立刻返回 EOF（`eof 1`）。所以不是断管，是**没人再写**。
- **`txt 0/0` 把上一轮的结论也修掉了**：我说过"客户端那条修的是真问题"——机制仍然成立（自绘 `Control` 不带 `TextInputMethodClient` 就拿不到文本输入），但**它不是这次的症状**，因为 `OnTextInput` 一次都没进过。别再把它当已生效的修复记着。
- **新怀疑（比 flush 更上游）**：`pk` 恒 0 可能不是仪器清零的假象，而是 **4ms 的 `DispatcherTimer` 一次都没 tick**——本控件没有动画、没有 `RequestAnimationFrame`，画面完全靠这个 timer 驱动；timer 不跑 → 按键字节留在 `_inbox` 里没进引擎 → 屏幕自然不动。这与"输出侧一个字都没回来"是**两个独立症状**，之前被我按成一个了。
- **因此这轮只做一件事**：叠层加 `tick N`（`Drain` 入口自增，与 `pk` 分开数）。判据写死：
  - `tick` 每秒约 250 且 `rd` 仍 225 → pump 正常，病在 ConPTY 输入投递（下一档查 `WriteFile` 返回值/管道句柄归属）；
  - `tick` 恒 0 → 病在 UI 线程调度（`DispatcherTimer` 优先级或消息循环），与 pty 无关。
- **我这轮的错**：上一轮把 flush 当根因写完就交了，没有先证"改动所在的代码路径确实被执行过"。`rd` 没动就是那次结论的否证。

## 第十张图：输入整条通了，最后一环是"静止时没人排消息队列"

- **读数**：`tick 470 · rd 14/236B · sent 11 · keys 12`，屏上出现 `>11111111111` —— 按键进控件、进管道、shell 回显、字节回来到引擎、画面更新，**整条链第一次全绿**。上一轮的 flush 判断不是错判，只是被"读到的二进制没带上"抵消了一次。
- **剩下的最后一环**：`iv 0 · pk 0 · r 0 · w 0.0`。字节进了 `_inbox`，但没有一帧被重绘。机制：**`InvalidateVisual()` 只是把视觉标脏，它不推动消息循环**。这个控件没有动画、没有 `RequestAnimationFrame`，静止时 UI 线程不排队列 → 4ms 的 `DispatcherTimer` tick 不再被投递 → 这一 tick 排空的那点输出画了（回显出现），后面排队的字节就永远等着。
- **我上一轮的一句话是错的**：我说"画面完全靠这个 timer 驱动"——不对，**驱动画面的还是画面自己**：timer 靠消息队列活着，而静止时唯一能让队列继续转的只有渲染那一腿。
- **改法（一行）**：`Drain()` 末尾 `Dispatcher.UIThread.RunJobs()`，把泵改成自持——这一 tick 排完字节就顺手让队列继续转，节奏仍由 timer 给。这不是"加个循环拉"，是补上"事件驱动 + 空闲时不排队的宿主"这个组合里缺的那一环。
- **判据**：`pk > 0`、`iv > 0`、`r > 0`，`dir` 的输出整屏出来；`type` 大文件时 `rd` 应该到 KB/MB 级。
- **顺带一条本机读数**：离屏 flood 在 12.03/13.34/12.39 ms 之间摆（同尺寸同档位数），**±5% 是这台机器的噪声下限**，别把单次读数当结论——#16 那格要的是真窗口 `p95`，不是离屏均值。

## 第十一张图"显示不完整"：网格是对的，画错了位置（2026-11-06）

- **症状**：banner 第二行 `(c) Microsoft Corporation。保留所有权利。` 看着像断成两行、提示符 `...QuickTerm.App>` 与光标块之间空一大截、`[版 本` 中间有缝。
- **先分家，不猜**：新加 `--pty-capture [cols] [rows] [ms] [command]`（收真实 pty 字节流到 `%TEMP%\quickterm-pty-capture.bin`）与 `--replay [cols] [rows]`（把那份字节喂进引擎，打印原始流 + 每行格子 + 宽字符落位 + run 切分表）。50×10 回放 215 字节的读数：**row0 = 40 格一整行、row1 = 41 格一整行，wide @ 19=版 21=本 / 25=。 27=保 … 39=。——引擎和 shell 都是对的**。所以"断行"根本不存在，是**同一行里后半截被画到了别处**，病在渲染层。
- **根因两条，都有数（`--bench metrics`）**：
  - `_cellWidth` 取的是 `Ceiling(advance)`：Cascadia Mono 14px 的 advance 实测 **8.203 px**，格子却按 **9.000** 排 → 每个字符欠 0.797 px，44 字符的提示符**欠 35.1 px**，下一段墨从"它自己那格的边界"起画 → 中间那道空。
  - CJK 回退字体给的是 **1.0 em = 14.000 px**，而两格是 **16.406 px**（旧口径 18.000）→ 每个宽字符欠 4 px，`Corporation。` 后面那截实测 **19.9 px**。
- **改法五条**：① 格子用精确 advance（**取整只发生在墨与像素相接的地方，不在格与格之间**）；② 每个 run 按"它占的列数"横向缩放到自己的带上（`DrawRun`），纯 ASCII run 比值=1，一颗 transform 都不推；③ run 切分从控件挪进内核 `InkRuns`——尾格并进头部 run、列数按 `Wide` 计，这段只剩索引算术，正好是离线册钉得住的那种算术；④ 基线锚到**网格基线**而不是文本盒下沿（CJK 回退字体的 ascent 与 ASCII 探针不同，锚盒子会让中文比同行的拉丁低半格）；⑤ 行带与背景矩形按 `floor` 平铺，分数格宽不留亮缝。
- **代价（200 帧档，两个二进制交替各跑两次）**：旧 12.44 / 12.67 ms，新 13.35 / 13.03 ms → **整屏 +5%**；churn 0.57/0.56 对 0.62/0.54 —— 打字那一档没动。
- **一次已经否掉的写法**：按**宽度类**切 run（每个 CJK 边界都断），每行从 1 次 shaping 变 5 次，60 帧档读出 23–31 ms。撤掉，改成整行一次缩放：带两端精确，带内均摊。
- **测法更正（这条比上面那条更要紧）**：同一颗二进制在 60 帧档三次跑出 **23.2 / 30.6 / 30.8 ms**——这台机器现在的噪声是 **±30%**，不是账上写的 ±5%。我最初那句"2× 回归"一半是噪声。**能用的是：200 帧档 + 新旧二进制交替跑**，绝对值单独摆出来不算结论。
- **本机能力更正（把"这台机器起不了孩子"那条收窄）**：从管道 shell 起 QuickTerm 时孩子并不是起不来，而是**挂到了继承来的 std 句柄上**——banner 落到我的 bash stdout，pty 管道只收到 16 字节起手式（`ESC[?9001h` `ESC[?1004h`）。**用 `Start-Process` 把同一颗 exe 脱离管道起，pty 正常交付 215 字节**。结论：我现在能自己抓真实 ConPTY 流并离线回放，不必每格都等截图；`ConPtyTests` 那颗 availability 红是 testhost 的形状，没改、也不该改。
- **判据（给他这一轮的）**：banner 第二行一行到底、提示符与光标块相邻、`dir` 里含 CJK 的行与纯 ASCII 行的列对得上；顺手在 `type` 大文件时把 `ms p95` 报上来——#16 的闸门数要的是那个，不是离屏均值。

## 第十二张图"内容无法滚动"：布局算的是"窗口格子数减网格行数"，那一向是 0（2026-11-06）

- **症状**：ipconfig 的输出把 dir 顶掉了，滚轮翻不回去。
- **根因（一行算式）**：旧 `ComputeLayout` 把网格单独摆在 `topRow = visibleRows - Rows - offset`，历史挂在它上面 `HistoryCount = min(topRow, offset)`。而 `visibleRows = floor(H / cellHeight)`、`Rows = floor(H / cellHeight)` —— **两个数是同一个式子，永远相等**，所以 `topRow = -offset → 0 → HistoryCount = 0`。滚轮确实改了 `_scrollbackOffset`，但**没有任何一个槽位读它**。引擎侧的回滚环、`PushScrollback`、`TotalRows` 都是好的（离线册早就钉着）。
- **改法**：窗口改成**一个地址空间上的视口**——回滚行在前、网格行在后，整帧只需要"哪个绝对行进 0 号槽"这一个数。算式挪进内核 [term/src/QuickTerm.Terminal/TermViewport.cs](../src/QuickTerm.Terminal/TermViewport.cs)，配 7 颗离线钉子（[ViewportTests.cs](../tests/QuickTerm.Tests/ViewportTests.cs)），其中一颗直接把症状钉死：200 行进 20 格面 → `ScrollbackCount 181 / TotalRows 201`，上拉 100 行 → 顶槽 = 绝对行 81 = `line 82`，拉到顶 = `line 1`。
- **顺带三条**：钳位从 `ScrollbackCount` 改成 `MaxOffset(visibleRows, TotalRows)`（旧钳位让 offset 长到布局兑现不了的值，看起来还是"翻不动"）；游标槽位走 `Viewport.CursorSlot` 且只在 `AtBottom` 画；脏行→槽位走 `SlotOfAbsolute`，越界直接不画（回滚中时 damage 全在窗外）。
- **本机新验法（把一格从"只能截图"挪进可自验）**：`--bench scroll [w] [h] [rows]` 出两张 PNG（`quickterm-scroll-bottom.png` / `-back.png`），我直接看同一屏在底部与上拉 30 行时的内容——实测底部 `line 98..110` + 游标块，上拉后 `line 68..80`、游标消失、`w 5.3 r 13`，**历史可达并且画出来了**。
- **一条别误读的数**：那次跑出来 `offset 30 of 97` 而不是 107——喂 120 行时网格还是构造期的 80×24，第一次 `Render` 才改成 13 行，所以回滚环里是 96 行而不是 107。是测法顺序，不是布局又错了。
- **判据（给他这一轮的）**：滚轮能翻回 ipconfig 的开头；叠层的 `sb / off / top` 三个数跟着滚轮动；`off 0` 时回到实时、游标回来。
- **第十三张图的更正：方向反了**，`ScrollBy(-(int)e.Delta.Y * 3)` 里那个负号是我顺手加的。口径写死在这里：**Avalonia 向前转 = `Delta.Y` 为正**，**本族 `offset` 为正 = 往历史走**，两边同号，所以**不取反**。同一条口径也管 alt 屏那一支（`Delta.Y > 0 → WheelUp → 上箭头`，与 less/vim 的"向上翻"一致）。顺带把 `(int)` 截断改成 `Math.Round`：精密触摸板的 delta 是小数，截断会得到 0，等于触摸板滚不动。这一格没有离线钉子可钉——方向是输入语义，只有真窗口能判；判据换成一句话：**向上转滚轮，`off` 应该变大**。

## W2-d 桌面半格落地：拖选、高亮、Ctrl+Shift+C/V、OSC 52 写剪贴板（2026-11-06）

- **接的是哪半格**：内核那半（`TermSelection` / `TermRange.Text` / OSC 52 解码）上一轮已带 18 颗离线钉子落地；这轮补的是**只有桌面才需要的那半**——指针到格子的换算、高亮进绘制、键位接剪贴板。
- **入口形状**：`BeginSelection / ExtendSelection / ClearSelection / SelectedText / HasSelection` 是控件的公开口，指针事件只是其中一个调用方——**离屏 bench 用同一个口拖选**，所以"高亮画在哪一行"和"复制出来的字符串长什么样"这两格从此不需要截图就能判。
- **指针规则（照 xterm/Windows Terminal 的共识）**：程序开了鼠标上报就归程序，**按住 Shift 表示"我要选"**；按下 = 起选（`ClickCount` 2 = 词、3 = 行），拖 = 延伸，`e.Pointer.Capture(this)` 让出窗边缘继续拖还跟着走；鼠标上报的行号走 `GridRowAt`（**网格行，不是窗口槽位**——回滚中时两者差一个 viewport + 历史，旧代码直接拿 `y / cellHeight` 当行号是错的）。
- **高亮怎么进管线**：`InkRuns` 的 key 回调多了**列号**（`Func<TermCell,int,uint>`），因为选中态是"位置"的属性、不是格子自己的属性——不加这一维，run 就不会在选区边界断开，高亮会盖掉原来的颜色。选区每帧 `Resolve` 一次，`_paintFrom/_paintTo` 走字段而不是闭包，**每帧零额外分配**（离线册新钉一颗 `A_Position_Key_Splits_Where_The_Colour_Does_Not`）。
- **配色**：选区是一对固定色（`0x2E4B66` / `0xF2F2F2`），不是逐格反色——一行本来就带背景色的字反色后常常"看着没被选"。
- **本机判据（都跑过了，不用等截图）**：`--bench select 420 200` 在 51 列面上拖 `(0,5)→(2,4)`，交回的字符串是
  `one is short⏎this logical line is long enough that it has to wraidth`
  ——**软换行那一对碎片重接成一行**（`wra` + `idth`），首末两行按列切；词/行档单点不产生选区（`[]`，与"点一下就选中一格"相反，这是刻意的）；行模式拖到 `(2,9)` 交回整条逻辑行；PNG 里高亮落在第 0 行第 5 列起、第 1 行整行、第 2 行前 5 列，第 3 行不动。
- **代价（这一档现在判不了）**：同一颗二进制 200 帧档三次跑出 flood **13.10 / 17.53 / 21.95 ms**（68% 摆幅，blit 自己就在 0.28–0.76 之间飘）。取三次最小值与改动前比：13.10 对 12.85 —— **看不出回归，但这台机器现在分辨不了 30% 以内的差**，所以这句只是"没有变贵的证据"，不是"没变贵"。真窗口的 `ms p95` 仍然是唯一能定档的数（#16 也等它）。
- **拖动的重绘口径（记下来免得当成免费）**：`ExtendSelection` 只在**格子真的换了**时才标脏重绘（`position == _selection.Focus` 直接返回），所以频率由"跨过几列"决定而不是鼠标事件频率；但它现在走整屏 `_fullRepaint`，不是"只补旧端点与新端点那两三行"——按离屏 13 ms/整屏估，拖过时大约每秒几十次整屏补画，能用，**窄到两行的优化还欠**，等真窗口 FPS 有数再定值不值。
- **还没还的（记进下面的缺口清单）**：拖到窗口边缘不会自动滚；选区不跟着内容走（格子滚一行后端点指向的是"同一位置"而不是"同一段字"）；OSC 52 只写不读（`?52` 查询不回）；矩形列选没做。

## 删字符之后光标停在提示符中间：本机复现不出来，于是把字节口留住了（2026-11-06）

- **症状**：他打 `qqqqqq` 再退格删掉，屏上提示符文本完好、绿色光标块却停在 `QuickTerm.` 与 `App>` 之间（约第 41 列），而提示符是 44 字符、光标本该在 44。
- **本机复现（`QUICKTERM_SEND` + `QUICKTERM_DUMP` + `--replay`，全离线）**：同一路径（cwd 就是 `...\term\src\QuickTerm.App`，120×20）发 6 个 `q` 加 9 个退格，cmd 交回的尾巴是
  `ESC[?25l` + 六个空格 + `ESC[4;45H` + `ESC[?25h`
  ——**cmd 的退格回显是"写空格 + 绝对定位"，不是逐格 `\b \b`**；引擎回放这一串得到 `cursor=3,44`、`row 3 = E:\...\QuickTerm.App>` **完全正确**。多出来的 3 个退格 cmd 直接不理（空输入行），字节流里没有第二手痕迹。
- **所以能排除的**：解析器对 CUP 的 1-based 处理、退格回显的形状、`AutoWrap`/宽字符计数——这几条在同一条真实流上都对得上。剩下只能在他那一侧：**渲染面残留**（他那张图叠层是 `w 0.0 b 0.0 r 0`，最后一帧一格没画，而光标是每帧从引擎现算的——"旧面 + 新光标"正好长这样）、或那台实例上先前的 resize/scroll/select 改过状态。
- **为此新加的口（这才是这轮的产出）**：`QUICKTERM_DUMP=<目录>` 让**正在跑的 App** 把 pty 交回的字节写 `quickterm-pty-out.bin`、把发出去的字节写 `quickterm-pty-in.bin`（[term/src/QuickTerm.App/ByteTap.cs](../src/QuickTerm.App/ByteTap.cs)，只在 `Start()` 里按变量开，默认零成本）；`--replay [cols] [rows] [file]` 现在能直接吃那份 dump。`--pty-capture` 也接了同一个 tap 与 `QUICKTERM_SEND`（base64 的待敲字节，控制字符走命令行会被管道吃掉，所以走环境变量），并**把异常写进日志**——探针静默死掉这件事本族已经吃过两次。
- **测法一条**：`Convert.FromBase64String` 那次抛的是我给的 base64 少了末尾 `=`，而 WinExe 死了**什么都看不见**（exit `-532462766` = `0xE0434352`）。 hence 上面那句"异常写进日志"。
- **判据（下一轮）**：他带 `QUICKTERM_DUMP` 复现一次，我把那份 out.bin 回放——如果回放出的 `cursor` 也是 41，病在引擎收到的字节；如果是 44，病在他的渲染面残留，就往 `w/r/iv` 那一侧查。

## 第十四张图"删完后就这样了"：run 末尾的空格把整行拉宽了（2026-11-06，根因在测量口径）

- **症状**：打 `qqqqqq` 再退格删掉，提示符文本完好、光标块却停在中间（看着像第 40 列），而引擎的游标在 44。
- **复现不再靠他**：`--pty-capture` 加了 `QUICKTERM_SEND`（base64 待敲字节，`;` 分片、片间 400ms）。**分片不是方便，是保真**：一次 burst 把 6 个 q 和 9 个退格全塞进去，ConPTY 按屏刷新会把"打上又擦掉"合并成净变化——抓到的流里 q 根本没出现过，复现出来的就不是人手的形状。分片后真实回显是 `qqqqqq ESC[?25l ESC[4;45H 六个空格 ESC[4;45H ESC[?25h`。
- **先排除的**：`--replay` 在同一条流上 120×20 / 134×15 / 80×24 / 51×15 四种尺寸、整喂与 24/13/7/1 字节分片喂，**游标一律 `cursor=3,44`、行内容完好**——解析器、CUP 的 1-based、退格字节（0x08 与 0x7F cmd 都吃，我们发 0x7F）全部无罪。
- **真正的根因在测量**：`--bench paint`（把抓到的流按小块喂给**渲染器**并出 PNG）复现出了他那张图。加一行"每十列标一个十位数字"的尺子行以后读图：块在 44 的格位上，**墨却比格子宽**。查 `--bench metrics`：`FormattedText.Width` **不计行尾空格**——`"AB"` 与 `"AB      "` 都是 **16.406 px**，而 8 格该占 72.000 px。cmd 擦字符留下的正是六个**行尾空格**，于是这一 run 的 reserved(410) ÷ natural(361) = **1.136**，整行墨被拉宽 13.6%，光标按网格画就显得"早了几列"。
- **修法**：`InkRuns.Build` 把 run 末尾的 typed space 摘掉（`columns` 同步减），摘空了就整条不产出。行**首**空格留着——测量计它们，宽度对得上；行**中**空格留着——它们本来就在两个实字之间。顺带 shaping 变短，是净赚。离线册三颗新钉：`Trailing_Blanks_Come_Off_The_Run`、`Spaces_Between_Ink_Still_Stay_And_Trailing_Ones_Still_Go`、`Leading_Blanks_Join_The_Ink_After_Them` + `A_Run_Of_Nothing_But_Blanks_Produces_Nothing`。带背景色的空格不受影响——背景走的是另一条按格子的循环。
- **判据（已在本机过了）**：同一份 capture 重画，PNG 里 `...QuickTerm.App>` 后面紧跟光标块，尺子的 4 与 5 之间正是第 44 列。
- **测法两条要记**：① **改了内核要重建那颗 exe**——`dotnet test` 不会重建 `bin/cap`，我第一次"修完还是错"就是跑了旧二进制；② 读图别用眼睛量像素，**把尺子画进图里**，让图自己报列号。

## W2-e 前半：调色板归属定在引擎，OSC 4/10/11/104/110/111 从此落地（2026-11-06）

- **决定**：256 槽 + 默认前景/背景住进 [term/src/QuickTerm.Terminal/TermPalette.cs](../src/QuickTerm.Terminal/TermPalette.cs)，`Terminal.Palette` 公开；控件不再自带颜色表，只问引擎要 rgb。这条不是整理，是**修 bug 的前提**：表烤在渲染器里，`OSC 4` 就是空请求，`dircolors`、vim 主题、`ls --color` 的自定义色全都看不见。
- **默认表照搬原来那套**：base16 是 Tango 系十六格（本族 W2-c 以来的观感，一字没动），16 起是 xterm 的 6×6×6 立方、232 起是 24 级灰阶——后两段是算术，所有终端一致，**用值钉住而不是用公式钉住**（`The_Cube_Is_Xterms_Arithmetic` 钉 16/46/100/196/231 五个点，公式改了屏幕就红）。
- **规范口径**：`#RRGGBB`（8 位带 alpha 的丢掉 alpha）、`rgb:RR/GG/BB`（每位 1–2 个十六进制，短的按 xterm 规则放大：`f` = 255，不是 15）、十六个系统名（含 bright*）。**读不懂就整条不动**，绝不半应用——半应用就是一个槽被改坏而主题其余部分照旧，那种错最难查。`?` 查询解析后丢弃（本引擎一条设备控制查询都不回，这条单独记账）。
- **一条必须有的副作用**：改颜色表要 `RepaintEverything()` → `Damage.MarkAll()`。格子本身没变（还是"槽 1"），变的是槽 1 的意思；不标脏就出现"请求收到了、屏上颜色不动，直到别的行恰好脏了才跳变"。两颗钉子：`A_Palette_Change_Dirties_The_Whole_Screen` 与 `An_Ignored_Request_Dirties_Nothing`（后者防的是把 `?` 也当成改动去刷全屏）。
- **一颗索引算术的坑**：`OSC 104`（无参 = 全部复位）在没有 `;` 时，旧写法把**设计符本身**当成 payload 交下去，于是 `OSC 104` 变成 `OSC 104;104`——复位了第 104 槽而不是整张表。修法：没有 `;` 就没有 payload。顺带把 `OSC 0/2` 的标题同一条路走通（以前 `OSC 0` 裸形会把 "0" 当标题）。
- **本机判据（不用截图）**：`--bench color` 出两张 PNG。前一张是 base16/立方/具名色/bold/背景色/inverse/truecolour 的样张；后一张在同一屏上发 `OSC 4;1;#FF6666;2;#66FF66;4;#6699FF` + `OSC 11;#102030` + `OSC 10;#E8F0FF`，实读：**整屏底色转成藏青、默认墨转成冷白、red/green/blue 三段换色、上面几行老文字也一起换**（调色板是全局的，老行跟着变正是 xterm 的行为），再发 `OSC 104;1` → red 回到 Tango 原色而 green 仍是新色。
- **册**：`PaletteTests` 16 颗；离线册 **219 绿 + 1 颗有意的红**（ConPTY availability）。
- **W2-e 剩下那半**：base16 与 `GridFontFamily` 都还是代码常量，要等设置面（选字体、选主题、持久化）才有归属；字体回退链现在只有 `Cascadia Mono, Consolas, Microsoft JhengHei Mono, monospace` 这一条硬编码。

## 第十五张图"最下面这行重叠了"：HUD 改成默认关，Ctrl+Shift+I 开（2026-11-06）

- **症状**：`dir` 把屏填满以后，最后一行叠层读数压在提示符上，读出来像 `E:\...\App▌0 r 0 | keys 132 ...`——**用户以为终端自己写出了这行**。
- **根因不是重叠，是默认值**：叠层从 W2-c 起就是"常驻"，因为它这几轮是唯一能在一张截图里交出十二个读数的探针。但终端面是终端的：任何不属于 shell 的墨长期盖在最后一行，都是错的形状。
- **改法**：`_showOverlay = Program.RunningBench` —— **日常默认关**，`Ctrl+Shift+I` 开关（键位走 Ctrl+Shift 那一族，cmd/vim 都不吃它）；离屏 bench 仍默认开，因为我出的 PNG 就是靠读数判的。关掉要 `InvalidateVisual()` 一次：叠层画在活层而不是自持面上，不重绘就留旧墨。
- **顺带把读数排成两行**：单行从右边被切掉，而**被切掉的正是每次要问的那半**（`sb/off/top`、`sel/clip` 都在尾巴）。现在第一行 fps/ms/w-b-r/last/列×行，第二行输入链 + 滚动 + 选区剪贴板，两行都在屏内。
- **代价**：无——少画一行字。
- **判据（给他）**：重建后最后一行应该只有提示符，没有读数；按 `Ctrl+Shift+I` 才出现两行 HUD，再按一次消失。

## W2-f：DEC 2026 合帧落地，HUD 改两行且默认关（2026-11-06）

- **形状**：`ESC[?2026h` 到 `ESC[?2026l` 之间**不起新帧**——`Drain()` 排完字节后先问 `MayPaint()`，开着括号就只记账不 `InvalidateVisual()`。damage 照旧在引擎里累加（一颗离线钉子钉的就是这条：延后的帧必须仍然欠着那次重画，否则合帧变成丢帧）。
- **安全阀不是可选项**：程序开了括号不死心（等输入、崩了、或者干脆忘了关）会把屏**冻住**。所以开着超过 `SyncGraceMilliseconds = 250` 就放行一帧，并把这一帧记成 `released`——HUD 上 `sync 开/延后/放行` 三个数直接把"谁没关括号"暴露出来。判据一句话：**裂帧的代价只值 250 ms，冻屏的代价是永远**。
- **本机读数（`--bench sync [片数]`）**：同一屏内容切成 8 片喂。无括号 → `deferred 0`；有括号 → `deferred 7 released 0 open 0`，**8 帧里省掉 7 帧**；括号不关 + 等 320ms 再喂一片 → `deferred 1 released 1 open 1` → **grace 放行了**，屏没冻。
- **顺带把 `Inject` 改走真路**：以前离屏直接 `_term.Write`，绕过了 inbox 与 pump——于是 bench 量的是一条**发布版不跑的路**（合帧这条决定就完全测不到）。现在 `Inject` = 入队 + `Drain()`，bench 与桌面同一条管线；帧时读数没变（flood 13.68 / churn 0.62 / quiet 0.60 ms，200 帧档）。
- **HUD 两行**：单行从右边被切，而**被切掉的正是每次要问的那半**（`sb/off/top`、`sel/clip`）。现在第一行 fps/ms/脏行/last/列×行，第二行输入链 + 滚动 + 选区剪贴板 + sync。默认关（`Program.RunningBench` 才开），`Ctrl+Shift+I` 开关——理由记在第十五张图那一节：**终端面是终端的**。
- **还欠的一格（诚实）**：真窗口的 FPS **曲线**没有——HUD 给的是"最近 128 帧的分位"，没有"这一秒掉了多少帧"；headless 的 GPU 路径本机也拿不到（离屏是 Skia CPU 光栅，比窗口慢约 5 倍，只能当上限）。这两条都要等一台能跑真窗口又能录屏的环境，不是再写一轮代码能变的。
- **册**：离线 **220 绿 + 1 颗有意的红**（ConPTY availability），新增 `Damage_Keeps_Accumulating_While_A_Sync_Update_Is_Open`。

## 缩窗口吃掉文字：resize reflow 落地，顺带挖出 SGR 40-47 整段没实现（2026-11-06）

- **症状（本机探针读出来的，不是他报的）**：40×6 面写一句 64 字符的软换行句，`Resize(20, 6)` 之后交回的是 `the quick brown fox` / `dog while the cat sl` / `short`——**"e lazy dog" 与 "eeps" 直接没了**。旧 `Screen.Resize` 是"每物理行截到新宽"，从不把续行接回去。
- **改法**：`Terminal.ReflowMain(columns, rows)`——按 `Wrapped` 位把物理行接回逻辑行，再按新宽重切，底部 `rows` 片留屏、其余重回回滚环，游标按"它在逻辑行里的第几个字头"重新落位。三条边界写死：**alt 屏不重切**（TUI 自己会重画，xterm 同规则）；**自定义滚动区（DECSTBM）退回截断路**（行号对程序有意义，悄悄重排等于把内容从程序脚底下抽走）；**行尾"真空格"先摘再切**（Rune 0 且无色的格子是地板不是内容——留着会把一个空行变成两行，第一次实测就是把正文推进了历史），带背景色的空格照旧保留。
- **顺带挖出来的大一颗**：`SGR 40–47`（普通背景色）**整段没有 case**，`ESC[41m` 掉进 `default: break`——也就是说 `ls --color`、vim 状态栏、git diff 的**背景色一直全被丢掉**，而前景色看着正常，所以截图里看不出来。发现路径本身值得记：不是去测颜色，是重切测试要一条"画满整行的色条"，一测 `painted=0`。补了 `Normal_Background_Range_40_To_47` 五段断言。
- **代价（`--bench resize 5000 60`）**：环里留着 2480–5000 行历史时，80↔120 交替 60 次 = **945.9 ms，每次 15.77 ms**。窗口拖动的 resize 是**每帧一次**（宿主在 `Render` 里同步，不是每个鼠标事件一次），所以整段拖动期间每帧要多付 ~16 ms——**能跟手但明显变沉**，5000 行满环更糟。这一格现在记为已知代价，不是"已优化"：真要压，方向是只重切最近 N 行（老行留旧宽）或把 List 逐行的分配换成复用一个 scratch 缓冲，等他拖动实测反馈再定值不值。
- **册**：`ReflowTests` 11 颗（缩/放/游标/宽字符不跨片/色条保宽/历史一起重切/alt 不重切/滚动区退回/内容短于窗口顶对齐），离线 **232 绿 + 1 颗有意的红**。

## 存储层落地：两份 json + DPAPI 密文，格式被真的读写过一次（2026-11-06）

- **形状**：`%APPDATA%\QuickTerm\settings.json`（全局一份）+ `hosts.json`（每台主机，可盖一层）。类型是 `QuickTermSettings` / `HostProfile`（`Kind` = Local/Ssh/Telnet/Wsl/Rdp、`Group` 就是树里的分组、`Terminal` 那页三个可空字段表示"继承"）。落法 [term/src/QuickTerm.App/QuickTermStore.cs](../src/QuickTerm.App/QuickTermStore.cs)。
- **为什么先做它**：§6 三条都拍定了，而格式只有被真的写一次才算定——纸上的 json 结构在第一次用时几乎总是要改。
- **AOT 口径是结构保证，不是祈祷**：序列化只走**源生成的 `JsonTypeInfo`**（`Context.QuickTermSettings` / `Context.ListHostProfile`），代码里没有一处 `typeof`/默认选项重载；DPAPI 走 `LibraryImport`（App 这颗因此开了 `AllowUnsafeBlocks`，内核两颗照旧不开）。整颗 App 的裁剪发布还没跑——那是 #9 那一格，等 C++ 工作负载。
- **密码**：存，但存的是 **DPAPI 当前用户作用域的 base64 密文**，读写各过一层 `CryptProtectData`/`CryptUnprotectData`，明文不落盘。代价写死在计划里：**换机器文件照用、密码不跟过去**（DPAPI 绑用户+机器），真要跨机同步将来加"用口令导出"，不预做。
- **一条按决定分出来的分支**：`Rdp` 类型的 `Secret` 在写入前被 `ForgetUnusableSecrets()` 清掉——`mstsc` 命令行不收密码，存了也不会被用；它的正确归宿是凭据管理器里 mstsc 自己读的那条 `TERMSRV\<host>`（§6 第 7 条）。
- **四条容错，都是"用户能自己修"的口径**：① 文件不存在 = 首次运行，不算问题；② 读不出来 → 回默认 + `LoadProblem` 说清是哪份文件，**绝不因为自己的配置文件把终端弄死**；③ 手改常见的尾逗号/注释照吃；④ **写 camelCase、读不分大小写**——这条是测试逼出来的：默认 STJ 大小写敏感，手写 `{"name": …}` 会静默变成空主机，什么都不报。写入还走 `tmp + move`，中途崩不会留半份文件。
- **册**：`StoreTests` 12 颗（首次运行/往返/两份文件落盘无 .tmp/尾逗号/坏文件回退/未知字段忽略/明文不出现/DPAPI 往返/空密码/解不开当没有/RDP 落盘前摘密/枚举写名字），离线 **244 绿 + 1 颗有意的红**。
- **接线（同一轮做完，不然它就是个没人用的格式）**：`App` 启动先 `QuickTermStore.Load()` → 语言取 `Settings.Language ?? 系统`（**先读设置再 Apply，否则会先闪一帧系统语言**）→ `MainWindow(store)` → `new TerminalView(store.Settings)`（回滚行数/字体名/字号在构造期定死，因为行环只分配一次、字形度量是全局基准；换字体要新建控件，这条写进注释而不是假装支持热切）→ `Start(Settings.Shell, Settings.StartDirectory)`。同一个 `ShowNotice` 接两种"一句话说明白"的失败：shell 起不来、设置文件读不出来。
- **设置真的到字形的证据（`--bench metrics` 尾部）**：默认 14 px → 格宽 8.203 px；`FontSize = 20` 的控件 → 11.719 px，**"the setting moved the grid"**。没这一行，"接线了"只是我的说法。

## Local 页落地：一个顶栏 + 一个二字段对话框，hosts.json 有了第一个消费者（2026-11-06）

- **为什么是这两样**：`§7` 那张参照图里 Local 页只剩"选哪个 shell、从哪个目录起"没做（其余格都依赖未定项），而这两样恰好也是**不走 UI 就只能改 json** 的两样——判据不是"像参照图"，是"不用手改文件也能换"。
- **入口形状**：顶栏一条（`MainWindow` 现在 `DockPanel`：顶栏 + 终端填满），按钮 `新建本地会话` 开模态框。**一个窗口一条会话**，所以它 `Restart` 当前会话而不是开新标签——标签条是下一步的事，现在假装有会做成两层要重写。
- **对话框两字段**（[term/src/QuickTerm.App/LocalSessionWindow.cs](../src/QuickTerm.App/LocalSessionWindow.cs)）：名称、Shell（下拉列举探测到的 + 可自由填）、起始目录（+ 系统目录选择器）。**目录不存在时不放行**：否则 cmd 会在用户以为已经在那个目录的会话里打一行"系统找不到指定的路径"。
- **存下来而不是只用一次**：确认后写进 `hosts.json`（`Kind = Local`，新增 `Shell`/`StartDirectory` 两个字段），于是**上一轮那个"没有消费者"的格式正式被用上**；同时把这两项也写进 `settings.json`——**Local 页就是"默认会话"**，不然下次启动还是老设置，读起来就是"它忘了"。写盘失败只在格上留一行 `设置保存失败：…`，会话照跑——"忘了"和"丢了"必须能分清。
- **`--bench local` 的口径（诚实版）**：它只印**探测到的 shell 列表**，不印对话框外观。试过把对话框的 `Content` 离屏画成 PNG，**结果是全白**——控件树没接到 TopLevel，主题样式解析不了（终端面能离屏画是因为它自己画一切、不依赖主题）。所以**布局这一格是他的桌面读数**，不是我的。
- **本机读数**：`--bench local` 交出 2 条——`C:\WINDOWS\system32\cmd.exe` 与 `System32\WindowsPowerShell\v1.0\powershell.exe`（本机没装 pwsh 7，列表里就没有，符合"探测到什么列什么"）。
- **顺带修的两颗（Restart 逼出来的）**：① pty 读线程原来每轮读 `_pty` 字段，重启把字段置空那一刻会空引用——改成捕获会话局部变量；② 退休会话的退出通知会盖到新会话的 banner 上——`Exited` 处理器加了 `ReferenceEquals` 守卫。另：`Restart` 先跑再设标题，因为 `RIS` 会清标题，反序的话名字一秒后被抹掉。
- **册**：仍 **244 绿 + 1 颗有意的红**（对话框本身不可离线测——构造控件要平台服务，testhost 里没有；这是形状限制，不是省事）。

## 外壳落地：左侧主机树 + 标签条 + 多会话（2026-11-06，照他给的参照图）

- **形状**：`Grid(250,1,*)` —— 左栏主机树（`我的主机` → 分组 → 主机，标签前缀 `ssh/rdp/tel/wsl/loc` 代替图标）、一条 1px 分隔线、右栏 = 标签条 + 会话面板。**一个标签一条会话**：每条自带 `TerminalView` 与自己的 pty，切标签只是 `IsVisible` 互换，后台会话照常跑。
- **三条默认，按上一轮提的照做**（他没逐条回，但"像这样设计"就是这个意思）：一个窗口多标签 ∶ 关标签 = `Stop()` 掉那条会话的子进程 ∶ 关窗口 = 全关，不追问。
- **两个会话级 bug 是提前修掉的，不是等他撞**：① 后台标签的 OSC 2 会抢窗口标题——`TitleChanged` 只在"这条就是当前标签"时才改标题；② `Start` 排在 `Dispatcher` 队列里（要等第一次布局拿到真实尺寸），若标签在那之前被关掉，就会给一条已经没人的会话起 shell——加了 `_sessions.Contains` 守卫。
- **明确没做（不摆死按钮）**：参照图右上那排图标（终端/目录/信息/工具/主题/设置）——它们对应的功能一件都没落地，先摆上去就是假按钮；分组折叠状态持久化、标签拖拽排序、右键菜单（改名/删除/复制）都欠。
- **一处分歧要说明**：树里是**单击开标签**，不是参照图的双击。原因是现在的树没有选中态之外的操作（没有右键菜单、没有属性页），单击更容得下误触；等有了右键菜单再改回双击。
- **判据**：外观只能他看（themed 控件离屏全白，这条已经记过一次）；**能离线判的都已判**——册 244 绿 + 1 颗有意的红，三颗项目 0 错 0 警，`hosts.json` 的读写有 12 颗钉子。

- **RDP 启动器（§6 第 7 条那半格落地）**：树里 `rdp` 前缀那条点下去不是开标签，而是 `mstsc.exe /v:<host>[:<port>]`——默认 3389 省略，别的端口附在主机后面；主机为空或起不来就在格里说一句，不抛。**凭据那一支故意没做**：RDP 页还没有密码字段，等它有了再写 `TERMSRV\<host>` 那条通用凭据（在那之前 mstsc 自己的"记住我"会写，位置一样）。
- **树能删了**：右键 → 删除，写盘后重建树。理由是"只能增长的列表就是会积满错误的列表"——他截图里已经有两条同名 `11` 了。
- **本族第一个 `InternalsVisibleTo`**：`QuickTerm.App` → `QuickTerm.Tests`，只为让 `RdpLauncherTests` 能钉那条命令行（5 颗：默认端口省略/自定义端口/缺端口/手改的空格/空地址拒发）。不开公开面，与内核那颗"没有读者就不留公开面"同一条口径。
- **判据**：册 **249 绿 + 1 颗有意的红**；RDP 那条真连一次只能他验（会开一个 mstsc 窗口）。

- **手动新增（2026-11-06，接他的词）**：一个对话框合并了原来的 Local 页——**类型选择 Local/RDP，字段随类型显隐**（Local：Shell + 起始目录；RDP：主机 + 端口）。**SSH/Telnet/WSL 不摆灰**：没有传输层，灰掉的选项等于许愿。入口两处（侧栏 `手动新增`、标签条 `+`），确认后**写盘 → 重建树 → 打开**（Local 开标签、RDP 起 mstsc）。一处必须分开的口径：**只有 Local 会更新默认会话**——加一条 RDP 不该把下次启动用的 shell 清空。校验两条：RDP 必须有主机、Local 的目录必须存在。
- **删掉了旧的 `LocalSessionWindow`**：同一套布局留两个框，迟早会改一处忘一处。
- **判据**：册 **249 绿 + 1 颗有意的红**；对话框外观与他路线的实测只能他看。

- **密码进框了，去处按类型分**（2026-11-06）：`手动新增` 现在有 `用户名` + `密码` 两行；密码**故意不放进 `Result`**（`HostEditorWindow.Password` 一次性交出明文），否则它会跟着 profile 被序列化进 `hosts.json`。落点两条：**RDP → Windows 凭据管理器的 `TERMSRV/<host>`**（`advapi32` 的 `CredWriteW`/`CredReadW`/`CredDeleteW`，本族第一处 advapi32；mstsc 命令行不收密码，它自己读这条），**其他类型 → DPAPI 密文写进 `hosts.json`**（§6 第 6 条）。密码**不做 trim**：首尾空格可能是密码的一部分，悄悄改掉就是"凭据错误"却什么都没得看。
- **凭据库那条是本机能验的**：`CredentialStoreTests` 3 颗（真实凭据库往返含中文、覆盖写、target 形状），用一次性 host 名并在 `finally` 删掉写进去的东西。
- **判据**：册 **252 绿 + 1 颗有意的红**；真连一次 RDP（mstsc 该不再问密码）只能他验。

## 已知缺口（P0 不遮丑，逐条给后果与还债窗口）

- `src/QuickTerm.Terminal/UnicodeWidth.cs + Wide`：**W/F 区间表是人肉挑的子集**，不是 EastAsianWidth.txt 生成的。后果：新增 emoji/生僻汉字可能算错宽度。W1 末换成生成物 + 逐区间回归。
- `src/QuickTerm.Terminal/Terminal.cs + PutRune`：**组合字符直接丢**——宽度为 0 的码位（重音符、变体选择符、ZWJ）不叠加也不占格，基底字留着。后果：越南语声调、emoji 的 VS16 观感错。`UnicodeWidth.IsCombining` 曾为这一格备过，本轮按"没有读者就不留公开面"摘掉；与选区一起做（要先定 grapheme 边界策略），W2 之后。同轮摘掉的还有 `TermCell.IsBlank` 与 `TerminalColor.FromBits`；`MouseAlternateScroll`（`?1007`）原本只声明没有写入方，现已接进 `SetMode`。
- `src/QuickTerm.Terminal/Terminal.cs + Resize`：**已 re-flow**（2026-11-06，形状与代价记在上一节）。还欠的两条：一是**满环历史时每次 15.77 ms**，拖动会明显变沉，压法（只重切最近 N 行 / 换掉逐行 List 分配）等他实测反馈再定；二是 **alt 屏与自定义滚动区按设计不重切**——前者由程序自己重画，后者退回截断，都是选择不是缺口。
- `src/QuickTerm.Terminal/VtParser.cs + Sgr`：`58`（下划线颜色）解析后丢；`53/73/74/26` 只认不实现。`4:4/4:5`（点线/虚线）塌成单下划线。
- `src/QuickTerm.Terminal/VtParser.cs + DcsPassthrough`：DCS 吞到 ST 不派——sixel、Kitty 图形协议、`DECRQSS` 查询都不回。图像类程序显示空白块而不是报错。
- `src/QuickTerm.Terminal/VtParser.cs + Dispatch` 的 `'t'`：窗口操作（标题/图标栈、`CSI 22/23 t`）整条不接，`vim` 的 title-stack 会静默失效。
- `src/QuickTerm.Terminal/Terminal.cs + DECSCUSR 之外的 saved cursor`：`ESC 7/8` 与 `CSI s/u` **共用同一槽位**（VT 原生即如此），而 xterm 把它们分开存。后果：极少数同用两套的 TUI 恢复错位。
- `src/QuickTerm.Terminal/VtParser.cs + OscString`：**OSC 4/10/11/104/110/111 已落地**（2026-11-06，`TermPalette`），还欠的是**查询不回**：`OSC 4;1;?`、`OSC 10;?`、`OSC 11;?` 解析后丢弃——本引擎现在**一条设备控制查询都不回**（DA1/DA2/DSR/CPR/DECRQPSR 同理），`vim` 的若干探测因此按"无响应"走它的默认路径，实测能用，但形状是缺的。`OSC 12`（光标色）、`OSC 46/47/51`（日志/读屏）不认。
- `src/QuickTerm.Terminal/Terminal.cs + ClipboardRequested`：OSC 52 已接到底（解码在内核、写剪贴板在宿主，2026-11-06）；**只写不读**——`OSC 52;c?` 那种读请求同样落在"查询不回"那一格。
- `src/QuickTerm.Terminal/Terminal.cs + SetMode` 的鼠标位：1000/1002/1003/1006 只存位，**不发上报字节**；`?1048/?1049` 之外的 `?47/?1047` 与 `?1049` 走同一条实现（存 cursor + 清屏 + 退出还原），legacy 差异按"没有客户端依赖它"处理。
- `src/QuickTerm.App/TerminalView.cs + MayPaint`：DEC 2026 **已合帧**（2026-11-06，含 250 ms 安全阀，读数记在 W2-f 一节）。还欠的是**同一帧内的多次重排合并**：现在一帧里 damage 覆盖多少行就画多少行，不做"同一行被写三次只画一次"的合并——churn 档 0.62 ms 说明这一格现在不值钱，等真窗口 p95 抬头再动。
- `src/QuickTerm.Terminal/Terminal.cs` 未接：`CSI S/T` 之外的横向滚动 `SL/SR`（`CSI @ / CSI g` 已接）。
- `src/QuickTerm.Pty/PtySession.cs + Start`：**Unix 侧一个实现都没有**（抛 `PlatformNotSupportedException`）。这一格欠的是一次决策：自带 20 行 native `forkpty` helper（要 C 工具链、破坏"内核零依赖"的打包形状）vs `openpty` + `posix_spawn` 纯托管（孩子拿不到控制终端，`Ctrl-Z`/作业控制/SIGWINCH 语义有折损）。W1 末定档，定完再写，不写半套。
- `src/QuickTerm.App`：**存在且已自持渲染面**（W2-c，2026-11-05），欠的账按档列清：字形提交方式没换（每 run 现建 `FormattedText`；行内容缓存试过又撤了，因为烘焙比直接画更贵，flood write 10.23 ms 就是这一处）∶ **选择与复制、OSC 52 写剪贴板、括号粘贴接线已落**（W2-d 桌面半格，2026-11-06，形状与本机判据见那一节），还欠三格：**选区不跟着内容走**（端点是"位置"而不是"快照"，格子滚一行后指向的不是当初那段字——xterm 靠跟踪 scroll delta 锚住，要还就得给 damage 带上滚动量）、**拖到窗口边缘不自动滚**、**OSC 52 只写不读**（`?52` 查询不回）。矩形列选没做。∶ **调色板归属已定**（W2-e 前半，2026-11-06）：256 槽 + 默认前景/背景都在引擎的 `TermPalette` 里，控件只问它要 rgb——所以 `OSC 4/10/11/104/110/111` 从此真落地，`dircolors`/vim 的主题不再是空请求；还欠的是**主题与字体设置面**（base16 那十六格现在是代码里的 Tango 常量，`GridFontFamily` 那句字体串也是硬编码，两者都要等 W2-e 的设置界面与持久化）∶ IME 组合输入、DPI 真机读数都还欠。

## 家规与打包形状（本族第一次立；2026-11-05 逐条定案，仍开的只剩 AOT 工具链与 Unix PTY 那一档）

- `term/Directory.Build.props`：内核两颗（`QuickTerm.Terminal`/`QuickTerm.Pty`）**零 NuGet 依赖**，只 P/Invoke 操作系统自己的库；GUI 依赖只准出现在 `QuickTerm.App`（`IsPackable=false`，永不发包）。这条比另五族多一句，因为终端内核必须能被无头基准、未来的 SSH 面和别人自己的宿主直接引用。
- **分发口径（2026-11-05 定：本族不上 NuGet）**：QuickTerm 走仓库源码 + 自包含产物，两颗内核件的 `IsPackable=false` 是**终态**而不是 P0 临时；`<Version>` 只作程序集身份，本族**不进**另五族那条"改版本同步 N 处 → 逐颗 pack → 剪贴板取 Key 推送 → 源站对账"的链路，也不挂 `PackageReadmeFile`。实查过风险面：仓库内没有 pack/push 入口，pack 颗数是外部按 `sql/src/*/*.csproj` 计数驱动的，所以 `term/` 加进解决方案不会撞那颗数——这条决定的影响只在本族。顺带一格免费了：`QuickTerm.*` 包名是否被占**不用再核**（不建公开包）。**不改另一条**：内核零第三方依赖照旧，它与发不发包无关。
- **总取向（2026-11-05 定）：以高性能为主，不满足者自研**。这条不是偏好，是判据：任何一格只要"用了别人的东西就得接受它的形状"，先量它，量不过就自己写，**不接受"慢但省事"**。它当场改了三格的方向，也都带数字要求：
  - **渲染层（W2-c）**：Avalonia 每趟给新层 + `InvalidateVisual()`，damage 只能决定**要不要重绘**、决定不了**重画哪几行**——这一格不达判据，**自研**：控件自持 render target，脏行区间补画。**前半截 2026-11-05 已落地并有数**：一块 `RenderTargetBitmap` + `CreateDrawingContext(clear:false)` 原地补画（不用双面包屑拷贝），进度条那一档 ~11.9 → 0.48 ms/帧（24×），零格变化那档只剩 0.40 ms 的固定底价。**后半截按"一次烘焙字形"的字面做法已经试过并否掉了**：行内容缓存（键=格子内容）实测烘焙一行 0.52 ms > 直接画一行 0.28 ms，整块撤掉，细节与两次模型误差记在"#16 行内容缓存"一节。剩下的判据是数字而不是形状：整屏 11.08 ms 里 write 10.23 ms（93%）全出自每 run 现场 `FormattedText`，真窗口同一档 21.00 ms → 上限 ~48 fps < 闸门 55。**要达标得换提交方式**（Skia 成批字形 ∶ 自定义合成视觉 ∶ 排版挪出 UI 线程），这三条都带依赖/形状决定，等他定档——原则不变：量过不达标就自己写，但"自己写"不等于绕过依赖决定。
  - **SSH 层（P1/W3）**：退路改成硬默认——`SSH.NET` 与 `libssh2` 两条都要过 W3 那份 200 MB 基准与 AOT/裁剪干净度，**任一不过就自研 transport**（TLS 走 BCL、KEX 自写、SFTP 包层自写）。要付的账说清楚：这是几人月量级，且安全原语自研的审查成本归他，不是归我。
  - **Unix PTY（任务 #7）**：`posix_spawn` 拿不到控制终端那一档算"不满足"，因此方向倾向**自研 native `forkpty` helper**；但在"8 在哪验"没定之前**仍然不写**——原则改的是选择，不改"没靶就不假装打过"。
- **平台基线（2026-11-05 定）：不做低平台兼容层**。具体清单：Windows 只走 ConPTY（Win10 1809+ 硬线，不实现老式 console mode 回退）；Linux/macOS 只走 `openpty` + `posix_spawn`/`forkpty` 这一档正路，**`script -qfc` 包裹那条"能跑但 SIGWINCH 与退出码语义打折"的折衷路直接关掉**（本轮曾把它列为候选，现按基线否掉）；P/Invoke 优先 `LibraryImport` + `fixed` 直读，不用 `DllImport` + 中间数组拷贝（`ConPtySession.Read` 目前经 `FileStream`，W2 换直读时一并处理）；行池/回滚环这类"内存换速度"的结构不为低配机型加开关。判据一句话：**老环境是"不支持"，不是"跑得差一点"**。
- **仓库位置（2026-11-05 定）**：暂作 QuickNet 第六家，后期再迁独立仓库。耦合面已实查：除 `term/Directory.Build.props` 那句 `Import ..\Directory.Build.props` 之外零跨族引用；迁移清单五条记在计划文档 §6 第 3 条。**因此现在不放任何"预备文件"**（不预先复制 `.gitattributes`/`LICENSE`、不预先内联属性）——预放只会变成两处要同步的重复。
- **README 第六行已加（2026-11-05）**：`NuGet` 那格写"—（本族不上 NuGet，源码随仓库）"并列明两颗内核件路径；一句话里标**开发中**、W1 已有的读数（88 颗 / 104.7 MiB/s / 稳态 0 分配）和没动的部分（SSH/SFTP、渲染层）。开头那句从"五个独立库"改成"六个独立库……以及开发中的 QuickTerm"，并把"全程流式处理"限定成**前五库**——那句对终端内核不成立，别顺手盖上去。

## P1 修复（2026-11-08 代码审查轮交回的七颗，每颗带钉子或桌面判据）

这一轮是审查员（另一条会话）对整棵 `term/` 的评审交回 7 颗 P1：内核 1、PTY 1、App 5。逐颗修完，整仓 build 0 错 0 警，册 **269 颗 = 268 绿 + 1 既定红**（哨兵照旧），其中 +12 颗是本轮新钉（内核 2 + KeyDown 判定表 9 档 + 合并 1）。

- `VtParser.Utf8`：**越界码位穿透进网格，三处消费点全炸**。`F4 90 80 80` 解出 U+110000 原样入格，`TerminalView` 绘制（656/727 两处）、`TermSelection.Text` 复制、`TermCell.ToString` 三处的 `char.ConvertFromUtf32` 对它一律抛——SSH 场景等价于**远端 4 字节杀掉终端**，临时探针实证过（写入活着、一拖选复制当场崩）。修法在源头收窄：补一个 `_utf8Min`，解码完成时 `rune < min || > 0x10FFFF` 一律丢弃（超长序列 `E0 80 80`→U+0000 同轮堵上）。钉子：`Out_Of_Range_Sequence_Is_Dropped_Not_Stored`（含"复制不炸"那条路径）与 `Overlong_Encodings_Are_Dropped`；**A/B 反验过**——把守卫改成编译器折不掉的恒假式，钉子当场红，还原即绿。
- `ConPtySession.StartProcess`：**attribute list 的 HGlobal 只在 catch 里 Free，成功路径每次开会话泄漏一块**。修法：外层补 `finally`，`DeleteProcThreadAttributeList` 挪进去且加 `initialized` 旗（初始化失败时不对未初始化内存调 Delete），Free 在 Delete 之后。⚠ **顺手更正审查报告里的一条**：报告写"失败路径 catch Free 之后 finally 又 Delete → UAF"——重读结构后不成立：`Delete` 在内层 try/finally（先跑）、`Free` 在外层 catch（后跑），失败路径顺序天然正确，真正的病只有成功路径泄漏这一颗。
- `TerminalView.OnKeyDown`：**Shift/CapsLock/NumPad 变体全部打成基础字符**。KeyA–Z/D0–9/NumPad0–9 无条件按 `CharFor`（不带档的基础字符）编码并 `Handled=true`，Avalonia Win32 后端对已 Handled 的 KeyDown 丢弃紧随的 WM_CHAR（对后端源码核实过），平台给出的真字符永远到不了 `OnTextInput`。修法立了一条**判定边界** `EncodedAtKeyDown(TermKey, TermModifiers)`：非打印键与"恰好一个 Ctrl/Alt"在 KeyDown 编码（控制码 / ESC 前缀），其余 printable 一律让位给 text input。**Ctrl+Alt 刻意排除**——查证 Avalonia Win32 的 `WindowsKeyboardDevice`，AltGr 就报成 `Alt|Control` 且无独立标志，拦它等于打断德/波等布局的日常输入；代价记明：真 Ctrl+Alt+字母的 WM_CHAR < 32 被后端滤掉，这颗 chord 现在无效果（xterm 老用法，接受）。顺手一颗 rider：Shift+Tab → `BackTab`（编码器那半早就有，`MapKey` 够不着修饰键）。钉子：`KeyDownPolicyTests` 9 档 Theory。**桌面待验**：Shift 大写、Shift+数字符号、AltGr 字符——键盘是本机证不了的那格，判据还是叠层的 `keys/txt/sent`。
- `TerminalView.Drain`：**内核应答通道断路**。`DrainReplies()` 全仓只有测试册在调，App 零调用——`tput u7`、vim 光标恢复、tmux 定位的 DSR/DA 应答全在内核里攒着发不出去（4096 上限满了还静默丢）。修法：Drain 的写循环之后取一次非空就 `Send`；DEC 2026 括号只管帧不管应答，注释里写死了。**桌面待验**：`tput u7` 在真会话里应回一行 CPR。
- `TerminalView.StartSshAsync`：**SSH 连接中关标签 → 整个会话泄漏**。连接要几秒（还可能弹密码框），期间关标签走 `Stop()` 时 `_pty` 还是 null、无事可做；ConnectAsync 回来后无条件 `StartSession`——连接、读线程、4ms pump 定时器全部挂上一个已离树的 view，无人 Dispose。修法：`_generation` 代数戳（`Stop`/`Restart` 各自自增，全在 UI 线程读写），`StartSshAsync` 在**每个 await 之后**重查，过期就把拿到的 session 就地 `Dispose`。钉不了离线（要真 connect），判据在桌面：连接期关标签后进程表里不得残留 SSH 连接与线程。
- `TerminalView.Render` 的 "EV" 横幅：**调试脚手架无条件压进产品帧**。任何输入事件置位 `_inputTick`，下一帧就在第一行叠 640px 横幅，不挂任何开关；无后续 damage 时（密码提示、空闲 shell）永久留屏。修法：并入 `_showOverlay`（Ctrl+Shift+I，与 HUD 同门）——它本来就是"最后手段诊断"，跟"终端面是终端的"那条口径对齐后不再常驻。
- per-host 终端设置（P1-7）：**编辑器收集、Store 序列化、OpenSession 从不读**——用户改了某台主机的字号/回滚，毫无效果。修法三处一起：① `QuickTermStore.EffectiveTerminal(global, profile)` 成为唯一合并口（null=继承，正是 `TerminalOverrides` 可空的本意）；② `OpenSession` 改用它构造 view；③ 编辑器初值从"会继承到的值"（自身覆盖 ?? 全局）起，保存时**留在继承值上的字段写 null** 而不是把今天的全局钉死进主机——这样之后改全局仍然跟得上。钉子：`Terminal_Overrides_Stack_Over_Global_And_Null_Means_Inherit`（合并语义 + 全局层不被改）。对话框本身照旧离屏画不出，外观判据在桌面。
