using System.Globalization;
using Kansei.Core;
using Kansei.Windowing;
using Touge;

// touge <ISO> [KURS_ZEIT] [--shot out.png]   z. B. touge "Initial D - Special Stage (Japan) (v2.00).iso" AKINA_NIT
// --shot rendert einen Frame als PNG und beendet sich; --at <n> startet bei Punkt n der Fahrlinie;
// --orbit <grad>[:<m>] Kamera ums geparkte Auto (0 vorne, 90 links, 180 hinten), Abstand Standard 5,5 m.
// --lights off|low|high: Autolicht beim Start (Standard: nachts/Regen Abblendlicht, tags aus; im Spiel L/H).
// --autodrive <s>: Pilot fährt die Fahrlinie ab, Log pro Sekunde; ohne --shot ohne Fenster, mit --shot Verfolgerbild am Ende.
//   Hinter dem Ziel: Auslauf bis vor die Endsperre (Drive.Coast), mit --ram Vollgas weiter in die Sperre; Zusammenfassung danach.
// --audio-capture <wav> <s> (mit --autodrive): Spielton offline (OpenAL-Loopback) als WAV + Auswertung, ohne Fenster; --no-music ohne BGM.
// --audio-capture <wav> 0 --sweep: nur Motor, Drehzahlrampe Leerlauf → Begrenzer (Vollgas, 3. Gang) und zurück (Schub), WAV + CSV je Tick.
// --ground <png>: Kollision des Kurses laden, Raycasts timen, Draufsicht mit Wandsegmenten, Sperren und Fahrlinie/Auslauf schreiben, Ausschnitte bei --at, Start, Ziel (ohne Fenster).
// --bench <s>: Pilot fährt höchstens <s> Sekunden (bzw. bis ins Ziel) in Echtzeit mit Verfolgerkamera und Ton; pro Sekunde Position, fps, CPU-ms,
//   Draws, Effekte, GC, Speicher, Wärmezustand; am Ende Frametimes (avg/p99/max, > 18/25 ms) und je 500 m. Mit --flow: erst durch die Menüs (Kurs/Zeit
//   wie angegeben, 24 Autovorschauen), dann das Rennen.
// --quality off: ohne MSAA/Bloom starten (F2 schaltet um).
// --offscreen (Metal): Bild nur in ein eigenes Ziel statt ins Fenster, ohne Display-Takt – mit --bench zeigt die Frametime dann die echten GPU-Kosten.
// --flicker <prefix>: Z-Fighting im Bild messen (8 Punkte der Fahrlinie + 8 Winkel ums Auto, je 3× mit verschobener Rundung), Ausschnitte als <prefix>_course/_car.png;
//   dazu _motion: Kamera in 1-cm-Schritten, je mit/ohne SSR (Springen nasser Spiegelungen).
// --zfight [filter]: Z-Fighting-Kandidaten (fast koplanar, überlappend) aller Kurse und Autos auflisten (ohne Fenster).
// --hud north|overview|off: Minimap nordausgerichtet / ganze Strecke / HUD aus (Standard: mitdrehend; N und F4 schalten um).
// --hud-scale <prozent>: HUD-Größe 80–130 (Standard 100; im Menü Options HUD SIZE).
// --reverse: Gegenrichtung (bergauf): CRS_COLI_<KURS>_1 (Rundkurse _0) + CRS_DRV_<KURS>_O, Start am anderen Ende; im Spiel B.
// --car <NAME|index> (HCAR-Name wie AE86T, FD3S, R32, EVO3 … oder 0–31), --paint <n> (CAR_ENV-Farbe, 0 = Standard).
// --cars <png>: Kontaktbogen aller 32 Autos (Orbit 35°, 4 × 8 Kacheln in CarPaint.Cars-Reihenfolge), dann Ende; mit --hud off.
// --fog: dichter Nebel über dem Tag- oder Nachtkurs (Sicht ~60 m; im Menü Wetter FOG).
// --sun: freie Kamera am Startpunkt schaut zur Sonne (Blendung prüfen).
// Ohne Kurs und ohne Test-Flags (außer --backend) startet das Spiel im Front-End (Ui/FrontEnd: Hinweis, Karten, Titel, Hauptmenü) mit den gespeicherten Einstellungen (Ui/Settings).
// --menu boot|logo|disclaimer|title|mode|quit (Front-End; quit = QUIT GAME mit offener Abfrage) bzw. course|route|time|weather|maker|car|gearbox|intro|pause|records|options bzw. guide|guide-list|guide-talk (Car Guide: Dialog, Liste, Iketani spricht): diesen Schritt/dieses Menü beim Start öffnen (auch mit Test-Flags, z. B. --menu mode --shot out/m.png).
// --flow <dir>: ganzer Ablauf per Skript im Fenster (Titel → Auswahl → Laden → Countdown → Rennen (Pilot, 16×) → Pause → Ziel → Ergebnis → Rekorde → Optionen), PNG je Schritt nach <dir>, Einstellungen bleiben unberührt.
// --jukebox <s>: Renn-Musik (Jukebox) offline ohne Fenster: Zufallsfolge, Weiterschalten, Songende → nächster Titel, Log.
// --frontend-capture <wav>: ganzer Menüablauf per Skript offline (Titel → Auswahl → Countdown → Ergebnis mit erfundener Fahrt) mit Original-SE/BGM als WAV, Log aller Auslöser.
// --shot-size WxH: Größe des --shot-Bildes (Standard 1280x720), z. B. 3200x1800 für die HUD-Skalierung.
// --render-scale <prozent>: 3D-Auflösung in % des Fensters (50–150, Optionen SCREEN), z. B. mit --bench für GPU-Kosten.
// --input-debug: Eingabe-Overlay (Geräte, Rohachsen/-tasten, Lenkung/Pedale wie das Spiel sie liest, Force-Feedback-Anteile); auch beim normalen Start.
// --sim-wheel: virtuelles Lenkrad (Lenkung pendelt, Pedale pumpen) für Bilder/Tests ohne Hardware; --menu controls:keyboard|pad|wheel öffnet die Steuerungsseite.
// --battle <rivale|auto> [--rule race|chase] [--lead player|rival]: Schnellbattle gegen die KI (Telop, Countdown, Battle-HUD, Ergebnis);
//   mit --autodrive <s> ohne Fenster: Autopilot gegen die KI, Log je Sekunde (Abstand, Führung, Kontakte) + Zusammenfassung.
// --ai-bench solo|drift|battle|corners[:KURS,…] [--car X] bzw. human:<datei.rpl>: KI ohne Fenster vermessen (Touge/Race/AiBench): Zeiten je Kurs/Richtung/Auto/
//   Können gegen eine Referenz, Wandtreffer, Schräglauf je Kurventyp, Drift-Prototyp, Battles gegen Rivalen, Straßenbreite in Kurven, ein aufgezeichneter Lauf daneben.
// --legend-sim [--legend-progress <json>] [--autodrive <s>]: Legend of the Streets ohne Fenster, Autopilot fährt jede Rivalenleiter hoch, Freischaltungen + Fortschritt (JSON).
// --player-skill k[,aggr,drift]: der Autopilot der Testläufe (Battles, Legend-/Story-Prüfung) fährt als Spieler mit Können k (sonst 0,8).
// --legend-progress <json>: Legend-Fortschritt aus/in diese Datei (Testläufe sonst nur im Speicher); --flow <dir> --legend: Legend-Ablauf per Skript (2 Battles).
// --menu legend|legend-rivals|legend-card[:KURS/rivale]: Legend-Schritt beim Start öffnen (z. B. --menu legend-card:AKINA/takumi --shot …).
// --menu story[:n[:scene[:teil[:zeile]]|:race|:end]]: STORY-Kapitelwahl, eine Szene, der Rennstart von Kapitel n oder THE END; --progress <n>: Kapitel 0…n−1 gelten
//   als geschafft (nur Testläufe); --flow <dir> --story: Ablauf durch STORY (Wahl, Szene, Battle, Ergebnis, Szene danach, ein verlorenes Kapitel).
// --story-check [n]: Kapiteltabelle und Szenen der Disc gegen die Übersetzung prüfen, dann jedes Kapitel (oder nur n) mit dem Autopiloten fahren (ohne Fenster).
// --story-check media[:n]: jede Manga-Sequenz und Porträt-Szene (oder nur Kapitel n) wie im Spiel laden und prüfen, Untertitel mit Zeiten auf der Stimmspur.
// --story-check audio:n: die Shows von Kapitel n headless über ein Loopback-Gerät abspielen (AUTO aus/an, DECIDE, Überspringen), Pegel und Spuren je 0,5 s, stille Blöcke zählen.
// --headless [--host | --join <ip[:port]>] [--bot] [--port n] [--name X] [--players n] [--races n] [--seconds s] [--net-sim ms[:verlust[:jitter]]] [--net-rule battle|race|free [--ghost-cars]]:
//   Mehrspieler-Teilnehmer ohne Fenster (Touge/Net/Headless): Host oder Client einer echten UDP-Sitzung, Auto per Autopilot (--bot), Log je Sekunde + Zusammenfassung.
// --flow <dir> --versus flow: Versus-Ablauf (geteilter Bildschirm) per Skript statt des Time-Attack-Ablaufs.
// --menu freebattle: VERSUS → VS CPU-Lobby beim Start; --flow <dir> --freebattle: freies Battle gegen die KI per Skript (Lobby → LEAD/CHASE → Ergebnis → RETRY → Pause-Exit → RACE → EXIT).
// --versus split|host|join[:ip[:port]]|online [--bot] [--split vertical] [--car2 X] [--players n] [--net-rule battle|race|free [--ghost-cars]]: Versus direkt (Testläufe/Bilder):
//   geteilter Bildschirm bzw. Online-Host/-Client im Fenster; --bot: Autopilot fährt, Lobby läuft von selbst (Host startet bei --players Spielern).
// --shot-after <s>: --shot erst nach so vielen Sekunden (statt sofort), das Spiel läuft bis dahin normal (Versus, Replay).
// --replay-test <s> [--battle <rivale>] [--drift] [--save <datei.rpl>]: Lauf ohne Fenster aufnehmen, Datei schreiben/lesen, auf frischen Autos abspielen,
//   Positionsfehler je Tick (mit Keyframes) und nur aus Eingaben (Determinismus), Sprünge; optional die Replay-Datei.
// --replay <datei.rpl> [--replay-at <s>] [--replay-cam tv|chase|far|hood|cockpit|bumper|free] [--replay-focus 1]: Replay im Viewer öffnen (z. B. mit
//   --shot), --replay-focus: welchem Auto die Kameras folgen (1 = der Rivale eines Battles).
// --ghost <datei.rpl>: dieser Lauf fährt als Geist mit (sonst mit Menüs der Bestzeit-Lauf); --data-dir <ordner>: anderer App-Daten-Ordner (Einstellungen samt Rekorden, Fortschritt, Replays, Spielstände, Fotos) wie ein echtes Profil, auch in Testläufen;
//   ohne --data-dir schreibt nur ein schlichter Start (ISO [--menu x]) ins echte Profil, jeder andere Lauf in einen Wegwerf-Ordner im Temp-Verzeichnis.
// --menu replay|replay-best|replay-records|replay-delete|saveload|saveload-actions|saveload-name|photo: REPLAY & RECORD, SAVE & LOAD, Fotomodus (Bilder).
// --cam-bench [KURS_ZEIT,…] [--car X]: Kameras ohne Fenster (Pilot mit Drifts bis ins Ziel, beide Richtungen): Bilder hinter/in Geometrie, über dem Nichts, Straße verdeckt, Ruckeln; Replay-Schritte, TV-Schnitte.
// --cam chase|far|hood|cockpit|bumper: Startkamera (sonst Einstellung bzw. Verfolger), im Spiel C / Pad BACK.
// --drift: Pilot reißt alle 7 s (ab 4,5 s) einen 2,5-s-Handbremsdrift (Reifenrauch/Bremsspuren testen), z. B. --autodrive 6.3 --drift --shot.
// Ohne ISO (weder Argument noch INITIALD_ISO): Launcher im Fenster (Touge/Launcher, Ui/LauncherScreen) – startet die zuletzt gewählte Disc direkt,
//   sonst Disc-Liste (Suche in Downloads, Schreibtisch, Dokumente, Home, Laufwerken), BROWSE, Systemdialog, Drag & Drop, Pfad einfügen.
//   --launcher: die gemerkte Disc nicht starten; --menu browse [--browse <ordner>]: gleich im Dateibrowser; --drop <datei>: wie hineingezogen;
//   --shot <png> [--shot-size WxH]: ein Bild des Launchers (nach der Suche), dann Ende – mit --data-dir und gemerkter Disc ein Bild des Front-Ends.
// --dualsense-test: 20 s alle DualSense-Effekte nacheinander (Lightbar, Trigger, Rumble, Lautsprecher, Spieler-/Mikro-LED, Neigung), jeder Befehl mit SDL-Rückgabe geloggt, dann Ende.
// --dualsense-log: jeden an den DualSense gesendeten Befehl loggen (auch beim normalen Start).
// started from Finder/Explorer/launcher (no args = launcher, or just the ISO): no console to read, so everything also goes to touge.log in the profile folder
// ponytail: "desktop start" guessed from the arguments, not from the console; an explicit --log flag if that ever guesses wrong
// --texts pack <json> <tl> | unpack <tl> <json>: a language file to/from the packed form kept in texts/ (Story/Translation.Pack)
if (args is ["--texts", var textsOp and ("pack" or "unpack"), var textsFrom, var textsTo])
{
    if (textsOp == "pack") File.WriteAllBytes(textsTo, Touge.Story.Translation.Pack(File.ReadAllText(textsFrom)));
    else File.WriteAllText(textsTo, Touge.Story.Translation.Unpack(File.ReadAllBytes(textsFrom)));
    return 0;
}
if (args.All(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)))
    Touge.LogFile.Start(Path.Combine(Path.GetDirectoryName(Touge.Ui.Settings.FilePath)!, "touge.log"));
var iso = args.Where((a, i) => i == 0 || args[i - 1] is not ("--drop" or "--browse")).FirstOrDefault(a => a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
          ?? Environment.GetEnvironmentVariable("INITIALD_ISO") ?? "";
// double-click/drag & drop with only a moved or wrong ISO: the launcher shows why (as if dropped there) instead of a silent exit
string? badIso = null;
if (args is [var isoArg] && isoArg == iso && !Disc.Check(iso).Ok) (badIso, iso) = (iso, "");
var launcher = iso == "";
// --hidden or TOUGE_HIDDEN=1 (the agents' run wrapper): window never shown, no focus steal, frames rendered offscreen (Metal only)
var hidden = (args.Contains("--hidden") || Environment.GetEnvironmentVariable("TOUGE_HIDDEN") == "1") && KanseiApp.ResolveBackend(args) == GraphicsBackend.Metal;
Kansei.Audio.AudioDevice.Silent = hidden || args.Contains("--mute"); // background runs make no sound
Kansei.Input.GamepadState.Trace = args.Contains("--dualsense-log") || args.Contains("--dualsense-test");
string[] launcherFlags = ["--launcher", "--menu", "--shot", "--shot-size", "--data-dir", "--backend", "--drop", "--browse", "--input-debug", "--sim-wheel", "--hint-device", "--dualsense-log"];
string[] valueFlags = ["--drop", "--browse", "--story-check", "--progress", "--battle", "--rule", "--lead", "--flow", "--shot", "--at", "--orbit", "--ground", "--autodrive", "--backend", "--bench", "--quality", "--audio-capture", "--zfight", "--flicker", "--hud", "--hud-scale", "--car", "--paint", "--cars", "--menu", "--shot-size", "--livery", "--frontend-capture", "--lights", "--render-scale", "--jukebox", "--legend-progress",
    "--join", "--port", "--name", "--net-sim", "--players", "--races", "--seconds", "--net-rule", "--versus", "--split", "--car2", "--shot-after",
    "--replay-test", "--ai-bench", "--replay", "--replay-at", "--replay-cam", "--replay-focus", "--save", "--data-dir", "--ghost", "--hint-device", "--cam", "--player-skill", "--ai-level", "--cam-bench"];
string? Arg(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (launcher ? badIso == null && !args.Where((a, i) => i == 0 || !valueFlags.Contains(args[i - 1])).All(launcherFlags.Contains) : !File.Exists(iso))
{
    Console.Error.WriteLine("usage: touge <Initial D Special Stage (SLPM-65268).iso> [KURS_ZEIT, z. B. AKINA_DAY]  (oder INITIALD_ISO setzen; ohne ISO: Launcher)");
    return 1;
}
// menus (and the saved settings) only when started plainly: any course or test flag means a scripted run
var plain = args.Where((a, i) => a != iso && a != badIso && a != "--backend" && (i == 0 || args[i - 1] != "--backend")).All(a => a is "--menu" or "--launcher" or "--input-debug" or "--sim-wheel" or "--hint-device" or "--dualsense-log" || a == Arg("--menu") || a == Arg("--hint-device"));
// app data (settings, records, progress, replays, save slots, photos): --data-dir, the real profile for a plain start, a throwaway folder for any other run
Touge.Ui.Settings.FilePath = Touge.Ui.Settings.RunFile(Arg("--data-dir"), plain);
AppDomain.CurrentDomain.ProcessExit += (_, _) => Touge.Story.Translation.SaveMissing(); // texts the language lacks, for the translator
if (plain) Updater.Check(); // a newer release: UPDATE TO … on the main menu (packaged builds only)
// --car: HCAR name (AE86T, FD3S, R32, EVO3, …) or index 0–31 in that list (Touge.Formats.CarPaint.Cars)
var carArg = Arg("--car") ?? "AE86T";
var carIndex = int.TryParse(carArg, out var ci) ? ci : Array.FindIndex(Touge.Formats.CarPaint.Cars, c => c.Equals(carArg, StringComparison.OrdinalIgnoreCase));
if ((uint)carIndex >= Touge.Formats.CarPaint.Cars.Length)
{
    Console.Error.WriteLine($"--car {carArg}: unbekannt, möglich: {string.Join(' ', Touge.Formats.CarPaint.Cars)} oder 0–{Touge.Formats.CarPaint.Cars.Length - 1}");
    return 1;
}
var car = Touge.Formats.CarPaint.Cars[carIndex];
var paint = int.Parse(Arg("--paint") ?? "0");
// --livery: none (no decals/plates), stock (the game's stock car) or rival (default: the anime character's car), also 0–2
if (!Enum.TryParse<Touge.Formats.Livery>(Arg("--livery") ?? "rival", true, out var livery) || !Enum.IsDefined(livery))
{
    Console.Error.WriteLine($"--livery {Arg("--livery")}: unbekannt, möglich: none stock rival");
    return 1;
}
var shot = Arg("--shot");
var at = int.Parse(Arg("--at") ?? "0");
float? orbit = Arg("--orbit") is { } o ? float.Parse(o.Split(':')[0], CultureInfo.InvariantCulture) : null;
var orbitDistance = Arg("--orbit") is { } od && od.Split(':') is [_, var m] ? float.Parse(m, CultureInfo.InvariantCulture) : 5.5f;
if (Arg("--cam") is { } camArg && !Enum.TryParse<CameraView>(camArg, true, out _))
{
    Console.Error.WriteLine($"--cam {camArg}: unbekannt, möglich: chase far hood cockpit bumper");
    return 1;
}
if (Arg("--lights") is { } lightArg && !Enum.TryParse<Headlights.Mode>(lightArg, true, out _))
{
    Console.Error.WriteLine($"--lights {lightArg}: unbekannt, möglich: off low high");
    return 1;
}
var course = args.Where((a, i) => i == 0 || !valueFlags.Contains(args[i - 1]))
                 .FirstOrDefault(a => a.Contains('_') && !a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? "AKINA_DAY";
float? autodrive = Arg("--autodrive") is { } ad ? float.Parse(ad, CultureInfo.InvariantCulture) : null;
float? bench = Arg("--bench") is { } b ? float.Parse(b, CultureInfo.InvariantCulture) : null;
// --player-skill k[,aggression,drift]: the autopilot that drives the player's car in test runs (battles, Legend/Story checks) as a player of skill k (default 0.8)
if (Arg("--player-skill") is { } playerSkill)
{
    // k, or k,aggression,drift (a mirror of a rival's style)
    var ps = playerSkill.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    Touge.Race.BattleRun.Autopilot = ps.Length == 3 ? new(ps[0], ps[1], ps[2]) : Touge.Race.BattleRun.Autopilot with { Skill = ps[0] };
}
if (Arg("--frontend-capture") is { } frontWav)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.FrontEnd(isoFile, frontWav) ? 0 : 2;
}
if (args.Contains("--story-check"))
{
    // the story's chapter table and scenes against the English, then every chapter with the autopilot (Touge/Story)
    using var isoFile = new Touge.Formats.Iso9660(iso);
    if (Arg("--story-check") is { } media && media.StartsWith("media"))
        return Touge.Story.StoryHeadless.Media(iso, int.TryParse(media.Split(':').Last(), out var ch) ? ch : null) ? 0 : 2;
    if (Arg("--story-check") is { } audio && audio.StartsWith("audio:"))
        return Touge.Story.StoryHeadless.Audio(iso, int.Parse(audio[6..])) ? 0 : 2;
    if (Arg("--story-check") == "calibrate")
    {
        Touge.Story.StoryHeadless.Calibrate(isoFile);
        return 0;
    }
    return Touge.Story.StoryHeadless.Run(isoFile, int.TryParse(Arg("--story-check"), out var only) ? only : null) ? 0 : 2;
}
if (Arg("--jukebox") is { } jukeboxSeconds)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.Jukebox(isoFile, float.Parse(jukeboxSeconds, CultureInfo.InvariantCulture)) ? 0 : 2;
}
if (Arg("--audio-capture") is { } wav)
{
    var capIndex = Array.IndexOf(args, "--audio-capture");
    if (args.Contains("--sweep"))
    {
        using var sweepIso = new Touge.Formats.Iso9660(iso);
        return AudioCapture.Sweep(sweepIso, wav, car) ? 0 : 2;
    }
    if (autodrive == null || capIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("usage: touge <iso> [KURS] --autodrive <s> --audio-capture <out.wav> <s> [--no-music]");
        return 1;
    }
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return AudioCapture.Run(isoFile, course.ToUpperInvariant(), at, autodrive.Value, wav, float.Parse(args[capIndex + 2], CultureInfo.InvariantCulture),
        !args.Contains("--no-music"), car) ? 0 : 2;
}
if (args.Contains("--headless"))
{
    var port = int.Parse(Arg("--port") ?? Touge.Net.NetSession.DefaultPort.ToString());
    System.Net.IPEndPoint? join = null;
    if (Arg("--join") is { } joinArg && (join = Touge.Net.NetLink.Resolve(joinArg, port)) == null)
    {
        Console.Error.WriteLine($"--join {joinArg}: Adresse unbekannt");
        return 1;
    }
    if (join == null && !args.Contains("--host"))
    {
        Console.Error.WriteLine("--headless braucht --host oder --join <ip[:port]>");
        return 1;
    }
    var config = new Touge.Net.RaceConfig(course.ToUpperInvariant(), args.Contains("--reverse"), args.Contains("--fog"),
        Arg("--net-rule") switch { "race" => Touge.Net.NetRule.Race, "free" => Touge.Net.NetRule.Free, _ => Touge.Net.NetRule.Battle }, args.Contains("--ghost-cars"));
    return Touge.Net.Headless.Run(new Touge.Net.Headless.Options(iso, join, port, args.Contains("--bot"), Arg("--name") ?? (join == null ? "HOST" : "BOT"), car, paint, config,
        int.Parse(Arg("--players") ?? "2"), float.Parse(Arg("--seconds") ?? "600", CultureInfo.InvariantCulture),
        Arg("--net-sim") is { } sim ? Touge.Net.NetSim.Parse(sim) : null, int.Parse(Arg("--races") ?? "1")));
}
// --battle <rival|car>[@skill] [--rule race|chase] [--lead player|rival] [--ai-level easy|normal|hard|legend]: quick battle against
// the AI (Touge/Race), the rival's skill optionally set (takumi@0.9) or as the free battle's AI level sets it
Touge.Race.BattleSetup? battle = null;
if (Arg("--battle") is { } rivalArg)
{
    try
    {
        var rival = Touge.Race.Rivals.Find(rivalArg.Split('@')[0]);
        if (rivalArg.Split('@') is [_, var rivalSkill]) rival = rival with { Style = rival.Style with { Skill = float.Parse(rivalSkill, CultureInfo.InvariantCulture) } };
        battle = new Touge.Race.BattleSetup(rival, Arg("--rule") is "chase" or "leadchase" ? Touge.Race.BattleRule.LeadChase : Touge.Race.BattleRule.Race,
            Arg("--lead") == "player" ? 0 : 1);
        // --ai-level easy|normal|hard|legend: as the free battle sets the rival up (skill band, engine, rubber band, mistakes)
        if (Arg("--ai-level") is { } level)
            battle = Touge.Ui.FreeBattle.Setup(new Touge.Ui.FreeBattleChoice
            {
                Rival = rival.Id, Rule = battle.Rule, PlayerLeads = battle.Leader == 0,
                Level = Enum.Parse<Touge.Ui.AiLevel>(level, true),
            });
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}
// --ai-bench: KI ohne Fenster vermessen (siehe oben)
if (Arg("--ai-bench") is { } bench1)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var parts = bench1.Split(':');
    return Touge.Race.AiBench.Run(isoFile, parts[0], parts.Length > 1 ? parts[1].Split(',') : [], Arg("--car")) ? 0 : 2;
}
// --cam-bench [KURS_ZEIT,…] [--car X]: Kameras ohne Fenster vermessen (Touge/CameraBench)
if (args.Contains("--cam-bench"))
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var list = Arg("--cam-bench") is { } cb && !cb.StartsWith("--") ? cb.Split(',') : [];
    return CameraBench.Run(isoFile, list, car) ? 0 : 2;
}
// --legend-sim [--legend-progress <json>] [--car X] [--autodrive <s per battle>]: Legend of the Streets headless, the autopilot up every ladder
if (args.Contains("--legend-sim"))
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return Touge.Race.LegendSim.Run(isoFile, car, Arg("--legend-progress"), autodrive ?? 600) ? 0 : 2;
}
if (Arg("--replay-test") is { } replaySeconds)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    return Touge.Replays.ReplayProof.Run(isoFile, course.ToUpperInvariant(), args.Contains("--reverse"), car, float.Parse(replaySeconds, CultureInfo.InvariantCulture), battle,
        args.Contains("--drift"), Arg("--save")) ? 0 : 2;
}
if (autodrive is { } battleSeconds && shot == null && battle != null)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var drive = new Drive(isoFile, course.ToUpperInvariant(), args.Contains("--reverse"), Kansei.Physics.CarSpecs.All[car]);
    return Touge.Race.BattleRun.Headless(drive, battle, battleSeconds, car, args.Contains("--rubber-band")) ? 0 : 2;
}
if (autodrive is { } seconds && shot == null)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var drive = new Drive(isoFile, course.ToUpperInvariant(), args.Contains("--reverse"), Kansei.Physics.CarSpecs.All[car]);
    drive.ResetTo(at);
    (drive.ForceDrift, drive.Ram) = (args.Contains("--drift"), args.Contains("--ram"));
    return drive.AutoDrive(seconds) ? 0 : 2;
}
if (args.Contains("--zfight"))
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var filter = Arg("--zfight");
    ZFightReport.Run(isoFile, filter == null || filter.StartsWith("--") ? null : filter);
    return 0;
}
if (Arg("--ground") is { } groundPng)
{
    using var isoFile = new Touge.Formats.Iso9660(iso);
    var c = course.ToUpperInvariant();
    CourseGround.Proof(isoFile, c, groundPng, at, args.Contains("--reverse"));
    return 0;
}

// --hint-device pad|wheel|keys: button hints of that device whatever is pressed (screenshots)
Touge.Ui.Hints.Forced = Arg("--hint-device") switch { "pad" => Touge.DeviceKind.Pad, "wheel" => Touge.DeviceKind.Wheel, "keys" => Touge.DeviceKind.Keyboard, _ => null };
// a plain start opens the window as saved (Options: SCREEN), test runs always in a 1600×900 window
var saved = plain ? Touge.Ui.Settings.Load() : new Touge.Ui.Settings();
var shotSize = Arg("--shot-size") is { } size && size.Split('x') is [var sw, var sh] ? (int.Parse(sw), int.Parse(sh)) : (1280, 720);
// the game: from the ISO argument, or from the launcher's disc (then a player's start with menus, and Options → GAME DISC leads back)
TougeGame NewGame(string isoPath, Action? changeDisc) => new TougeGame(isoPath, course.ToUpperInvariant(), shot, at, orbit, autodrive, bench, Arg("--quality") != "off", args.Contains("--drift"), Arg("--flicker"))
    { HudMode = Arg("--hud"), HudScale = Arg("--hud-scale") is { } hs ? float.Parse(hs, CultureInfo.InvariantCulture) / 100 : 1, Reverse = args.Contains("--reverse"), Fog = args.Contains("--fog"), Car = car, Paint = paint, Livery = livery, ContactSheet = Arg("--cars"), LookAtSun = args.Contains("--sun"), OrbitDistance = orbitDistance,
      VersusStart = Arg("--versus"), VersusBot = args.Contains("--bot"), VersusVertical = Arg("--split") == "vertical", Car2 = Arg("--car2"),
      VersusPlayers = int.Parse(Arg("--players") ?? "2"), NetSim = Arg("--net-sim") is { } vsSim ? Touge.Net.NetSim.Parse(vsSim) : null,
      NetPort = Arg("--port") is { } vsPort ? int.Parse(vsPort) : null, PlayerName = Arg("--name"), VersusGhost = args.Contains("--ghost-cars"), VersusRule = Arg("--net-rule") switch { "race" => Touge.Net.NetRule.Race, "free" => Touge.Net.NetRule.Free, _ => Touge.Net.NetRule.Battle },
      ShotAfter = Arg("--shot-after") is { } after ? float.Parse(after, CultureInfo.InvariantCulture) : 0,
      Battle = battle, LegendProgressPath = Arg("--legend-progress"), LegendFlow = args.Contains("--legend"), ShotBattleResult = args.Contains("--battle-result"), Lights = Arg("--lights") is { } lights ? Enum.Parse<Headlights.Mode>(lights, true) : null,
      StartCamera = Arg("--cam") is { } cam ? Enum.Parse<CameraView>(cam, true) : null,
      RenderScale = Arg("--render-scale") is { } rs ? int.Parse(rs) : 100,
      InputDebug = args.Contains("--input-debug"), SimWheel = args.Contains("--sim-wheel"), DualSenseTest = args.Contains("--dualsense-test"),
      SaveRuns = Arg("--data-dir") != null, ReplayFile = Arg("--replay"), GhostFile = Arg("--ghost"), ReplayAt = Arg("--replay-at") is { } ra ? float.Parse(ra, CultureInfo.InvariantCulture) : 0,
      ReplayCam = Enum.TryParse<Touge.Ui.ReplayViewer.Camera>(Arg("--replay-cam") ?? "tv", true, out var rc) ? rc : Touge.Ui.ReplayViewer.Camera.Tv,
      ReplayFocus = int.Parse(Arg("--replay-focus") ?? "0"),
      UseMenus = plain || changeDisc != null, StartMenu = changeDisc != null && Arg("--menu") == "browse" ? null : Arg("--menu"), ChangeDisc = changeDisc, Flow = Arg("--flow"), Offscreen = hidden || args.Contains("--offscreen"),
      StoryFlow = args.Contains("--story"), SaveLoadFlow = args.Contains("--saveload"), FreeBattleFlow = args.Contains("--freebattle"), FreePlayFlow = args.Contains("--freeplay"), FourPassFlow = args.Contains("--fourpasses") || args.Contains("--fourpasses-wet"), FourPassWet = args.Contains("--fourpasses-wet"), StoryProgress = int.TryParse(Arg("--progress"), out var progress) ? progress : 0,
      ShotSize = shotSize };
KanseiApp.Run(launcher ? new LauncherGame(NewGame, args.Contains("--launcher"), shot, shotSize, Arg("--menu"), Arg("--drop") ?? badIso, Arg("--browse")) : NewGame(iso, null), new WindowSettings
{
    Title = launcher ? "Touge" : $"Touge – {course}",
    WindowPixelWidth = saved.Width,
    WindowPixelHeight = saved.Height,
    VSync = saved.VSync,
    Hidden = hidden,
    FullscreenMode = saved.Display switch
    {
        Touge.Ui.Settings.DisplayMode.Borderless => FullscreenMode.Borderless,
        Touge.Ui.Settings.DisplayMode.Fullscreen => FullscreenMode.Exclusive,
        _ => FullscreenMode.Windowed,
    },
    Backend = KanseiApp.ResolveBackend(args),
});
return 0;
