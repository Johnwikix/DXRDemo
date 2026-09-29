using ComputeSharp;
using DXRDemo.Hdr;
using DXRDemo.Shaders;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinUIEx;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;
using Microsoft.UI;

namespace DXRDemo;

public sealed partial class MainWindow : WindowEx
{
    private readonly SettingsWindow _settings = new();
    private readonly HashSet<IShaderPass> _initializedPasses = [];
    private readonly DispatcherTimer _recheckTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _renderScaleTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _pendingRenderScale = 67;
    private ReconstructionStatus? _lastReconstructionStatus;
    private ComboBox ShaderSelector => _settings.ShaderSelector;
    private ComboBox ModelSelector => _settings.ModelSelector;
    private ComboBox DenoiserSelector => _settings.DenoiserSelector;
    private NumberBox MaxBouncesBox => _settings.MaxBouncesBox;
    private NumberBox SamplesBox => _settings.SamplesBox;
    private FrameworkElement RayTraceParamBar => _settings.RayTraceParamBar;
    private ToggleSwitch HdrToggle => _settings.HdrToggle;
    private TextBlock HdrStatusText => _settings.HdrStatusText;
    private GraphicsDevice _device = null!;
    private ShaderFactory _factory = null!;
    private HdrShaderPanel _shaderPanel = null!;
    private HdrDisplayInfoTracker? _hdrTracker;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private IShaderPass? _activePass;
    private bool _hdrAutoEnabled;
    private bool _hdrDetectionInitialized;
    private bool _layoutInitialized;
    private bool _disposed;
    private bool _syncingRayTraceParams;

    // XAML sizes are in DIPs; the swap chain / render target must be physical pixels.
    private double DpiScale => RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DXR Demo";
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.ResizeClient(new SizeInt32(1280, 720));
        MinWidth = 480; MinHeight = 320;
        ShaderSelector.SelectionChanged += OnShaderSelected;
        DenoiserSelector.SelectionChanged += OnDenoiserSelected;
        MaxBouncesBox.ValueChanged += OnMaxBouncesChanged;
        SamplesBox.ValueChanged += OnSamplesChanged;
        _settings.ReconstructionSelector.SelectionChanged += OnReconstructionChanged;
        _settings.RenderScaleSlider.ValueChanged += OnRenderScaleChanged;
        _renderScaleTimer.Tick += (_, _) =>
        {
            _renderScaleTimer.Stop();
            if (_activePass is RayTracePass pass) pass.RenderScalePercent = _pendingRenderScale;
        };
        HdrToggle.Toggled += OnHdrToggled;
        ModelSelector.SelectionChanged += (_, _) =>
        {
            if (!_syncingRayTraceParams && ModelSelector.SelectedIndex >= 0 && _activePass is RayTracePass pass)
                pass.SceneIndex = ModelSelector.SelectedIndex;
        };
        _settings.ResetCameraButton.Click += (_, _) => { if (_activePass is RayTracePass pass) pass.SceneIndex = pass.SceneIndex; };
        _settings.DiagnosticsToggle.Toggled += (_, _) => { if (_shaderPanel != null) _shaderPanel.ShowDiagnostics = _settings.DiagnosticsToggle.IsOn; };
        _settings.ResolutionButton.Click += (_, _) => AppWindow.ResizeClient(new SizeInt32(1280, 720));
        // Create the GPU device and shader panel
        _device = GraphicsDevice.GetDefault();
        _factory = new ShaderFactory();

        _shaderPanel = new HdrShaderPanel(_device);
        PanelHost.Children.Add(_shaderPanel);

        // Populate shader selector
        ShaderSelector.ItemsSource = ShaderFactory.Catalog;
        ShaderSelector.SelectedIndex = 0;

        // Mouse tracking
        _shaderPanel.PointerMoved += OnPointerMoved;
        _shaderPanel.PointerWheelChanged += OnPointerWheelChanged;
        _shaderPanel.SizeChanged += OnShaderPanelSizeChanged;
        _shaderPanel.RenderingFailed += OnRenderingFailed;
        _shaderPanel.OutputCapabilitiesChanged += OnOutputCapabilitiesChanged;

        // Track window moves/resizes so the HDR state of the current display stays accurate
        // (multi-monitor setups with mixed HDR/SDR outputs).
        AppWindow.Changed += OnAppWindowChanged;

        // Safety-net output recheck (HDR state can change without a window event)
        _recheckTimer.Tick += (_, _) => { UpdateWindowBoundsAndRecheckOutput(); RefreshReconstructionUi(); };
        _recheckTimer.Start();

        // HDR detection is deferred until the window is activated: DisplayInformation
        // is not reliably available while the window is still being constructed.
        Activated += OnWindowActivated;

        // Initial toolbar state (refreshed once detection + the DXGI output query complete)
        HdrStatusText.Text = "HDR: 检测中…";
        HdrToggle.IsEnabled = false;
        ApplyHdrMode();

        // Cleanup
        Closed += (_, _) => Dispose();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_hdrDetectionInitialized) return;
        _hdrDetectionInitialized = true;

        // Detect HDR support and keep tracking display changes (monitor switch, Windows HDR toggle...)
        try
        {
            _hdrTracker = HdrDisplayInfoTracker.Create();
            _hdrTracker.Changed += OnHdrStateChanged;
        }
        catch (Exception ex)
        {
            _hdrTracker = null;

            Debug.WriteLine($"[HDR] DisplayInformation unavailable: {ex.Message}");
        }

        // Track DPI/monitor changes (SizeChanged does not fire for DPI-only changes)
        if (RootGrid.XamlRoot is XamlRoot xamlRoot)
        {
            xamlRoot.Changed += OnXamlRootChanged;
        }

        TrySyncPanelAndBuffer();
        UpdateHdrUi();
    }

    private void OnShaderSelected(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is ShaderAuthoringInfo info)
        {
            SwitchShader(info);
        }
    }

    private void SwitchShader(ShaderAuthoringInfo info)
    {
        // Cached passes stay alive until the renderer has stopped. Never dispose GPU resources on this UI thread.

        var pass = _factory.GetOrCreate(info.Id);
        Int2 size = default;

        if (_shaderPanel.ActualWidth > 0 && _shaderPanel.ActualHeight > 0)
        {
            size = new Int2((int)_shaderPanel.ActualWidth, (int)_shaderPanel.ActualHeight);
        }

        if (_initializedPasses.Add(pass)) pass.Initialize(_device, size);
        pass.OnResize(size);

        _shaderPanel.ShaderRunner = pass;
        _shaderPanel.IsPaused = false;
        _activePass = pass;

        _settings.ErrorText.Text = string.Empty;

        if (pass is RayTracePass rayTracePass)
        {
            SyncRayTraceParams(rayTracePass);
        }
        else
        {
            RayTraceParamBar.Visibility = Visibility.Collapsed;
        }

        // The parameter bar visibility changes the canvas slot height without resizing
        // RootGrid itself, so re-sync the panel/buffer once the pending layout pass has
        // updated the row heights.
        DispatcherQueue.TryEnqueue(() => SafeTry(TrySyncPanelAndBuffer));
    }

    // Populates the ray-trace parameter bar from the pass state. The sync flag keeps the
    // control change handlers from writing back (and resetting the frame counter) while
    // the controls are being initialized.
    private void SyncRayTraceParams(RayTracePass pass)
    {
        _syncingRayTraceParams = true;
        try
        {
            RayTraceParamBar.Visibility = Visibility.Visible;
            if (!ReferenceEquals(ModelSelector.ItemsSource, pass.Scenes)) ModelSelector.ItemsSource = pass.Scenes;
            ModelSelector.SelectedIndex = pass.SceneIndex;
            DenoiserSelector.SelectedIndex = (int)pass.DenoiserMode;
            MaxBouncesBox.Value = pass.MaxBounces;
            SamplesBox.Value = pass.Samples;
            _settings.ReconstructionSelector.SelectedIndex = pass.ReconstructionMode switch
            {
                ReconstructionMode.Fsr => 1, ReconstructionMode.XeSS => 2, ReconstructionMode.Dlss => 3, _ => 0
            };
            _settings.RenderScaleSlider.Value = pass.RenderScalePercent;
        }
        finally
        {
            _syncingRayTraceParams = false;
        }
    }

    private void OnReconstructionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRayTraceParams || _activePass is not RayTracePass pass) return;
        pass.ReconstructionMode = _settings.ReconstructionSelector.SelectedIndex switch
        {
            1 => ReconstructionMode.Fsr, 2 => ReconstructionMode.XeSS, 3 => ReconstructionMode.Dlss, _ => ReconstructionMode.Off
        };
        _settings.RenderScaleSlider.IsEnabled = pass.ReconstructionMode != ReconstructionMode.Off;
        _settings.ReconstructionStatusText.Text = "正在应用超分设置；首次启用需要初始化 SDK。";
    }

    private void OnRenderScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingRayTraceParams || double.IsNaN(e.NewValue)) return;
        _pendingRenderScale = (int)Math.Round(e.NewValue);
        _renderScaleTimer.Stop();
        _renderScaleTimer.Start();
    }

    private void RefreshReconstructionUi()
    {
        if (_activePass is not RayTracePass pass || pass.ReconstructionStatus is not ReconstructionStatus status ||
            ReferenceEquals(_lastReconstructionStatus, status)) return;
        _lastReconstructionStatus = status;
        _settings.ReconstructionSelector.IsEnabled = true;
        ((ComboBoxItem)_settings.ReconstructionSelector.Items[1]).IsEnabled = status.Supports(ReconstructionMode.Fsr);
        ((ComboBoxItem)_settings.ReconstructionSelector.Items[2]).IsEnabled = status.Supports(ReconstructionMode.XeSS);
        ((ComboBoxItem)_settings.ReconstructionSelector.Items[3]).IsEnabled = status.Supports(ReconstructionMode.Dlss);
        ((ComboBoxItem)_settings.DenoiserSelector.Items[3]).IsEnabled = status.NrdAvailable;
        _settings.RenderScaleSlider.IsEnabled = pass.ReconstructionMode != ReconstructionMode.Off;
        _settings.ReconstructionStatusText.Text = status.Message;
    }

    private void OnDenoiserSelected(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingRayTraceParams || _activePass is not RayTracePass pass) return;

        pass.DenoiserMode = (RayTraceDenoiserMode)DenoiserSelector.SelectedIndex;
    }

    private void OnMaxBouncesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingRayTraceParams || _activePass is not RayTracePass pass || double.IsNaN(args.NewValue)) return;

        pass.MaxBounces = (int)Math.Round(args.NewValue);
    }

    private void OnSamplesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingRayTraceParams || _activePass is not RayTracePass pass || double.IsNaN(args.NewValue)) return;

        pass.Samples = (int)Math.Round(args.NewValue);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activePass?.Capabilities.HasFlag(ShaderCapabilities.UsesMouse) == true)
        {
            var pointerPoint = e.GetCurrentPoint(_shaderPanel);

            // The panel is sized in physical pixels, so its coordinates are already
            // buffer pixels — no DPI scaling needed.
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                _activePass.SetMouse(
                    (float)pointerPoint.Position.X,
                    (float)pointerPoint.Position.Y,
                    (float)_shaderPanel.ActualWidth,
                    (float)_shaderPanel.ActualHeight);
            }
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_activePass is not null)
        {
            var pointerPoint = e.GetCurrentPoint(_shaderPanel);
            float delta = (float)pointerPoint.Properties.MouseWheelDelta / 120.0f;
            _activePass.SetZoom(delta);
        }
    }

    private void OnShaderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activePass is null) return;

        var size = new Int2((int)e.NewSize.Width, (int)e.NewSize.Height);
        _activePass.OnResize(size);
    }

    // Re-evaluates HDR whenever the display configuration changes (HDR toggle in
    // Windows settings, monitor switch, ...).
    private void OnHdrStateChanged(object? sender, HdrDisplayInfo info)
    {
        UpdateHdrUi();
    }

    private void OnOutputCapabilitiesChanged(object? sender, EventArgs e)
    {
        UpdateHdrUi();
    }

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        ApplyHdrMode();
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SafeTry(() => TrySyncPanelAndBuffer());
    }

    // Fires on DPI/monitor changes (moving across monitors with different scaling):
    // re-derive the physical buffer size and re-evaluate the HDR state of the new display.
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        SafeTry(() =>
        {
            TrySyncPanelAndBuffer();
            UpdateWindowBoundsAndRecheckOutput();
        });
    }

    // Fires on window position/size changes: keep the current-output HDR state accurate.
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        SafeTry(() =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                UpdateWindowBoundsAndRecheckOutput();
            }

            if (args.DidSizeChange)
            {
                TrySyncPanelAndBuffer();
            }
        });
    }

    // Event handlers must never break the event chain on a single exception.
    private void SafeTry(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[HDR] UI handler failed: {e.Message}");
        }
    }

    // Merges the WinRT display detection and the DXGI hardware query into a single
    // effective HDR capability snapshot.
    private HdrDisplayInfo GetEffectiveHdrInfo()
    {
        HdrDisplayInfo info = _hdrTracker?.Current ?? HdrDisplayInfoTracker.Unsupported;

        return new HdrDisplayInfo(
            Kind: info.Kind,
            IsSupported: info.IsSupported || _shaderPanel.IsCurrentOutputHdrCapable,
            MaxLuminanceInNits: info.MaxLuminanceInNits > 0 ? info.MaxLuminanceInNits : _shaderPanel.CurrentOutputMaxLuminanceInNits,
            MinLuminanceInNits: info.MinLuminanceInNits,
            SdrWhiteLevelInNits: info.SdrWhiteLevelInNits);
    }

    // Refreshes the toolbar state and applies the HDR mode.
    private void UpdateHdrUi()
    {
        if (_shaderPanel is null) return;

        HdrDisplayInfo effective = GetEffectiveHdrInfo();

        HdrStatusText.Text = effective.StatusText;
        HdrToggle.IsEnabled = effective.IsSupported;

        // Auto-enable HDR the first time it becomes available; the user can still
        // toggle it afterwards.
        if (effective.IsSupported && !_hdrAutoEnabled)
        {
            _hdrAutoEnabled = true;
            HdrToggle.IsOn = true;
        }

        ApplyHdrMode();
    }

    // Applies the current toggle + detection state to the rendering pipeline.
    private void ApplyHdrMode()
    {
        if (_shaderPanel is null) return;

        HdrDisplayInfo effective = GetEffectiveHdrInfo();
        bool enabled = HdrToggle.IsOn && effective.IsSupported;

        _shaderPanel.SetHdrParameters(
            effective.SdrWhiteLevelInNits > 0 ? effective.SdrWhiteLevelInNits : 200,
            effective.MaxLuminanceInNits > 0 ? effective.MaxLuminanceInNits : 1000);

        _shaderPanel.IsHdrEnabled = enabled;
    }

    // SwapChainPanel displays swapchain buffer pixels as DIPs (WinUI bug #8219): the panel
    // host is sized to the physical pixel size (1 buffer px = 1 px, no crop), and the panel
    // is counter-scaled by 1/DpiScale so it fits its layout slot again.
    private void TrySyncPanelAndBuffer()
    {
        SafeTry(() =>
        {
            if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
            {
                return;
            }

            double dpiScale = DpiScale;
            double contentWidth = RootGrid.ActualWidth;
            double contentHeight = Math.Max(1, RootGrid.ActualHeight);

            double w = Math.Round(contentWidth * dpiScale);
            double h = Math.Round(contentHeight * dpiScale);
            if (w <= 0 || h <= 0)
            {
                return;
            }

            bool dirty = double.IsNaN(PanelHost.Width) || double.IsNaN(PanelHost.Height)
                || Math.Abs(PanelHost.Width - w) > 0.5 || Math.Abs(PanelHost.Height - h) > 0.5;

            if (dirty)
            {
                PanelHost.Width = w;
                PanelHost.Height = h;
            }

            if (double.IsNaN(_shaderPanel.Width) || Math.Abs(_shaderPanel.Width - w) > 0.5)
            {
                _shaderPanel.Width = w;
            }

            if (double.IsNaN(_shaderPanel.Height) || Math.Abs(_shaderPanel.Height - h) > 0.5)
            {
                _shaderPanel.Height = h;
            }

            _shaderPanel.SetDpiScale(dpiScale);
            _shaderPanel.QueueResize(w, h);

            _layoutInitialized = true;
        });
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => _settings.Activate();
    private void OnSettingsShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        _settings.Activate(); e.Handled = true;
    }

    // Keeps the current-output HDR state in sync with the window position (multi-monitor).
    private void UpdateWindowBoundsAndRecheckOutput()
    {
        SafeTry(() =>
        {
            if (!_layoutInitialized) return;

            PointInt32 position = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;

            if (size.Width > 0 && size.Height > 0)
            {
                _shaderPanel.SetWindowBounds(new RectInt32(position.X, position.Y, size.Width, size.Height));
            }

            _shaderPanel.RecheckOutput();
        });
    }

    private void OnRenderingFailed(object? sender, Exception e)
    {
        // Do NOT clear the shader runner: the render loop is resilient to transient
        // errors (resizes, presents), so a single failure must not stop rendering.
        Debug.WriteLine($"Rendering failed: {e}");
        _settings.ErrorText.Text = e.ToString();
        _settings.Activate();
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recheckTimer.Stop();
        _renderScaleTimer.Stop();
        _settings.AllowClose = true;
        _settings.Close();
        _hdrTracker?.Dispose();

        if (RootGrid.XamlRoot is XamlRoot xamlRoot)
        {
            xamlRoot.Changed -= OnXamlRootChanged;
        }

        AppWindow.Changed -= OnAppWindowChanged;

        // Pass resources and the HDR tracker are released by the renderer's background
        // teardown: after the render thread has exited and the GPU is idle, but before
        // the GraphicsDevice itself is disposed (it is disposed by the renderer, last).
        _shaderPanel.Dispose(() =>
        {
            _factory.Dispose();

        });

        _stopwatch.Stop();
    }
}
