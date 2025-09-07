using System.Text;

namespace GPhotosMetaFixer.Services;

/// <summary>
/// Simple CLI progress display for multi-step workflows.
/// Renders a single-line progress bar that updates from 0-100% with counts.
/// </summary>
public class ProgressDisplay
{
    public static ProgressDisplay? ActiveInstance { get; private set; }
    
    // Temporary flag to disable progress bar rendering
    private static bool _disabled = true;

    private readonly object _renderLock = new();
    private string _currentStep = string.Empty;
    private int _currentTotal;
    private int _currentProcessed;
    private bool _active;
    private TimeSpan _minUpdateInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _lastRenderUtc = DateTime.MinValue;

    /// <summary>
    /// Begins a new step with the provided name and total count.
    /// </summary>
    public void StartStep(string stepName, int total)
    {
        if (_disabled) return;
        
        lock (_renderLock)
        {
            _currentStep = stepName;
            _currentTotal = Math.Max(0, total);
            _currentProcessed = 0;
            _active = true;
            ForceRender();
        }
    }

    /// <summary>
    /// Updates the current step's processed count.
    /// </summary>
    public void Report(int processed)
    {
        if (_disabled) return;
        
        lock (_renderLock)
        {
            if (!_active) return;
            _currentProcessed = Math.Clamp(processed, 0, _currentTotal > 0 ? _currentTotal : int.MaxValue);
            if (DateTime.UtcNow - _lastRenderUtc >= _minUpdateInterval)
            {
                RenderInternal();
            }
        }
    }

    /// <summary>
    /// Completes the current step and writes a newline to keep logs readable.
    /// </summary>
    public void CompleteStep()
    {
        if (_disabled) return;
        
        lock (_renderLock)
        {
            if (!_active) return;
            _currentProcessed = _currentTotal;
            ForceRender();
            Console.WriteLine();
            _active = false;
        }
    }

    /// <summary>
    /// Make this instance the globally active progress renderer so logs can re-render it.
    /// </summary>
    public void AttachAsActive()
    {
        if (_disabled) return;
        ActiveInstance = this;
    }

    /// <summary>
    /// Detach this instance from global active slot.
    /// </summary>
    public void Detach()
    {
        if (ActiveInstance == this) ActiveInstance = null;
    }

    /// <summary>
    /// Called by external writers (like logger) to re-render the bar after a log line.
    /// </summary>
    public static void ReRenderIfActive()
    {
        if (_disabled) return;
        
        var instance = ActiveInstance;
        if (instance == null) return;
        lock (instance._renderLock)
        {
            if (!instance._active) return;
            instance.ForceRender();
        }
    }

    /// <summary>
    /// Clears the current progress line so the next console write (e.g., a log) starts on a clean line.
    /// </summary>
    public static void ClearLineIfActive()
    {
        if (_disabled) return;
        
        var instance = ActiveInstance;
        if (instance == null) return;
        lock (instance._renderLock)
        {
            if (!instance._active) return;
            try
            {
                var width = Console.WindowWidth;
                Console.Write("\r" + new string(' ', Math.Max(0, width - 1)) + "\r");
            }
            catch
            {
                Console.Write("\r\r");
            }
        }
    }

    /// <summary>
    /// Optional: adjust the minimum update interval for throttling.
    /// </summary>
    public void SetUpdateInterval(TimeSpan interval)
    {
        lock (_renderLock)
        {
            _minUpdateInterval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : interval;
        }
    }

    private void ForceRender()
    {
        RenderInternal(force: true);
    }

    private void RenderInternal(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRenderUtc < _minUpdateInterval) return;
        var percent = _currentTotal <= 0
            ? 0
            : (int)Math.Round((_currentProcessed / (double)_currentTotal) * 100.0);

        const int barWidth = 30;
        var filled = _currentTotal <= 0 ? 0 : (int)Math.Round((percent / 100.0) * barWidth);
        var sb = new StringBuilder();
        sb.Append('[');
        sb.Append(new string('#', Math.Clamp(filled, 0, barWidth)));
        sb.Append(new string('-', Math.Clamp(barWidth - filled, 0, barWidth)));
        sb.Append(']');

        var label = $" {_currentStep} {percent,3}%  {_currentProcessed}/{_currentTotal}";

        // Render on a single line; logs still print as separate lines.
        try
        {
            Console.Write("\r" + sb + label + new string(' ', Math.Max(0, Console.WindowWidth - sb.Length - label.Length - 1)));
        }
        catch
        {
            Console.Write("\r" + sb + label);
        }
        _lastRenderUtc = DateTime.UtcNow;
    }
}


