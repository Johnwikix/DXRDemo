using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.DXR;

public sealed class DxrSwapChain : IDisposable
{
    public IDXGISwapChain1 SwapChain { get; private set; } = null!;
    private IDXGISwapChain3 SwapChain3 = null!;
    public uint Width  { get; private set; }
    public uint Height { get; private set; }

    public void Initialize(
        IDXGIFactory7 factory,
        ID3D12CommandQueue commandQueue,
        SwapChainPanel panel,
        uint width,
        uint height)
    {
        Width = width;
        Height = height;

        var desc = new SwapChainDescription1(
            width: width,
            height: height,
            format: Format.R8G8B8A8_UNorm,
            stereo: false,
            bufferUsage: Usage.RenderTargetOutput,
            bufferCount: 2,
            scaling: Scaling.Stretch,
            swapEffect: SwapEffect.FlipSequential,
            alphaMode: AlphaMode.Ignore);

        SwapChain = factory.CreateSwapChainForComposition(commandQueue, desc, null);
        SwapChain3 = SwapChain.QueryInterface<IDXGISwapChain3>();

        BindToPanel(panel);
    }

    private void BindToPanel(SwapChainPanel panel)
    {
        // WinUI 3's Microsoft.UI.Xaml.Media.DxInterop.ISwapChainPanelNative IID.
        // (Distinct from the UWP Windows.UI.Xaml one, which is f92f19d2-3ade-45a6-a20c-f6f1ea90554b.)
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");

        // CsWinRT projected objects expose their real COM identity via IWinRTObject;
        // Marshal.GetIUnknownForObject returns the CLR CCW IUnknown, which cannot QI for
        // the native ISwapChainPanelNative. ThisPtr is a borrowed reference (no Release).
        IntPtr panelUnknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;

        int hr = Marshal.QueryInterface(panelUnknown, iid, out IntPtr nativePtr);
        if (hr != 0 || nativePtr == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to query ISwapChainPanelNative (hr=0x{hr:X8}).");

        // GetTypedObjectForIUnknown hands the QI reference to the RCW, which releases it
        // when finalized. Do NOT Marshal.Release(nativePtr) here - that would double-release.
        var native = (ISwapChainPanelNative)Marshal.GetTypedObjectForIUnknown(
            nativePtr, typeof(ISwapChainPanelNative));
        int setHr = native.SetSwapChain(SwapChain.NativePointer);
        if (setHr != 0)
            throw new InvalidOperationException(
                $"SetSwapChain failed (hr=0x{setHr:X8}).");
    }

    public void Resize(IDXGIFactory7 factory, ID3D12CommandQueue commandQueue, SwapChainPanel panel, uint width, uint height)
    {
        SwapChain?.Dispose();
        SwapChain3?.Dispose();
        Width = width;
        Height = height;

        var desc = new SwapChainDescription1(
            width: width,
            height: height,
            format: Format.R8G8B8A8_UNorm,
            bufferCount: 2,
            scaling: Scaling.Stretch,
            swapEffect: SwapEffect.FlipSequential,
            alphaMode: AlphaMode.Ignore);

        SwapChain = factory.CreateSwapChainForComposition(commandQueue, desc, null);
        SwapChain3 = SwapChain.QueryInterface<IDXGISwapChain3>();

        BindToPanel(panel);
    }

    public ID3D12Resource GetCurrentBackBuffer()
    {
        // Flip-model swap chains rotate which buffer is the current back buffer after each
        // Present; writing to a non-current buffer triggers device removal (DXGI_ERROR_ACCESS_DENIED).
        return SwapChain.GetBuffer<ID3D12Resource>(SwapChain3.CurrentBackBufferIndex);
    }

    public void Present()
    {
        SwapChain.Present(1, PresentFlags.None).CheckError();
    }

    public void Dispose()
    {
        SwapChain3?.Dispose();
        SwapChain?.Dispose();
    }
}

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain);
}