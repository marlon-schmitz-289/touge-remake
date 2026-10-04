using System.Numerics;

namespace Kansei.Physics;

/// <summary>Result of <see cref="CarCollision.Resolve"/>: contact point, normal (from car a to car b), closing speed, overlap.</summary>
public readonly record struct CarContact(Vector3 Point, Vector3 Normal, float ImpactSpeed, float Depth);

/// <summary>
///     Car-to-car contact: each body is an oriented box in the XZ plane (yaw only — touge cars stay on their wheels, and
///     a box tilted with the road would only add roll impulses the arcade handling does not want), separated by SAT over
///     the 4 box axes. <see cref="Resolve"/> is swept: the relative motion of a tick is sampled in steps of a quarter car
///     width, so even a 200 km/h closing speed cannot pass one box through the other between two ticks. The overlap is
///     pushed apart by inverse mass, then one restitution impulse plus Coulomb friction at the contact point with the
///     full rigid-body effective mass (spins a car hit off-centre, as a PIT does).
/// </summary>
public static class CarCollision
{
    /// <summary>Bounce of a body-panel hit (bumpers crumple: little rebound) and paint-on-paint friction.</summary>
    public const float Restitution = 0.15f, Friction = 0.35f;

    /// <summary>Vertical reach of a body from its CoG: boxes further apart in height (a car on a bridge above) do not touch.</summary>
    public const float HalfHeight = 0.7f;

    /// <summary>Box on XZ: centre, unit right/forward axes, half width/half length, CoG height.</summary>
    public readonly record struct Box(Vector2 Centre, Vector2 Right, Vector2 Forward, Vector2 Half, float Y)
    {
        /// <summary>Half extent of the box along unit <paramref name="axis"/>.</summary>
        public float Radius(Vector2 axis) => MathF.Abs(Vector2.Dot(Right, axis)) * Half.X + MathF.Abs(Vector2.Dot(Forward, axis)) * Half.Y;

        public bool Contains(Vector2 p, float margin = 1e-3f)
        {
            var d = p - Centre;
            return MathF.Abs(Vector2.Dot(d, Right)) <= Half.X + margin && MathF.Abs(Vector2.Dot(d, Forward)) <= Half.Y + margin;
        }

        public Vector2 Corner(int i) => Centre + Right * (i is 0 or 3 ? Half.X : -Half.X) + Forward * (i < 2 ? Half.Y : -Half.Y);
    }

    public static Box BoxOf(Vehicle car) => BoxOf(car.Position, car.Orientation, car.Spec);

    public static Box BoxOf(Vector3 position, Quaternion orientation, CarSpec spec)
    {
        var f3 = Vector3.Transform(Vector3.UnitZ, orientation);
        var f = new Vector2(f3.X, f3.Z);
        f = f.LengthSquared() > 1e-6f ? Vector2.Normalize(f) : Vector2.UnitY; // nose straight up/down: any yaw
        return new Box(new Vector2(position.X, position.Z), new Vector2(f.Y, -f.X), f, new Vector2(spec.Width, spec.Length) / 2, position.Y);
    }

    /// <summary>
    ///     SAT: false when separated; else the axis of least overlap as <paramref name="normal"/> (unit, pointing from
    ///     <paramref name="a"/> to <paramref name="b"/>) and that overlap as <paramref name="depth"/>.
    /// </summary>
    public static bool Overlap(in Box a, in Box b, out Vector2 normal, out float depth)
    {
        (normal, depth) = (Vector2.Zero, float.MaxValue);
        if (MathF.Abs(a.Y - b.Y) > 2 * HalfHeight) return false;
        var d = b.Centre - a.Centre;
        Span<Vector2> axes = [a.Right, a.Forward, b.Right, b.Forward];
        foreach (var axis in axes)
        {
            var dist = Vector2.Dot(d, axis);
            var overlap = a.Radius(axis) + b.Radius(axis) - MathF.Abs(dist);
            if (overlap <= 0) return false;
            if (overlap < depth) (depth, normal) = (overlap, dist < 0 ? -axis : axis);
        }
        return true;
    }

    /// <summary>Where the boxes touch: the mean of the corners of each lying inside the other, else halfway between the faces along <paramref name="normal"/>.</summary>
    public static Vector2 ContactPoint(in Box a, in Box b, Vector2 normal)
    {
        var sum = Vector2.Zero;
        var n = 0;
        for (var i = 0; i < 4; i++)
        {
            if (b.Contains(a.Corner(i))) (sum, n) = (sum + a.Corner(i), n + 1);
            if (a.Contains(b.Corner(i))) (sum, n) = (sum + b.Corner(i), n + 1);
        }
        if (n > 0) return sum / n;
        // edge crossing edge: the midpoint between the two supporting faces, on the line between the centres
        var faceA = a.Centre + normal * a.Radius(normal);
        var faceB = b.Centre - normal * b.Radius(normal);
        return (faceA + faceB) / 2;
    }

    /// <summary>
    ///     After both cars stepped from (<paramref name="prevA"/>, <paramref name="prevRotA"/>) and (<paramref name="prevB"/>,
    ///     <paramref name="prevRotB"/>) to their current pose: finds the first overlap along the tick's motion, separates the
    ///     current poses along that contact normal (also when the motion went right through) and applies the contact impulse.
    ///     Null when they did not touch.
    /// </summary>
    public static CarContact? Resolve(Vehicle a, Vehicle b, Vector3 prevA, Quaternion prevRotA, Vector3 prevB, Quaternion prevRotB)
    {
        var relMove = (b.Position - prevB) - (a.Position - prevA);
        var step = 0.25f * MathF.Min(a.Spec.Width, b.Spec.Width);
        var steps = Math.Clamp((int)MathF.Ceiling(new Vector2(relMove.X, relMove.Z).Length() / step), 1, 64);
        Vector2 normal = default;
        var found = false;
        for (var k = 1; k <= steps && !found; k++)
        {
            var t = (float)k / steps;
            var ba = BoxOf(Vector3.Lerp(prevA, a.Position, t), Quaternion.Slerp(prevRotA, a.Orientation, t), a.Spec);
            var bb = BoxOf(Vector3.Lerp(prevB, b.Position, t), Quaternion.Slerp(prevRotB, b.Orientation, t), b.Spec);
            found = Overlap(ba, bb, out normal, out _);
        }
        if (!found) return null;

        // separation along the found normal at the current poses; d < 0 = b went through a to the other side
        var boxA = BoxOf(a);
        var boxB = BoxOf(b);
        var depth = boxA.Radius(normal) + boxB.Radius(normal) - Vector2.Dot(boxB.Centre - boxA.Centre, normal);
        var point2 = ContactPoint(boxA, boxB, normal);
        var n = new Vector3(normal.X, 0, normal.Y);
        if (depth > 0)
        {
            // apart by inverse mass, 2 mm beyond touching so rounding leaves no overlap for the next tick's sweep
            float ma = a.Spec.Mass, mb = b.Spec.Mass, push = depth + 0.002f;
            a.Translate(-n * (push * mb / (ma + mb)));
            b.Translate(n * (push * ma / (ma + mb)));
        }
        // impulse at the contact point, at the bodies' mean CoG height: no roll/pitch kick from the contact
        var point = new Vector3(point2.X, (a.Position.Y + b.Position.Y) / 2, point2.Y);
        var vn = Vector3.Dot(b.VelocityAt(point) - a.VelocityAt(point), n);
        var impact = MathF.Max(-vn, 0);
        if (vn < 0)
        {
            var j = -(1 + Restitution) * vn / (a.InverseMassAt(point, n) + b.InverseMassAt(point, n));
            a.ApplyImpulseAt(point, -n * j);
            b.ApplyImpulseAt(point, n * j);
            // Coulomb friction on the horizontal sliding velocity, at most what stops the sliding
            var vr = b.VelocityAt(point) - a.VelocityAt(point);
            var vt = vr - n * Vector3.Dot(vr, n);
            vt.Y = 0;
            var len = vt.Length();
            if (len > 1e-4f)
            {
                var t = vt / len;
                var jt = MathF.Min(Friction * j, len / (a.InverseMassAt(point, t) + b.InverseMassAt(point, t)));
                a.ApplyImpulseAt(point, t * jt);
                b.ApplyImpulseAt(point, -t * jt);
            }
        }
        return new CarContact(point, n, impact, MathF.Max(depth, 0));
    }
}
