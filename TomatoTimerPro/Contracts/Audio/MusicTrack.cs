namespace TomatoTimerPro.Contracts.Audio;

/// <summary>A library entry references an original file; removing it never deletes that file.</summary>
public sealed record MusicTrack(Guid Id, string FilePath, string Title, string? Issue = null)
{
    public bool IsAvailable => Issue is null;
    public string AvailabilityText => Issue ?? "本地音频";
}
