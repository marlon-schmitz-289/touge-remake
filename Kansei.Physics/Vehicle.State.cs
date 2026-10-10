using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     The car's complete simulation state (body, engine, gearbox, clutch, steering, drift and every wheel) as bytes:
///     restored into a car of the same spec, the next <see cref="Step"/> with the same input gives bit-identical results
///     (replay keyframes, rewinding). Not stored: spec, surface grip function (both come with the car).
/// </summary>
public sealed partial class Vehicle
{
    /// <summary>Bytes of one <see cref="Save"/>.</summary>
    public const int StateBytes = 4 * (3 + 4 + 3 + 3 + 1 + 1 + 1 + 1 + 1 + 3 + 3 + 1) + 4 * 9 + 6 + 4 * WheelBytes;

    private const int WheelBytes = 4 * 11 + 1;

    public void Save(BinaryWriter w)
    {
        W(w, Position);
        w.Write(Orientation.X); w.Write(Orientation.Y); w.Write(Orientation.Z); w.Write(Orientation.W);
        W(w, Velocity);
        W(w, AngularVelocity);
        w.Write(Rpm); w.Write(Gear); w.Write(Throttle); w.Write(SlipAngle); w.Write(WallContacts);
        W(w, WallPoint);
        W(w, WallNormal);
        w.Write(WallImpactSpeed);
        w.Write(_steer); w.Write(_clutchPedal); w.Write(_shiftTimer); w.Write(_rearGrip); w.Write(_prevBeta); w.Write(_clutch); w.Write(_gearRpm);
        w.Write(_sideForce[0]); w.Write(_sideForce[1]);
        w.Write(_drifting); w.Write(_locked); w.Write(_shifting); w.Write(_shiftDown); w.Write(AutomaticGearbox); w.Write((byte)0);
        foreach (var x in _wheels)
        {
            W(w, x.LocalCenter);
            w.Write(x.Contact);
            w.Write(x.Surface); w.Write(x.Compression); w.Write(x.SteerAngle); w.Write(x.SpinAngle); w.Write(x.AngularVelocity);
            w.Write(x.Load); w.Write(x.SlipRatio); w.Write(x.SlipAngle);
        }
    }

    public void Load(BinaryReader r)
    {
        Position = V(r);
        Orientation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        Velocity = V(r);
        AngularVelocity = V(r);
        (Rpm, Gear, Throttle, SlipAngle, WallContacts) = (r.ReadSingle(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32());
        (WallPoint, WallNormal, WallImpactSpeed) = (V(r), V(r), r.ReadSingle());
        (_steer, _clutchPedal, _shiftTimer, _rearGrip, _prevBeta, _clutch, _gearRpm) =
            (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        (_sideForce[0], _sideForce[1]) = (r.ReadSingle(), r.ReadSingle());
        (_drifting, _locked, _shifting, _shiftDown, AutomaticGearbox) = (r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean());
        r.ReadByte();
        for (var i = 0; i < 4; i++)
        {
            ref var x = ref _wheels[i];
            x.LocalCenter = V(r);
            x.Contact = r.ReadBoolean();
            (x.Surface, x.Compression, x.SteerAngle, x.SpinAngle, x.AngularVelocity) = (r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            (x.Load, x.SlipRatio, x.SlipAngle) = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }

    public byte[] SaveState()
    {
        using var ms = new MemoryStream(StateBytes);
        using (var w = new BinaryWriter(ms)) Save(w);
        return ms.ToArray();
    }

    public void LoadState(byte[] state)
    {
        using var r = new BinaryReader(new MemoryStream(state));
        Load(r);
    }

    private static void W(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 V(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
