using Touge.Race;
using Touge.Replays;

namespace Touge;

/// <summary>
///     --flow &lt;dir&gt; --saveload --data-dir &lt;dir&gt;: SAVE &amp; LOAD played end to end in one session, like a player:
///     slot 1 with a new name (arcade letters, then typed) → AUTOSAVE off → Legend: beat Akina's first rival → Time Attack run
///     (record + best run) → slot 2 → LOAD slot 1 (ladder, best runs, records as before) → LOAD slot 2 (all back) → RENAME,
///     DELETE (NO, then YES) → AUTOSAVE on → a run autosaves into the slot in use. After each load and the autosave a
///     <c>[SaveLoadCheck] … PASS|FAIL</c> line with the live state; the last line sums them up.
/// </summary>
public sealed partial class TougeGame
{
    public bool SaveLoadFlow { get; init; }

    private int _slChecks, _slFails;
    private DateTime _slBefore;

    private static (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back) S(string at, float wait, string? shot = null, int x = 0, int y = 0, bool ok = false, bool back = false) =>
        (at, wait, shot, x, y, ok, back);

    private static (string, float, string?, int, int, bool, bool)[] Rep(int n, string at, int x = 0, int y = 0) => [.. Enumerable.Repeat(S(at, 0.4f, null, x, y), n)];

    /// <summary>Steps with something besides keys: typing into the name entry ('\b' = Backspace) or a state check.</summary>
    private static readonly Dictionary<string, string> SaveLoadActs = new()
    {
        ["sl_name_arcade"] = "type:\b\b\b\b\b\b\b\bTAKUMI", ["sl_rename_start"] = "type:\b\b\b\b\b\b\b\bRYOSUKE",
        ["sl_bestruns_slot1"] = "check:slot1", ["sl_bestruns_slot2"] = "check:slot2", ["sl_autosave_on"] = "note", ["sl_modes_after_autosave"] = "check:autosave",
    };

    /// <summary>One Time Attack run from the course select on (the choices as last time), pilot at 16×, result → EXIT.</summary>
    private static (string, float, string?, int, int, bool, bool)[] SaveLoadRun(string n) =>
    [
        S("Course", 1, $"sl_course_{n}", ok: true), S("Route", 0.6f, null, ok: true), S("Time", 0.6f, null, ok: true), S("Weather", 0.6f, null, ok: true),
        S("Maker", 0.8f, null, ok: true), S("Car", 1, null, ok: true), S("Gearbox", 0.6f, null, ok: true),
        S("Loading", 0.5f), S("Intro", 1), S("Race", 2, $"sl_race_{n}"), S("Finish", 0.8f, $"sl_finish_{n}"),
        S("Result", 3.6f, $"sl_result_{n}", x: 1), .. Rep(3, "Result", x: 1), S("Result", 0.5f, $"sl_result_exit_{n}", ok: true),
    ];

    /// <summary>LOAD slot <paramref name="slot"/> from the main menu's SAVE &amp; LOAD (its row under the cursor already), then the ladder and the profile's best runs/records.</summary>
    private static (string, float, string?, int, int, bool, bool)[] SaveLoadLoadAndLook(int slot, int fromModes) =>
    [
        .. Rep(Math.Abs(fromModes), "Modes", y: Math.Sign(fromModes)), S("Modes", 0.6f, null, ok: true),
        S("SaveLoad", 1, $"sl_before_load{slot}", y: slot == 1 ? -1 : 1), S("SaveLoad", 0.5f, null, ok: true), S("SaveLoad", 0.5f, null, x: 1),
        S("SaveLoad", 0.5f, $"sl_actions_load{slot}", ok: true), S("SaveLoad", 0.6f, $"sl_confirm_load{slot}", x: -1), S("SaveLoad", 0.4f, null, ok: true),
        S("SaveLoad", 1, $"sl_loaded_slot{slot}", back: true),
        // the career as the loaded profile has it: Legend ladder, REPLAY & RECORD's best runs and records
        .. Rep(2, "Modes", y: 1), S("Modes", 0.4f, null, y: 1), S("Modes", 0.6f, null, ok: true),
        S("LegendCourse", 1.2f, $"sl_courses_slot{slot}", ok: true), S("LegendRivals", 1.5f, $"sl_ladder_slot{slot}", back: true), S("LegendCourse", 0.8f, null, back: true),
        .. Rep(4, "Modes", y: 1), S("Modes", 0.6f, null, ok: true),
        S("ReplayMenu", 1, $"sl_replays_slot{slot}", y: 1), S("ReplayMenu", 0.8f, $"sl_bestruns_slot{slot}", y: 1), S("ReplayMenu", 0.8f, $"sl_records_slot{slot}", back: true),
    ];

    private static readonly (string At, float Wait, string? Shot, int X, int Y, bool Ok, bool Back)[] SaveLoadFlowScript =
    [
        S("Boot", 1.2f, null, ok: true), S("Logo", 1, null, ok: true), S("Title", 1.5f, null, ok: true),
        // main menu (LEGEND) → SAVE & LOAD (three up) → empty slot 1 → name: one letter by pad, then typed → SAVE
        .. Rep(2, "Modes", y: -1), S("Modes", 0.6f, "sl_modes_saveload", y: -1), S("Modes", 0.6f, null, ok: true),
        S("SaveLoad", 1.2f, "sl_empty", ok: true), S("SaveLoad", 0.8f, "sl_name_start", x: -1), S("SaveLoad", 0.4f, null, y: -1), S("SaveLoad", 0.4f, null, y: -1),
        S("SaveLoad", 0.6f, "sl_name_arcade"), S("SaveLoad", 0.6f, "sl_name_typed", ok: true),
        // AUTOSAVE off: the Legend win and the run below must not reach slot 1
        S("SaveLoad", 1, "sl_slot1_saved", y: -1), S("SaveLoad", 0.4f, null, ok: true), S("SaveLoad", 0.6f, "sl_autosave_off", back: true),
        // Legend: Akina's first rival with the autopilot, the ladder after it
        .. Rep(2, "Modes", y: 1), S("Modes", 0.4f, null, y: 1), S("Modes", 0.6f, null, ok: true),
        S("LegendCourse", 1.2f, null, y: 1), S("LegendCourse", 0.6f, "sl_legend_akina", ok: true),
        .. LegendFlowBattle("sl"),
        S("LegendRivals", 1.5f, "sl_ladder_won", back: true), S("LegendCourse", 0.8f, null, back: true),
        // Time Attack: a record and its best run (the ghost)
        S("Modes", 0.8f, null, y: 1), S("Modes", 0.6f, null, ok: true), .. SaveLoadRun("1"),
        // slot 2 holds all of it
        .. Rep(5, "Modes", y: 1), S("Modes", 0.6f, null, ok: true),
        S("SaveLoad", 1, null, y: 1), S("SaveLoad", 0.5f, null, ok: true), S("SaveLoad", 0.6f, "sl_name_slot2", ok: true), S("SaveLoad", 1, "sl_slot2_saved", back: true),
        // back to slot 1, then slot 2 (Modes on SAVE & LOAD = 6, after the look on REPLAY & RECORD = 4)
        .. SaveLoadLoadAndLook(1, 0), .. SaveLoadLoadAndLook(2, 2),
        // RENAME slot 2, DELETE slot 1 (NO first, then YES), AUTOSAVE back on
        .. Rep(2, "Modes", y: 1), S("Modes", 0.6f, null, ok: true),
        S("SaveLoad", 1, "sl_rename_slot", ok: true), .. Rep(2, "SaveLoad", x: 1), S("SaveLoad", 0.5f, "sl_actions_rename", ok: true),
        S("SaveLoad", 0.6f, "sl_rename_start"), S("SaveLoad", 0.6f, "sl_rename_typed", ok: true),
        S("SaveLoad", 1, "sl_renamed", y: -1), S("SaveLoad", 0.4f, null, ok: true), .. Rep(2, "SaveLoad", x: -1), S("SaveLoad", 0.5f, "sl_actions_delete", ok: true),
        S("SaveLoad", 0.6f, "sl_delete_confirm", ok: true), S("SaveLoad", 0.6f, "sl_delete_no", ok: true), S("SaveLoad", 0.4f, null, x: -1),
        S("SaveLoad", 0.6f, "sl_delete_yes", ok: true), S("SaveLoad", 1, "sl_deleted", y: -1), S("SaveLoad", 0.4f, null, ok: true),
        S("SaveLoad", 0.6f, "sl_autosave_on", back: true),
        // a run with AUTOSAVE on: it lands in slot 2 (in use), slot 1 stays deleted
        .. Rep(5, "Modes", y: -1), S("Modes", 0.6f, null, ok: true), .. SaveLoadRun("2"),
        S("Modes", 1, "sl_modes_after_autosave", y: 1), .. Rep(4, "Modes", y: 1), S("Modes", 0.6f, null, ok: true),
        S("SaveLoad", 1.2f, "sl_end", back: true), S("Modes", 1),
    ];

    /// <summary>After step <paramref name="i"/> fired: its typing or check (<see cref="SaveLoadActs"/>), the summary after the last.</summary>
    private void SaveLoadFlowDo(int i)
    {
        if (SaveLoadFlowScript[i].Shot is { } shot && SaveLoadActs.TryGetValue(shot, out var act))
        {
            if (act.StartsWith("type:")) _saveMenu!.Type(act[5..]);
            else if (act == "note") _slBefore = SaveSlots.Default.Read(SaveSlots.Default.ReadState().Active)?.Saved ?? default;
            else SaveLoadCheck(act[6..]);
        }
        if (i == SaveLoadFlowScript.Length - 1)
            Console.WriteLine($"\n[SaveLoadCheck] SUMMARY {(_slFails == 0 && _slChecks == 3 ? "PASS" : "FAIL")} {_slChecks - _slFails}/{_slChecks} checks");
    }

    /// <summary>The live state each screen shows now against what the loaded profile <paramref name="what"/> must have.</summary>
    private void SaveLoadCheck(string what)
    {
        var slots = SaveSlots.Default;
        var state = slots.ReadState();
        var rival = Legend.Of(Array.IndexOf(Legend.CourseIds, "AKINA"))[0].Key;
        var runKey = _settings.RunKey(_courseTime[.._courseTime.LastIndexOf('_')], _drive.Reverse);
        var (beaten, ladder, records, bestRuns, ghost) = (_progress.Beaten(rival), _legend!.Progress.Beaten(rival), _settings.Best.Count, _replayMenu!.Items.Count,
            File.Exists(ReplayStore.BestPath(runKey)));
        var meta = state.Active >= 0 ? slots.Read(state.Active) : null;
        var ok = what switch
        {
            "slot1" => !beaten && !ladder && records == 0 && bestRuns == 0 && !ghost && state.Active == 0,
            "slot2" => beaten && ladder && records >= 1 && bestRuns >= 1 && ghost && state.Active == 1,
            _ => state is { Active: 1, Autosave: true } && meta is { Name: "RYOSUKE", Legend: 1, Records: >= 1 } && meta.Saved > _slBefore && slots.Read(0) == null,
        };
        (_slChecks, _slFails) = (_slChecks + 1, _slFails + (ok ? 0 : 1));
        Console.WriteLine($"\n[SaveLoadCheck] {what} {(ok ? "PASS" : "FAIL")} active={state.Active + 1} {rival}={(beaten ? "WIN" : "open")} ladder={(ladder ? "WIN" : "open")} " +
                          $"records={records} bestRuns={bestRuns} bestFiles={ReplayStore.List(ReplayStore.BestDir).Count} ghost({runKey})={(ghost ? "yes" : "no")} car={_settings.Car} " +
                          $"slot={meta?.Name ?? "-"}:{meta?.Legend}:{meta?.Records}:{meta?.Saved:HH:mm:ss}");
    }
}
