namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tmds.Ssh;

/// <summary>
/// Termora-shaped file transfer: two side-by-side column tables (local left, remote right), each with a path
/// bar + up/refresh and the columns name/type/size/mtime/permissions, over a shared transfer-queue table at the
/// bottom. Double-click a directory to enter it; double-click a file to push it across (remote file downloads
/// into the local dir, local file uploads into the remote dir). Reuses the session's authenticated connection.
/// First cut: no drag-drop, no resume, no recursive directory transfer.
/// </summary>
internal sealed class SftpWindow : Window
{
    private const string FileCols = "*,90,90,150,100";
    private const string QueueCols = "*,80,70,90,*,*";

    private readonly SshSession _session;
    private readonly ListBox _localList = new();
    private readonly ListBox _remoteList = new();
    private readonly TextBox _localPathBox = new();
    private readonly TextBox _remotePathBox = new();
    private readonly ListBox _queue = new();
    private readonly List<QueueRow> _queueRows = new();
    private SftpClient? _sftp;
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _remotePath = "/";

    public SftpWindow(SshSession session)
    {
        _session = session;
        Title = UiStrings.Get("SftpTitle");
        Width = 1100;
        Height = 700;

        _localPathBox.Text = _localPath;
        _localList.ItemTemplate = new FuncDataTemplate<FileRow>((r, _) => FileRowControl(r));
        _remoteList.ItemTemplate = new FuncDataTemplate<FileRow>((r, _) => FileRowControl(r));
        _queue.ItemTemplate = new FuncDataTemplate<QueueRow>((r, _) => QueueRowControl(r));

        var localPane = BuildPane(UiStrings.Get("Local"), _localPathBox, _localList,
            () => _ = LocalUpAsync(), () => _ = LoadLocalAsync(), () => _ = LocalOpenAsync(), false);
        var remotePane = BuildPane(session.Profile.Host, _remotePathBox, _remoteList,
            () => _ = RemoteUpAsync(), () => _ = LoadRemoteAsync(), () => _ = RemoteOpenAsync(), true);

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        var divider = new Border { Background = new SolidColorBrush(Color.FromRgb(0xD6, 0xD8, 0xDA)) };
        Grid.SetColumn(localPane, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(remotePane, 2);
        panes.Children.Add(localPane);
        panes.Children.Add(divider);
        panes.Children.Add(remotePane);

        var queueArea = new DockPanel { LastChildFill = true, Height = 170 };
        var queueHeader = TableHeader(QueueCols,
            UiStrings.Get("QueueName"), UiStrings.Get("QueueStatus"), UiStrings.Get("QueueProgress"),
            UiStrings.Get("QueueSize"), UiStrings.Get("QueueSource"), UiStrings.Get("QueueDest"));
        DockPanel.SetDock(queueHeader, Dock.Top);
        queueArea.Children.Add(queueHeader);
        queueArea.Children.Add(new ScrollViewer { Content = _queue });

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(queueArea, Dock.Bottom);
        root.Children.Add(queueArea);
        root.Children.Add(panes);
        Content = root;

        Opened += async (_, _) => await InitAsync();
        Closed += (_, _) =>
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
            foreach (var cts in _uploadDebounce.Values)
                cts.Cancel();
        };
    }

    private sealed record FileRow(string Name, bool IsDir, string Type, string Size, string Mtime, string Perm, string Path);

    private sealed record QueueRow(string Name, string Status, string Progress, string Size, string Source, string Dest);

    private Control BuildPane(string title, TextBox pathBox, ListBox list, Action onUp, Action onRefresh, Action onOpen, bool remote)
    {
        var header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(8, 6, 8, 2) };

        var up = new Button { Content = TermIcons.Icon(TermIcons.ChevronRight, 14), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 2) };
        up.Click += (_, _) => onUp();
        var refresh = new Button { Content = UiStrings.Get("Refresh"), Padding = new Thickness(8, 2) };
        refresh.Click += (_, _) => onRefresh();
        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                onRefresh();
        };
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 6, 4) };
        DockPanel.SetDock(up, Dock.Left);
        DockPanel.SetDock(refresh, Dock.Right);
        toolbar.Children.Add(up);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(pathBox);

        list.DoubleTapped += (_, _) => onOpen();
        list.ContextMenu = BuildContextMenu(remote, list, onRefresh);

        var table = new DockPanel { LastChildFill = true };
        var cols = TableHeader(FileCols,
            UiStrings.Get("ColName"), UiStrings.Get("ColType"), UiStrings.Get("ColSize"),
            UiStrings.Get("ColMtime"), UiStrings.Get("ColPerm"));
        DockPanel.SetDock(cols, Dock.Top);
        table.Children.Add(cols);
        table.Children.Add(new ScrollViewer { Content = list });

        var pane = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        pane.Children.Add(header);
        pane.Children.Add(toolbar);
        pane.Children.Add(table);

        if (remote)
        {
            // Drop local files anywhere on the remote pane to upload them into the current remote directory.
            DragDrop.SetAllowDrop(pane, true);
            DragDrop.AddDragOverHandler(pane, OnRemoteDragOver);
            DragDrop.AddDropHandler(pane, OnRemoteDrop);
        }
        return pane;
    }

    private static Grid TableHeader(string cols, params string[] labels)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF0)) };
        for (var i = 0; i < labels.Length; i++)
        {
            var cell = new TextBlock { Text = labels[i], FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Control FileRowControl(FileRow r)
    {
        // Avalonia calls the template with null while recycling/clearing containers on ItemsSource replace.
        if (r is null)
            return new Panel();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(FileCols) };
        var name = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 3) };
        var icon = r.IsDir ? TermIcons.FolderIcon(14) : TermIcons.Icon(TermIcons.File, 14);
        DockPanel.SetDock(icon, Dock.Left);
        name.Children.Add(icon);
        name.Children.Add(new TextBlock { Text = r.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) });
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);
        AddCell(grid, 1, r.Type);
        AddCell(grid, 2, r.Size);
        AddCell(grid, 3, r.Mtime);
        AddCell(grid, 4, r.Perm);
        return grid;
    }

    private static Control QueueRowControl(QueueRow r)
    {
        if (r is null)
            return new Panel();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(QueueCols) };
        AddCell(grid, 0, r.Name);
        AddCell(grid, 1, r.Status);
        AddCell(grid, 2, r.Progress);
        AddCell(grid, 3, r.Size);
        AddCell(grid, 4, r.Source);
        AddCell(grid, 5, r.Dest);
        return grid;
    }

    private static void AddCell(Grid grid, int col, string text)
    {
        var cell = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(cell, col);
        grid.Children.Add(cell);
    }

    private static string Human(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:F2} KB",
        < 1024 * 1024 * 1024 => $"{n / (1024.0 * 1024):F2} MB",
        _ => $"{n / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static string Perm(UnixFilePermissions p)
    {
        Span<char> c = stackalloc char[9];
        c[0] = p.HasFlag(UnixFilePermissions.UserRead) ? 'r' : '-';
        c[1] = p.HasFlag(UnixFilePermissions.UserWrite) ? 'w' : '-';
        c[2] = p.HasFlag(UnixFilePermissions.UserExecute) ? 'x' : '-';
        c[3] = p.HasFlag(UnixFilePermissions.GroupRead) ? 'r' : '-';
        c[4] = p.HasFlag(UnixFilePermissions.GroupWrite) ? 'w' : '-';
        c[5] = p.HasFlag(UnixFilePermissions.GroupExecute) ? 'x' : '-';
        c[6] = p.HasFlag(UnixFilePermissions.OtherRead) ? 'r' : '-';
        c[7] = p.HasFlag(UnixFilePermissions.OtherWrite) ? 'w' : '-';
        c[8] = p.HasFlag(UnixFilePermissions.OtherExecute) ? 'x' : '-';
        return new string(c);
    }

    private static string TypeOf(string name, bool isDir) => isDir
        ? UiStrings.Get("FolderType")
        : (Path.GetExtension(name).TrimStart('.') is { Length: > 0 } ext ? ext : UiStrings.Get("FileType"));

    private static string Combine(string dir, string name) => dir.TrimEnd('/') + "/" + name;

    private async Task InitAsync()
    {
        try
        {
            _sftp = await Task.Run(() => _session.OpenSftpAsync());
            _remotePath = _sftp!.WorkingDirectory.Path;
            _remotePathBox.Text = _remotePath;
            await Task.WhenAll(LoadLocalAsync(), LoadRemoteAsync());
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow("-", UiStrings.Format("TransferFailed", ex.Message), "", "", "", ""));
        }
    }

    private async Task LoadLocalAsync()
    {
        _localPath = _localPathBox.Text is { Length: > 0 } t ? t : _localPath;
        try
        {
            var rows = await Task.Run(() =>
            {
                var info = new DirectoryInfo(_localPath);
                var list = new List<FileRow>();
                foreach (var d in info.EnumerateDirectories())
                    list.Add(new FileRow(d.Name, true, TypeOf(d.Name, true), "0 B", d.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), "---------", d.FullName));
                foreach (var f in info.EnumerateFiles())
                    list.Add(new FileRow(f.Name, false, TypeOf(f.Name, false), Human(f.Length), f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), "---------", f.FullName));
                return list;
            });
            _localList.ItemsSource = rows.OrderBy(r => !r.IsDir).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _localPathBox.Text = _localPath;
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(_localPath, UiStrings.Format("TransferFailed", ex.Message), "", "", "", ""));
        }
    }

    private async Task LoadRemoteAsync()
    {
        if (_sftp is null)
            return;
        var path = _remotePathBox.Text is { Length: > 0 } t ? t : _remotePath;
        _remotePath = path;
        Trace($"loadRemote start {path}");
        try
        {
            var work = Task.Run(async () =>
            {
                var list = new List<FileRow>();
                var options = new Tmds.Ssh.EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    FollowDirectoryLinks = false,
                    FollowFileLinks = false,
                };
                await foreach (var row in _sftp.GetDirectoryEntriesAsync(
                    path,
                    (ref SftpFileEntry e) => new FileRow(
                        e.FileName.ToString(),
                        e.FileType == UnixFileType.Directory,
                        TypeOf(e.FileName.ToString(), e.FileType == UnixFileType.Directory),
                        Human(e.Length),
                        e.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        Perm(e.Permissions),
                        Combine(path, e.FileName.ToString())),
                    options))
                {
                    if (row.Name is "." or "..")
                        continue;
                    list.Add(row);
                }
                return list;
            });
            var finished = await Task.WhenAny(work, Task.Delay(15000));
            if (finished != work)
            {
                Trace($"loadRemote TIMEOUT {path}");
                PushQueue(new QueueRow(path, "timeout", "", "", "", ""));
                return;
            }
            var rows = await work;
            Trace($"loadRemote end {path} count={rows.Count}");
            _remoteList.ItemsSource = rows.OrderBy(r => !r.IsDir).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _remotePathBox.Text = path;
        }
        catch (Exception ex)
        {
            Trace($"loadRemote ERROR {path} {ex}");
            PushQueue(new QueueRow(path, UiStrings.Format("TransferFailed", ex.Message), "", "", "", ""));
        }
    }

    private static void Trace(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickTerm-sftp.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private async Task LocalUpAsync()
    {
        var parent = Directory.GetParent(_localPath.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is not null)
        {
            _localPath = parent.FullName;
            _localPathBox.Text = _localPath;
            await LoadLocalAsync();
        }
    }

    private async Task RemoteUpAsync()
    {
        var trimmed = _remotePath.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        _remotePath = idx <= 0 ? "/" : trimmed[..idx];
        _remotePathBox.Text = _remotePath;
        await LoadRemoteAsync();
    }

    private async Task LocalOpenAsync()
    {
        if (_localList.SelectedItem is not FileRow row)
            return;
        if (row.IsDir)
        {
            _localPath = row.Path;
            _localPathBox.Text = row.Path;
            await LoadLocalAsync();
        }
        else
        {
            OpenWithShell(row.Path);
        }
    }

    private async Task RemoteOpenAsync()
    {
        Trace($"remoteOpen sel={(_remoteList.SelectedItem as FileRow)?.Name}");
        if (_remoteList.SelectedItem is not FileRow row)
            return;
        if (row.IsDir)
        {
            _remotePath = row.Path;
            _remotePathBox.Text = row.Path;
            await LoadRemoteAsync();
        }
        else
        {
            await OpenRemoteAsync(row);
        }
    }

    private async Task OpenRemoteAsync(FileRow row)
    {
        if (_sftp is null)
            return;
        var dir = Path.Combine(Path.GetTempPath(), "QuickTerm-open");
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, row.Name);
        try
        {
            await Task.Run(() => _sftp.DownloadFileAsync(row.Path, temp, true, null, default));
            OpenWithShell(temp);
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(row.Name, UiStrings.Format("TransferFailed", ex.Message), "", "", row.Path, temp));
        }
    }

    private static void OpenWithShell(string path)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    private void OnRemoteDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRemoteDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_sftp is null)
            return;
        var files = e.DataTransfer?.TryGetFiles();
        if (files is null)
            return;
        foreach (var item in files)
        {
            // Files only for now; dropping a folder would need recursive directory upload (a later cut).
            var local = item is IStorageFile file ? file.TryGetLocalPath() : null;
            if (local is null)
                continue;
            var name = Path.GetFileName(local);
            await TransferAsync(name, "", local, Combine(_remotePath, name), upload: true);
        }
    }

    private async Task EditSelectedAsync(bool remote, ListBox list)
    {
        if (list.SelectedItem is not FileRow { IsDir: false } row)
            return;
        if (remote)
            await EditRemoteAsync(row);
        else
            OpenWithShell(row.Path);
    }

    /// <summary>
    /// Edit a remote file: pull a working copy, open it in the OS editor, and watch it so each save is
    /// debounced and uploaded back over the same SFTP connection. Watchers live until the window closes.
    /// </summary>
    private async Task EditRemoteAsync(FileRow row)
    {
        if (_sftp is null)
            return;
        var dir = Path.Combine(Path.GetTempPath(), "QuickTerm-edit");
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, row.Name);
        try
        {
            await Task.Run(() => _sftp.DownloadFileAsync(row.Path, temp, true, null, default));
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(row.Name, UiStrings.Format("TransferFailed", ex.Message), "", "", row.Path, temp));
            return;
        }
        OpenWithShell(temp);
        var watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        void OnTouch(string? name)
        {
            if (!string.Equals(name, row.Name, StringComparison.OrdinalIgnoreCase))
                return;
            Trace($"watch touch {name}");
            _ = QueueUploadAsync(temp, row.Path, row.Name);
        }
        watcher.Changed += (_, e) => OnTouch(e.Name);
        watcher.Created += (_, e) => OnTouch(e.Name);
        watcher.Renamed += (_, e) => OnTouch(e.Name);
        _watchers.Add(watcher);
        PushQueue(new QueueRow(row.Name, UiStrings.Get("Editing"), "", row.Size, row.Path, temp));
    }

    private async Task QueueUploadAsync(string temp, string remote, string name)
    {
        if (_sftp is null)
            return;
        if (_uploadDebounce.TryGetValue(temp, out var previous))
            previous.Cancel();
        var cts = new CancellationTokenSource();
        _uploadDebounce[temp] = cts;
        try
        {
            await Task.Delay(600, cts.Token);
            Trace($"upload start {temp} -> {remote}");
            // Editors can hold the file briefly during save; retry a couple of times before giving up.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await Task.Run(() => _sftp.UploadFileAsync(temp, remote, true, null, null, default));
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(300, cts.Token);
                }
            }
            Trace($"upload done {remote}");
            PushQueue(new QueueRow(name, UiStrings.Get("TransferDone"), "100%", "", temp, remote));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer save.
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(name, UiStrings.Format("TransferFailed", ex.Message), "", "", temp, remote));
        }
    }

    private async Task TransferAsync(string name, string size, string source, string dest, bool upload)
    {
        if (_sftp is null)
            return;
        var index = _queueRows.Count;
        _queueRows.Add(new QueueRow(name, UiStrings.Get("Transferring"), "", size, source, dest));
        RefreshQueue();
        try
        {
            if (upload)
                await Task.Run(() => _sftp.UploadFileAsync(source, dest, true, null, null, default));
            else
                await Task.Run(() => _sftp.DownloadFileAsync(source, dest, true, null, default));
            _queueRows[index] = _queueRows[index] with { Status = UiStrings.Get("TransferDone"), Progress = "100%" };
            RefreshQueue();
            if (upload)
                await LoadRemoteAsync();
            else
                await LoadLocalAsync();
        }
        catch (Exception ex)
        {
            _queueRows[index] = _queueRows[index] with { Status = UiStrings.Format("TransferFailed", ex.Message) };
            RefreshQueue();
        }
    }

    private void PushQueue(QueueRow row)
    {
        // Save-watch callbacks arrive on a thread-pool thread; the queue is UI and must be touched there.
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PushQueue(row));
            return;
        }
        _queueRows.Add(row);
        RefreshQueue();
    }

    private void RefreshQueue() => _queue.ItemsSource = _queueRows.ToList();

    private static readonly List<(string Path, bool Remote)> FileClipboard = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, CancellationTokenSource> _uploadDebounce = new();

    private ContextMenu BuildContextMenu(bool remote, ListBox list, Action onRefresh)
    {
        var menu = new ContextMenu();
        MenuItem Add(string key, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = UiStrings.Get(key), IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
            return item;
        }
        Add("MenuTransfer", () => _ = TransferSelectedAsync(remote, list));
        Add("MenuEdit", () => _ = EditSelectedAsync(remote, list));
        menu.Items.Add(new Separator());
        Add("MenuCopy", () => CopySelected(remote, list));
        Add("MenuPaste", () => _ = PasteAsync(remote));
        menu.Items.Add(new Separator());
        Add("MenuCopyPath", () => CopyPathSelected(list));
        Add("MenuOpenInManager", () => OpenInManager(list), !remote);
        menu.Items.Add(new Separator());
        Add("MenuRename", () => _ = RenameAsync(remote, list));
        Add("MenuDelete", () => _ = DeleteAsync(remote, list));
        menu.Items.Add(new Separator());
        Add("MenuRefresh", onRefresh);
        menu.Items.Add(new Separator());
        var newMenu = new MenuItem { Header = UiStrings.Get("MenuNew") };
        var folder = new MenuItem { Header = UiStrings.Get("NewFolder") };
        folder.Click += (_, _) => _ = CreateAsync(remote, list, true);
        var file = new MenuItem { Header = UiStrings.Get("NewFile") };
        file.Click += (_, _) => _ = CreateAsync(remote, list, false);
        newMenu.Items.Add(folder);
        newMenu.Items.Add(file);
        menu.Items.Add(newMenu);
        return menu;
    }

    private async Task TransferSelectedAsync(bool remote, ListBox list)
    {
        if (list.SelectedItem is not FileRow { IsDir: false } row)
            return;
        if (remote)
            await TransferAsync(row.Name, row.Size, row.Path, Path.Combine(_localPath, row.Name), upload: false);
        else
            await TransferAsync(row.Name, row.Size, row.Path, Combine(_remotePath, row.Name), upload: true);
    }

    private void CopySelected(bool remote, ListBox list)
    {
        FileClipboard.Clear();
        if (list.SelectedItem is FileRow row)
            FileClipboard.Add((row.Path, remote));
    }

    private async Task PasteAsync(bool remote)
    {
        if (_sftp is null || FileClipboard.Count == 0)
            return;
        foreach (var (src, srcRemote) in FileClipboard.ToList())
        {
            var name = srcRemote ? src[(src.LastIndexOf('/') + 1)..] : Path.GetFileName(src);
            var dest = remote ? Combine(_remotePath, name) : Path.Combine(_localPath, name);
            try
            {
                if (srcRemote == remote)
                {
                    if (remote)
                        await Task.Run(() => _sftp.CopyFileAsync(src, dest, false, null, default));
                    else
                        await Task.Run(() => File.Copy(src, dest, true));
                }
                else
                {
                    await TransferAsync(name, "", src, dest, upload: remote);
                }
            }
            catch (Exception ex)
            {
                PushQueue(new QueueRow(name, UiStrings.Format("TransferFailed", ex.Message), "", "", src, dest));
            }
        }
        await ReloadAsync(remote);
    }

    private void CopyPathSelected(ListBox list)
    {
        if (list.SelectedItem is FileRow row)
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(row.Path);
    }

    private void OpenInManager(ListBox list)
    {
        if (list.SelectedItem is not FileRow row)
            return;
        var args = row.IsDir ? $"\"{row.Path}\"" : $"/select,\"{row.Path}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private async Task RenameAsync(bool remote, ListBox list)
    {
        if (list.SelectedItem is not FileRow row)
            return;
        var newName = await PromptAsync(UiStrings.Get("RenameTitle"), row.Name);
        if (newName is not { Length: > 0 } || newName == row.Name)
            return;
        try
        {
            if (remote)
            {
                if (_sftp is null) return;
                var parent = row.Path[..row.Path.LastIndexOf('/')];
                await Task.Run(() => _sftp.RenameAsync(row.Path, Combine(parent, newName), default));
            }
            else
            {
                var dest = Path.Combine(Path.GetDirectoryName(row.Path)!, newName);
                await Task.Run(() =>
                {
                    if (row.IsDir) Directory.Move(row.Path, dest);
                    else File.Move(row.Path, dest);
                });
            }
            await ReloadAsync(remote);
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(row.Name, UiStrings.Format("TransferFailed", ex.Message), "", "", "", ""));
        }
    }

    private async Task DeleteAsync(bool remote, ListBox list)
    {
        if (list.SelectedItem is not FileRow row)
            return;
        if (!await ConfirmAsync(UiStrings.Get("MenuDelete"), UiStrings.Format("DeleteConfirmQuestion", row.Name)))
            return;
        try
        {
            if (remote)
            {
                if (_sftp is null) return;
                if (row.IsDir)
                    await Task.Run(() => _sftp.DeleteDirectoryAsync(row.Path, true, null, default));
                else
                    await Task.Run(() => _sftp.DeleteFileAsync(row.Path, default));
            }
            else
            {
                await Task.Run(() =>
                {
                    if (row.IsDir) Directory.Delete(row.Path, true);
                    else File.Delete(row.Path);
                });
            }
            await ReloadAsync(remote);
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(row.Name, UiStrings.Format("TransferFailed", ex.Message), "", "", "", ""));
        }
    }

    private async Task CreateAsync(bool remote, ListBox list, bool isDir)
    {
        var name = await PromptAsync(UiStrings.Get(isDir ? "NewFolder" : "NewFile"), "");
        if (name is not { Length: > 0 })
            return;
        try
        {
            if (remote)
            {
                if (_sftp is null) return;
                var path = Combine(_remotePath, name);
                if (isDir)
                    await Task.Run(() => _sftp.CreateDirectoryAsync(path, false, UnixFilePermissions.UserRead | UnixFilePermissions.UserWrite | UnixFilePermissions.UserExecute | UnixFilePermissions.GroupRead | UnixFilePermissions.GroupExecute | UnixFilePermissions.OtherRead | UnixFilePermissions.OtherExecute, default));
                else
                    await Task.Run(async () =>
                    {
                        var f = await _sftp.CreateNewFileAsync(path, FileAccess.Write, null, default);
                        f.Dispose();
                    });
            }
            else
            {
                var path = Path.Combine(_localPath, name);
                await Task.Run(() =>
                {
                    if (isDir) Directory.CreateDirectory(path);
                    else using (File.Create(path)) { }
                });
            }
            await ReloadAsync(remote);
        }
        catch (Exception ex)
        {
            PushQueue(new QueueRow(name, UiStrings.Format("TransferFailed", ex.Message), "", "", "", ""));
        }
    }

    private Task ReloadAsync(bool remote) => remote ? LoadRemoteAsync() : LoadLocalAsync();

    private async Task<string?> PromptAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(12) };
        var ok = new Button { Content = UiStrings.Get("Confirm"), IsDefault = true };
        var cancel = new Button { Content = UiStrings.Get("Cancel"), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(12) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var dialog = new Window { Title = title, Width = 380, Height = 140, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        dialog.Content = root;
        string? typed = null;
        ok.Click += (_, _) => { typed = box.Text; dialog.Close(true); };
        cancel.Click += (_, _) => dialog.Close(false);
        await dialog.ShowDialog<bool>(this);
        return typed;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var ok = new Button { Content = UiStrings.Get("Confirm"), IsDefault = true };
        var cancel = new Button { Content = UiStrings.Get("Cancel"), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(12) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var dialog = new Window { Title = title, Width = 400, Height = 150, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock { Text = message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        dialog.Content = root;
        var result = false;
        ok.Click += (_, _) => { result = true; dialog.Close(true); };
        cancel.Click += (_, _) => dialog.Close(false);
        await dialog.ShowDialog(this);
        return result;
    }
}