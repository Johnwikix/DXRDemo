using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Display;

namespace DXRDemo.Hdr;

/// <summary>
/// Snapshot of the HDR capabilities of the display currently hosting the window.
/// </summary>
/// <param name="Kind">The advanced color kind reported by the system.</param>
/// <param name="IsSupported">Whether the display currently accepts HDR output.</param>
/// <param name="MaxLuminanceInNits">Peak luminance the display can show.</param>
/// <param name="MinLuminanceInNits">Minimum (black-level) luminance of the display.</param>
/// <param name="SdrWhiteLevelInNits">Luminance the display shows for SDR white (1.0).</param>
public readonly record struct HdrDisplayInfo(
    AdvancedColorKind Kind,
    bool IsSupported,
    float MaxLuminanceInNits,
    float MinLuminanceInNits,
    float SdrWhiteLevelInNits)
{
    /// <summary>
    /// Gets a human readable status line for the toolbar, e.g. "HDR: 可用 · 1000 nits".
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!IsSupported)
            {
                return "HDR: 不可用";
            }

            return MaxLuminanceInNits > 0
                ? $"HDR10: 可用 · 峰值 {MaxLuminanceInNits:0} nits"
                : "HDR10: 可用";
        }
    }
}

/// <summary>
/// Queries the advanced color / HDR state of the display hosting the app window,
/// and keeps the snapshot up to date when the display configuration changes
/// (monitor switch, Windows HDR toggle, display mode change, ...).
/// </summary>
public sealed class HdrDisplayInfoTracker
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DisplayInformation _displayInformation;
    private HdrDisplayInfo _current;
    private bool _disposed;

    private HdrDisplayInfoTracker(DisplayInformation displayInformation)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _displayInformation = displayInformation;
        _current = Query(displayInformation);

        _displayInformation.AdvancedColorInfoChanged += OnAdvancedColorInfoChanged;
        _displayInformation.ColorProfileChanged += OnColorProfileChanged;
    }

    /// <summary>
    /// Raised (on the UI thread) whenever the display's HDR state changes.
    /// </summary>
    public event EventHandler<HdrDisplayInfo>? Changed;

    /// <summary>
    /// Gets the latest snapshot of the display's HDR capabilities.
    /// </summary>
    public HdrDisplayInfo Current => _current;

    /// <summary>
    /// Gets whether HDR output can be enabled right now.
    /// </summary>
    public bool IsHdrEnabled => _current.IsSupported;

    /// <summary>
    /// Gets a snapshot representing an unsupported display (used when detection is unavailable).
    /// </summary>
    public static HdrDisplayInfo Unsupported { get; } = new(
        Kind: AdvancedColorKind.StandardDynamicRange,
        IsSupported: false,
        MaxLuminanceInNits: 0,
        MinLuminanceInNits: 0,
        SdrWhiteLevelInNits: 0);

    /// <summary>
    /// Creates a tracker bound to the current thread (must be the UI thread).
    /// </summary>
    public static HdrDisplayInfoTracker Create()
    {
        DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();

        return new HdrDisplayInfoTracker(displayInformation);
    }

    private static HdrDisplayInfo Query(DisplayInformation displayInformation)
    {
        HdrDisplayInfo fallback = new(
            Kind: AdvancedColorKind.StandardDynamicRange,
            IsSupported: false,
            MaxLuminanceInNits: 0,
            MinLuminanceInNits: 0,
            SdrWhiteLevelInNits: 0);

        try
        {
            AdvancedColorInfo info = displayInformation.GetAdvancedColorInfo();

            AdvancedColorKind kind = info.CurrentAdvancedColorKind;
            bool available = info.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange);

            Debug.WriteLine(
                $"[HDR] WinRT detection: kind={kind} hdrAvailable={available} " +
                $"maxLum={info.MaxLuminanceInNits:0} minLum={info.MinLuminanceInNits:0} " +
                $"sdrWhite={info.SdrWhiteLevelInNits:0} maxAvgLum={info.MaxAverageFullFrameLuminanceInNits:0}");

            // Treat the display as HDR-capable whenever it supports the HDR kind, not only when it is
            // currently reporting HDR as its active kind — some WinUI 3 desktop configurations report
            // a stale/incorrect "current" kind even with Windows HDR enabled. The actual color space
            // switch is still validated at runtime by the swap chain.
            bool isSupported = available || kind == AdvancedColorKind.HighDynamicRange;

            return new HdrDisplayInfo(
                Kind: kind,
                IsSupported: isSupported,
                MaxLuminanceInNits: info.MaxLuminanceInNits,
                MinLuminanceInNits: info.MinLuminanceInNits,
                SdrWhiteLevelInNits: info.SdrWhiteLevelInNits);
        }
        catch (Exception e)
        {
            // Older OS / unsupported API: treat as SDR.
            Debug.WriteLine($"[HDR] WinRT detection failed: {e.Message}");

            return fallback;
        }
    }

    private void OnAdvancedColorInfoChanged(DisplayInformation sender, object args)
    {
        if (_disposed) return;

        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;

            Refresh();
        });
    }

    private void OnColorProfileChanged(DisplayInformation sender, object args)
    {
        if (_disposed) return;

        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;

            Refresh();
        });
    }

    /// <summary>
    /// Re-queries the display state and raises <see cref="Changed"/> when the snapshot differs.
    /// Must be called on the UI thread.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;

        HdrDisplayInfo updated = Query(_displayInformation);

        if (updated == _current)
        {
            return;
        }

        _current = updated;

        Changed?.Invoke(this, updated);
    }

    /// <summary>
    /// Unsubscribes from all display events. The instance must not be used afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        _displayInformation.AdvancedColorInfoChanged -= OnAdvancedColorInfoChanged;
        _displayInformation.ColorProfileChanged -= OnColorProfileChanged;
    }
}
