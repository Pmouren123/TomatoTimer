using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using TomatoTimerPro.Infrastructure.Desktop;

public static class SingleInstanceChecks
{
    public static async Task RunAsync()
    {
        var key = "TomatoTimerPro.CoreChecks." + Guid.NewGuid().ToString("N");
        using (var primary = new SingleInstanceService(key, "same-build"))
        using (var secondary = new SingleInstanceService(key, "same-build"))
        {
            Check(primary.IsPrimary && !secondary.IsPrimary, "An isolated instance key elects exactly one primary");
            var result = await secondary.ActivateExistingAsync();
            Check(result.Status == ActivationStatus.Activated && result.ExistingBuildId == "same-build"
                && result.ExistingProcessId == Environment.ProcessId && result.ExistingProcessPath == Environment.ProcessPath,
                "Same-build negotiation returns primary metadata");
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.ActivationRequested += () => activated.TrySetResult();
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(true, "An activation arriving before subscription is replayed rather than lost");
        }

        key = "TomatoTimerPro.CoreChecks." + Guid.NewGuid().ToString("N");
        using (var primary = new SingleInstanceService(key, "old-memory-build"))
        using (var secondary = new SingleInstanceService(key, "new-memory-build"))
        {
            var activations = 0;
            primary.ActivationRequested += () => Interlocked.Increment(ref activations);
            var result = await secondary.ActivateExistingAsync();
            Check(result.Status == ActivationStatus.VersionMismatch && result.ExistingBuildId == "old-memory-build"
                && activations == 0, "Different builds return a mismatch without activating the old window");
            using (var malformed = new NamedPipeClientStream(".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await malformed.ConnectAsync(timeout.Token);
                await malformed.WriteAsync(new byte[] { 99 }, timeout.Token);
            }
            var retry = await secondary.ActivateExistingAsync();
            Check(retry.Status == ActivationStatus.VersionMismatch, "A malformed request cannot permanently stop the listener");
            primary.Dispose();
            using var replacement = new SingleInstanceService(key, "new-memory-build");
            Check(replacement.IsPrimary, "Disposing a primary releases ownership even while a secondary handle exists");
        }

        key = "TomatoTimerPro.CoreChecks.Legacy." + Guid.NewGuid().ToString("N");
        using (var legacy = new FakePrimary(key, legacy: true))
        using (var secondary = new SingleInstanceService(key, "new-build"))
        {
            var timer = Stopwatch.StartNew();
            var result = await secondary.ActivateExistingAsync();
            Check(!secondary.IsPrimary && result.Status == ActivationStatus.LegacyUnknown && timer.Elapsed < TimeSpan.FromSeconds(4),
                "A legacy one-byte primary is reported explicitly without hanging or being mistaken for the current build");
        }

        key = "TomatoTimerPro.CoreChecks.Stalled." + Guid.NewGuid().ToString("N");
        using (var stalled = new FakePrimary(key, legacy: false))
        using (var secondary = new SingleInstanceService(key, "new-build"))
        {
            var timer = Stopwatch.StartNew();
            var result = await secondary.ActivateExistingAsync();
            Check(result.Status == ActivationStatus.Unavailable && timer.Elapsed < TimeSpan.FromSeconds(4),
                "A connected primary that never replies is bounded by the two-second handshake timeout");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Single-instance check failed: " + message);
    }

    private sealed class FakePrimary : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly CancellationTokenSource _cancel = new();
        private readonly Thread _owner;
        private readonly Task _server;
        public FakePrimary(string key, bool legacy)
        {
            var pipeName = "TomatoTimerPro-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            using var ready = new ManualResetEventSlim(false);
            _owner = new Thread(() =>
            {
                using var mutex = new Mutex(true, "Local\\" + pipeName);
                ready.Set();
                _stop.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            _owner.Start();
            ready.Wait();
            _server = ServeAsync(pipeName, legacy);
        }

        private async Task ServeAsync(string pipeName, bool legacy)
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, legacy ? PipeDirection.In : PipeDirection.InOut,
                        1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cancel.Token);
                    if (!legacy) { await Task.Delay(Timeout.Infinite, _cancel.Token); return; }
                    var marker = new byte[1];
                    await pipe.ReadExactlyAsync(marker, _cancel.Token);
                    // Legacy releases ignored the marker's value and closed
                    // after the one-byte activation request, without a reply.
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException) { }
        }

        public void Dispose()
        {
            _cancel.Cancel();
            _server.GetAwaiter().GetResult();
            _stop.Set();
            _owner.Join(TimeSpan.FromSeconds(2));
            _stop.Dispose();
            _cancel.Dispose();
        }
    }
}
