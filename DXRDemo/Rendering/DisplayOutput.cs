using System.Numerics;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using Vortice.Direct3D12;

namespace DXRDemo.Rendering;

/// <summary>两条管线唯一的曝光、色调映射与 SDR/HDR10 编码阶段。</summary>
internal sealed unsafe class DisplayOutput : GpuResourceOwner
{
    private readonly ComputePass _pass;
    private readonly ID3D12Resource _constants;
    internal DisplayOutput(DxrDevice device)
    {
        try
        {
            _pass = Keep(new ComputePass(device.Device, "DisplayOutput.hlsl", "Encode", 1, 1));
            _constants = Keep(DxrResources.CreateUploadBuffer(device.Device, 256));
        }
        catch { Dispose(); throw; }
    }
    internal void Record(ID3D12GraphicsCommandList4 commands, ID3D12Resource color, GpuTexture<Rgba64> target,
        in HdrRenderParameters hdr, float exposure, bool toneMap = true)
    {
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError();
        ((Vector4*)pointer)[0] = new(target.Width, target.Height, toneMap ? 1 : 0, exposure);
        ((Vector4*)pointer)[1] = new(hdr.IsHdrEnabled ? 1 : 0, hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits, 0);
        _constants.Unmap(0);
        _pass.Srv(0, color);
        _pass.Uav(0, target.Resource);
        _pass.Dispatch(commands, _constants, target.Width, target.Height);
        commands.ResourceBarrierUnorderedAccessView(target.Resource);
    }
}
