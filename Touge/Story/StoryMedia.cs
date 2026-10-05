using System.Numerics;
using Kansei.Audio;
using Kansei.Graphics;
using Touge.Formats;
using Touge.Ui;

namespace Touge.Story;

/// <param name="Koma">A manga sequence (KOMATC <paramref name="Number" />), else the portrait scene of slot <paramref name="Number" />.</param>
public sealed record ShowRequest(int Chapter, bool Koma, int Number)
{
    public override string ToString() => Koma ? $"Manga KOMATC{Number:00}" : $"Szene STR{Chapter:00} Slot {Number}";
}

/// <summary>
///     One story show as loaded from the disc (FORMATS.md "Story – Manga"): the pictures it needs decoded (manga panels and
///     sky/title cards, or portraits with their face sprites and backdrops), its voice track and the subtitles lined up to
///     it. Decoded on a worker thread (<see cref="StoryMedia.Load" />), uploaded a few pictures per frame, freed when done.
/// </summary>
public sealed class ShowMedia
{
    public required ShowRequest Request { get; init; }
    public KomaSequence? Koma { get; init; }
    public PortraitScene? Scene { get; init; }
    public Manga.Face?[][] Faces { get; init; } = [];
    /// <summary>Scene backdrop per portrait (BGSTR STRnn, else STR00).</summary>
    public Dictionary<int, string> Backdrops { get; init; } = [];
    public required Show Show { get; init; }
    public string Voice { get; init; } = "";
    internal Dictionary<string, (int W, int H, byte[] Rgba)> Decoded { get; init; } = [];
    internal (short[] Pcm, int Channels, int Rate)? Pcm { get; set; }
    /// <summary>Length of the voice track (0 = none).</summary>
    public double VoiceSeconds { get; init; }
    public Dictionary<string, (int Id, int W, int H)> Textures { get; } = [];
    internal AudioDevice.Clip? Clip;
    internal AudioDevice.Track? Track;
    public bool Uploaded => _uploaded;
    internal bool _uploaded;
}

/// <summary>
///     Loads, plays and draws the story's manga sequences and portrait scenes for <see cref="StoryMode" />: pictures through
///     <see cref="SpriteRenderer" /> (the original's art at runtime from the ISO), the voice track on its own
///     <see cref="AudioDevice.Track" /> at <see cref="Volume" />. Without an audio device the show runs on the frame clock.
/// </summary>
public sealed class StoryMedia(string isoPath, SpriteRenderer? sprites)
{
    private const string Dir = "CDVD/DATA/";
    /// <summary>Bytes of pictures uploaded per frame (the rest next frame, the show waits).</summary>
    private const int UploadBudget = 6 << 20;
    public float Volume { get; set; } = 1;
    /// <summary>The voice output (null: no sound, the shows run on the frame clock).</summary>
    public AudioDevice? Audio { get; set; }

    /// <summary>Starts decoding a show on a worker thread (<see cref="Decoder" />: at once).</summary>
    public Task<ShowMedia> Load(ShowRequest r) => Decoder != null ? Task.FromResult(Decoder(r)) : Task.Run(() => Decode(isoPath, r));

    /// <summary>Tests: shows made up instead of read from the disc, synchronously (no thread-pool timing in the flow).</summary>
    public Func<ShowRequest, ShowMedia>? Decoder { get; init; }

    /// <summary>Everything a show needs, decoded (no GPU, no audio device): also the headless check.</summary>
    public static ShowMedia Decode(string isoPath, ShowRequest r)
    {
        using var iso = new Iso9660(isoPath);
        var elf = iso.ReadFile(StoryScript.ElfPath);
        var voices = iso.OpenAfs(Dir + "MANGAV/MG_KOMAS.AFS");
        var images = new Dictionary<string, (int, int, byte[])>();
        return r.Koma ? DecodeKoma(iso, elf, voices, r, images) : DecodeScene(iso, elf, voices, r, images);
    }

    private static (short[], int, int)? Voice(Afs voices, string name)
    {
        if (voices.Find(name) is not { } e) return null;
        var adx = new Adx(voices.Read(e));
        return (adx.DecodeAll(), adx.Channels, adx.SampleRate);
    }

    private static double Seconds((short[] Pcm, int Channels, int Rate)? v) => v is var (p, c, rate) ? p.Length / (double)c / rate : 0;

    private static ShowMedia DecodeKoma(Iso9660 iso, byte[] elf, Afs voices, ShowRequest r, Dictionary<string, (int, int, byte[])> images)
    {
        var komam = iso.OpenAfs(Dir + "MANGA/MG_KOMAM.AFS");
        var bin = komam.Read(komam.Find("KOMABIN.FPK") ?? throw new FileNotFoundException("KOMABIN.FPK"));
        var entry = Manga.Fpk(bin).First(e => e.Name == $"KOMATC{r.Number:00}.BIN");
        var koma = KomaSequence.Parse(Manga.Timeline(Manga.Read(bin, entry)));
        var komaf = iso.OpenAfs(Dir + "MANGA/MG_KOMAF.AFS");
        var bg = Manga.KomaBackground(elf, r.Number);
        // the panels sit in the episode's KOMAnn.FPK (KOMATC24's 31_nn too), else in the one their name says
        foreach (var fpkName in new[] { bg }.Concat(koma.Panels.Select(p => int.Parse(p.Name[..2]))).Distinct().Select(n => $"KOMA{n:00}.FPK"))
        {
            if (komaf.Find(fpkName) is not { } f) continue;
            var fpk = komaf.Read(f);
            foreach (var e in Manga.Fpk(fpk))
            {
                var name = Path.GetFileNameWithoutExtension(e.Name);
                if (!images.ContainsKey(name) && koma.Panels.Any(p => p.Name == name)) images[name] = Manga.Tim2(Manga.Read(fpk, e));
            }
        }
        if (komaf.Find($"KOMABG{bg:00}.PAC") is { } pe)
        {
            var pac = komaf.Read(pe);
            var wanted = koma.Backdrops.Select(b => b.Name).ToHashSet();
            foreach (var e in Pac.Entries(pac).Where(e => e.Type == 1 && wanted.Contains(e.Name)))
                images[e.Name] = Gim.Decode(pac.AsSpan(e.Offset, e.Size));
        }
        var voice = Manga.KomaVoice(r.Number);
        var pcm = r.Number == 27 ? null : Voice(voices, voice); // KOMATC27: the game plays none
        var lines = MangaText.Lines(r.Number);
        return new ShowMedia
        {
            Request = r, Koma = koma, Decoded = images, Pcm = pcm, Voice = pcm == null ? "-" : voice, VoiceSeconds = Seconds(pcm),
            Show = new Show(lines, Math.Max(koma.Quit / 60.0, Seconds(pcm)), [.. koma.Steps]),
        };
    }

    private static ShowMedia DecodeScene(Iso9660 iso, byte[] elf, Afs voices, ShowRequest r, Dictionary<string, (int, int, byte[])> images)
    {
        var obj = iso.OpenAfs(Dir + "MANGA/MG_OBJ.AFS");
        var robj = obj.Read(obj.Find($"STR{r.Chapter:00}.BIN") ?? throw new FileNotFoundException($"STR{r.Chapter:00}.BIN"));
        var lips = Manga.Lips(robj);
        var part = Part(lips, r.Number);
        var str = iso.OpenAfs(Dir + "MANGA/MG_STR.AFS");
        var fpk = str.Read(str.Find(Manga.Pictures(robj)) ?? throw new FileNotFoundException(Manga.Pictures(robj)));
        var entries = Manga.Fpk(fpk).ToDictionary(e => e.Name);
        // STR21/STR22 each name one portrait their archive lacks: the one before stays, its mouth shut
        var stages = StoryScript.Staging(robj, lips)[part];
        var missing = stages.Where(s => s.Step == StoryScript.Step.Picture && !entries.ContainsKey($"{s.Value:00}.ICP")).Select(s => s.Value).ToHashSet();
        var scene = new PortraitScene(stages, lips[r.Number], missing);
        var faces = Manga.Faces(robj);
        foreach (var n in scene.Pictures)
        {
            var (w, h, rgba) = Manga.Tim2(Manga.Read(fpk, entries[$"{n:00}.ICP"]));
            images[$"P{n:00}"] = (w, Math.Min(h, 512), rgba[..(w * Math.Min(h, 512) * 4)]);
            images[$"F{n:00}"] = FacePatches(w, h, rgba, faces, n);
        }
        var episode = Manga.ReadChapters(elf)[r.Chapter].Episode;
        var bgp = iso.OpenAfs(Dir + "MANGA/MG_BGP.AFS");
        var backdrops = new Dictionary<int, string>();
        if (bgp.Find($"BGSTR{episode:00}.PAC") is { } be)
        {
            var pac = bgp.Read(be);
            var all = Pac.Entries(pac).Where(e => e.Type == 1).ToDictionary(e => e.Name);
            foreach (var n in scene.Pictures)
            {
                var name = all.ContainsKey($"STR{n:00}") ? $"STR{n:00}" : "STR00";
                if (!all.TryGetValue(name, out var e)) continue;
                backdrops[n] = name;
                if (!images.ContainsKey(name)) images[name] = FillWhiteEdges(Gim.Decode(pac.AsSpan(e.Offset, e.Size)));
            }
        }
        var voice = Manga.SceneVoice(r.Chapter, r.Number);
        var pcm = Voice(voices, voice);
        var times = StoryScript.Times(robj, lips)[part];
        var english = StoryText.Chapters[r.Chapter].Scene;
        var text = part < english.Length ? english[part] : [];
        var lines = times.Zip(text, (t, l) => new ShowLine(t, l)).ToList();
        var length = pcm != null ? Seconds(pcm) : times.DefaultIfEmpty(0).Max() + 4;
        return new ShowMedia
        {
            Request = r, Scene = scene, Faces = Manga.Faces(robj), Backdrops = backdrops, Decoded = images, Pcm = pcm, Voice = pcm == null ? "-" : voice, VoiceSeconds = Seconds(pcm),
            Show = new Show(lines, length),
        };
    }

    /// <summary>
    ///     Some BGSTR backdrops (BGSTR21 STR20/21, BGSTR10 STR17, …) have 2–32 pure white rows at the top or bottom that the
    ///     stretched 640 × 448 view would show as a bar: the picture without them (and their lighter rim row) is stretched over
    ///     the full height (a quarter at most is cut).
    /// </summary>
    public static (int, int, byte[]) FillWhiteEdges((int W, int H, byte[] Rgba) image)
    {
        var (w, h, rgba) = image;
        bool White(int y)
        {
            for (var i = y * w * 4; i < (y + 1) * w * 4; i += 4)
                if (rgba[i] < 240 || rgba[i + 1] < 240 || rgba[i + 2] < 240) return false;
            return true;
        }
        int top = 0, bottom = 0;
        while (top < h / 4 && White(top)) top++;
        while (bottom < h / 4 && White(h - 1 - bottom)) bottom++;
        if (top > 0) top = Math.Min(top + 1, h / 4);
        if (bottom > 0) bottom = Math.Min(bottom + 1, h / 4);
        if (top + bottom == 0) return image;
        var row = w * 4;
        var src = (byte[])rgba.Clone();
        for (var y = 0; y < h; y++) Array.Copy(src, (top + y * (h - top - bottom) / h) * row, rgba, y * row, row);
        return image;
    }

    /// <summary>Atlas cell of a face patch: 80 × 128 with a 2-px gutter; columns mouth 0–5, first eye 6–8, second eye 9–11, row = face.</summary>
    private const int PatchW = 82, PatchH = 130;

    /// <summary>
    ///     The face sprites of a portrait laid over the portrait itself at full resolution (as the PS2 draws them 1:1): per face
    ///     every mouth and eye frame as an opaque patch of portrait + sprite. Drawn filtered over the scaled portrait, a patch
    ///     matches it at its rim (a sprite alone would leave a seam where the hole's rim and its own both blend half).
    /// </summary>
    private static (int, int, byte[]) FacePatches(int w, int h, byte[] rgba, Manga.Face?[][] faces, int picture)
    {
        var atlas = new byte[12 * PatchW * PortraitScene.Faces * PatchH * 4];
        var aw = 12 * PatchW;
        for (var k = 0; k < PortraitScene.Faces && k < faces.Length; k++)
        {
            if (picture >= faces[k].Length || faces[k][picture] is not { } f) continue;
            for (var col = 0; col < 12; col++)
            {
                var (row, x, y) = col < 6 ? (512 + 256 * k, f.MouthX, f.MouthY) : col < 9 ? (640 + 256 * k, f.Eye1X, f.Eye1Y) : (640 + 256 * k, f.Eye2X, f.Eye2Y);
                var frame = col < 6 ? col : col - 6; // eye 1: frames 0–2, eye 2: 3–5
                if (x <= 0 || row + 128 > h) continue;
                for (var py = 0; py < 128; py++)
                    for (var px = 0; px < 80; px++)
                    {
                        int bx = x + px, by = y + py, d = ((k * PatchH + 1 + py) * aw + col * PatchW + 1 + px) * 4, s = ((row + py) * w + 80 * frame + px) * 4;
                        var b = bx < w && by < 512 ? (by * w + bx) * 4 : -1;
                        float sa = rgba[s + 3] / 255f, ba = b < 0 ? 0 : rgba[b + 3] / 255f, oa = sa + ba * (1 - sa);
                        for (var ch = 0; ch < 3; ch++)
                            atlas[d + ch] = oa <= 0 ? (byte)0 : (byte)((rgba[s + ch] * sa + (b < 0 ? 0 : rgba[b + ch]) * ba * (1 - sa)) / oa);
                        atlas[d + 3] = (byte)(oa * 255 + 0.5f);
                    }
            }
        }
        return (aw, PortraitScene.Faces * PatchH, atlas);
    }

    /// <summary>The script part (<see cref="StoryScript.ParseScript" />) of a slot: the parts are the slots with a page list, in order.</summary>
    public static int Part(string[][] lips, int slot) => Enumerable.Range(0, slot).Count(s => lips[s].Length > 0);

    /// <summary>Uploads what is left of the show's pictures within the frame budget and its voice; true when it can play.</summary>
    public bool Upload(ShowMedia m, bool all = false)
    {
        if (m._uploaded) return true;
        var budget = UploadBudget;
        foreach (var (name, (w, h, rgba)) in m.Decoded.ToList())
        {
            if (budget <= 0 && !all) return false;
            m.Textures[name] = (sprites?.Upload(w, h, rgba, name) ?? 0, w, h);
            m.Decoded.Remove(name);
            budget -= rgba.Length;
        }
        if (m.Pcm is var (pcm, ch, rate) && Audio is { } audio)
        {
            m.Clip = audio.CreateClip(pcm, ch, rate);
            m.Track = audio.CreateTrack(m.Clip);
            m.Pcm = null; // in the audio device now
        }
        m._uploaded = true;
        return true;
    }

    /// <summary>Stops the voice and frees the pictures.</summary>
    public void Free(ShowMedia m)
    {
        m.Track?.Dispose();
        m.Clip?.Dispose();
        (m.Track, m.Clip) = (null, null);
        sprites?.Free(m.Textures.Values.Select(t => t.Id));
        m.Textures.Clear();
    }

    /// <summary>
    ///     Per frame: the clock follows the voice track (restarted where the show seeked, paused while it holds or is
    ///     paused); <paramref name="run" /> false holds both.
    /// </summary>
    public void Play(ShowMedia m, double dt, bool run)
    {
        var s = m.Show;
        var t = m.Track;
        if (t != null) t.Gain = Volume;
        if (t == null)
        {
            if (run) s.Tick(dt, null);
            s.Seeked = false;
            return;
        }
        if (!run || s.Held || s.Done) t.Pause();
        else if (s.Seeked || !t.Playing)
        {
            if (s.Time < m.VoiceSeconds - 0.05) t.Play((float)s.Time);
        }
        s.Seeked = false;
        if (run) s.Tick(dt, t.Playing ? t.Seconds : null);
    }

    // ---------------------------------------------------------------- drawing (640 × 448 screen of the original in the 4:3 frame)

    private const float BackdropScale = 448f / 512; // KOMABG/portraits 512² shown 448 high (title card at BI_(96,0) is then centred)

    private static Vector2 P(Canvas c, float x, float y) => c.P(x * 0.8f, y);

    private void Picture(Canvas c, ShowMedia m, string name, float x, float y, float w, float h, Vector2 uv0, Vector2 uv1, uint tint)
    {
        if (!m.Textures.TryGetValue(name, out var t)) return;
        sprites?.Add(t.Id, P(c, x, y), P(c, x + w, y + h), uv0, uv1, tint);
    }

    private static uint Tint(float a, float k = 1) => Overlay.Rgba(k, k, k, Math.Clamp(a, 0, 1));

    /// <summary>The show's pictures at its current time (under the overlay: subtitles and fades come from <see cref="StoryMode" />).</summary>
    public void Draw(Canvas c, ShowMedia m)
    {
        sprites?.Fill(Vector2.Zero, new Vector2(c.Width, c.Height), Overlay.Rgba(0, 0, 0));
        if (!m._uploaded) return;
        if (m.Koma is { } k) DrawKoma(c, m, k, m.Show.Time * 60);
        else if (m.Scene is { } s) DrawScene(c, m, s, m.Show.Time);
    }

    private void DrawKoma(Canvas c, ShowMedia m, KomaSequence k, double frame)
    {
        if (k.BackdropAt(frame) is var (b, offset, a) && m.Textures.TryGetValue(b.Name, out var tex))
        {
            float w = tex.W * BackdropScale, h = tex.H * BackdropScale;
            if (b.Sky)
            {
                // skies scroll and repeat across the whole width (also beyond 4:3)
                var left = c.Left / 0.8f;
                var x = b.At.X - offset.X * BackdropScale;
                x -= MathF.Ceiling((x - left) / w) * w;
                for (; x < c.Right / 0.8f; x += w) Picture(c, m, b.Name, x, b.At.Y - offset.Y * BackdropScale, w, h, Vector2.Zero, Vector2.One, Tint(a));
            }
            else Picture(c, m, b.Name, b.At.X, b.At.Y, w, h, Vector2.Zero, Vector2.One, Tint(a));
        }
        foreach (var (p, at, pa) in k.PanelsAt(frame))
            if (m.Textures.TryGetValue(p.Name, out var t))
                Picture(c, m, p.Name, at.X, at.Y, t.W, t.H, Vector2.Zero, Vector2.One, Tint(pa, p.Dark ? 0.45f : 1));
    }

    private void DrawScene(Canvas c, ShowMedia m, PortraitScene s, double t)
    {
        var (picture, mouth) = s.At(t);
        if (picture < 0) return;
        if (m.Backdrops.TryGetValue(picture, out var bg)) Picture(c, m, bg, 0, 0, 640, 448, Vector2.Zero, Vector2.One, Tint(1));
        var name = $"P{picture:00}";
        if (!m.Textures.TryGetValue(name, out var tex)) return;
        const float x0 = (640 - 512 * BackdropScale) / 2;
        Picture(c, m, name, x0, 0, tex.W * BackdropScale, tex.H * BackdropScale, Vector2.Zero, Vector2.One, Tint(1));
        // the face sprites always go over the holes the portrait leaves for mouth and eyes (patches: see FacePatches)
        var atlas = $"F{picture:00}";
        if (!m.Textures.TryGetValue(atlas, out var at)) return;
        void Cell(int col, int k, int x, int y)
        {
            if (x <= 0) return;
            Vector2 uv0 = new((col * PatchW + 1f) / at.W, (k * PatchH + 1f) / at.H), uv1 = uv0 + new Vector2(80f / at.W, 128f / at.H);
            Picture(c, m, atlas, x0 + x * BackdropScale, y * BackdropScale, 80 * BackdropScale, 128 * BackdropScale, uv0, uv1, Tint(1));
        }
        for (var k = 0; k < PortraitScene.Faces && k < m.Faces.Length; k++)
        {
            if (picture >= m.Faces[k].Length || m.Faces[k][picture] is not { } f) continue;
            var eyes = PortraitScene.Eyes(t, k);
            Cell(mouth[k], k, f.MouthX, f.MouthY);
            Cell(6 + eyes, k, f.Eye1X, f.Eye1Y);
            Cell(9 + eyes, k, f.Eye2X, f.Eye2Y);
        }
    }
}
