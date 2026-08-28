# Every-enable family warning UI regression

Run `./tools/FamilyWarningBench/Run.ps1` from the repository root.

Compiles current sources with an isolated UI entry point. Uses transient settings,
a fresh temporary game library and synthetic renderer observations. Never starts
the tuning runtime. Only the owned test process is terminated on the 55-second timeout.

Coverage:

- Both languages, both themes, 100–300% DPI: warning content, text bounds and safe default action.
- Unobserved renderer, observed renderer, and a profile named 英雄联盟.
- Cancel, accept, disable, re-enable, and cancel again through the actual UI handler and modal dialog.
- Persisted library policy and renderer badge preservation.

2026-08-28: 368 assertions passed (including 300 layout assertions).
The broader pre-existing `LibraryFamilyUiChecks.Run` also was attempted and failed
in the unrelated AddGameDialog scan-limit hint height check; this focused bench
does not claim that broader suite passes.
