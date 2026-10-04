using Touge.Ui;

namespace Touge.Tests;

public class JukeboxTests
{
    /// <summary>Every enabled song once per round, never two in a row across round boundaries, switched-off songs never.</summary>
    [Fact]
    public void Shuffle_RoundsWithoutRepeats()
    {
        var shuffle = new Jukebox.Shuffle(31, new Random(1));
        bool Enabled(int i) => i is not (3 or 17);
        var last = -1;
        for (var round = 0; round < 20; round++)
        {
            var picks = Enumerable.Range(0, 29).Select(_ => shuffle.Next(Enabled)).ToArray();
            Assert.Equal(29, picks.Distinct().Count());
            Assert.DoesNotContain(3, picks);
            Assert.DoesNotContain(17, picks);
            Assert.NotEqual(last, picks[0]);
            last = picks[^1];
        }
    }

    [Fact]
    public void Shuffle_ToggledSongsJoinOrLeaveTheRound()
    {
        var shuffle = new Jukebox.Shuffle(5, new Random(2));
        var on = new[] { true, true, true, false, false };
        var first = new[] { shuffle.Next(i => on[i]), shuffle.Next(i => on[i]) };
        on[3] = true; // switched on mid-round: still to come in this round
        on[first[0]] = false;
        var rest = new[] { shuffle.Next(i => on[i]), shuffle.Next(i => on[i]) };
        Assert.Equal(new[] { 0, 1, 2, 3 }.Except(first).Where(i => on[i]).Order(), rest.Order());
    }

    [Fact]
    public void Shuffle_NoneOrOneEnabled()
    {
        var shuffle = new Jukebox.Shuffle(4, new Random(3));
        Assert.Equal(-1, shuffle.Next(_ => false));
        Assert.Equal(2, shuffle.Next(i => i == 2));
        Assert.Equal(2, shuffle.Next(i => i == 2)); // the only song repeats rather than silence
    }

    /// <summary>Picks superseded while loading (fast skips) are not marked played and stay in the round.</summary>
    [Fact]
    public void Shuffle_UnstartedPicksStayInRound()
    {
        var shuffle = new Jukebox.Shuffle(3, new Random(4));
        for (var n = 0; n < 10; n++) shuffle.Pick(_ => true); // skipped before they started
        var round = Enumerable.Range(0, 3).Select(_ => shuffle.Next(_ => true)).ToArray();
        Assert.Equal([0, 1, 2], round.Order());
    }

    /// <summary>Stop reports the device's unplayed tail; Rewind replays exactly those frames, then continues the song.</summary>
    [Fact]
    public void Rewind_ReplaysUnplayedTail()
    {
        var next = 0; // counting mono source, 3000 frames long
        var r = new Jukebox.Rewind(dst =>
        {
            var n = Math.Min(dst.Length, 3000 - next);
            for (var i = 0; i < n; i++) dst[i] = (short)next++;
            return n;
        }, 1);
        var a = new short[1000];
        Assert.Equal(1000, r.Read(a));
        r.Back(300);
        var b = new short[1000];
        Assert.Equal(300, r.Read(b)); // the replayed tail first
        Assert.Equal(a[700..], b[..300]);
        Assert.Equal(1000, r.Read(b));
        Assert.Equal(1999, b[^1]);
        r.Back(int.MaxValue); // clamped to what was read
        var c = new short[4000];
        Assert.Equal(2000, r.Read(c)); // whole song so far (ring holds more)
        Assert.Equal(0, c[0]);
    }

    /// <summary>Playlist: toggles per song and ALL SONGS write Settings.MusicOff; Back returns to the PLAYLIST plate of Options.</summary>
    [Fact]
    public void Playlist_TogglesSettings()
    {
        var settings = new Settings();
        var catalog = new Catalog([new Catalog.Course("AKINA", "AKINA", ["DAY"], [new(0, 0), new(1, 0)], 1, 1, true, false)],
            [new Catalog.Car("AE86T", "TOYOTA", "TRUENO", "FR", 130, 940, [1])]);
        var m = new Menu(catalog, settings);
        m.Open(Menu.Screen.Options, "AKINA_DAY", false, "AE86T", 0);
        m.Settle();
        Assert.True(m.Options.OpenPage("PLAYLIST"));
        Assert.Equal(Menu.Action.SettingsChanged, m.Update((0, 0, true, false), 1 / 60f)); // ALL SONGS off
        Assert.Equal(Jukebox.Songs.Length, settings.MusicOff.Count);
        m.Update((0, 0, true, false), 1 / 60f); // all on again
        Assert.Empty(settings.MusicOff);
        m.Update((0, 1, false, false), 1 / 60f);
        m.Update((1, 0, false, false), 1 / 60f);
        Assert.Equal(new[] { Jukebox.Songs[0].File }, settings.MusicOff);
        m.Update((0, 0, false, true), 1 / 60f);
        Assert.Equal(Menu.Screen.Options, m.Current);
        Assert.Null(m.Options.Current); // back on the section list, PLAYLIST still chosen
        Assert.Equal("PLAYLIST", m.Options.Pages[m.Options.Section].Title);
        Assert.Equal("TOKYO.adx", m.Music("TOKYO.adx")); // options and playlist keep the music playing
    }
}
