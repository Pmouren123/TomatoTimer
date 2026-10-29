using System.Globalization;
using TomatoTimerPro.Infrastructure.Binding;

namespace TomatoTimerPro.Controllers.Components.Countdown;

/// <summary>Editable hours/minutes/seconds, independent of any running countdown.</summary>
public sealed class DurationInputController : ObservableObject
{
    private string _hours = "0";
    private string _minutes = "25";
    private string _seconds = "0";
    private bool _updating;
    public event Action? Changed;
    public string Hours { get => _hours; set { if (SetProperty(ref _hours, value)) NotifyChanged(); } }
    public string Minutes { get => _minutes; set { if (SetProperty(ref _minutes, value)) NotifyChanged(); } }
    public string Seconds { get => _seconds; set { if (SetProperty(ref _seconds, value)) NotifyChanged(); } }
    public string ValidationMessage => "请输入有效时长：小时为 0–24 的整数，分钟为 0–59 的整数，秒为 0 至不足 60；总时长为 0.6 秒至 24 小时。";

    public bool TryGetDuration(out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!int.TryParse(Hours, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours is < 0 or > 24
            || !int.TryParse(Minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 0 or > 59
            || !decimal.TryParse(Seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds is < 0 or >= 60)
            return false;
        var total = hours * 3600m + minutes * 60m + seconds;
        if (total is < .6m or > 86400m) return false;
        duration = TimeSpan.FromTicks((long)decimal.Round(total * TimeSpan.TicksPerSecond, 0));
        return true;
    }

    public void SetDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds is < .6 or > 86400) throw new ArgumentOutOfRangeException(nameof(duration));
        _updating = true;
        try
        {
            var ticks = duration.Ticks;
            Hours = (ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture);
            Minutes = (ticks / TimeSpan.TicksPerMinute % 60).ToString(CultureInfo.InvariantCulture);
            Seconds = ((ticks % TimeSpan.TicksPerMinute) / (decimal)TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture);
        }
        finally { _updating = false; }
        NotifyChanged();
    }

    // Keeps existing callers and decimal-minute preference files compatible.
    public void SetMinutes(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            && double.IsFinite(minutes) && minutes is >= .01 and <= 1440)
        { SetDuration(TimeSpan.FromMinutes(minutes)); return; }
        _updating = true;
        try { Hours = "0"; Minutes = value; Seconds = "0"; }
        finally { _updating = false; }
        NotifyChanged();
    }

    private void NotifyChanged() { if (!_updating) Changed?.Invoke(); }
}
