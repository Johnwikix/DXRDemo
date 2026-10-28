using System.Numerics;
using DXRDemo.Camera;
using Vortice.Direct3D12;

namespace DXRDemo.Rendering;

internal readonly record struct FrameRevisions(int Scene, int Lighting, int CameraCut, int Pipeline);

/// <summary>借用当前提交的线性 Rec.709 HDR 与 SR guides；资源进入/离开公共后处理时均为 NonPixelShaderResource。</summary>
internal readonly record struct LinearFrame(ID3D12Resource Color, ID3D12Resource Depth,
    ID3D12Resource Motion, ID3D12Resource Reactive, int Width, int Height,
    CameraFrame Camera, Vector2 Jitter, Vector2 PreviousJitter, double Seconds, bool Reset,
    bool NeedsNativeTaa = false, ID3D12Resource? NormalRoughness = null, FrameRevisions Revisions = default);
