using DXRDemo.Scene;

namespace DXRDemo.Shaders;

/// <summary>场景渲染入口借用同一会话，UI 不依赖具体光传输算法。</summary>
public interface ISceneShaderPass : IShaderPass
{
    SceneSession Session { get; }
}
