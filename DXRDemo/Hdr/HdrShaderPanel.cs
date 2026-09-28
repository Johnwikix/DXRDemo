using System.Numerics;
using ComputeSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DXRDemo.Hdr;

/// <summary>
/// A <see cref="SwapChainPanel"/> that renders animated compute shader frames with
/// optional HDR10 output, powered by <see cref="HdrSwapChainRenderer"/>.
/// </summary>
public sealed class HdrShaderPanel : SwapChainPanel, IDisposable
{
    private readonly HdrSwapChainRenderer _renderer;
    private readonly ScaleTransform _panelScale = new();
    private bool _isPaused;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="HdrShaderPanel"/> instance bound to the given GPU device.
    /// </summary>
    public HdrShaderPanel(GraphicsDevice device)
    {
        _renderer = new HdrSwapChainRenderer(this, device);

        // SwapChainPanel displays swapchain buffer pixels as DIPs (WinUI bug #8219):
        // the panel is sized to the physical pixel size (1 buffer px = 1 px, no crop)
        // and counter-scaled by 1/DpiScale so it fits its layout slot again. The scale
        // factor is updated by the host via <see cref="SetDpiScale"/>.
        RenderTransform = _panelScale;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// Raised (on the UI thread) whenever the render thread reports a failure.
    /// </summary>
    public event EventHandler<Exception>? RenderingFailed;

    /// <summary>
    /// Raised (on the UI thread) whenever the HDR state of the output currently hosting
    /// the window changes (monitor switch, display HDR toggle, ...).
    /// </summary>
    public event EventHandler? OutputCapabilitiesChanged;

    /// <summary>
    /// Gets whether the DXGI output currently hosting the window is HDR-capable
    /// (queried from the hardware).
    /// </summary>
    public bool IsCurrentOutputHdrCapable => _renderer.CurrentOutputHdrCapable;

    /// <summary>
    /// Gets the peak luminance (nits) of the current output, if available.
    /// </summary>
    public float CurrentOutputMaxLuminanceInNits => _renderer.CurrentOutputMaxLuminanceInNits;

    /// <summary>
    /// Gets or sets the shader runner producing the frames to display.
    /// </summary>
    public IHdrShaderRunner? ShaderRunner
    {
        get => _renderer.ShaderRunner;
        set => _renderer.ShaderRunner = value;
    }

    /// <summary>
    /// Gets or sets whether frame production is paused.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set { _isPaused = value; _renderer.IsPaused = value; }
    }

    /// <summary>
    /// Enables or disables HDR10 output (ST 2084 / BT.2020 color space).
    /// </summary>
    public bool ShowDiagnostics { get => _renderer.ShowDiagnostics; set => _renderer.ShowDiagnostics = value; }

    public bool IsHdrEnabled
    {
        get => _renderer.IsHdrEnabled;
        set => _renderer.SetHdrMode(value);
    }

    /// <summary>
    /// Sets the luminance mapping used for the HDR10 signal.
    /// </summary>
    /// <param name="sdrWhiteLevelInNits">Luminance (nits) that SDR white (1.0) maps to.</param>
    /// <param name="maxLuminanceInNits">Peak luminance (nits) of the display.</param>
    public void SetHdrParameters(float sdrWhiteLevelInNits, float maxLuminanceInNits)
        => _renderer.SetHdrParameters(sdrWhiteLevelInNits, maxLuminanceInNits);

    /// <summary>
    /// Applies the counter-scale that makes the physical-sized panel fit its layout slot.
    /// </summary>
    public void SetDpiScale(double dpiScale)
    {
        float scale = dpiScale > 0 ? (float)(1.0 / dpiScale) : 1;

        _panelScale.ScaleX = scale;
        _panelScale.ScaleY = scale;
    }

    /// <summary>
    /// Queues a resize of the render surface (physical pixels). Applies on the render thread.
    /// </summary>
    public void QueueResize(double width, double height)
        => _renderer.QueueResize(width, height);

    /// <summary>
    /// Records the window bounds (screen coordinates) used to determine the current DXGI output.
    /// </summary>
    public void SetWindowBounds(RectInt32 bounds)
        => _renderer.SetWindowBounds(bounds);

    /// <summary>
    /// Re-queries the DXGI outputs and updates the HDR state of the current output.
    /// </summary>
    public void RecheckOutput()
        => _renderer.RecheckOutput();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restart (idempotently) and re-queue the size in case the visual tree was
        // re-created (e.g. when the window moves between monitors with different DPI).
        _renderer.QueueResize(ActualWidth, ActualHeight);
        _renderer.StartRenderLoop();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Intentionally keep rendering: a composition swap chain presents safely even
        // while its panel is detached from the visual tree, and stopping the loop here
        // risks leaving rendering permanently stopped when Unloaded is not followed by
        // a Loaded (e.g. during monitor/DPI transitions).
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _renderer.QueueResize(e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>
    /// Forwards a render failure from the render thread to the UI thread.
    /// </summary>
    internal void OnRenderingFailed(Exception exception)
    {
        RenderingFailed?.Invoke(this, exception);
    }

    /// <summary>
    /// Forwards the DXGI output capability query result to the UI thread.
    /// </summary>
    internal void OnOutputCapabilitiesChanged()
    {
        OutputCapabilitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(null);
    }

    /// <summary>
    /// Disposes the panel and its renderer.
    /// </summary>
    /// <param name="beforeDeviceDispose">
    /// Optional callback invoked on the background teardown thread after the render thread has
    /// stopped and the GPU is idle, but before the <see cref="GraphicsDevice"/> is disposed
    /// (used to release runner resources that depend on the device).
    /// </param>
    public void Dispose(Action? beforeDeviceDispose)
    {
        if (_disposed) return;
        _disposed = true;

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;

        _renderer.Dispose(beforeDeviceDispose);
    }
}
