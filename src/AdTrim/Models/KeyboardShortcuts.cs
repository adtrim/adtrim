namespace AdTrim.Models;

/// <summary>Every keyboard shortcut, as shown by the F1 list. Keep in sync with MainWindow.OnPreviewKeyDown.</summary>
public static class KeyboardShortcuts
{
    public static readonly (string Group, (string Keys, string Action)[] Items)[] Groups =
    [
        ("Playback", [
            ("Space", "Play or pause"),
            ("L", "Play forward; press again for 2× and 4×"),
            ("K", "Pause and return to normal speed"),
            ("J", "Rewind; press again for 2× and 4×"),
            (", / .", "Previous / next frame"),
            ("← / →", "Step back / forward (depends on zoom)"),
            ("[ / ]", "Back / forward one second"),
            ("Home / End", "Start / end of the timeline"),
            ("0–9", "Jump to 0%–90% of the timeline"),
            ("P", "Collapse excluded scenes on or off"),
        ]),
        ("Markers", [
            ("S", "Add a split at the playhead"),
            ("M", "Move the selected split to the playhead"),
            ("C", "Confirm the selected splits"),
            ("Shift+C", "Confirm and go to the next unconfirmed split"),
            ("Ctrl+Shift+C", "Confirm all remaining splits"),
            ("R", "Refine the selected splits"),
            ("Ctrl+R", "Refine all splits"),
            ("Del", "Delete the selected markers"),
            ("Ctrl+Del", "Delete all markings (asks first)"),
            ("X", "Exclude or include the selected segment"),
            ("F2", "Rename the selected segment"),
        ]),
        ("Selecting and moving around", [
            ("Click", "Select a marker or segment"),
            ("Ctrl+click", "Add or remove a marker from the selection"),
            ("Shift+click", "Select every marker between two markers"),
            ("Ctrl+A", "Select all markers"),
            ("Ctrl+Shift+A", "Select the red (uncertain) markers"),
            ("Ctrl+Alt+A", "Select the markers visible on the timeline"),
            ("Esc", "Clear the selection, or cancel a running task"),
            ("Alt+← / Alt+→", "Previous / next marker"),
            ("N / Shift+N", "Next / previous unconfirmed split"),
        ]),
        ("File and view", [
            ("Ctrl+O", "Open a recording"),
            ("Ctrl+Shift+O", "View unexported files"),
            ("Ctrl+N", "New window"),
            ("Ctrl+S", "Save the project"),
            ("Ctrl+W", "Close the project"),
            ("E", "Export"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo"),
            ("Ctrl+= / Ctrl+−", "Zoom in / out"),
            ("Ctrl+0", "Fit the timeline"),
            ("Ctrl+,", "Settings"),
            ("F1", "Show these shortcuts"),
        ]),
    ];
}
