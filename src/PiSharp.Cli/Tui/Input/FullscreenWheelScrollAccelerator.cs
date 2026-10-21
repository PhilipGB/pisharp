namespace PiSharp.Cli.Tui;

/// <summary>Matches Pi's fixed and velocity-sensitive fullscreen wheel line policy.</summary>
internal sealed class FullscreenWheelScrollAccelerator(TimeProvider timeProvider, bool? accelerate = null)
{
    private const double BurstGapMilliseconds = 5;
    private const double GestureGapMilliseconds = 200;
    private const double ReferenceGapMilliseconds = 100;
    private const double MaximumAutoLines = 6;
    private FullscreenWheelScrollSetting _setting = FullscreenWheelScrollSetting.Automatic;
    private readonly bool _accelerate = accelerate ?? !TerminalAcceleratesWheel();
    private long _lastTimestamp;
    private int _lastDirection;
    private double? _averageGap;
    private double _carry;
    private bool _hasLastTimestamp;

    public void SetSetting(FullscreenWheelScrollSetting setting)
    {
        if (setting.Lines is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(setting));
        _setting = setting;
        Reset();
    }

    /// <param name="direction">Pi's wheel direction: -1 for up, +1 for down.</param>
    public int Next(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!_setting.Auto) return _setting.Lines;
        if (!_accelerate) return 1;

        var now = timeProvider.GetTimestamp();
        var gap = _hasLastTimestamp ? timeProvider.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds :
            double.PositiveInfinity;
        var sameGesture = direction == _lastDirection && gap <= GestureGapMilliseconds;
        _lastTimestamp = now;
        _hasLastTimestamp = true;
        _lastDirection = direction;
        if (!sameGesture)
        {
            _averageGap = null;
            _carry = 0;
            return 1;
        }
        if (gap < BurstGapMilliseconds) return 1;

        _averageGap = _averageGap is { } average ? (average + gap) / 2 : gap;
        var lines = Math.Min(MaximumAutoLines, Math.Max(1, ReferenceGapMilliseconds / _averageGap.Value)) + _carry;
        var whole = (int)Math.Floor(lines);
        _carry = lines - whole;
        return whole;
    }

    private void Reset()
    {
        _lastTimestamp = 0;
        _lastDirection = 0;
        _averageGap = null;
        _carry = 0;
        _hasLastTimestamp = false;
    }

    private static bool TerminalAcceleratesWheel()
    {
        var environment = Environment.GetEnvironmentVariables();
        return OperatingSystem.IsMacOS() && environment["SSH_CONNECTION"] is null &&
            environment["SSH_CLIENT"] is null && environment["SSH_TTY"] is null;
    }
}
