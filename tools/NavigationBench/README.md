# Deep Tuning navigation regression

Run `./tools/NavigationBench/Run.ps1` on Windows with .NET Framework installed.

The runner builds into a unique temporary directory and constructs the real
window off-screen, with process-local settings and a fresh temporary library.
It never starts the application's runtime, game monitor, suppression workers,
or UI polling. Window construction may read hardware/driver capability data;
the tests do not toggle tuning options or perform system restoration.

Checks cover canceled entry, both sidebars, direct category switching, warning
boundaries, search, return/Escape, remembered category, inner tabs and scroll,
UI rebuilds, and keyboard category activation. Chinese/English, light/dark
screenshots and 100–300% sidebar layout checks are saved with the run logs.
Checks also cover top-aligned return navigation with no duplicate branding,
and removal of the Overview entry while preserving its three footer links.
The production executable is neither built over nor launched.

Interaction checks exercise the real pages, tabs, search flyout, window entry
and exit, selection controls, and collapsible cards with the UI animation clock
enabled but runtime workers still disabled. Tab content and navigation switch
immediately; the sidebar and inner-tab selected indicators animate independently
of page activation. Both real sidebars are checked through their item-invocation
paths, including the deep-tuning callback's duplicate silent synchronization.
Main-page content is revealed by fading only a separately painted background
window; the real page and its native child controls are active immediately.
Checks verify native alpha initialization/progress, the veil's initial paint,
and synchronous WM_PAINT delivery to the real native child tree. Off-screen
children can have an empty visible region, so this does not claim desktop pixel validation.
They also cover
input-transparent/nonactivating window styles, reuse during rapid changes,
and full-page versus clipped background matching with and without a cover.
Native owner disabling, popup opening, hide/move/resize, clock suspension,
window exit, rebuild, and disposal must remove the veil and its frame listener.
The veil is not counted as a real dialog by the auto-hide logic.
Window entry and exit retain opacity transitions without changing position.
Checks cover immediate selected text, indicator progress and reversal, rapid switching,
preserved scroll, fixed geometry, and window animation cleanup. Sidebar checks also
cover reordered/grouped items and resized bottom anchors. Both indicators settle
on hide, resize, handle recreation, and clock freeze/suspension; inner tabs also
settle during page restoration.
Native print-message probes ensure page and tab switches, including indicator frames,
do not capture control snapshots; reveal tests observe the complete child tree.
Background comparisons call the paint methods directly, without capturing the desktop.
The test windows stay
off-screen and do not activate; no interactive desktop test mode is provided.

Backdrop checks use generated images held in memory, without loading or changing
the user's cover image or settings. They compare popup and dialog rendering with
the cover disabled and with two different images, while ensuring the main window
still displays its cover. Nested controls and the fitted-image cache are checked
as well, so an owned dialog cannot inherit or rebuild the main window backdrop.

## Unified add-game checks

Run `./tools/NavigationBench/Run-LibraryChecks.ps1` for the merged installed/running
program list. It checks discovery order, path deduplication, live refresh, preserved
selection, keyboard actions, GPU recommendation timing, icon ownership and rendering,
and the whitelist picker's asynchronous result lifecycle. Layouts cover both languages,
both themes and 100–300% DPI. The picker scroll rails are exercised with native wheel,
keyboard and character-navigation messages, plus synthetic drag geometry without
capturing the mouse. Tests cover range updates, filtering, native scrollbar removal,
theme painting, and handle recreation. This entry uses synthetic candidates and transient
settings; it never shows a window, scans live programs, starts the application runtime,
or captures screenshots. Logs and the input-hash verification are written under `%TEMP%`.
