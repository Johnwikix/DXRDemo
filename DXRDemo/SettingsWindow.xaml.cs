using Microsoft.UI.Windowing;
using WinUIEx;

namespace DXRDemo;

public sealed partial class SettingsWindow : WindowEx
{
    internal bool AllowClose { get; set; }
    public SettingsWindow()
    {
        InitializeComponent();
        Title = "DXR Demo · 设置";
        Width = 460; Height = 810;
        MinWidth = 380; MinHeight = 480;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Closing += (_, args) =>
        {
            if (!AllowClose) { args.Cancel = true; AppWindow.Hide(); }
        };
    }
}
