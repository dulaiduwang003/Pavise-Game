# Isolated regression checks

This directory contains historical test fixtures. Current checks compile the production source under `src/PaviseApp` and verify the development version `3.2.0.2`.

Run scripts from the repository root with a separate output directory. Fixtures copy source before replacing system endpoints, use transient settings, and keep windows hidden or on a private desktop. They do not start the Pavise product entry point. Read each runner's scope before use; test success applies to its frozen source snapshot.

| Area | Entry point |
| --- | --- |
| Workspace, release notes and AI catalog | `tools/Test-OfficialWorkspace.ps1` |
| Build identity and retained contracts | `build.cmd -b dev <output.exe> --selftest` |
| Laboratory controllers and recovery | `tools/Test-OfficialLaboratory.ps1` |
| Laboratory power and current-plan idle | `tools/Test-LaboratoryPower.ps1`, `tools/Test-CurrentPlanIdle.ps1` |
| Thread affinity | `tools/Test-OfficialThreadAffinity.ps1 -Mutations` |
| Managed power resources and routing | `tools/Test-SharedManagedPower.ps1` |
| Power restoration faults | `tools/Test-PowerRecoveryFaults.ps1` |
| Configuration presets | `tools/Test-ConfigurationPresets.ps1`, `tools/Test-ConfigurationPresetReads.ps1`, `tools/Test-ConfigurationPresetProduction.ps1` |
| Explicit graphics OFF and recovery | `tools/Test-GraphicsDisable.ps1` |
| In-app update manifest, installer and UI | `tools/Test-SelfUpdateManifest.ps1`, `tools/Test-SelfUpdateInstaller.ps1`, `tools/Test-SelfUpdateUi.ps1` |

Managed power checks verify original resource identities, architecture routing, use of existing local performance plans, preservation of user preferences and exact restoration. Native power endpoints use in-memory substitutes. See [managed power behavior](../../docs/MANAGED-POWER-PRESETS.md).

AI tuning checks cover every currently available feature and user-visible operation, including GPU interrupt inspection, placement, reboot verification, separate MSI repair and restoration. Retired capabilities must remain absent. Coverage is derived from the current catalog rather than a historical item count.

Workspace checks cover DPI, language, theme, narrow layouts and native resize behavior. Fixed telemetry and in-memory settings isolate rendering; these screenshots are fixture output, not hardware measurements.

Fault checks exercise write/readback failures, pending recovery, cancellation, stale work and later external changes. Negative controls verify that removing a relevant protection causes a check to fail. These checks do not establish game performance gains or universal hardware compatibility.
