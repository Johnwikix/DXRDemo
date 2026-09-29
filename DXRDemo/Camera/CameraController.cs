using System.Numerics;

namespace DXRDemo.Camera;

/// <summary>Defines viewport navigation modes.</summary>
public enum CameraMode { Orbit, Fly }

/// <summary>Defines held movement keys, independent of keyboard repeat.</summary>
[Flags]
public enum CameraMovement { None = 0, Forward = 1, Backward = 2, Left = 4, Right = 8, Up = 16, Down = 32, Fast = 64 }

/// <summary>Provides one consistent, unjittered camera for tracing and reconstruction.</summary>
public readonly record struct CameraFrame(Vector3 Origin, Vector3 Forward, Vector3 Right, Vector3 Up,
    float VerticalFov, float Near, float Far)
{
    /// <summary>Creates the original demo camera for compatibility with diagnostic callers.</summary>
    public static CameraFrame FromOrbit(float yaw, float pitch, float distance)
    {
        Vector3 offset = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)) * distance;
        return Look(offset + new Vector3(0, .9f, 0), -Vector3.Normalize(offset), 2 * MathF.Atan(.5f), .01f, 1000);
    }

    /// <summary>Creates an orthonormal camera basis with world Y as the up axis.</summary>
    public static CameraFrame Look(Vector3 origin, Vector3 forward, float fov, float near, float far)
    {
        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, MathF.Abs(forward.Y) > .9999f ? Vector3.UnitZ : Vector3.UnitY));
        return new(origin, forward, right, Vector3.Cross(right, forward), fov, near, far);
    }
}

/// <summary>Integrates viewport input under a lock without per-frame heap allocations (.NET 10).</summary>
public sealed class CameraController
{
    private readonly object _gate = new();
    private Vector3 _target = new(0, .9f, 0), _resetTarget = new(0, .9f, 0), _position;
    private float _yaw, _pitch = .165f, _distance = 2.7f, _resetDistance = 2.7f, _resetYaw;
    private float _scale = 1, _near = .01f, _far = 1000, _speed = 1;
    private CameraMode _mode;
    private CameraMovement _movement;
    private int _revision, _cut;

    /// <summary>Initializes a camera at the demo's initial pose.</summary>
    public CameraController() => UpdatePosition();
    /// <summary>Gets or sets navigation mode while preserving the current view.</summary>
    public CameraMode Mode
    {
        get { lock (_gate) return _mode; }
        set { lock (_gate) { if (_mode == value) return; _mode = value; _target = _position + Forward() * _distance; _movement = 0; } }
    }
    /// <summary>Gets or sets movement speed as a multiplier of scene scale.</summary>
    public float Speed { get { lock (_gate) return _speed; } set { lock (_gate) _speed = Math.Clamp(value, .01f, 100); } }

    /// <summary>Stores the default framing for a scene and resets the camera.</summary>
    public void FrameBounds(Vector3 minimum, Vector3 maximum, float aspect = 16f / 9)
    {
        lock (_gate)
        {
            _resetTarget = (minimum + maximum) * .5f;
            _scale = MathF.Max(Vector3.Distance(minimum, maximum) * .5f, .001f);
            float halfFov = MathF.Min(MathF.Atan(.5f), MathF.Atan(.5f * MathF.Max(.1f, aspect)));
            _resetDistance = _scale / MathF.Sin(halfFov) * 1.1f;
            _resetYaw = 0; _near = MathF.Max(_scale * .0001f, .00001f); _far = MathF.Max(1000, _scale * 1000);
            ResetCore();
        }
    }

    /// <summary>Restores the original framing of a packaged demo.</summary>
    public void FrameDemo(int scene)
    {
        lock (_gate)
        {
            _resetTarget = new(0, .9f, 0); _resetDistance = scene == 3 ? 6.5f : 2.7f;
            _resetYaw = scene == 1 ? MathF.PI : 0; _scale = 1; _near = .01f; _far = 1000; ResetCore();
        }
    }

    /// <summary>Restores the current scene's initial framing and invalidates temporal history.</summary>
    public void Reset() { lock (_gate) ResetCore(); }
    private void ResetCore()
    {
        _target = _resetTarget; _distance = _resetDistance; _yaw = _resetYaw; _pitch = .165f;
        _movement = 0; UpdatePosition(); _revision++; _cut++;
    }
    private Vector3 Forward() => -new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));
    private void UpdatePosition() => _position = _target - Forward() * _distance;

    /// <summary>Applies a relative pointer delta in viewport-independent angular units.</summary>
    public void Rotate(float dx, float dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_gate)
        {
            _yaw = MathF.IEEERemainder(_yaw + dx, 2 * MathF.PI);
            _pitch = Math.Clamp(_pitch + dy, -1.55f, 1.55f);
            if (_mode == CameraMode.Orbit) UpdatePosition(); else _target = _position + Forward() * _distance;
            _revision++;
        }
    }

    /// <summary>Pans the orbit target by normalized screen displacement.</summary>
    public void Pan(float dx, float dy)
    {
        lock (_gate)
        {
            var frame = Frame(); Vector3 offset = (-frame.Right * dx + frame.Up * dy) * _distance;
            _target += offset; _position += offset; _revision++;
        }
    }

    /// <summary>Zooms the orbit or adjusts the free camera's movement speed.</summary>
    public void Zoom(float steps)
    {
        lock (_gate)
        {
            if (_mode == CameraMode.Fly) { _speed = Math.Clamp(_speed * MathF.Exp(steps * .1f), .01f, 100); return; }
            _distance = Math.Clamp(_distance * MathF.Exp(-steps * .1f), _scale * .001f, _scale * 1000);
            UpdatePosition(); _revision++;
        }
    }

    /// <summary>Updates held keys; clearing them prevents movement after focus loss.</summary>
    public void SetMovement(CameraMovement movement) { lock (_gate) _movement = movement; }

    /// <summary>Advances movement and copies a coherent frame without allocating.</summary>
    public CameraFrame Advance(double seconds, out int revision, out int cut)
    {
        lock (_gate)
        {
            var frame = Frame();
            if (_mode == CameraMode.Fly && _movement != 0)
            {
                float Axis(CameraMovement positive, CameraMovement negative) => ((_movement & positive) != 0 ? 1 : 0) - ((_movement & negative) != 0 ? 1 : 0);
                Vector3 motion = frame.Forward * Axis(CameraMovement.Forward, CameraMovement.Backward) +
                    frame.Right * Axis(CameraMovement.Right, CameraMovement.Left) + Vector3.UnitY * Axis(CameraMovement.Up, CameraMovement.Down);
                if (motion.LengthSquared() > 0)
                {
                    Vector3 offset = Vector3.Normalize(motion) * (float)Math.Clamp(seconds, 0, .1) * _scale * _speed * ((_movement & CameraMovement.Fast) != 0 ? 4 : 1);
                    _position += offset; _target += offset; _revision++; frame = Frame();
                }
            }
            revision = _revision; cut = _cut; return frame;
        }
    }
    private CameraFrame Frame() => CameraFrame.Look(_position, Forward(), 2 * MathF.Atan(.5f), _near, _far);
}
