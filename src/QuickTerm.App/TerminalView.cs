namespace QuickTerm.App;using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QuickTerm.Pty;
using QuickTerm.Terminal;



/// <summary>
/// The terminal surface: one control, self-drawn, hosting a <see cref="Terminal" /> engine and a pty.
/// </summary>
/// <remarks>
/// The grid is painted into a surface the control owns and every frame blits that surface, so the expensive
/// part of a frame happens only for rows the engine reported dirty: a progress line costs one row of layout
/// instead of a screenful of it, and a frame with no damage at all costs only the blit.
///
/// That rests on one documented Avalonia promise: <c>CreateDrawingContext(clear: false)</c> leaves the target's
/// content alone. <see cref="RenderBench" /> checks it against the live backend instead of trusting the words,
/// because a target that quietly cleared would show every row but the one that changed.
///
/// Row-content caching was tried against that promise and removed: baking a row into its own target cost
/// 0.52 ms against 0.28 ms for painting it here, so the cache charged more than the work it skipped. The
/// remaining cost is one <c>FormattedText</c> per colour run, which is what the gate now comes down to.
///
/// The cursor is drawn over the blit, never into the surface: it moves on every keystroke and would otherwise
/// dirty a row per hop.
///
/// Still open in this family's own gates, not silently skipped: no palette overrides or settings UI yet
/// (W2-e), and a selection stays put when the grid scrolls — its endpoints are positions, not a snapshot.
///
/// The engine is single-threaded by contract, so the pty thread only queues bytes; everything that
/// touches the grid runs here, on the UI thread.
/// </remarks>
public sealed class TerminalView : Control
{
    private const int PumpBatchBytes = 256 * 1024;

    /// <summary>The stack a session falls back to when settings do not name one.</summary>
    internal const string DefaultFontStack = "Cascadia Mono, Consolas, Microsoft JhengHei Mono, monospace";

    /// <summary>How long a DEC 2026 update may hold frames back before the grace lets one through.</summary>
    private const int SyncGraceMilliseconds = 250;

    // Selection is a fixed pair rather than an inversion of each cell's own colours: a coloured line inverted
    // reads as "not selected" when its background happens to be the highlight colour.
    private const uint SelectionBackgroundRgb = 0xB7D1EE;
    private const uint SelectionForegroundRgb = 0xF2F2F2;

    internal static readonly FontFamily GridFontFamily = new(DefaultFontStack);

    // These three come from settings, so they are set in the constructor rather than by field initialisers:
    // the scrollback ring is allocated once and never resized, and the typeface is what every metric is measured
    // against. A host that changes them has to build a new control, which is honest — a live refont would mean
    // re-deriving every cell box under a running session.
    private readonly Terminal _term;
    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private readonly Queue<byte[]> _inbox = new();
    private readonly object _inboxGate = new();
    private readonly DispatcherTimer _pump;
    private readonly SolidColorBrush _cursorBrush = new(Color.FromRgb(0x8B, 0xC9, 0x4A));
    private readonly SolidColorBrush _overlayBrush = new(Color.FromRgb(0x6A, 0x73, 0x80));

    /// <summary>The floor colour, which is the palette's — OSC 11 moves it under a running program.</summary>
    private IBrush? _floorOverride;

    private IBrush Floor => _floorOverride ?? BrushFor(_term.Palette.Background);

    /// <summary>
    /// Over a window wallpaper the floor becomes a translucent dark tint so the image shows through while text
    /// stays legible. Translucent floors cannot use incremental row repaint (re-filling a semi-transparent row
    /// over itself darkens it each frame), so <see cref="Render" /> forces a full repaint while this is set.
    /// </summary>
    internal void SetWallpaperBackground(bool on, bool darkInk)
    {
        if (on)
        {
            // Wallpaper behind a transparent floor; ink contrast is chosen from the image brightness upstream.
            _term.Palette.SetForeground(darkInk ? 0x202020u : 0xE8E8E8u);
            _floorOverride = Brushes.Transparent;
        }
        else
        {
            _term.Palette.ResetForeground();
            _floorOverride = null;
        }
        _fullRepaint = true;
        InvalidateVisual();
    }
    private readonly Dictionary<uint, IBrush> _brushes = new();
    private InkRun[] _inkRuns = new InkRun[80];

    private IPtySession? _pty;
    /// <summary>
    /// Bumped by <see cref="Stop" /> and <see cref="Restart" /> so an async start in flight can tell the view
    /// it now belongs to nobody. Without it, closing a tab while an SSH connect is still awaiting delivered a
    /// live connection to a view already off the tree — and nothing ever disposed it. All accesses are on the
    /// UI thread, where <see cref="StartSshAsync" /> resumes.
    /// </summary>
    private int _generation;
    private readonly TermSelection _selection = new();
    private bool _selecting;
    private int _paintFrom = -1;
    private int _paintTo = -1;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _ascent = 12;
    private int _scrollbackOffset;
    private RenderTargetBitmap? _surface;
    private PixelSize _surfacePixels;
    private double _writeMs;
    private double _blitMs;
    private readonly double[] _samples = new double[128];
    private readonly double[] _sorted = new double[128];
    private int _sampleCount;
    private int _sampleCursor;
    private int _keyEvents;
    private int _textEvents;
    private int _sendBytes;
    private int _sendErrors;
    private int _sendNoPty;
    private int _sendExited;
    private int _pointerEvents;
    private int _wheelEvents;
    private int _clipboardWrites;
    private int _clipboardErrors;
    private int _clipboardMisses;
    private int _pasteBytes;
    private bool _showOverlay = Program.RunningBench;
    private long _syncOpenedAt;
    private int _syncDeferred;
    private int _syncReleased;
    private int _emptyTextEvents;
    private int _encodeZero;
    private bool _focused;
    private bool _fullRepaint = true;
    private int _lastPaintedRows;
    private double _frameMs;
    private double _lastFrameMs;
    private int _frames;
    private double _fps;
    private long _fpsAnchor;

    /// <summary>
    /// One session. The settings are read here and nowhere else: scrollback depth and the typeface are what the
    /// engine and every metric are built from, so they have to be fixed before the first measure.
    /// </summary>
    public TerminalView(QuickTermSettings? settings = null)
    {
        settings ??= new QuickTermSettings();
        _term = new Terminal(80, 24, Math.Max(0, settings.ScrollbackLines));
        _fontSize = Math.Clamp(settings.FontSize, 6, 72);
        _typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? DefaultFontStack : settings.FontFamily),
            FontStyle.Normal,
            FontWeight.Normal);

        _term.TitleChanged += title => TitleChanged?.Invoke(title);

        // OSC 52 arrives while the engine is being fed on this thread, so the payload is already decoded and
        // no marshalling is needed — a program writing the clipboard is the same call as the user copying.
        _term.ClipboardRequested += payload =>
        {
            if (!string.IsNullOrEmpty(payload))
                SetClipboardText(payload);
        };

        ClipToBounds = true;
        Focusable = true;

        // A terminal has to be the thing holding focus, and a plain Control placed as a Window's whole Content
        // is not focused automatically — nothing else in the tree is focusable, but "nothing else" is not the
        // same as "it happens". Requesting on attach covers the case where the window opens without ever
        // raising the event the host was listening to.
        AttachedToVisualTree += (_, _) => Focus();
        GotFocus += (_, _) =>
        {
            _focused = true;
            ImeTarget = this;
        };
        LostFocus += (_, _) => _focused = false;

        _pump = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(4),
        };
        _pump.Tick += (_, _) => Drain();
        _pump.Start();

        SetWallpaperBackground(
            !string.IsNullOrWhiteSpace(settings.BackgroundImagePath) && System.IO.File.Exists(settings.BackgroundImagePath),
            darkInk: true);

        // No SizeChanged hook: Render calls SyncGridFromBounds itself, so a host that lays this control out
        // offscreen — the bench, a headless gate — ends up with the same grid it would on screen.
        MeasureText();
    }

    /// <summary>Raised when the program sets a window title (OSC 0 / 2).</summary>
    public event Action<string?>? TitleChanged;

    /// <summary>Cost of the last frame in milliseconds, for the offscreen bench and any future readout.</summary>
    public double LastFrameMilliseconds => _lastFrameMs;

    /// <summary>Rows the last frame re-laid-out — the number the dirty-row path is judged on.</summary>
    public int LastPaintedRows => _lastPaintedRows;

    /// <summary>
    /// Where the last frame's milliseconds went: writing rows into the surface, and the surface-to-layer blit.
    /// Printed by <c>--bench</c> and by the overlay, because "paint is 13 ms" is not actionable while
    /// "write 12.4, blit 0.4" is.
    /// </summary>
    public (double Write, double Blit) LastPhaseMilliseconds => (_writeMs, _blitMs);

    /// <summary>
    /// The input chain counted end to end: key events seen, text events seen (and how many carried an empty
    /// string), runs the encoder turned into zero bytes, bytes written to the pty, write errors swallowed, the
    /// two reasons a write would be skipped, and whether the control holds focus. Keyboard input is the one
    /// thing this machine cannot prove — a piped session cannot spawn the child at all — and "无法输入" has
    /// causes that look identical on screen: no focus (foc 0), no delivery at all (keys 0 with foc 1), no
    /// TextInput (txt 0), an encoder returning 0 (enc0 &gt; 0), or a pipe write that threw (err &gt; 0). The
    /// overlay prints all of it so one screenshot names the failing link.
    /// </summary>
    public (int Keys, int Text, int EmptyText, int EncodeZero, int Sent, int Errors, int NoPty, int Exited, bool Focused) InputChain
        => (_keyEvents, _textEvents, _emptyTextEvents, _encodeZero, _sendBytes, _sendErrors, _sendNoPty, _sendExited, _focused);

    private long _ptyBytes;
    private int _ptyReads;
    private int _ptyEof;

    /// <summary>
    /// What the pty read thread has actually delivered. <c>pk 0</c> with keys going out has two very different
    /// stories behind it — the read call returning nothing, versus returning EOF the moment the shell started —
    /// and only the thread itself can tell them apart.
    /// </summary>
    public (long Bytes, int Reads, int Eof) PtyReadCounts
        => (Interlocked.Read(ref _ptyBytes), Volatile.Read(ref _ptyReads), Volatile.Read(ref _ptyEof));

    /// <summary>The SSH session behind this view, when the tab is an SSH one; null for a local pty.</summary>
    internal SshSession? Ssh => _pty as SshSession;

    /// <summary>
    /// Feeds the grid as though the pty had delivered these bytes, without spawning anything. The offscreen
    /// bench and the headless frame gate have to drive paint cost on a machine where a child process cannot
    /// start, and a host that overlays its own input line can use it too.
    /// </summary>
    /// <remarks>
    /// It goes through the same inbox and pump as a real read, because the pump is where the decisions live —
    /// how many bytes one frame takes, and whether a frame may be painted at all. Writing straight to the grid
    /// made the bench measure a path the shipping app never runs.
    /// </remarks>
    public void Inject(ReadOnlySpan<byte> bytes)
    {
        lock (_inboxGate)
            _inbox.Enqueue(bytes.ToArray());
        Drain();
    }

    /// <summary>
    /// Whether this frame may paint. A program that brackets its redraw with DEC 2026 is saying "what is on
    /// the screen between these two sequences is not a state" — painting inside the brackets shows the
    /// half-drawn frame a TUI was about to erase.
    ///
    /// The bit alone is not the whole answer: a program that turns it on and dies, or that leaves the brackets
    /// open across a wait for input, would wedge the display. So an update that has been open longer than
    /// <see cref="SyncGraceMilliseconds" /> is painted anyway — a torn frame is a worse reading than a stale
    /// one only for the duration of that grace.
    /// </summary>
    private bool MayPaint()
    {
        if (!_term.SynchronizedOutput)
        {
            _syncOpenedAt = 0;
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        if (_syncOpenedAt == 0)
            _syncOpenedAt = now;

        return (now - _syncOpenedAt) * 1000 / Stopwatch.Frequency > SyncGraceMilliseconds;
    }

    /// <summary>DEC 2026 state and what it cost: frames held back, and frames released by the grace.</summary>
    public (bool Open, int Deferred, int Released) SyncReadings
        => (_term.SynchronizedOutput, _syncDeferred, _syncReleased);

    /// <summary>
    /// Spawns the shell sized to the control. Call after the first layout pass. A null shell uses whatever the
    /// OS points at (<c>COMSPEC</c> / <c>SHELL</c>), which is also what an empty settings file means.
    /// </summary>
    public void Start(string? shell = null, string? workingDirectory = null)
    {
        if (_pty is not null)
            return;

        SyncGridFromBounds();
        ByteTap.Open();
        try
        {
            _pty = PtySession.Start(_term.Columns, _term.Rows, shell, workingDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            // A shell that cannot start is a message on the grid, not a crash dialog: on the Unix half the
            // factory throws by design, and a first-time user needs to read why rather than lose the window.
            _pty = null;
            ReportStartFailure(ex);
            return;
        }
        StartSession(_pty);
    }

    /// <summary>
    /// Wires an already-connected session (local pty or SSH shell channel) into the exit notice and the reader
    /// thread. The session is captured rather than re-read from the field: a restart replaces the field, and a
    /// thread that looked it up each iteration would dereference null the moment the old one was retired.
    /// </summary>
    public void StartSession(IPtySession session)
    {
        _pty = session;
        session.Exited += info => Dispatcher.UIThread.Post(() =>
        {
            // A session that has already been replaced is not this window's news: its exit notice would land
            // on top of the new session's banner.
            if (!ReferenceEquals(_pty, session))
                return;

            ShowNotice(UiStrings.Format("ExitedWithCode", info.ExitCode));
        });

        var reader = new Thread(() =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = session.Read(buffer);
                    if (read < 0)
                        break;
                    ByteTap.PtyOutput(buffer.AsSpan(0, read));
                    Interlocked.Add(ref _ptyBytes, read);
                    Interlocked.Increment(ref _ptyReads);
                    lock (_inboxGate)
                        _inbox.Enqueue(buffer.AsSpan(0, read).ToArray());
                }
            }
            catch (Exception)
            {
                // A transport (the SSH channel especially) can fault mid-read. An unhandled exception on this
                // thread terminates the whole process, so a broken session must end as a quiet EOF instead.
            }

            Interlocked.Increment(ref _ptyEof);
        })
        {
            IsBackground = true,
            Name = "QuickTerm.pty.read",
        };
        reader.Start();
    }

    /// <summary>
    /// Connects an SSH host and attaches its shell channel. A missing password is asked for once; a connect or
    /// auth failure lands on the grid as a notice rather than crashing the host.
    /// </summary>
    public async System.Threading.Tasks.Task StartSshAsync(
        HostProfile profile,
        string? password,
        Func<string, string, System.Threading.Tasks.Task<bool>> trustHostKey,
        Func<System.Threading.Tasks.Task<string?>> askPassword)
    {
        if (_pty is not null)
            return;

        var generation = _generation;
        SyncGridFromBounds();
        ByteTap.Open();
        try
        {
            if (profile.AuthKind == SshAuthKind.Password && password is null)
            {
                password = await askPassword();
                if (generation != _generation)
                    return; // the tab died while the prompt was up: nobody is left to connect for
            }
            if (profile.AuthKind == SshAuthKind.Password && password is null)
                throw new InvalidOperationException(UiStrings.Get("SshNoPassword"));

            var session = await SshSession.ConnectAsync(profile, password, _term.Columns, _term.Rows, trustHostKey);
            if (generation != _generation || _pty is not null)
            {
                // The connect took seconds; a tab closed or a session restarted in that window. The channel
                // is ours alone to release — Stop() ran while _pty was still null and could not see it.
                session.Dispose();
                return;
            }

            StartSession(session);
        }
        catch (Exception ex)
        {
            ReportStartFailure(ex);
        }
    }

    /// <summary>
    /// Retires the shell and keeps the control usable for nothing else — a tab that was closed. Disposing
    /// terminates the child, which is the promise closing a tab makes.
    /// </summary>
    public void Stop()
    {
        _generation++;
        var previous = _pty;
        _pty = null;
        previous?.Dispose();
    }

    /// <summary>
    /// Retires the running shell and starts another with a different command line or directory. The grid is
    /// reset (RIS) rather than left behind: the old program's screen belongs to a session that no longer exists,
    /// and keeping it would let a user type into a window that looks like the old one.
    /// </summary>
    public void Restart(string? shell = null, string? workingDirectory = null)
    {
        _generation++;
        var previous = _pty;
        _pty = null;

        // Dispose terminates the child and closes the pseudo-console, which is what releases the read that the
        // old thread is parked in; the thread then sees EOF and finishes on its own.
        previous?.Dispose();

        _selection.Clear();
        _scrollbackOffset = 0;
        _term.Reset();
        _fullRepaint = true;
        InvalidateVisual();

        Start(shell, workingDirectory);
    }

    /// <summary>
    /// One reverse-video line on the grid: a failure the user has to see, that is not a crash. Used for a shell
    /// that will not start and for a settings file that will not parse — both are one line saying why, after
    /// which the session keeps working on defaults.
    /// </summary>
    public void ShowNotice(string text)
    {
        _term.Write(Encoding.UTF8.GetBytes($"\r\n\x1b[7m {text} \x1b[0m\r\n"));
        InvalidateVisual();
    }

    private void ReportStartFailure(Exception ex) => ShowNotice(UiStrings.Format("PtyStartFailed", ex.Message));

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    public override void Render(DrawingContext context)
    {
        var start = Stopwatch.GetTimestamp();

        // Last-resort diagnostic for "the window is up, focus is held, and no key ever reaches us": the
        // control's own input pipeline, painted straight onto the layer. It does not depend on damage, on a
        // repaint, or on the shell being alive — so if even this stays at 0, the events are not being routed
        // to this control at all, which is a different bug from anything inside it. It lives behind the same
        // switch as the HUD (Ctrl+Shift+I, on by default only for the bench): the terminal face is the
        // shell's, and an ungated banner sat on top of the first row of every real session.
        if (_showOverlay && Interlocked.Exchange(ref _inputTick, 0) != 0)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x1E)), new Rect(0, 0, 640, _cellHeight));
            context.DrawText(
                GlyphRun($"EV k{_keyEvents} t{_textEvents} p{_pointerEvents} f{(_focused ? 1 : 0)} {_lastEvent}", Brushes.White),
                new Point(2, _cellHeight - _ascent));
        }

        SyncGridFromBounds();

        var layout = ComputeLayout();
        if (layout.VisibleRows <= 0)
            return;

        // Wallpaper mode: the floor is transparent, and glyphs baked into a transparent offscreen bitmap come
        // out blocky (selection proved it — an opaque cell backdrop renders crisp). Paint straight onto the
        // window layer instead, which sits over the wallpaper and antialiases correctly; the transparent row
        // floor lets the image show through.
        if (_floorOverride is not null)
        {
            PaintDirect(context, in layout);
            if (ShowCursor(layout))
                DrawCursor(context, in layout);
            RecordFrame((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency * 1000);
            DrawOverlay(context, in layout);
            return;
        }

        if (!EnsureSurface())
            return;

        var damage = _term.Damage;

        // Three grades of work, cheapest last. A resize / screen switch / RIS latches IsFull; a scrolled-back
        // window is a different row mapping so per-row damage would be meaningless; and with no damage at all
        // the surface already holds this frame's content, which leaves only the blit.
        var fullPaint = _fullRepaint || damage.IsFull || _scrollbackOffset > 0;
        if (fullPaint)
            PaintAll(in layout);
        else if (damage.HasDamage)
            PaintDirtyRows(in layout, Math.Max(0, damage.Top), Math.Min(_term.Rows - 1, damage.Bottom));
        else
        {
            // Nothing moved: the surface already holds this frame's content, so only the blit runs.
            _lastPaintedRows = 0;
            _writeMs = 0;
        }

        damage.Consume();
        _fullRepaint = false;

        var blitStart = Stopwatch.GetTimestamp();
        context.DrawImage(_surface!, new Rect(Bounds.Size));
        _blitMs = (Stopwatch.GetTimestamp() - blitStart) / (double)Stopwatch.Frequency * 1000;
        if (ShowCursor(layout))
            DrawCursor(context, in layout);

        RecordFrame((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency * 1000);
        DrawOverlay(context, in layout);
    }

    /// <summary>
    /// Where the rows go for one frame. The mapping itself is the kernel's (<see cref="TermViewport" />)
    /// because it was written twice inside the renderer and wrong both times in ways only a desktop could
    /// see — first history was unreachable, then the clamp ran past what the layout could honour.
    /// </summary>
    private Viewport ComputeLayout()
        => TermViewport.Map(
            Math.Max(0, (int)(Bounds.Height / _cellHeight)),
            _term.TotalRows,
            _term.ScrollbackCount,
            _term.CursorRow,
            _scrollbackOffset);

    private bool ShowCursor(in Viewport layout)
        => _term.CursorVisible && layout.AtBottom
        && layout.CursorSlot >= 0 && layout.CursorSlot < layout.VisibleRows;

    /// <summary>
    /// Allocates the surface at the window's device-pixel size. A resize rebuilds it from scratch: the cell
    /// grid and the bitmap both changed shape under the old rows.
    /// </summary>
    private bool EnsureSurface()
    {
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (scaling <= 0)
            scaling = 1;

        var pixels = new PixelSize(
            (int)Math.Ceiling(Bounds.Width * scaling),
            (int)Math.Ceiling(Bounds.Height * scaling));

        if (pixels.Width < 2 || pixels.Height < 2)
            return false;

        if (_surface is not null && pixels == _surfacePixels)
            return true;

        _surface?.Dispose();
        _surface = new RenderTargetBitmap(pixels, new Vector(96 * scaling, 96 * scaling));
        _surfacePixels = pixels;
        _fullRepaint = true;
        return true;
    }

    /// <summary>Rebuilds the whole surface: every visible slot, the rest left on the background floor.</summary>
    private void PaintAll(in Viewport layout) => RepaintSurface(in layout, fromScreenRow: -1, toScreenRow: -1);

    /// <summary>
    /// Repaints only the rows the engine dirtied, leaving the rest of the surface alone. This is the whole
    /// point of owning the surface: a progress line costs one row instead of a screenful.
    /// </summary>
    private void PaintDirtyRows(in Viewport layout, int fromScreenRow, int toScreenRow)
        => RepaintSurface(in layout, fromScreenRow, toScreenRow);

    /// <summary>
    /// Paints the rows in range straight into the surface. Caching row content was measured and removed:
    /// baking a row into its own target cost 0.52 ms against 0.28 ms for painting it here, so the cache charged
    /// more than the work it skipped, and a shared atlas did not help either (12.8 ms against 11.9 ms without
    /// any cache at all). What has to get cheaper is drawing a row of text once, not drawing it twice.
    /// </summary>
    private void RepaintSurface(in Viewport layout, int fromScreenRow, int toScreenRow)
    {
        // Damage is reported in grid rows; the slot one lands in is its absolute row through the viewport.
        var full = fromScreenRow < 0;
        int first;
        int last;
        if (full)
        {
            first = 0;
            last = layout.VisibleRows - 1;
        }
        else
        {
            first = layout.SlotOfAbsolute(_term.ScrollbackCount + fromScreenRow);
            last = layout.SlotOfAbsolute(_term.ScrollbackCount + toScreenRow);
            if (first < 0 && last < 0)
                return; // the damage is entirely outside what this frame shows
            first = Math.Max(0, first);
            last = Math.Min(layout.VisibleRows - 1, last);
            if (first > last)
                return;
        }

        var rows = 0;
        var start = Stopwatch.GetTimestamp();

        // The selection is resolved once per frame rather than stored as ink: its endpoints are positions, so
        // what gets highlighted is whatever the grid holds at those positions now — the same rule that keeps a
        // selection from showing text the program already overwrote.
        var hasSelection = !_selection.IsEmpty;
        var range = hasSelection ? _selection.Resolve(_term) : default;

        using (var surface = _surface!.CreateDrawingContext(clear: full))
        {
            if (full)
                surface.FillRectangle(Floor, new Rect(Bounds.Size));

            for (var slot = first; slot <= last; slot++)
            {
                if (!TryRowCells(slot, in layout, out var cells))
                    continue;

                var absolute = layout.TopAbsolute + slot;
                _paintFrom = -1;
                _paintTo = -1;
                if (hasSelection && absolute >= range.Start.Row && absolute <= range.End.Row)
                {
                    _paintFrom = absolute == range.Start.Row ? range.Start.Column : 0;
                    _paintTo = absolute == range.End.Row ? range.End.Column : int.MaxValue;
                }

                PaintRowInto(surface, cells, 0, slot * _cellHeight, Bounds.Width);
                rows++;
            }
        }

        _paintFrom = -1;
        _paintTo = -1;

        _writeMs = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency * 1000;
        _lastPaintedRows = rows;
    }

    /// <summary>Paints every visible slot straight into the window layer (wallpaper mode), so text antialiases
    /// against the opaque window backdrop instead of a transparent offscreen bitmap (which renders blocky).</summary>
    private void PaintDirect(DrawingContext context, in Viewport layout)
    {
        var hasSelection = !_selection.IsEmpty;
        var range = hasSelection ? _selection.Resolve(_term) : default;
        for (var slot = 0; slot < layout.VisibleRows; slot++)
        {
            if (!TryRowCells(slot, in layout, out var cells))
                continue;

            var absolute = layout.TopAbsolute + slot;
            _paintFrom = -1;
            _paintTo = -1;
            if (hasSelection && absolute >= range.Start.Row && absolute <= range.End.Row)
            {
                _paintFrom = absolute == range.Start.Row ? range.Start.Column : 0;
                _paintTo = absolute == range.End.Row ? range.End.Column : int.MaxValue;
            }

            PaintRowInto(context, cells, 0, slot * _cellHeight, Bounds.Width);
        }

        _paintFrom = -1;
        _paintTo = -1;
    }

    /// <summary>Which cells belong in the window slot at <paramref name="slot"/>: history, grid, or nothing.</summary>
    private bool TryRowCells(int slot, in Viewport layout, out ReadOnlySpan<TermCell> cells)
    {
        var absolute = layout.TopAbsolute + slot;
        if (absolute >= layout.TotalRows)
        {
            // The address space is shorter than the window: the grid sits at the top and the rest stays on
            // the floor colour.
            cells = default;
            return false;
        }

        cells = absolute < _term.ScrollbackCount
            ? _term.ScrollbackRow(absolute)
            : _term.Row(absolute - _term.ScrollbackCount);
        return true;
    }

    private void PaintRowInto(DrawingContext context, ReadOnlySpan<TermCell> cells, double x, double y, double width)
    {
        // The row's floor goes down first: this is what erases the cells that scrolled or were eaten by EL,
        // since nothing else in this row's band is being touched. The band is the whole pixel row between this
        // slot's floor and the next slot's, because a fractional cell height otherwise leaves a seam.
        var top = Math.Floor(y);
        var bottom = Math.Floor(y + _cellHeight);
        context.FillRectangle(Floor, new Rect(x, top, width, bottom - top));

        // 1) backgrounds next, merged across runs that share one colour, so a full-width coloured line is a
        //    single rectangle rather than 120 of them. Runs are keyed by rgb, not by brush identity: the
        //    brushes come out of a cache, and comparing objects that are equal-but-distinct is how a run
        //    loop silently stops matching itself.
        var index = 0;
        while (index < cells.Length)
        {
            var bg = BackgroundKeyAt(cells[index], index);
            var runEnd = index + 1;
            while (runEnd < cells.Length && BackgroundKeyAt(cells[runEnd], runEnd) == bg)
                runEnd++;

            if (bg != NoBackground)
            {
                var left = Math.Floor(x + index * _cellWidth);
                var right = Math.Floor(x + runEnd * _cellWidth);
                context.FillRectangle(BrushFor(bg), new Rect(left, top, right - left, bottom - top));
            }

            index = runEnd;
        }

        // 2) glyphs next, batched per run — one colour at a time. The split lives in the kernel (InkRuns) where
        //    its index arithmetic can be pinned by a test, and it does not cut at CJK boundaries: doing that
        //    measured 29 ms against 11 ms for a screenful, because every extra run is another shaped line.
        //    One run per line stretches onto its own band instead, which is exact at the band's edges.
        var runs = EnsureRunBuffer();
        var count = InkRuns.Build(cells, PaintKey, runs);
        for (var run = 0; run < count; run++)
        {
            var ink = runs[run];
            var builder = new StringBuilder();
            for (var cell = ink.Start; cell < ink.End; cell++)
            {
                var value = cells[cell];
                if (value.Rune != 0 && (value.Flags & CellFlags.Tail) == 0)
                    builder.Append(char.ConvertFromUtf32(value.Rune));
            }

            DrawRun(context, GlyphRun(builder.ToString(), BrushFor(ink.Key)), x + ink.Start * _cellWidth, y, ink.Columns * _cellWidth);
        }
    }

    /// <summary>A row can hold at most one run per cell, so the buffer is the grid's width and is reused.</summary>
    private InkRun[] EnsureRunBuffer()
    {
        if (_inkRuns.Length < _term.Columns)
            _inkRuns = new InkRun[_term.Columns];
        return _inkRuns;
    }

    /// <summary>
    /// Draws one run so its ink fills the cells it was given. The grid promises that cell k starts at
    /// k * cellWidth, and a font keeps that promise only while its own advance equals the cell width — measured
    /// here (<c>--bench metrics</c>) at 8.203 px against the 9.000 px the grid used to reserve, and 14.000 px
    /// for a CJK fallback glyph against the 16.406 px its two cells reserve. Left alone, a 44-character prompt
    /// stopped 35 px short of its own cursor cell and every wide glyph left a 4 px gap behind it.
    ///
    /// So the run is scaled onto its band. For a run of nothing but ASCII on an exact cell width the ratio is
    /// 1 and no transform is pushed at all; the scale only ever applies to a line that carries wide glyphs.
    ///
    /// The baseline is the grid's, not the text box's bottom edge: a CJK fallback's own ascent differs from the
    /// ASCII probe's, and anchoring the box instead of the baseline makes its glyphs sit lower than the Latin
    /// on the same row.
    /// </summary>
    private void DrawRun(DrawingContext context, FormattedText text, double x, double y, double reserved)
    {
        // Integer origins where the grid allows it: a fractional cell width puts every run's start between two
        // pixels, and the rasteriser pays for subpixel text placement it cannot cache. Half a pixel of error
        // per run does not accumulate, because each run is anchored to its own cell rather than to the last
        // run's end.
        var originX = Math.Round(x);
        var originY = Math.Round(y + _ascent - text.Baseline);
        var natural = text.Width;
        if (reserved <= 0 || natural <= 0 || Math.Abs(natural - reserved) < 0.01)
        {
            context.DrawText(text, new Point(originX, originY));
            return;
        }

        var scaleX = reserved / natural;
        using (context.PushTransform(new Matrix(scaleX, 0, 0, 1, originX, 0)))
            context.DrawText(text, new Point(0, originY));
    }

    private FormattedText GlyphRun(string text, IBrush foreground)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, foreground);

    private void DrawCursor(DrawingContext context, in Viewport layout)
    {
        var column = Math.Min(_term.CursorColumn, _term.Columns - 1);
        var x = column * _cellWidth;
        var y = layout.CursorSlot * _cellHeight;
        var cell = _term.Row(_term.CursorRow)[column];
        var span = (cell.Flags & CellFlags.Wide) != 0 ? 2 * _cellWidth : _cellWidth;

        // Same pixel band the row was painted into: the block and the cell it covers have to share their edges,
        // and a fractional cell width means those edges are rounded, not exact.
        var top = Math.Floor(y);
        var bottom = Math.Floor(y + _cellHeight);
        var left = Math.Floor(x);
        var right = Math.Floor(x + span);

        if (_term.CursorStyle is TerminalCursorStyle.BlockSteady or TerminalCursorStyle.BlockBlink)
        {
            context.FillRectangle(_cursorBrush, new Rect(left, top, right - left, bottom - top));
            if (cell.Rune != 0)
                DrawRun(context, GlyphRun(char.ConvertFromUtf32(cell.Rune), Floor), x, y, span);
            return;
        }

        switch (_term.CursorStyle)
        {
            case TerminalCursorStyle.UnderlineSteady:
            case TerminalCursorStyle.UnderlineBlink:
                context.FillRectangle(_cursorBrush, new Rect(left, bottom - 2, right - left, 2));
                break;
            default:
                context.FillRectangle(_cursorBrush, new Rect(left, top, 2, bottom - top));
                break;
        }
    }

    private void DrawOverlay(DrawingContext context, in Viewport layout)
    {
        // The HUD overprints the bottom row, so it only belongs on screen when someone asked for it: a shell
        // that fills the window would otherwise read "E:\...\App▌0 r 0 | keys 132 ..." as its own prompt.
        // Ctrl+Shift+I brings it up; the offscreen bench starts with it on because its PNG is the reading.
        if (!_showOverlay || layout.VisibleRows < 2)
            return;

        // Two lines, because the single one ran off the right edge and the half that got cut was always the
        // half being asked about (`sb/off/top` and `sel/clip` live at the end).
        // `med/p95/max` are over the last 128 frames — the EMA alone said "21 ms" about a renderer whose
        // steady frames were 2 ms. `keys/txt/sent/err` is the input chain end to end, which is the only way
        // to tell "no key events reached the control" from "bytes went out and the shell ignored them".
        var first = $"{_fps:F0} fps | ms med {Percentile(50):F1} p95 {Percentile(95):F1} max {Percentile(100):F1}"
            + $" | w {_writeMs:F1} b {_blitMs:F1} r {_lastPaintedRows}"
            + $" | last {_lastEvent} | {_term.Columns}x{_term.Rows}";
        var second = $"keys {_keyEvents} txt {_textEvents}/{_emptyTextEvents} enc0 {_encodeZero} sent {_sendBytes} err {_sendErrors}"
            + $" | foc {(_focused ? 1 : 0)} ptr {_pointerEvents} wh {_wheelEvents} tick {_pumpTicks} iv {_shownInvalidates} pk {_shownPumped} rd {PtyReadCounts.Reads}/{PtyReadCounts.Bytes}B eof {PtyReadCounts.Eof}"
            + $" | sb {_term.ScrollbackCount} off {_scrollbackOffset} top {layout.TopAbsolute}"
            + $" | sel {(_selection.IsEmpty ? 0 : 1)} {_selection.Mode} clip {_clipboardWrites}/{_clipboardErrors}/{_clipboardMisses} paste {_pasteBytes}B"
            + $" | sync {(_term.SynchronizedOutput ? 1 : 0)}/{_syncDeferred}/{_syncReleased}";

        var rows = Math.Min(2, layout.VisibleRows);
        context.DrawText(GlyphRun(first, _overlayBrush), new Point(4, Bounds.Height - rows * _cellHeight - 2));
        if (rows == 2)
            context.DrawText(GlyphRun(second, _overlayBrush), new Point(4, Bounds.Height - _cellHeight - 2));
    }

    private void Drain()
    {
        // Counted before anything else: if this never increments, the dispatcher is not running the timer at
        // all, and every "nothing happens" reading downstream is explained by that rather than by the pty.
        _pumpTicks++;

        var total = 0;
        while (total < PumpBatchBytes)
        {
            byte[] chunk;
            lock (_inboxGate)
            {
                if (_inbox.Count == 0)
                    break;
                chunk = _inbox.Dequeue();
            }

            _term.Write(chunk);
            total += chunk.Length;
        }

        if (total > 0)
            _pumpedChunks++;

        // The engine's answers (DA1, DSR/CPR) queue behind the parse and are valid until the next write;
        // they are only useful on the wire. Nothing ever drained them, so tput u7 and vim's cursor restore
        // hung on their timeouts. DEC 2026 brackets hold back frames, not answers.
        var reply = _term.DrainReplies();
        if (!reply.IsEmpty)
            Send(reply.Span);

        // Damage is consumed by Render, not here: Avalonia may coalesce several ticks into one frame, and a
        // span read at pump time would be missing whatever the next tick dirtied.
        if (total > 0 && (_term.Damage.HasDamage || total >= PumpBatchBytes))
        {
            if (MayPaint())
            {
                // Held back by an open DEC 2026 bracket and then released by the grace is its own reading: it
                // says a program left the brackets open, which is a bug shape worth seeing.
                if (_syncOpenedAt != 0)
                    _syncReleased++;
                _invalidates++;
            }
            else
            {
                _syncDeferred++;
            }
        }

        // Requesting a frame is not enough on its own. InvalidateVisual marks the visual dirty; it does not
        // pump the dispatcher, and nothing else pumps it when the window is otherwise idle — so the tick that
        // drained these bytes would be the last one, and the bytes behind it would sit in the queue forever.
        // Running jobs here is what keeps the pump feeding itself; the timer supplies the cadence.
        Dispatcher.UIThread.RunJobs();

        if (_invalidates > 0)
            InvalidateVisual();
    }

    private void RecordFrame(double elapsed)
    {
        _lastFrameMs = elapsed;
        _frameMs = _frameMs == 0 ? elapsed : _frameMs * 0.8 + elapsed * 0.2;
        CountFrame();

        // The ring is the whole point: an EMA over a startup burst says "21 ms" about a renderer whose
        // steady-state frames are 2 ms, and the gate is written in P95. Keep the last frames, report the
        // distribution.
        _samples[_sampleCursor] = elapsed;
        _sampleCursor = (_sampleCursor + 1) % _samples.Length;
        if (_sampleCount < _samples.Length)
            _sampleCount++;

        var now = Stopwatch.GetTimestamp();
        if (_fpsAnchor == 0)
            _fpsAnchor = now;
        var seconds = (now - _fpsAnchor) / (double)Stopwatch.Frequency;
        if (seconds >= 1)
        {
            _fps = _frames / seconds;
            _frames = 0;
            _fpsAnchor = now;
        }
    }

    /// <summary>Percentile over the last frames kept in the ring, in milliseconds. Sorts a 128-slot copy.</summary>
    private double Percentile(int hundredths)
    {
        if (_sampleCount == 0)
            return 0;

        for (var i = 0; i < _sampleCount; i++)
            _sorted[i] = _samples[i];

        Array.Sort(_sorted, 0, _sampleCount);
        var index = (int)((long)_sampleCount * hundredths / 100);
        return _sorted[Math.Min(index, _sampleCount - 1)];
    }

    /// <summary>Background key meaning "this cell has no background of its own; the row's floor shows through".</summary>
    private const uint NoBackground = uint.MaxValue;

    /// <summary>Background as painted, which is the cell's own unless this column is inside the selection.</summary>
    private uint BackgroundKeyAt(TermCell cell, int column)
        => column >= _paintFrom && column < _paintTo ? SelectionBackgroundRgb : BackgroundKey(cell);

    /// <summary>Ink as painted. It deliberately ignores the selection: recolouring selected cells would split the
    /// glyph runs at the selection edges, and each re-split run is rescaled to its own cell span — the text would
    /// visibly shift as the selection grows. Selection shows as a background highlight only.</summary>
    private uint PaintKey(TermCell cell, int column) => ForegroundKey(cell);

    private uint BackgroundKey(TermCell cell)
    {
        var bg = cell.Bg;
        if ((cell.Effects & TermEffects.Inverse) != 0)
            bg = cell.Fg; // inverse swaps the two roles, and the glyph side reads the same pair the other way

        return bg.IsDefault ? NoBackground : ColorKey(bg);
    }

    private uint ForegroundKey(TermCell cell)
    {
        var fg = cell.Fg;
        var bg = cell.Bg;
        if ((cell.Effects & TermEffects.Inverse) != 0)
            (fg, bg) = (bg, fg);

        // Hidden text paints the background colour as its glyph: the cell keeps its shape, the ink disappears.
        if ((cell.Effects & TermEffects.Hidden) != 0)
            return fg.IsDefault ? _term.Palette.Background : ColorKey(fg);

        if (!fg.IsDefault)
            return ColorKey(fg);

        // Bold is a palette lift, not a heavier font: index 0-7 step up to 8-15, the xterm convention.
        if ((cell.Effects & TermEffects.Bold) != 0 && cell.Fg.Kind == 1 && cell.Fg.Index < 8)
            return _term.Palette.Rgb(cell.Fg.Index + 8);

        return _term.Palette.Foreground;
    }

    /// <summary>
    /// A cell's colour resolved to rgb. Slot lookups go through the engine's palette rather than a table of
    /// our own, because OSC 4 rewrites those slots while a program runs — a baked table would make OSC 4 a
    /// no-op and leave `dircolors`, vim and `ls --color` themes unread.
    /// </summary>
    private uint ColorKey(TerminalColor color) => color.Kind switch
    {
        1 => _term.Palette.Rgb(color.Index),
        2 => (uint)color.RgbValue & 0xFFFFFF,
        _ => throw new InvalidOperationException("ColorKey is only reached for non-default colours"),
    };

    /// <summary>
    /// Brushes are made once per colour and kept: a row of 120 cells would otherwise hand the garbage collector
    /// 120 fresh solids every repaint, and the run loops above compare rgb rather than brush identity.
    /// </summary>
    private IBrush BrushFor(uint rgb)
    {
        if (!_brushes.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _brushes[rgb] = brush;
        }

        return brush;
    }

    private double FontSize => _fontSize;

    private void MeasureText()
    {
        var probe = new FormattedText(
            "0",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            Brushes.White);

        // The cell is the font's own advance, not that advance rounded up. Rounding is what made every run
        // stop short of the cell after it: 0.797 px of slack per character, 35 px across a prompt line, and
        // the gap the user reads as "the display is incomplete". Rounding happens where ink meets pixels (see
        // PaintRowInto), never where cells meet each other.
        _cellWidth = Math.Max(1, probe.Width);
        _cellHeight = Math.Max(1, probe.Height);
        _ascent = probe.Baseline;
    }

    private void SyncGridFromBounds()
    {
        var columns = Math.Max(1, (int)(Bounds.Width / _cellWidth));
        var rows = Math.Max(1, (int)(Bounds.Height / _cellHeight));
        if (columns == _term.Columns && rows == _term.Rows)
            return;

        _term.Resize(columns, rows);
        _pty?.Resize(columns, rows);
        _scrollbackOffset = 0;
        _fullRepaint = true;
    }

    /// <summary>
    /// Avalonia hands text input to the focused element through the client that element supplies, and a stock
    /// Control supplies none — which is how a terminal can hold focus, show a live prompt, and still type
    /// nothing. The window answers the platform's request with this single instance; it holds no per-session
    /// state of its own and resolves the live focused view (<see cref="ImeTarget" />) for the one thing an IME
    /// still needs even without preedit: where the caret is, so its candidate window lands on the cursor
    /// instead of a window corner.
    /// </summary>
    internal static readonly TextInputMethodClient SharedTextInputClient = new TerminalTextInputClient();

    /// <summary>The view whose caret the shared IME client should report; set when a terminal takes focus.</summary>
    internal static TerminalView? ImeTarget;

    /// <summary>The caret cell in this view's own coordinates, for the IME candidate-window placement.</summary>
    internal Rect ImeCursorRectangle
    {
        get
        {
            var layout = ComputeLayout();
            var column = Math.Min(_term.CursorColumn, _term.Columns - 1);
            var slot = Math.Clamp(layout.CursorSlot, 0, Math.Max(0, layout.VisibleRows - 1));
            return new Rect(column * _cellWidth, slot * _cellHeight, _cellWidth, _cellHeight);
        }
    }

    /// <summary>
    /// The last input event the control was handed at all, whether or not it was handled. Five rounds of
    /// readings have said "the window is up, focus is held, and no key ever reaches us" — so the next thing
    /// to establish is whether the control sees any input event whatsoever, including the mouse.
    /// </summary>
    private string _lastEvent = "none";
    private int _inputTick;
    private int _invalidates;
    private int _pumpedChunks;
    private int _shownInvalidates;
    private int _shownPumped;
    private int _pumpTicks;

    /// <summary>Any input event, including the ones we drop, gets a beat so the next frame shows the counter.</summary>
    private void NoteInput(string description)
    {
        _lastEvent = description;
        Volatile.Write(ref _inputTick, 1);
        InvalidateVisual();
    }

    /// <summary>
    /// A frame that paints nothing is still a frame, and that is the difference between "the renderer is
    /// asleep" and "the renderer ran and found nothing to draw" — identical in a screenshot of a terminal
    /// that stopped echoing. The per-frame counters are carried over before they reset: zeroing them and
    /// then printing them in the same frame reports 0 forever, which is exactly how this instrumentation
    /// managed to say "no bytes pumped" while the read thread had delivered 225.
    /// </summary>
    private void CountFrame()
    {
        _frames++;
        _shownInvalidates = _invalidates;
        _shownPumped = _pumpedChunks;
        _invalidates = 0;
        _pumpedChunks = 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _keyEvents++;
        NoteInput($"key {e.Key}/{e.KeyModifiers}");
        // Ctrl+Shift+C/V are the app's, not the shell's: on a keyboard layout that already spells Ctrl+C as
        // SIGINT, letting the chord through would interrupt the program the user meant to copy from.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            if (e.Key == Key.C)
            {
                CopySelection();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V)
            {
                PasteFromClipboard();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.I)
            {
                _showOverlay = !_showOverlay;
                InvalidateVisual(); // the HUD lives on the live layer, so the next frame is what clears it
                e.Handled = true;
                return;
            }
        }

        // Ctrl+V (no Shift) also pastes — the convention most terminal users expect, even though some shells
        // reserve Ctrl+V for quoted-insert.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && (e.KeyModifiers & KeyModifiers.Shift) == 0 && e.Key == Key.V)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        var modifiers = MapModifiers(e.KeyModifiers);
        var key = MapKey(e.Key);
        if (key == TermKey.Tab && (modifiers & TermModifiers.Shift) != 0)
            key = TermKey.BackTab; // the one printable key whose shifted form is its own sequence

        // KeyDown only knows the unshifted base character: encoding A–Z / 0–9 here and marking the event
        // handled makes the Win32 backend drop the WM_CHAR that carried the real one, so Shift+A typed "a"
        // and Shift+1 typed "1" forever. Non-printable keys and the chords we can spell ourselves (Ctrl
        // alone, Alt alone) are encoded here; every other printable shape goes to OnTextInput, which
        // receives the character the platform actually produced.
        if (!EncodedAtKeyDown(key, modifiers))
            return;

        var buffer = new byte[64];
        var input = new TermInput
        {
            Key = key,
            Char = e.Key == Key.Space ? ' ' : CharFor(e.Key),
            Modifiers = modifiers,
        };

        var written = TerminalInputEncoder.EncodeKey(input, _term, buffer);
        if (written > 0)
        {
            Send(buffer.AsSpan(0, written));
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Whether a key press is encoded right here in <see cref="OnKeyDown" /> rather than left to the
    /// platform's text input. Printable keys are the platform's to spell; the deliberate exclusion is
    /// Ctrl+Alt, which is exactly how the Win32 backend reports AltGr — intercepting it turned AltGr+Q on
    /// a German layout into a control code. The cost, stated plainly: a genuine Ctrl+Alt+letter produces
    /// a WM_CHAR below 32, which the backend never raises as text input, so that chord does nothing.
    /// </summary>
    internal static bool EncodedAtKeyDown(TermKey key, TermModifiers modifiers)
    {
        if (key != TermKey.None)
            return true; // arrows, function keys, Enter/Tab/Escape: the encoder spells these

        var ctrl = (modifiers & TermModifiers.Ctrl) != 0;
        var alt = (modifiers & TermModifiers.Alt) != 0;
        return ctrl != alt; // exactly one of the two — control codes, or the ESC prefix
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        _textEvents++;
        NoteInput($"text \"{e.Text}\"");

        // Typed into the grid so a screenshot can tell "no event at all" from "event with an empty string":
        // this branch used to return silently, which is exactly how "opens fine, types nothing" stayed dark
        // for two rounds of readings.
        if (string.IsNullOrEmpty(e.Text))
        {
            _emptyTextEvents++;
            InvalidateVisual();
            return;
        }

        var buffer = new byte[e.Text.Length * 4 + 16];
        var written = TerminalInputEncoder.EncodeKey(
            new TermInput { Text = e.Text },
            _term,
            buffer);

        if (written > 0)
            Send(buffer.AsSpan(0, written));
        else
            _encodeZero++;

        e.Handled = true;
        base.OnTextInput(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pointerEvents++;
        var point = e.GetCurrentPoint(this);
        NoteInput($"ptr {point.Position.X:F0},{point.Position.Y:F0} c{e.ClickCount}");
        Focus();

        // Right-click copies the selection when there is one, otherwise pastes — the terminal convention.
        if (point.Properties.IsRightButtonPressed)
        {
            if (HasSelection)
                CopySelection();
            else
                PasteFromClipboard();
            e.Handled = true;
            return;
        }

        // A program that asked for mouse events gets them, unless Shift is held — the one chord every terminal
        // reads as "no, I want to select". Otherwise the press starts a selection: double click picks a word,
        // triple a line, and a drag extends it.
        if (ReportsMouse(e.KeyModifiers))
        {
            SendMouse(point, TermMouseAction.Press);
        }
        else
        {
            BeginSelection(PositionAt(point.Position), e.ClickCount switch
            {
                >= 3 => TermSelectMode.Line,
                2 => TermSelectMode.Word,
                _ => TermSelectMode.Cell,
            });
            _selecting = true;
            e.Pointer.Capture(this);
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_selecting)
        {
            ExtendSelection(PositionAt(point.Position));
        }
        else if (ReportsMouse(e.KeyModifiers))
        {
            var drag = point.Properties.IsLeftButtonPressed;
            if (drag || _term.MouseAnyMove)
                SendMouse(point, drag ? TermMouseAction.Drag : TermMouseAction.Motion);
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
        else if (ReportsMouse(e.KeyModifiers))
        {
            SendMouse(e.GetCurrentPoint(this), TermMouseAction.Release);
        }

        base.OnPointerReleased(e);
    }

    /// <summary>True when the running program wants the mouse, and the user has not overridden it with Shift.</summary>
    private bool ReportsMouse(KeyModifiers modifiers)
        => _term.MouseTracking && (modifiers & KeyModifiers.Shift) == 0;

    private void SendMouse(PointerPoint point, TermMouseAction action)
    {
        var buffer = new byte[32];
        var written = TerminalInputEncoder.EncodeMouse(
            new TermMouseEvent
            {
                Button = ButtonOf(point.Properties),
                Action = action,
                Column = (int)(point.Position.X / _cellWidth),
                Row = GridRowAt(point.Position.Y),
            },
            _term,
            buffer);

        if (written > 0)
            Send(buffer.AsSpan(0, written));
    }

    /// <summary>
    /// Grid row a pixel y belongs to. Mouse reporting counts the grid's rows, not the window's slots, so a
    /// report made while the view is pulled up over history has to leave both the viewport and the history out.
    /// </summary>
    private int GridRowAt(double y)
    {
        var layout = ComputeLayout();
        var slot = Math.Clamp((int)(y / _cellHeight), 0, Math.Max(0, layout.VisibleRows - 1));
        return layout.TopAbsolute + slot - _term.ScrollbackCount;
    }

    /// <summary>Start a selection at an absolute row — history rows first, then the grid — and a column.</summary>
    public void BeginSelection(TermPosition position, TermSelectMode mode = TermSelectMode.Cell)
    {
        _selection.Start(position, mode);
        _fullRepaint = true;
        InvalidateVisual();
    }

    /// <summary>Move the far end. The anchor stays where the drag began, as xterm does.</summary>
    public void ExtendSelection(TermPosition position)
    {
        if (position == _selection.Focus)
            return;

        _selection.Extend(position);
        _fullRepaint = true;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selection.IsEmpty)
            return;

        _selection.Clear();
        _fullRepaint = true;
        InvalidateVisual();
    }

    /// <summary>True when a drag has covered at least one cell.</summary>
    public bool HasSelection => !_selection.IsEmpty;

    /// <summary>Grid width, for a host's own chrome (a tab title that says 80×24, a bench reading).</summary>
    public int GridColumns => _term.Columns;

    /// <summary>Grid height, same purpose.</summary>
    public int GridRows => _term.Rows;

    /// <summary>Where the engine says the cursor is, and where the frame will therefore draw the block.</summary>
    public (int Row, int Column) CursorPosition => (_term.CursorRow, _term.CursorColumn);

    /// <summary>Cell width in logical pixels — the number every column-to-x conversion multiplies by.</summary>
    public double CellWidth => _cellWidth;

    /// <summary>The selected text: wraps rejoined, trailing blanks trimmed, wide tails contributing nothing.</summary>
    public string SelectedText() => _selection.IsEmpty ? string.Empty : _selection.Text(_term);

    /// <summary>The cell under a pixel position, in the same absolute row space the selection lives in.</summary>
    private TermPosition PositionAt(Point position)
    {
        var layout = ComputeLayout();
        var slot = Math.Clamp((int)(position.Y / _cellHeight), 0, Math.Max(0, layout.VisibleRows - 1));
        var row = Math.Clamp(layout.TopAbsolute + slot, 0, Math.Max(0, _term.TotalRows - 1));
        var column = Math.Clamp((int)(position.X / _cellWidth), 0, Math.Max(0, _term.Columns - 1));
        return new TermPosition(row, column);
    }

    /// <summary>
    /// Pull the view up over history (the wheel, a host's own scrollbar, later Shift+PageUp). Positive rows
    /// go back in time; zero lands on the live grid.
    /// </summary>
    public void ScrollBy(int rows)
    {
        var max = TermViewport.MaxOffset(Math.Max(0, (int)(Bounds.Height / _cellHeight)), _term.TotalRows);
        var next = Math.Clamp(_scrollbackOffset + rows, 0, max);
        if (next == _scrollbackOffset)
            return;

        _scrollbackOffset = next;
        InvalidateVisual(); // every row's mapping moved, so the whole surface is repainted
    }

    /// <summary>How far the view can currently be pulled up — what <see cref="ScrollBy" /> will honour.</summary>
    public int ScrollMaximum
        => TermViewport.MaxOffset(Math.Max(0, (int)(Bounds.Height / _cellHeight)), _term.TotalRows);

    /// <summary>Rows the view is currently pulled up.</summary>
    public int ScrollOffset => _scrollbackOffset;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _wheelEvents++;
        var buffer = new byte[32];
        var wheel = e.Delta.Y > 0 ? TermMouseButton.WheelUp : TermMouseButton.WheelDown;
        var written = TerminalInputEncoder.EncodeWheelAsKeys(wheel, MapModifiers(e.KeyModifiers), _term, buffer);

        if (written > 0)
        {
            Send(buffer.AsSpan(0, written));
        }
        else if (!_term.InAltScreen && !_term.MouseTracking)
        {
            // Three rows per notch, the same figure every terminal uses. The sign is not negated: Avalonia
            // reports a forward rotation as a positive delta, and a positive offset is older content — the
            // minus that used to sit here made the wheel run the wrong way. Rounded rather than truncated so
            // a precision touchpad's fractional delta still moves something.
            ScrollBy((int)Math.Round(e.Delta.Y * 3));
        }

        base.OnPointerWheelChanged(e);
    }

    private void Send(ReadOnlySpan<byte> bytes)
    {
        if (_pty is null)
        {
            _sendNoPty++;
            return;
        }

        if (_pty.HasExited)
        {
            _sendExited++;
            return;
        }

        try
        {
            _pty.Write(bytes);
            ByteTap.PtyInput(bytes);
            _sendBytes += bytes.Length;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The shell is gone mid-write; the exit event owns the message the user sees. Counted, because
            // "typed and nothing happened" has three very different causes and this line is the only cheap
            // way to tell them apart from a screenshot.
            _sendErrors++;
        }
    }

    /// <summary>
    /// The clipboard lives on the window, not on the control: a host that overlays this view (the bench, a
    /// future tab strip) reaches the same one, and a detached visual simply has none — which is why every
    /// entry point here tolerates null instead of throwing at the first Ctrl+Shift+C of a headless run.
    /// </summary>
    private Avalonia.Input.Platform.IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    private void CopySelection()
    {
        var text = SelectedText();
        if (text.Length == 0)
            return;

        SetClipboardText(text);
    }

    private async void SetClipboardText(string text)
    {
        var clipboard = Clipboard;
        if (clipboard is null)
        {
            _clipboardMisses++;
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            _clipboardWrites++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A clipboard that refuses is a counter, not a crash: on Windows it can be held by another
            // process at the exact moment the user copies.
            _clipboardErrors++;
        }
    }

    private async void PasteFromClipboard()
    {
        var clipboard = Clipboard;
        if (clipboard is null)
        {
            _clipboardMisses++;
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[bytes.Length + 16];
        var written = TerminalInputEncoder.EncodePaste(bytes, _term, buffer);
        if (written > 0)
        {
            Send(buffer.AsSpan(0, written));
            _pasteBytes += bytes.Length;
        }
    }

    private static TermModifiers MapModifiers(KeyModifiers modifiers)
    {
        var result = TermModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0)
            result |= TermModifiers.Shift;
        if ((modifiers & KeyModifiers.Alt) != 0)
            result |= TermModifiers.Alt;
        if ((modifiers & KeyModifiers.Control) != 0)
            result |= TermModifiers.Ctrl;
        return result;
    }

    private static TermMouseButton ButtonOf(PointerPointProperties properties) => properties switch
    {
        _ when properties.IsLeftButtonPressed => TermMouseButton.Left,
        _ when properties.IsMiddleButtonPressed => TermMouseButton.Middle,
        _ when properties.IsRightButtonPressed => TermMouseButton.Right,
        _ => TermMouseButton.NoButton,
    };

    private static TermKey MapKey(Key key) => key switch
    {
        Key.Enter => TermKey.Enter,
        Key.Tab => TermKey.Tab,
        Key.Back => TermKey.Backspace,
        Key.Escape => TermKey.Escape,
        Key.Up => TermKey.Up,
        Key.Down => TermKey.Down,
        Key.Left => TermKey.Left,
        Key.Right => TermKey.Right,
        Key.Home => TermKey.Home,
        Key.End => TermKey.End,
        Key.PageUp => TermKey.PageUp,
        Key.PageDown => TermKey.PageDown,
        Key.Insert => TermKey.Insert,
        Key.Delete => TermKey.Delete,
        Key.F1 => TermKey.F1,
        Key.F2 => TermKey.F2,
        Key.F3 => TermKey.F3,
        Key.F4 => TermKey.F4,
        Key.F5 => TermKey.F5,
        Key.F6 => TermKey.F6,
        Key.F7 => TermKey.F7,
        Key.F8 => TermKey.F8,
        Key.F9 => TermKey.F9,
        Key.F10 => TermKey.F10,
        Key.F11 => TermKey.F11,
        Key.F12 => TermKey.F12,
        _ => TermKey.None,
    };

    private static char CharFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
        _ => '\0',
    };
}
