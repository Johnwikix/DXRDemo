using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.D3DCompiler.ShaderFlags;

namespace DXRDemo.Hdr;

/// <summary>
/// The fullscreen pass that copies the shader frame texture
/// (<c>R16G16B16A16_UNORM</c>, PQ or sRGB encoded by the shaders)
/// into the swap chain back buffer (<c>R10G10B10A2_UNORM</c>, 10-bit HDR10).
/// </summary>
internal sealed unsafe class HdrFullScreenPass : IDisposable
{
    /// <summary>
    /// HLSL source for the vertex and pixel shaders.
    /// Root signature: parameter 0 = descriptor table with one SRV (t0) plus a static sampler (s0).
    /// </summary>
    private const string ShaderSource =
        """
        Texture2D<float4> gFrameTexture : register(t0);
        SamplerState gFrameSampler : register(s0);

        void VSMain(uint vertexId : SV_VertexID, out float4 position : SV_Position, out float2 uv : TEXCOORD0)
        {
            float2 xy = float2((vertexId << 1) & 2, vertexId & 2);
            position = float4(xy * 2.0 - 1.0, 0.0, 1.0);
            uv = xy;
        }

        float4 PSMain(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            // The interpolated uv.y is 1 at the top of the viewport (NDC +Y is up),
            // but texture row 0 is the top of the frame written by the shaders, so
            // the vertical coordinate must be flipped to preserve the orientation.
            return gFrameTexture.Sample(gFrameSampler, float2(uv.x, 1.0 - uv.y));
        }
        """;

        private readonly byte[] _vsBytecode;
    private readonly byte[] _psBytecode;

    public HdrFullScreenPass(ID3D12Device device)
    {
        _vsBytecode = CompileShader("VSMain", "vs_5_0");
        _psBytecode = CompileShader("PSMain", "ps_5_0");

        RootSignature = CreateRootSignature(device);

        GraphicsPipelineStateDescription description = new()
        {
            RootSignature = RootSignature,
            VertexShader = _vsBytecode,
            PixelShader = _psBytecode,
            InputLayout = default,
            IndexBufferStripCutValue = IndexBufferStripCutValue.Disabled,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.R10G10B10A2_UNorm],
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
            BlendState = BlendDescription.Opaque,
            RasterizerState = new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
            },
            DepthStencilState = default,
            StreamOutput = default,
            NodeMask = 0,
            Flags = PipelineStateFlags.None,
        };

        PipelineState = device.CreateGraphicsPipelineState(description);
    }

    /// <summary>
    /// Gets the root signature shared by the vertex and pixel shaders.
    /// </summary>
    public ID3D12RootSignature RootSignature { get; }

    /// <summary>
    /// Gets the graphics pipeline state for the fullscreen pass.
    /// </summary>
    public ID3D12PipelineState PipelineState { get; }

    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.None,
            [
                new RootParameter1(
                    new RootDescriptorTable1(
                        [
                            new DescriptorRange1(
                                DescriptorRangeType.ShaderResourceView,
                                numDescriptors: 1,
                                baseShaderRegister: 0,
                                registerSpace: 0,
                                offsetInDescriptorsFromTableStart: 0,
                                flags: DescriptorRangeFlags.None),
                        ]),
                    ShaderVisibility.Pixel),
            ],
            [
                new StaticSamplerDescription(
                    shaderRegister: 0,
                    filter: Filter.MinMagMipLinear,
                    addressU: TextureAddressMode.Clamp,
                    addressV: TextureAddressMode.Clamp,
                    addressW: TextureAddressMode.Clamp,
                    mipLODBias: 0,
                    maxAnisotropy: 0,
                    comparisonFunction: ComparisonFunction.Never,
                    borderColor: StaticBorderColor.TransparentBlack,
                    minLOD: 0,
                    maxLOD: float.MaxValue,
                    shaderVisibility: ShaderVisibility.Pixel,
                    registerSpace: 0),
            ]);

        return device.CreateRootSignature(in description);
    }

    private static byte[] CompileShader(string entryPoint, string profile)
    {
        byte[] source = Encoding.UTF8.GetBytes(ShaderSource);

        fixed (byte* pSource = source)
        {
            Compiler.Compile(
                pSource,
                new PointerUSize((nuint)source.Length),
                "HdrPresentShader.hlsl",
                defines: null,
                include: null,
                entryPoint,
                profile,
                OptimizationLevel3,
                EffectFlags.None,
                out Blob shader,
                out Blob errorBlob);

            if (shader is null)
            {
                string errors = errorBlob is not null
                    ? ReadBlobText(errorBlob)
                    : "unknown compiler error";

                throw new InvalidOperationException($"HLSL compilation failed for {entryPoint} ({profile}): {errors}");
            }

            byte[] bytecode = new byte[(int)(ulong)shader.BufferSize];

            Marshal.Copy(shader.BufferPointer, bytecode, 0, bytecode.Length);

            return bytecode;
        }
    }

    private static string ReadBlobText(Blob blob)
    {
        ReadOnlySpan<byte> data = new(blob.BufferPointer.ToPointer(), (int)(ulong)blob.BufferSize);

        int length = data.IndexOf((byte)0);

        return Encoding.UTF8.GetString(data[..(length >= 0 ? length : data.Length)]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        PipelineState.Dispose();
        RootSignature.Dispose();
    }
}

