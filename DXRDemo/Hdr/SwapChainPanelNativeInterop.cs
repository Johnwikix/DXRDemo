using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace DXRDemo.Hdr;

/// <summary>
/// COM interface used to attach an <c>IDXGISwapChain</c> to a <see cref="SwapChainPanel"/>.
/// Declared here because the package-provided interop is internal.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ISwapChainPanelNative
{
    /// <summary>
    /// IID of <c>ISwapChainPanelNative</c> for the WinUI 3 projection.
    /// </summary>
    public static readonly Guid IID = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    /// <summary>
    /// The vtable pointer of the current COM object.
    /// </summary>
    public void** lpVtbl;

    /// <summary>
    /// Calls <c>ISwapChainPanelNative::SetSwapChain</c> (vtable slot 3).
    /// </summary>
    public readonly int SetSwapChain(void* swapChain)
    {
        return ((delegate* unmanaged[MemberFunction]<ISwapChainPanelNative*, void*, int>)lpVtbl[3])((ISwapChainPanelNative*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)), swapChain);
    }

    /// <summary>
    /// Calls <c>IUnknown::Release</c> (vtable slot 2).
    /// </summary>
    public readonly uint Release()
    {
        return ((delegate* unmanaged[MemberFunction]<ISwapChainPanelNative*, uint>)lpVtbl[2])((ISwapChainPanelNative*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)));
    }
}

/// <summary>
/// Helpers to unwrap the native <see cref="ISwapChainPanelNative"/> object from a <see cref="SwapChainPanel"/>.
/// </summary>
internal static unsafe class SwapChainPanelNativeMarshaller
{
    /// <summary>
    /// Retrieves the underlying <see cref="ISwapChainPanelNative"/> object for the input panel.
    /// The returned pointer is an additional COM reference that must be released by the caller.
    /// </summary>
    public static ISwapChainPanelNative* GetNativeObject(SwapChainPanel panel)
    {
        if (((IWinRTObject)panel).NativeObject.TryAs(ISwapChainPanelNative.IID, out nint nativeObject) != 0)
        {
            throw new InvalidOperationException("Failed to obtain ISwapChainPanelNative from the SwapChainPanel.");
        }

        return (ISwapChainPanelNative*)nativeObject.ToPointer();
    }
}
