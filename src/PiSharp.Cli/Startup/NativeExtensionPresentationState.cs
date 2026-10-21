using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Stores persistent extension working presentation and applies it to the bound screen.</summary>
internal sealed class NativeExtensionPresentationState
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PresentationContribution> _contributions = [];
    private TerminalScreen? _screen;
    private Timer? _workingTimer;
    private int _workingFrameIndex;
    private long _nextContributionOrder;

    public void BindScreen(TerminalScreen? screen)
    {
        lock (_gate) _screen = screen;
        UpdateWorkingPresentation();
        UpdateHiddenThinkingLabel();
    }

    public void SetWorkingMessage(Guid owner, string? message)
    {
        lock (_gate)
        {
            var current = _contributions.GetValueOrDefault(owner) ?? new();
            _contributions[owner] = current with
            {
                WorkingMessageOrder = ++_nextContributionOrder,
                WorkingMessage = string.IsNullOrWhiteSpace(message) ? null : NormalizeOneLine(message)
            };
        }
        UpdateWorkingPresentation();
    }

    public void SetWorkingVisible(Guid owner, bool visible)
    {
        lock (_gate)
        {
            var current = _contributions.GetValueOrDefault(owner) ?? new();
            _contributions[owner] = current with
            {
                WorkingVisibleOrder = ++_nextContributionOrder,
                WorkingVisible = visible
            };
        }
        UpdateWorkingPresentation();
    }

    public void SetWorkingIndicator(Guid owner,
        NativeExtensions.PiSharpExtensionWorkingIndicatorOptions? options)
    {
        if (options is not null)
        {
            if (options.Frames is null || options.Frames.Count > 32)
                throw new ArgumentException("Working indicator must define at most 32 frames.", nameof(options));
            var interval = options.Interval ?? TimeSpan.FromMilliseconds(80);
            if (interval < TimeSpan.FromMilliseconds(50) || interval > TimeSpan.FromSeconds(5))
                throw new ArgumentOutOfRangeException(nameof(options), "Working indicator interval must be between 50 ms and 5 s.");
            options = new(options.Frames.Select(frame =>
            {
                if (frame is null) throw new ArgumentException("Working indicator frames cannot be null.", nameof(options));
                if (frame.Length == 0) return string.Empty;
                if (string.IsNullOrWhiteSpace(frame))
                    throw new ArgumentException("Working indicator frames cannot be whitespace.", nameof(options));
                var safe = NormalizeOneLine(frame);
                return safe.Length > 16 ? safe[..16] : safe;
            }).ToArray(), interval);
        }

        lock (_gate)
        {
            var current = _contributions.GetValueOrDefault(owner) ?? new();
            _contributions[owner] = current with
            {
                WorkingIndicatorOrder = ++_nextContributionOrder,
                WorkingIndicator = options
            };
            _workingFrameIndex = 0;
        }
        UpdateWorkingPresentation();
    }

    public void SetHiddenThinkingLabel(Guid owner, string? label)
    {
        lock (_gate)
        {
            var current = _contributions.GetValueOrDefault(owner) ?? new();
            _contributions[owner] = current with
            {
                HiddenThinkingLabelOrder = ++_nextContributionOrder,
                HiddenThinkingLabel = string.IsNullOrWhiteSpace(label) ? null : NormalizeOneLine(label)
            };
        }
        UpdateHiddenThinkingLabel();
    }

    public void RemoveOwner(Guid owner)
    {
        lock (_gate) _contributions.Remove(owner);
        UpdateWorkingPresentation();
        UpdateHiddenThinkingLabel();
    }

    private void UpdateWorkingPresentation()
    {
        TerminalScreen? screen;
        string? message;
        bool? visibility;
        string frame;
        lock (_gate)
        {
            screen = _screen;
            var contributions = _contributions.Values.ToArray();
            message = contributions.Where(item => item.WorkingMessage is not null)
                .MaxBy(item => item.WorkingMessageOrder)?.WorkingMessage;
            visibility = contributions.Where(item => item.WorkingVisible is not null)
                .MaxBy(item => item.WorkingVisibleOrder)?.WorkingVisible;
            var indicator = contributions.Where(item => item.WorkingIndicator is not null)
                .MaxBy(item => item.WorkingIndicatorOrder)?.WorkingIndicator;
            frame = indicator is null ? "⠋" : indicator.Frames.Count == 0 ? string.Empty :
                indicator.Frames.ElementAtOrDefault(_workingFrameIndex) ?? string.Empty;
            if (screen is { HasActiveWorkingStatus: true } && visibility != false && indicator is { Frames.Count: > 1 })
            {
                _workingTimer ??= new Timer(static state => ((NativeExtensionPresentationState)state!).AdvanceWorkingFrame(),
                    this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _workingTimer.Change(indicator.Interval ?? TimeSpan.FromMilliseconds(80),
                    indicator.Interval ?? TimeSpan.FromMilliseconds(80));
            }
            else
            {
                _workingTimer?.Dispose();
                _workingTimer = null;
            }

            if (screen is not null && ReferenceEquals(_screen, screen))
                screen.SetExtensionWorkingPresentation(message, visibility, frame, indicator is not null);
        }
    }

    private void AdvanceWorkingFrame()
    {
        lock (_gate)
        {
            var indicator = _contributions.Values.Where(item => item.WorkingIndicator is not null)
                .MaxBy(item => item.WorkingIndicatorOrder)?.WorkingIndicator;
            if (indicator is not { Frames.Count: > 1 }) return;
            _workingFrameIndex = (_workingFrameIndex + 1) % indicator.Frames.Count;
            if (_screen is not null) _screen.SetWorkingFrame(indicator.Frames[_workingFrameIndex]);
        }
    }

    private void UpdateHiddenThinkingLabel()
    {
        lock (_gate)
        {
            var label = _contributions.Values.Where(item => item.HiddenThinkingLabel is not null)
                .MaxBy(item => item.HiddenThinkingLabelOrder)?.HiddenThinkingLabel;
            if (_screen is not null) _screen.SetHiddenThinkingLabel(label);
        }
    }

    private static string NormalizeOneLine(string value) =>
        TerminalSafeText.Normalize(value).Replace('\n', ' ');
}
