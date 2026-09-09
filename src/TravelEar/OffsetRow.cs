using BepInEx.Logging;
using Il2CppInterop.Runtime;
using TMPro;
using TravelEar.Core;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TravelEar;

/// <summary>
/// The read-only "TravelEar offset: N ms" row at the bottom of the game's Audio settings
/// (docs/DESIGN.md "Config and settings UI"; M3 question 5). Every <c>SettingsMenu</c> in the
/// scene (the main menu and the pause menu each own one, and the scene can rebuild them) gets a
/// clone of its Audio category's last <c>SettingsRow</c>, stripped of its controls and of every
/// navigation target, whose title carries the rolling 10 s Offset average from
/// <see cref="OffsetMonitor"/> (<see cref="OffsetRowCaption"/>), refreshed every 10 s. Main
/// thread only, ticked every frame from <see cref="TravelEarBehaviour"/>.
/// <para>
/// Unlike <see cref="GameSymbols.Bind"/>, which hard-fails the whole mod on a missing symbol, the
/// settings-UI types used here are resolved lazily and softly: the first exception anywhere
/// (including a JIT-time <c>MissingMethodException</c> or <c>TypeLoadException</c> in one of the
/// methods below, which is why every interop access sits in a method only ever called inside
/// the try in <see cref="Tick"/>) disables the row, destroys any clone already made and logs one
/// warning; the Offset figure stays in the log. A settings-UI change in a game update must not
/// take Local Voice down.
/// </para>
/// </summary>
internal sealed class OffsetRow : IDisposable
{
    private const float CaptionIntervalSeconds = 10f;
    private const float ScanIntervalSeconds = 2f;
    private const string RowName = "TravelEar Offset";

    public static OffsetRow Instance { get; private set; }

    private readonly ManualLogSource _log;
    private readonly Func<double> _averageMs;
    private readonly List<Entry> _entries = new();
    private float _nextCaptionAt;
    private float _nextScanAt;
    private string _caption = OffsetRowCaption.Format(double.NaN);
    private bool _disabled;

    /// <summary>One installed row: the menu it belongs to (by native pointer) and the clone.</summary>
    private sealed class Entry
    {
        public IntPtr MenuPointer;
        public GameObject Row;
        public LocalizedText Title;
    }

    public OffsetRow(ManualLogSource log, Func<double> averageMs)
    {
        _log = log;
        _averageMs = averageMs;
        Instance = this;
    }

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>Main thread, every frame: caption refresh every 10 s, menu scan every 2 s. Never throws.</summary>
    public void Tick()
    {
        if (_disabled) return;
        try
        {
            var now = Time.unscaledTime;
            if (now >= _nextCaptionAt)
            {
                _nextCaptionAt = now + CaptionIntervalSeconds;
                RefreshCaption();
            }
            if (now >= _nextScanAt)
            {
                _nextScanAt = now + ScanIntervalSeconds;
                Scan();
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    /// <summary>Formats the current average and applies it to every installed row.</summary>
    private void RefreshCaption()
    {
        _caption = OffsetRowCaption.Format(_averageMs());
        foreach (var entry in _entries)
        {
            if (entry.Row == null) continue;
            Apply(entry.Title, _caption);
        }
    }

    /// <summary>
    /// Drops entries whose clone the scene destroyed, then installs a row on every live
    /// <c>SettingsMenu</c> (inactive ones included: the pause menu sits inactive until opened)
    /// that has none yet. <c>Object.FindObjectOfType</c> is stripped from this IL2CPP build
    /// (see <c>LocalVoiceRenderer.FindAnchor</c>), hence <c>Resources.FindObjectsOfTypeAll</c>,
    /// which also returns prefabs and assets: those are skipped by their hide flags and by not
    /// living in a valid scene.
    /// </summary>
    private void Scan()
    {
        _entries.RemoveAll(e => e.Row == null);

        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SettingsMenu>());
        if (all == null) return;
        foreach (var candidate in all)
        {
            var menu = candidate?.TryCast<SettingsMenu>();
            if (menu == null) continue;
            if ((menu.hideFlags & (HideFlags.HideInHierarchy | HideFlags.NotEditable)) != 0) continue;
            var menuObject = menu.gameObject;
            if (menuObject == null || !menuObject.scene.IsValid()) continue;

            var pointer = menu.Pointer;
            if (_entries.Exists(e => e.MenuPointer == pointer)) continue;
            Install(menu);
        }
    }

    /// <summary>
    /// Clones the Audio category's last row under the same parent, right after it, and strips it
    /// down to its title. The clone's <c>SettingsRow</c> is disabled in the same frame as the
    /// Instantiate so its Start never runs (Start looks up a settings hanger by
    /// <c>settingsType</c> and logs a miss). Every <c>Selectable</c> under the clone is
    /// deactivated so the row has no navigation targets: the category wired its navigation over
    /// the original rows in <c>SettingsCatagory.Start</c>, and the clone must not be reachable.
    /// </summary>
    private void Install(SettingsMenu menu)
    {
        var category = menu.catagoryAudio;
        if (category == null) throw new InvalidOperationException("SettingsMenu.catagoryAudio is missing.");
        var rows = category.rows;
        if (rows == null || rows.Length == 0) throw new InvalidOperationException("The Audio SettingsCatagory has no rows.");
        // The last row is the template. Every Audio row is a slider with no `title` (runs 3-4):
        // its heading ("MENU MUSIC VOLUME") is a plain LocalizedText child, and `sliderLabel` is
        // the narrow value box, so the caption goes to the widest heading text (PickCaption).
        SettingsRow template = null;
        for (var i = rows.Length - 1; i >= 0 && template == null; i--)
            if (rows[i] != null) template = rows[i];
        if (template == null) throw new InvalidOperationException("The Audio SettingsCatagory has no non-null SettingsRow to clone.");

        var templateObject = template.gameObject;
        var templateTransform = templateObject.transform;
        var clone = Object.Instantiate(templateObject, templateTransform.parent);
        var go = clone?.TryCast<GameObject>();
        if (go == null) throw new InvalidOperationException("Instantiate of the template row returned no GameObject.");
        go.name = RowName;
        go.transform.SetSiblingIndex(templateTransform.GetSiblingIndex() + 1);

        LocalizedText title;
        string captionField = null;
        try
        {
            var row = go.GetComponent<SettingsRow>();
            if (row == null) throw new InvalidOperationException("The cloned row has no SettingsRow component.");
            row.enabled = false;

            title = PickCaption(row, go, out captionField);
            if (title == null) throw new InvalidOperationException("The cloned SettingsRow has no text to caption.");
            var titleTransform = title.transform;

            foreach (var selectable in go.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable == null) continue;
                // A Selectable above the title (the row root, say) cannot be deactivated without
                // hiding the caption; disabling the component takes it out of navigation just the same.
                if (titleTransform.IsChildOf(selectable.transform)) selectable.enabled = false;
                else selectable.gameObject.SetActive(false);
            }
            foreach (var text in go.GetComponentsInChildren<LocalizedText>(true))
            {
                if (text == null || text.transform.IsChildOf(titleTransform)) continue;
                text.gameObject.SetActive(false);
            }
            foreach (var text in go.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text == null || text.transform.IsChildOf(titleTransform)) continue;
                text.gameObject.SetActive(false);
            }

            Apply(title, _caption);
        }
        catch
        {
            Object.Destroy(go);
            throw;
        }

        _entries.Add(new Entry { MenuPointer = menu.Pointer, Row = go, Title = title });
        _log.LogInfo($"Offset row: added to the Audio settings ({(menu.isInMainMenu ? "main menu" : "pause menu")}, from row '{templateObject.name}' via {captionField}).");
    }

    /// <summary>
    /// The text that carries the caption: the row's <c>title</c> when it has one, else the widest
    /// <c>LocalizedText</c> in the row that is not a value label (<c>sliderLabel</c>,
    /// <c>arrayLabel</c>) and not inside a child <c>Selectable</c>, i.e. the heading; else the
    /// value label as a last resort. <paramref name="source"/> says which, for the log.
    /// </summary>
    private static LocalizedText PickCaption(SettingsRow row, GameObject go, out string source)
    {
        if (row.title != null) { source = "title"; return row.title; }
        LocalizedText best = null;
        var bestWidth = -1f;
        var slider = row.sliderLabel;
        var array = row.arrayLabel;
        foreach (var text in go.GetComponentsInChildren<LocalizedText>(true))
        {
            if (text == null) continue;
            if (slider != null && text.Pointer == slider.Pointer) continue;
            if (array != null && text.Pointer == array.Pointer) continue;
            var selectable = text.GetComponentInParent<Selectable>();
            if (selectable != null && selectable.gameObject.Pointer != go.Pointer) continue;
            var rect = text.transform.TryCast<RectTransform>();
            var width = rect != null ? rect.rect.width : 0f;
            if (best == null || width > bestWidth) { best = text; bestWidth = width; }
        }
        if (best != null) { source = $"heading '{best.gameObject.name}' ({bestWidth:F0} px wide)"; return best; }
        source = slider != null ? "sliderLabel" : "arrayLabel";
        return slider ?? array;
    }

    /// <summary>
    /// Writes the caption into the title as a raw (unlocalized) value through the game's own
    /// <c>LocalizedText.Change</c>, with the fields and the text element written directly as
    /// the fallback.
    /// </summary>
    private static void Apply(LocalizedText title, string caption)
    {
        if (title == null) return;
        title.displayType = LocalizedText.DisplayType.RawValue;
        title.rawValue = caption;
        try
        {
            // One line, never wrapped into a column (run 4 screenshot).
            var element = title.textElement;
            if (element != null)
            {
                element.enableWordWrapping = false;
                element.overflowMode = TextOverflowModes.Overflow;
            }
        }
        catch (Exception)
        {
            // Cosmetic; the caption still shows.
        }
        try
        {
            title.Change(caption, LocalizedText.DisplayType.RawValue);
        }
        catch (Exception)
        {
            // The direct writes above and below are the fallback.
        }
        var text = title.textElement;
        if (text != null) text.text = caption;
    }

    /// <summary>Question 5's fallback: give up for good, remove the clones, warn once.</summary>
    private void Disable(Exception e)
    {
        _disabled = true;
        DestroyRows();
        _log.LogWarning($"Offset row: unavailable ({e.GetType().Name}: {e.Message}); the Offset figure stays in the log.");
    }

    private void DestroyRows()
    {
        foreach (var entry in _entries)
        {
            try
            {
                if (entry.Row != null) Object.Destroy(entry.Row);
            }
            catch (Exception)
            {
                // Already gone, or not on the main thread; nothing left to keep.
            }
        }
        _entries.Clear();
    }

    public void Dispose()
    {
        try
        {
            DestroyRows();
        }
        catch (Exception)
        {
            // Unload must not fail on a UI teardown.
        }
        if (Instance == this) Instance = null;
    }
}
