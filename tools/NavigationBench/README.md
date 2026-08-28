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
