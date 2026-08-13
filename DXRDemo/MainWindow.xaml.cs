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

    public MainWindow()
    {
        InitializeComponent();
        Title = "DXR Demo — Monte Carlo Path Tracer";

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

        uint w = (uint)RenderPanel.ActualWidth;
        uint h = (uint)RenderPanel.ActualHeight;
        if (w == 0 || h == 0)
            return; // layout not done yet; OnRenderPanelSizeChanged will retry

        try
        {
            _renderer = new DxrRenderer();
            _renderer.Initialize(RenderPanel, w, h);

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

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renderer == null)
        {
            TryInitializeRenderer();
            return;
        }

        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _renderer.Resize(RenderPanel, (uint)e.NewSize.Width, (uint)e.NewSize.Height);
            _frame = 0; // reset progressive accumulation
        }
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

        CompositionTarget.Rendering -= OnRendering;
        _renderer?.Dispose();
        _stopwatch.Stop();
    }
}