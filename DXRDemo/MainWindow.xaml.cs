using System;
using System.Diagnostics;
using DXRDemo.DXR;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DXRDemo;

public sealed partial class MainWindow : Window
{
    private DxrRenderer? _renderer;
    private DispatcherTimer? _fpsTimer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _frame;
    private int _fpsBaseline;
    private float _mouseX, _mouseY;
    private float _cameraDist = 6.5f;
    private bool _disposed;

    // XAML sizes are in DIPs; the swap chain / render target must be physical pixels.
    private double DpiScale => RenderPanel.XamlRoot?.RasterizationScale ?? 1.0;

    // SwapChainPanel displays swapchain buffer pixels as DIPs (WinUI bug #8219): a
    // physical-sized buffer overflows the DIP-sized panel and gets cropped. Workaround:
    // set the panel's DIP size to the physical pixel size (no crop, 1 buffer px = 1 px),
    // then counter-scale it visually by 1/DpiScale so it fits its layout slot again.
    private readonly ScaleTransform _panelScale = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "DXR Demo — Monte Carlo Path Tracer";
        RenderPanel.RenderTransform = _panelScale;

        Activated += OnFirstActivated;
        Closed += (_, _) => Dispose();

        // FPS readout (poll ~every 500ms); must NOT reset _frame, otherwise the
        // progressive accumulation weight and RNG seed cycle every 500ms (noise loops).
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, _) =>
        {
            if (_renderer != null && _renderer.Width > 0)
            {
                int fps = (int)((_frame - _fpsBaseline) / 0.5);
                FpsText.Text = $"FPS: {fps}  ({_renderer.Width}×{_renderer.Height})";
                _fpsBaseline = _frame;
            }
        };
        _fpsTimer.Start();

        // Pointer + wheel handlers for orbit camera + zoom
        RenderPanel.PointerMoved += OnPointerMoved;
        RenderPanel.PointerWheelChanged += OnPointerWheelChanged;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        TryInitializeRenderer();
    }

    private void TryInitializeRenderer()
    {
        if (_renderer != null)
            return;

        uint w = (uint)Math.Round(RootGrid.ActualWidth * DpiScale);
        uint h = (uint)Math.Round((RootGrid.ActualHeight - ToolbarHeight) * DpiScale);
        if (w == 0 || h == 0)
            return; // layout not done yet; OnRootSizeChanged will retry

        try
        {
            _renderer = new DxrRenderer();
            _renderer.Initialize(RenderPanel, w, h);
            RenderPanel.XamlRoot.Changed += OnXamlRootChanged;
            if (RootGrid.ActualWidth > 0 && RootGrid.ActualHeight > 0)
                SyncPanelAndBuffer(RootGrid.ActualWidth, RootGrid.ActualHeight);

            // Drive frames from the composition thread (sync with display refresh)
            CompositionTarget.Rendering += OnRendering;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            var inner = ex.InnerException;
            while (inner != null) { msg += $" → {inner.Message}"; inner = inner.InnerException; }
            StatusText.Text = $"Init failed: {msg}";
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (_renderer == null) return;

        float time = (float)_stopwatch.Elapsed.TotalSeconds;
        _renderer.Render(time, _mouseX, _mouseY, _cameraDist, _frame);
        _frame++;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(RenderPanel);
        if (p.Properties.IsLeftButtonPressed)
        {
            // Align with ComputeSharp SetMouse: store Y flipped (panelHeight - y).
            // The panel is sized in physical pixels, so its coordinates are already
            // buffer pixels — no DPI scaling needed here.
            _mouseX = (float)p.Position.X;
            _mouseY = (float)(RenderPanel.ActualHeight - p.Position.Y);
            _frame = 0; // reset progressive accumulation
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(RenderPanel);
        float delta = p.Properties.MouseWheelDelta / 120.0f;
        _cameraDist *= 1.0f - delta * 0.1f;
        _cameraDist = Math.Clamp(_cameraDist, 1.0f, 50.0f);
        _frame = 0;
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renderer == null)
        {
            TryInitializeRenderer();
            return;
        }

        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            SyncPanelAndBuffer(e.NewSize.Width, e.NewSize.Height);
    }

    // SizeChanged does not fire when only the DPI changes (e.g. moving across monitors
    // with different scaling); re-derive the physical size in that case.
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_renderer == null) return;
        if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0) return;
        SyncPanelAndBuffer(RootGrid.ActualWidth, RootGrid.ActualHeight);
        _frame = 0;
    }

    private double ToolbarHeight => RootGrid.RowDefinitions.Count > 0
        ? RootGrid.RowDefinitions[0].ActualHeight : 0;

    // Enlarge the panel host (Canvas) to the physical pixel size: the swapchain buffer
    // then maps 1:1 to panel DIPs so the full buffer is visible, and the panel is
    // counter-scaled by 1/DpiScale to fit the layout again. The Canvas is enlarged
    // instead of the SwapChainPanel, which always arranges itself at its slot size.
    private void SyncPanelAndBuffer(double rootWidth, double rootHeight)
    {
        double w = Math.Round(rootWidth * DpiScale);
        double h = Math.Round(Math.Max(1, rootHeight - ToolbarHeight) * DpiScale);
        if (w <= 0 || h <= 0) return;

        bool dirty = double.IsNaN(PanelHost.Width) || double.IsNaN(PanelHost.Height)
            || Math.Abs(PanelHost.Width - w) > 0.5 || Math.Abs(PanelHost.Height - h) > 0.5;
        if (dirty)
        {
            PanelHost.Width = w;
            PanelHost.Height = h;
        }
        if (double.IsNaN(RenderPanel.Width) || Math.Abs(RenderPanel.Width - w) > 0.5)
            RenderPanel.Width = w;
        if (double.IsNaN(RenderPanel.Height) || Math.Abs(RenderPanel.Height - h) > 0.5)
            RenderPanel.Height = h;
        double s = 1.0 / DpiScale;
        _panelScale.ScaleX = s;
        _panelScale.ScaleY = s;

        _renderer?.Resize(RenderPanel, (uint)w, (uint)h);
        _frame = 0; // reset progressive accumulation
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_fpsTimer != null)
        {
            _fpsTimer.Stop();
            _fpsTimer = null;
        }

        if (RenderPanel.XamlRoot != null)
            RenderPanel.XamlRoot.Changed -= OnXamlRootChanged;

        CompositionTarget.Rendering -= OnRendering;
        _renderer?.Dispose();
        _stopwatch.Stop();
    }
}