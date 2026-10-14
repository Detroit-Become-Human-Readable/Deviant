using System.Numerics;

namespace Deviant.Desktop;

public sealed record ModelCameraState(Vector3 Position, Vector3 Target, Vector3 Up,
    string Projection = "Perspective", double FieldOfView = 45, double OrthographicWidth = 10);
