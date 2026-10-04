using Kansei.Audio;
using Kansei.Physics;
using Touge.Formats;

namespace Touge;

/// <summary>
///     <c>--audio-capture &lt;wav&gt; &lt;s&gt;</c> (with <c>--autodrive</c>): the pilot drives, the game's own mix is rendered
///     offline through an OpenAL loopback device (48 kHz stereo) in step with the 120 Hz physics and written as WAV.
///     Prints per second what the sound does next to the car state, then correlations, level, clipping and allocations.
/// </summary>
internal static class AudioCapture
{
    private const int Rate = 48000, FramesPerTick = Rate / 120, TicksPerBlock = 12; // analysis blocks of 0.1 s

    public static bool Run(Iso9660 iso, string courseTime, int at, float driveSeconds, string wavPath, float seconds, bool music, string carName)
    {
        using var dev = new AudioDevice(Rate);
        if (!dev.Enabled) return false;
        var drive = new Drive(iso, courseTime, spec: CarSpecs.All[carName]);
        drive.ResetTo(at);
        using var audio = new GameAudio(iso, courseTime, dev, carName);
        if (music) audio.PlayTrack(background: false);

        var ticks = (int)(seconds * 120) / TicksPerBlock * TicksPerBlock;
        var pcm = new short[ticks * FramesPerTick * 2];
        float[] rpm = new float[ticks], pitch = new float[ticks], zone = new float[ticks], jump = new float[ticks], slip = new float[ticks], squeal = new float[ticks], kmh = new float[ticks], gas = new float[ticks];
        long allocated = 0;
        int wallTicks = 0;
        float maxImpact = 0, maxScrape = 0;
        var car = drive.Car;
        var prevPitch = new float[16];
        for (var n = 0; n < ticks; n++)
        {
            var input = n * Drive.Dt < driveSeconds ? drive.Pilot.Drive(car) : new VehicleInput(0, 0, 0);
            car.Step(input, drive.Ground, Drive.Dt);
            var before = GC.GetAllocatedBytesForCurrentThread();
            audio.Update(car, input.Throttle, input.Handbrake, Drive.Dt);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            dev.Render(pcm.AsSpan(n * FramesPerTick * 2, FramesPerTick * 2));
            // largest pitch step of an audible engine voice since the last tick (semitones)
            zone[n] = audio.EngineZone;
            for (var i = 0; i < 16; i++)
            {
                var v = audio.EngineVoices[i];
                if (n > 0 && v.Gain > 0.01f) jump[n] = MathF.Max(jump[n], MathF.Abs(12 * MathF.Log2(v.Pitch / prevPitch[i])));
                prevPitch[i] = v.Pitch;
            }
            (rpm[n], pitch[n], slip[n], squeal[n], kmh[n], gas[n]) = (car.Rpm, audio.EnginePitch, audio.Slip, audio.SquealGain, car.SpeedKmh, input.Throttle);
            if (car.WallContacts > 0) wallTicks++;
            (maxImpact, maxScrape) = (MathF.Max(maxImpact, car.WallImpactSpeed), MathF.Max(maxScrape, audio.ScrapeGain));
        }
        Wav.Write(wavPath, pcm, 2, Rate);

        // per 0.1 s block: means of the car/voice state, spectral centroid and RMS of the mix
        var blocks = ticks / TicksPerBlock;
        var b = new (double Rpm, double Pitch, double Gas, double Slip, double Squeal, double Centroid, double Rms)[blocks];
        var mono = new float[TicksPerBlock * FramesPerTick / 4];
        for (var k = 0; k < blocks; k++)
        {
            var r = new Range(k * TicksPerBlock, (k + 1) * TicksPerBlock);
            var s = pcm.AsSpan(k * TicksPerBlock * FramesPerTick * 2, TicksPerBlock * FramesPerTick * 2);
            double sq = 0;
            foreach (var v in s) sq += (double)v * v;
            for (var i = 0; i < mono.Length; i++) // 12 kHz mono (4-frame average) for the spectrum
                mono[i] = (s[i * 8] + s[i * 8 + 1] + s[i * 8 + 2] + s[i * 8 + 3] + s[i * 8 + 4] + s[i * 8 + 5] + s[i * 8 + 6] + s[i * 8 + 7]) / 8f;
            b[k] = (rpm[r].Average(), pitch[r].Average(), gas[r].Average(), slip[r].Average(), squeal[r].Average(), Centroid(mono, Rate / 4), Math.Sqrt(sq / s.Length));
        }

        Console.WriteLine("   t  km/h    rpm  motor_pitch  schwerpunkt_Hz  schlupf  quietschen  rms_dBFS");
        for (var k = 0; k < blocks; k += 10)
            Console.WriteLine($"{k / 10.0,4:F0} {kmh[k * TicksPerBlock],5:F0} {b[k].Rpm,6:F0} {b[k].Pitch,12:F2} {b[k].Centroid,14:F0} {b[k].Slip,8:F2} {b[k].Squeal,11:F2} {Db(b[k].Rms),9:F1}");

        int peak = 0, clipped = 0;
        double total = 0;
        foreach (var v in pcm)
        {
            peak = Math.Max(peak, Math.Abs((int)v));
            if (v is short.MaxValue or short.MinValue) clipped++;
            total += (double)v * v;
        }
        var eng = b.Where(x => x.Rpm > 1200).ToArray();
        var full = eng.Where(x => x.Gas > 0.9).ToArray();
        Console.WriteLine($"[Audio] {wavPath}: {seconds:F0} s, Musik {(music ? audio.Track : "aus")}, RMS {Db(Math.Sqrt(total / pcm.Length)):F1} dBFS, Spitze {Db(peak):F2} dBFS, Samples an ±32767: {clipped}");
        Console.WriteLine($"[Audio] Korrelation Drehzahl ↔ Motor-Pitch (gesetzt) r = {Pearson(eng.Select(x => x.Rpm), eng.Select(x => x.Pitch)):F3}, ↔ spektraler Schwerpunkt des Mix r = {Pearson(eng.Select(x => x.Rpm), eng.Select(x => x.Centroid)):F3}, Pitch ↔ Schwerpunkt r = {Pearson(eng.Select(x => x.Pitch), eng.Select(x => x.Centroid)):F3} ({eng.Length} Blöcke > 1200 U/min)");
        Console.WriteLine($"[Audio] nur Vollgas (nur _U-Schichten, {full.Length} Blöcke): Drehzahl ↔ Motor-Pitch r = {Pearson(full.Select(x => x.Rpm), full.Select(x => x.Pitch)):F3}, ↔ Schwerpunkt r = {Pearson(full.Select(x => x.Rpm), full.Select(x => x.Centroid)):F3}");
        // zones, pitch steps (off the limiter, whose fuel cut drops the pitch 4 %) and set pitch off vs on throttle per 500-rpm bin
        var running = Enumerable.Range(0, ticks).Where(i => rpm[i] > 1200 && rpm[i] < car.Spec.RevLimit - 150).ToArray();
        var bins = b.Where(x => x.Rpm > 1200).GroupBy(x => (int)(x.Rpm / 500))
            .Select(g => (On: g.Where(x => x.Gas > 0.9).Select(x => x.Pitch).DefaultIfEmpty().Average(), Off: g.Where(x => x.Gas < 0.1).Select(x => x.Pitch).DefaultIfEmpty().Average()))
            .Where(p => p.On > 0 && p.Off > 0).Select(p => 12 * Math.Log2(p.Off / p.On)).ToArray();
        Console.WriteLine($"[Audio] {carName}: Zone {running.Min(i => zone[i]):F2}–{running.Max(i => zone[i]):F2}; größter Pitchsprung einer hörbaren Schicht je Tick {running.Max(i => jump[i]):F3} Halbtöne " +
                          $"(größter Drehzahlsprung {running.Where(i => i > 0).Max(i => MathF.Abs(12 * MathF.Log2(rpm[i] / rpm[i - 1]))):F3}); " +
                          $"Pitch Schub − Last je 500 U/min: {(bins.Length > 0 ? $"Ø {bins.Average():F2}, max {bins.Max(Math.Abs):F2} Halbtöne ({bins.Length} Klassen)" : "keine Klasse mit beidem")}");
        Console.WriteLine($"[Audio] Wand: {wallTicks} Ticks mit Kontakt, max Aufprall {maxImpact:F1} m/s, Crash-Sounds {audio.Crashes}, max Kratz-Gain {maxScrape:F2}");
        Console.WriteLine($"[Audio] Korrelation Schlupf ↔ Quietsch-Gain r = {Pearson(b.Select(x => x.Slip), b.Select(x => x.Squeal)):F3}; Blöcke mit Quietschen > 0,1: {b.Count(x => x.Squeal > 0.1)} von {blocks}, mittlerer Schlupf dort {b.Where(x => x.Squeal > 0.1).Select(x => x.Slip).DefaultIfEmpty().Average():F2} (sonst {b.Where(x => x.Squeal <= 0.1).Select(x => x.Slip).DefaultIfEmpty().Average():F2})");
        Console.WriteLine($"[Audio] Allokationen in GameAudio.Update: {allocated} B über {ticks} Updates ({(double)allocated / ticks:F1} B/Update)");
        return peak < short.MaxValue;
    }

    /// <summary>
    ///     <c>--sweep</c>: engine only, no car/pilot. 3rd gear, 1 s idle, full throttle while the rpm rises linearly idle → rev
    ///     limit over 10 s, 1 s on the limiter, then off throttle falling linearly to idle over 8 s. WAV + per-tick CSV
    ///     (t, rpm, throttle, zone, gain/pitch of the 16 engine voices) next to it.
    /// </summary>
    public static bool Sweep(Iso9660 iso, string wavPath, string carName)
    {
        using var dev = new AudioDevice(Rate);
        if (!dev.Enabled) return false;
        var spec = CarSpecs.All[carName];
        using var audio = new GameAudio(iso, "AKINA_DAY", dev, carName);
        const int ticks = 20 * 120;
        var pcm = new short[ticks * FramesPerTick * 2];
        using var csv = new StreamWriter(Path.ChangeExtension(wavPath, ".csv"));
        csv.WriteLine("t,rpm,throttle,zone," + string.Join(',', Enumerable.Range(0, 16).Select(i => $"g{i},p{i}")));
        var voices = audio.EngineVoices;
        for (var n = 0; n < ticks; n++)
        {
            var t = n / 120f;
            var (rpm, throttle) = t switch
            {
                < 1 => (spec.IdleRpm, 0f),
                < 11 => (spec.IdleRpm + (spec.RevLimit - spec.IdleRpm) * (t - 1) / 10, 1f),
                < 12 => (spec.RevLimit, 1f),
                _ => (spec.RevLimit - (spec.RevLimit - spec.IdleRpm) * MathF.Min((t - 12) / 8, 1), 0f),
            };
            audio.UpdateEngine(spec, rpm, 3, throttle, Drive.Dt);
            dev.Render(pcm.AsSpan(n * FramesPerTick * 2, FramesPerTick * 2));
            csv.WriteLine(FormattableString.Invariant($"{t:F4},{rpm:F1},{throttle},{audio.EngineZone:F3},{string.Join(',', voices.Select(v => FormattableString.Invariant($"{v.Gain:F4},{v.Pitch:F4}")))}"));
        }
        Wav.Write(wavPath, pcm, 2, Rate);
        var peak = pcm.Max(v => Math.Abs((int)v));
        Console.WriteLine($"[Audio] Sweep {carName}: {wavPath}, Spitze {Db(peak):F2} dBFS");
        return peak < short.MaxValue;
    }

    private static double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-9) / 32768);

    /// <summary>
    ///     Spectral centroid 40–4000 Hz (Hann window, DFT every 20 Hz). A resampled (pitched) sound moves its centroid by
    ///     the same factor, so it tracks the engine's playback rate in an engine-dominated mix; crossfades between layers
    ///     with different timbre add noise to it.
    /// </summary>
    private static double Centroid(float[] x, int rate)
    {
        double num = 0, den = 0;
        for (var f = 40; f <= 4000; f += 20)
        {
            double re = 0, im = 0, w = 2 * Math.PI * f / rate;
            for (var i = 0; i < x.Length; i++)
            {
                var v = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1)));
                (re, im) = (re + v * Math.Cos(w * i), im + v * Math.Sin(w * i));
            }
            var mag = Math.Sqrt(re * re + im * im);
            (num, den) = (num + f * mag, den + mag);
        }
        return den > 0 ? num / den : 0;
    }

    private static double Pearson(IEnumerable<double> xs, IEnumerable<double> ys)
    {
        var p = xs.Zip(ys).ToArray();
        if (p.Length < 2) return double.NaN;
        double mx = p.Average(t => t.First), my = p.Average(t => t.Second);
        double sxy = p.Sum(t => (t.First - mx) * (t.Second - my)), sxx = p.Sum(t => (t.First - mx) * (t.First - mx)), syy = p.Sum(t => (t.Second - my) * (t.Second - my));
        return sxy / Math.Sqrt(sxx * syy);
    }
}
