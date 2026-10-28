using System.Runtime.ExceptionServices;

namespace DXRDemo.Rendering;

/// <summary>仅供配置/关闭路径使用；单项清理失败后继续其余项，最后保留原始错误堆栈。</summary>
internal sealed class ResourceCleanup
{
    private Exception? _failure;
    internal void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) { _failure ??= error; }
    }
    internal void Release(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception error) { _failure ??= error; }
    }
    internal void Finish()
    {
        if (_failure != null)
            ExceptionDispatchInfo.Capture(_failure).Throw();
    }
}
