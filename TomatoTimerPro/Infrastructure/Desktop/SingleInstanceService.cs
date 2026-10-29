using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TomatoTimerPro.Infrastructure.Desktop;

public enum ActivationStatus { Activated, VersionMismatch, LegacyUnknown, Unavailable }
public sealed record ActivationResult(ActivationStatus Status, string? ExistingBuildId = null,
    string? ExistingProcessPath = null, int? ExistingProcessId = null, string? Message = null);

public sealed class SingleInstanceService : IDisposable
{
    private sealed record InstanceIdentity(string BuildId, string ProcessPath, int ProcessId);
    private sealed record ActivationReply(int Protocol, ActivationStatus Status, InstanceIdentity Identity);
    private const byte Negotiation = 2;
    private const int MaximumFrameBytes = 16 * 1024;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ManualResetEventSlim _releaseOwnership = new(false);
    private readonly Thread _ownershipThread;
    private readonly object _events = new();
    private readonly Task? _listener;
    private Action? _activationRequested;
    private bool _pendingActivation;
    private bool _disposed;
    public bool IsPrimary { get; }
    public string PipeName { get; }
    public string BuildId { get; }
    public string ProcessPath { get; } = Environment.ProcessPath ?? "未知程序路径";
    public int ProcessId { get; } = Environment.ProcessId;
    public event Action? ActivationRequested
    {
        add
        {
            bool replay;
            lock (_events)
            {
                _activationRequested += value;
                replay = _pendingActivation && value is not null;
                if (replay) _pendingActivation = false;
            }
            if (replay) value?.Invoke();
        }
        remove { lock (_events) _activationRequested -= value; }
    }

    public SingleInstanceService(string? instanceKey = null, string? buildIdOverride = null)
    {
        var identity = instanceKey ?? (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        PipeName = "TomatoTimer-" + token;
        BuildId = buildIdOverride ?? typeof(SingleInstanceService).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        using var ready = new ManualResetEventSlim(false);
        Exception? ownershipError = null;
        var primary = false;
        _ownershipThread = new Thread(() =>
        {
            var signaled = false;
            try
            {
                using var mutex = new Mutex(false, "Local\\" + PipeName);
                try { primary = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { primary = true; }
                ready.Set();
                signaled = true;
                if (!primary) return;
                try { _releaseOwnership.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception error)
            {
                if (!signaled) { ownershipError = error; ready.Set(); }
                else Trace.TraceError("Instance ownership cleanup failed: {0}", error);
            }
        }) { IsBackground = true, Name = "TomatoTimer instance ownership" };
        _ownershipThread.Start();
        ready.Wait();
        if (ownershipError is not null) throw new InvalidOperationException("无法检查现有实例。", ownershipError);
        IsPrimary = primary;
        // Open the first pipe before returning from construction. Requests that
        // arrive before App wires its activation event are replayed on subscribe.
        if (IsPrimary) _listener = ListenAsync();
    }

    public void StartListening() { ObjectDisposedException.ThrowIf(_disposed, this); }

    public async Task<ActivationResult> ActivateExistingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(new byte[] { Negotiation }, timeout.Token);
            await WriteFrameAsync(client, new InstanceIdentity(BuildId, ProcessPath, ProcessId), timeout.Token);
            var reply = await ReadFrameAsync<ActivationReply>(client, timeout.Token);
            if (reply.Protocol != Negotiation) return new(ActivationStatus.LegacyUnknown, Message: "现有实例不支持版本识别。");
            return new(reply.Status, reply.Identity.BuildId, reply.Identity.ProcessPath, reply.Identity.ProcessId);
        }
        catch (OperationCanceledException)
        {
            return new(ActivationStatus.Unavailable, Message: "现有实例在 2 秒内未响应，可能仍在启动或退出。请从托盘退出后重试。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // The previous release exposed an inbound-only pipe and accepted a
            // single activation byte. It cannot return build information.
            try
            {
                using var legacy = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await legacy.ConnectAsync(timeout.Token);
                await legacy.WriteAsync(new byte[] { 1 }, timeout.Token);
            }
            catch (Exception legacyError) when (legacyError is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            return new(ActivationStatus.LegacyUnknown, Message: "检测到无法识别版本的旧实例。请从系统托盘退出旧实例，再重新运行本次构建。");
        }
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var marker = new byte[1];
                await server.ReadExactlyAsync(marker, timeout.Token);
                if (marker[0] == 1) { RequestActivation(); continue; }
                if (marker[0] != Negotiation) continue;
                var identity = await ReadFrameAsync<InstanceIdentity>(server, timeout.Token);
                if (string.IsNullOrWhiteSpace(identity.BuildId)) throw new InvalidDataException("Missing client build identity.");
                var compatible = StringComparer.Ordinal.Equals(identity.BuildId, BuildId);
                var status = compatible ? ActivationStatus.Activated : ActivationStatus.VersionMismatch;
                await WriteFrameAsync(server, new ActivationReply(Negotiation, status,
                    new InstanceIdentity(BuildId, ProcessPath, ProcessId)), timeout.Token);
                if (compatible) RequestActivation();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
            {
                // A malformed, disconnected or stalled client cannot permanently
                // stop the listener. The next request receives a fresh pipe.
                Trace.TraceInformation("Instance activation request failed: {0}", error.Message);
                if (!_lifetime.IsCancellationRequested)
                {
                    try { await Task.Delay(25, _lifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
    }

    private void RequestActivation()
    {
        Action? handlers;
        lock (_events)
        {
            if (_disposed) return;
            handlers = _activationRequested;
            if (handlers is null) { _pendingActivation = true; return; }
        }
        try { handlers(); }
        catch (Exception error) { Trace.TraceError("Instance activation callback failed: {0}", error); }
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumFrameBytes) throw new InvalidDataException("Instance metadata frame is too large.");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(data, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Invalid instance metadata frame length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Missing instance metadata.");
    }

    public void Dispose()
    {
        lock (_events)
        {
            if (_disposed) return;
            _disposed = true;
            _activationRequested = null;
            _pendingActivation = false;
        }
        _lifetime.Cancel();
        _releaseOwnership.Set();
        _ownershipThread.Join(TimeSpan.FromSeconds(2));
        _releaseOwnership.Dispose();
        if (_listener is null) _lifetime.Dispose();
        else _ = _listener.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
