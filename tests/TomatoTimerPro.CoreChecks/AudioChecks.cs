using System.IO;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Infrastructure.Audio;

public static class AudioChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "Audio");
        var data = Path.Combine(root, "Data");
        var source = Path.Combine(root, "original.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        Guid id;
        using (var music = new LocalMusicService(data, new ImmediateDispatcher()))
        {
            await music.InitializeAsync();
            Check(!Directory.Exists(data), "Loading an empty music library creates no files or players");
            await music.ImportAsync(new[] { source, source.ToUpperInvariant() });
            Check(music.Tracks.Count == 1, "Music import deduplicates paths case-insensitively");
            id = music.Tracks[0].Id;
            music.MusicVolume = 4;
            music.AlarmVolume = -2;
            Check(music.MusicVolume == 1 && music.AlarmVolume == 0, "Independent volumes clamp to supported range");
            Check(!music.IsPlaying && !music.IsAlarmPlaying, "Import never starts music or alarms");
            Check(!File.Exists(Path.Combine(data, "default-alarm.wav")), "Default alarm generation is deferred until first use");
        }
        using (var music = new LocalMusicService(data, new ImmediateDispatcher()))
        {
            await music.InitializeAsync();
            Check(music.Tracks.Count == 1 && music.Tracks[0].Id == id, "Music index roundtrips stable IDs");
            await music.RemoveAsync(id);
            Check(music.Tracks.Count == 0 && File.Exists(source), "Removing a music entry preserves the original audio file");
            await music.ImportAsync(new[] { Path.Combine(root, "missing.mp3") });
            Check(music.Tracks.Count == 1 && !music.Tracks[0].IsAvailable, "Missing imports are marked unavailable");
        }
        Console.WriteLine("PASS Audio library: lazy loading, deduplication, volume bounds, persistence, safe removal, missing-file state");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Audio check failed: " + message);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
}
