using System.Numerics;
using Kansei.Graphics;
using Kansei.Physics;
using Penelope;
using Touge.Formats;
using Touge.Race;
using Touge.Ui;

namespace Touge;

/// <summary>
///     Battle against an AI rival (--battle, <see cref="BattleSetup"/>): the rival's car (model with the character's
///     livery, lamps, engine/tyre sound heard from where it is with distance, doppler and pan), the <see cref="RaceSession"/>
///     that steps both cars with car-to-car contacts and the battle rules, effects of the rival's wheels and of contacts,
///     the battle HUD and, once decided, the finish banner (YOU WIN/LOSE) and result sheet in the menus. Everything
///     course-bound is rebuilt with the course (<see cref="LoadBattle"/>), a retry starts a fresh session (<see cref="NewBattle"/>).
/// </summary>
public sealed partial class TougeGame
{
    /// <summary>--battle: rival and rules of a quick battle; null = time attack.</summary>
    public BattleSetup? Battle { get; init; }

    /// <summary>--battle-result: with --shot, a decided battle shows the result sheet instead of the finish banner.</summary>
    public bool ShotBattleResult { get; init; }

    private RaceSession? _race;
    private readonly ManualDriver _playerDriver = new();
    private CarModel? _rivalModel;
    private Matrix4x4 _rivalModelToBody, _rivalPose, _rivalBody;
    private readonly Matrix4x4[] _rivalWheels = new Matrix4x4[4];
    private Headlights _rivalLights = new(Headlights.Mode.Off);
    private readonly SceneLights _rivalLamps = new();
    private GameAudio? _rivalAudio;
    private readonly float[] _rivalSmoke = new float[4], _rivalSpray = new float[4];
    private BattleHud? _battleHud;

    /// <summary>Speed of sound for the rival's doppler (m/s), and the distance its sound is at full level within.</summary>
    private const float SoundSpeed = 343, RivalNear = 6;

    /// <summary>With the course: the rival's model (its character livery) and lamps, then a new session.</summary>
    private void LoadBattle(Iso9660 iso)
    {
        if (Battle == null) return;
        LoadRivalModel(iso);
        _rivalLights = new Headlights(Lights ?? Headlights.For(_courseTime, _fog));
        _battleHud = new BattleHud(Battle.Rival.Name, Battle.Rival.Team);
        if (_menu != null) _menu.Versus = Battle.Rival.Name;
        NewBattle();
    }

    /// <summary>The rival's car model with the character's livery (after the player's: its textures are freed first).</summary>
    private void LoadRivalModel(Iso9660 iso)
    {
        if (Battle == null) return;
        var car = Battle.Rival.Car;
        _rivalModel = CarModel.Load(iso, car, 0, _renderer, Livery.Rival);
        _rivalModelToBody = ModelToBody(_rivalModel, CarSpecs.All[car]);
    }

    /// <summary>The rival's engine, tyres and walls as a second <see cref="GameAudio"/> (no music, rain or wind of its own).</summary>
    private void StartRivalAudio(Iso9660 iso)
    {
        _rivalAudio?.Dispose();
        _rivalAudio = Battle != null ? new GameAudio(iso, _courseTime, _audioDevice!, Battle.Rival.Car, other: true) : null;
    }

    /// <summary>
    ///     A fresh battle on the grid: the player's car by keyboard/pad (the autopilot for --autodrive/--bench/--flow runs,
    ///     which then also has no rubber band), the rival by the AI.
    /// </summary>
    private void NewBattle()
    {
        if (Battle == null || _rivalModel == null) return;
        ICarDriver player = autodrive != null || bench != null || Flow != null ? new AiDriver(new RivalPilot(_drive.Line, BattleRun.Autopilot)) : _playerDriver;
        _race = BattleRun.Create(_drive, Battle, player);
        _race.RubberBanding = player == _playerDriver;
        _rivalPrevVelocity = Vector3.Zero;
        Array.Clear(_rivalSmoke);
        Array.Clear(_rivalSpray);
        if (_menu != null) _menu.Battle = null;
        SyncPose();
        UpdateRivalMatrices(1);
    }

    /// <summary>One tick of the session (both cars); returns the input the player's car actually got (its own, or the auto-run).</summary>
    private VehicleInput BattleStep(VehicleInput input, float dt)
    {
        var race = _race!;
        race.Cars[0].Vehicle = _drive.Car; // a car change at standstill swaps the Vehicle
        _playerDriver.Input = input;
        race.Tick(dt);
        var rival = race.Cars[1];
        _rivalLights.Tick(dt);
        if (_rivalAudio != null)
        {
            // heard from the camera: inverse distance beyond RivalNear, doppler from both velocities, panned in camera space
            var to = rival.Vehicle.Position - _pos;
            var d = MathF.Max(to.Length(), 0.1f);
            var dir = to / d;
            var gain = MathF.Min(1, RivalNear / d) * Math.Clamp((300 - d) / 100, 0, 1);
            var doppler = Math.Clamp((SoundSpeed + Vector3.Dot(_camVelocity, dir)) / (SoundSpeed + Vector3.Dot(rival.Vehicle.Velocity, dir)), 0.5f, 2);
            var fwd = Vector3.Normalize(_camLook - _pos);
            var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
            var pan = new Vector3(Vector3.Dot(dir, right), 0, -Vector3.Dot(dir, fwd));
            _rivalAudio.Spatial(gain, doppler, pan.LengthSquared() > 1e-6f ? Vector3.Normalize(pan) : -Vector3.UnitZ);
            _rivalAudio.Update(rival.Vehicle, rival.Input.Throttle, rival.Input.Handbrake, dt);
        }
        if (race.LastContact is { } hit) _audio?.Bump(hit.ImpactSpeed);
        return race.Cars[0].Input;
    }

    private Vector3 _rivalPrevVelocity;

    /// <summary>Per tick (from <see cref="TickEffects"/>): the rival's smoke/skids/spray/wall sparks, sparks and a camera knock at car-to-car contacts.</summary>
    private void BattleEffects(float dt)
    {
        if (_race == null) return;
        var rival = _race.Cars[1].Vehicle;
        WheelEffects(rival, _rivalSmoke, _rivalSpray, 4, dt);
        if (rival.WallContacts > 0) WallSparks(rival);
        _rivalPrevVelocity = rival.Velocity;
        if (_race.LastContact is not { ImpactSpeed: > 0.5f } hit) return;
        _shake = MathF.Max(_shake, Math.Clamp((hit.ImpactSpeed - 0.5f) / 5, 0, 1));
        // paint-on-paint: sparks along the contact, sideways along the sliding direction
        var slide = _drive.Car.Velocity - rival.Velocity;
        var count = (int)MathF.Min(hit.ImpactSpeed * 2, 10);
        for (var i = 0; i < count; i++)
        {
            var r = new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle(), _rng.NextSingle() - 0.5f);
            _fx.EmitSpark(hit.Point - Vector3.UnitY * 0.1f, slide * (0.2f * _rng.NextSingle()) + hit.Normal * (2 * r.X) + Vector3.UnitY * (1 + r.Y) + r * 3);
        }
    }

    /// <summary>--battle with --autodrive (and --shot): the session runs that long before the first frame, effects and HUD ticking along.</summary>
    private void BattleAutoDrive(float seconds)
    {
        BattleRun.Run(_race!, seconds, () =>
        {
            TickEffects(Drive.Dt);
            _hud.Tick(_drive.Car, Drive.Dt);
        });
        _simTime = seconds;
        _brakeLight = _race!.Cars[0].Input.Brake;
    }

    private void UpdateRivalMatrices(float alpha)
    {
        if (_race == null || _rivalModel == null) return;
        var r = _race.Cars[1];
        (_rivalPose, _rivalBody) = PoseCar(r.Vehicle, _rivalModel, _rivalModelToBody, r.PrevPosition, r.PrevOrientation, alpha, _rivalWheels, Matrix4x4.Identity);
    }

    /// <summary>The rival's body and wheels as sun-shadow casters into <paramref name="dst"/>; returns how many.</summary>
    private int RivalCasters(Span<(StaticMesh, Matrix4x4)> dst)
    {
        if (_race == null || _rivalModel == null) return 0;
        dst[0] = (RivalShell.Body, _rivalBody);
        for (var i = 0; i < 4; i++) dst[1 + i] = (_rivalModel.Wheel, _rivalWheels[i]);
        return 5;
    }

    private CarModel.Shell RivalShell => _rivalLights.State != Headlights.Mode.Off ? _rivalModel!.Lit : _rivalModel!.Day;

    /// <summary>
    ///     The rival's car into the scene pass, its own lens glow and brake/reverse lamps for its draws (the scene's light
    ///     sources stay the player's: the rival's beams do not light the road, see PLAN.md).
    /// </summary>
    private void DrawRival(IRenderPassEncoder pass, in Matrix4x4 viewProj)
    {
        if (_race == null || _rivalModel == null || _probe != null) return;
        var rival = _race.Cars[1];
        var l = _renderer.Lights;
        _rivalLights.Apply(_rivalLamps, _rivalModel.Lamp, _rivalBody, _renderer.Atmosphere.LocalLightShare, rival.Input.Brake, rival.Vehicle.Gear < 0);
        var (glow, brake, reverse) = (l.LampGlow, l.Brake, l.Reverse);
        (l.LampGlow, l.Brake, l.Reverse) = (_rivalLamps.LampGlow, _rivalLamps.Brake, _rivalLamps.Reverse);
        var shell = RivalShell;
        _carRenderer.Draw(pass, shell.Body, shell.Decals, _rivalModel.Wheel, _rivalBody, _rivalWheels, viewProj, _pos);
        if (shell.PopUp is { } popUp) _carRenderer.DrawPart(pass, popUp, _rivalModel.Lamp.PopUpAt(_rivalLights.Open) * _rivalBody, viewProj, _pos);
        (l.LampGlow, l.Brake, l.Reverse) = (glow, brake, reverse);
    }

    private void BuildBattleHud(int width, int height)
    {
        if (_race?.Battle is { } b && _battleHud != null) _battleHud.Build(_overlay, width, height, b, _menuTime);
    }

    /// <summary>
    ///     Once the battle is decided (and a second has passed): the finish banner (YOU WIN/LOSE, WIN/LOSE.adx), then the
    ///     battle result sheet. True when it took over this frame.
    /// </summary>
    private bool BattleFinished()
    {
        // --bench keeps racing (its log runs to the time limit)
        if (_race is not { Battle: { Outcome: not BattleOutcome.None } b } || _finished || _menu == null || bench != null) return false;
        if (b.Time - b.DecidedAt < 1 && shotPath == null) return false;
        _finished = true;
        var rival = Battle!.Rival;
        var name = _catalog?.Cars.FirstOrDefault(c => c.Id == rival.Car)?.Name ?? rival.Car;
        _menu.Battle = BattleReport.Of(_race, rival, name);
        var t = _hud.Timer;
        _menu.Finish(new Menu.Run(t.Time, (float[])t.Splits.Clone(), [.. Enumerable.Range(0, LapTimer.Sectors).Select(t.Delta)], _previousBest, false, _hud.Drift.Total));
        if (shotPath != null)
        {
            if (ShotBattleResult) OpenMenu(Menu.Screen.Result);
            _menu.Settle(ShotBattleResult ? Menu.ButtonsAt + 0.5f : 1);
        }
        return true;
    }

    private void DisposeRival()
    {
        _rivalModel?.Dispose();
        _rivalModel = null;
    }
}
