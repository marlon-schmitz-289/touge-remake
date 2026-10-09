using System.Numerics;
using Kansei.Core;
using Touge.Ui;

namespace Touge;

/// <summary>Where and when the race music's NOW PLAYING toast (<see cref="NowPlaying"/>) shows.</summary>
public sealed partial class TougeGame
{
    /// <summary>Options → HUD → NOW PLAYING: ON, OFF, or only while paused.</summary>
    private bool ShowsNowPlaying(bool paused) => _settings.NowPlaying switch
    {
        Settings.Toast.Off => false,
        Settings.Toast.PauseOnly => paused,
        _ => true,
    };

    /// <summary>Below the battle / versus position panel and the Story goal panel (units of the whole screen); 0 without HUD.</summary>
    private float NowPlayingBelow((Viewport First, Viewport Second)? split) => !_hud.Visible ? 0
        : NowPlaying.Below(_race?.Battle != null, _vsRace != null ? VersusHud.Height(_vsRace.Cars.Count) : 0, split != null && !_versusUi!.Vertical,
            _story?.HudHeight(_race?.Battle) ?? 0) + FourPassHudBelow;

    /// <summary>The HUD's top panels of every view (timing, drift combo) the toast keeps clear of.</summary>
    private (Vector2 Min, Vector2 Max)[] NowPlayingClear(int w, int h, (Viewport First, Viewport Second)? split)
    {
        if (!_hud.Visible) return [];
        if (split is not var (a, b)) return Hud.TopBoxes(w, h, _hud.Scale, _hud.Mirror);
        return [.. new[] { a, b }.SelectMany(v => Hud.TopBoxes(v.Width, v.Height, _hud.Scale)
            .Select(x => (x.Min + new Vector2(v.X, v.Y), x.Max + new Vector2(v.X, v.Y))))];
    }
}
