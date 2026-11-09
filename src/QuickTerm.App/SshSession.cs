namespace QuickTerm.App;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuickTerm.Pty;
using Tmds.Ssh;

/// <summary>
/// An SSH shell channel behind the same byte-stream contract as a local pty, so the terminal's reader thread,
/// resize path and exit notice work unchanged. Tmds.Ssh is async; a background pump moves its output into a
/// blocking queue because <see cref="IPtySession.Read" /> is blocking by design. Lives in the App layer only —
/// the kernel stays free of NuGet dependencies.
/// </summary>
internal sealed class SshSession : IPtySession
{
    private readonly SshClient _client;
    private readonly RemoteProcess _process;
    private readonly HostProfile _profile;
    private readonly Stream _input;
    private readonly BlockingCollection<byte[]> _inbox = new();
    private readonly BlockingCollection<byte[]> _outbox = new();
    private readonly CancellationTokenSource _cts = new();
    private byte[]? _current;
    private int _offset;
    private int _columns;
    private int _rows;
    private volatile bool _exited;

    private SshSession(SshClient client, RemoteProcess process, int columns, int rows, HostProfile profile)
    {
        _client = client;
        _process = process;
        _profile = profile;
        _columns = columns;
        _rows = rows;
        _input = process.StandardInputStream;
        _ = PumpAsync(process.ReadAsStream(StderrHandler.Ignore));
        _ = Task.Run(WriteLoopAsync);
    }

    internal HostProfile Profile => _profile;

    public event Action<PtyExitInfo>? Exited;

    /// <summary>Opens an SFTP channel over this session's already-authenticated connection.</summary>
    internal Task<SftpClient> OpenSftpAsync(CancellationToken ct = default) => _client.OpenSftpClientAsync(ct);

    public int Columns => _columns;

    public int Rows => _rows;

    public bool HasExited => _exited;

    /// <summary>
    /// Connects and opens a shell sized to the view. <paramref name="trustHostKey" /> is the trust-on-first-use
    /// prompt (host, SHA256 fingerprint); a changed or revoked key is always rejected, never prompted.
    /// </summary>
    public static async Task<SshSession> ConnectAsync(
        HostProfile profile,
        string? password,
        int columns,
        int rows,
        Func<string, string, Task<bool>>? trustHostKey,
        CancellationToken ct = default)
    {
        var settings = new SshClientSettings(profile.Host)
        {
            Port = profile.Port > 0 ? profile.Port : 22,
            UserName = profile.User,
            UpdateKnownHostsFileAfterAuthentication = true,
        };

        switch (profile.AuthKind)
        {
            case SshAuthKind.PrivateKey when profile.KeyPath is { Length: > 0 } key:
                settings.Credentials = new List<Credential> { new PrivateKeyCredential(key, password) };
                break;
            case SshAuthKind.Agent:
                break; // default credentials already offer the agent
            default:
                settings.Credentials = new List<Credential> { new PasswordCredential(password ?? string.Empty) };
                break;
        }

        settings.HostAuthentication = async (context, token) =>
        {
            if (context.KnownHostResult == KnownHostResult.Trusted)
                return true;
            if (context.KnownHostResult != KnownHostResult.Unknown || trustHostKey is null)
                return false;
            var info = context.ConnectionInfo;
            return await trustHostKey(info.HostName, info.ServerKey.Key.SHA256FingerPrint);
        };

        var client = new SshClient(settings);
        try
        {
            await client.ConnectAsync(ct);
            var options = new ExecuteOptions
            {
                AllocateTerminal = true,
                TerminalType = "xterm-256color",
                TerminalSize = new TerminalSize(columns, rows, 0, 0),
            };
            var process = await client.ExecuteShellAsync(options, ct);
            return new SshSession(client, process, columns, rows, profile);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task PumpAsync(Stream output)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await output.ReadAsync(buffer, _cts.Token);
                if (read <= 0)
                    break;
                _inbox.Add(buffer.AsSpan(0, read).ToArray(), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _inbox.CompleteAdding();
        }

        var code = 0;
        try
        {
            code = (await _process.GetExitStatusAsync(CancellationToken.None)).ExitCode;
        }
        catch
        {
            // Disposed mid-read: the exit code is unknowable, report 0 rather than throw on the pump thread.
        }
        _exited = true;
        Exited?.Invoke(new PtyExitInfo(code, 0));
    }

    public int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current is null || _offset >= _current.Length)
            {
                try
                {
                    _current = _inbox.Take(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return -1;
                }
                catch (InvalidOperationException)
                {
                    return -1; // queue completed: end of stream
                }
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _current.Length - _offset);
            if (n == 0)
            {
                _current = null;
                continue;
            }
            _current.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        // Never touch the SSH stream on the UI thread: its sync write can throw (NotSupportedException on an
        // async-only stream), and an exception here would propagate through key handling and take the host down.
        if (_outbox.IsAddingCompleted)
            return;
        try
        {
            _outbox.Add(bytes.ToArray(), _cts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Session already closing.
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            foreach (var chunk in _outbox.GetConsumingEnumerable(_cts.Token))
            {
                await _input.WriteAsync(chunk, _cts.Token);
                await _input.FlushAsync(_cts.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or NotSupportedException)
        {
            // The channel closed under us; the read pump/exit path owns the user-visible outcome.
        }
    }

    public void Resize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        try
        {
            _process.SetTerminalSize(new TerminalSize(columns, rows, 0, 0));
        }
        catch (InvalidOperationException)
        {
            // No terminal allocated (or already gone): nothing to tell the far side.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _inbox.CompleteAdding();
        _outbox.CompleteAdding();
        _process.Dispose();
        _client.Dispose();
    }
}