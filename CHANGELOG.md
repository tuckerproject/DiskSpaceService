# Changelog

All notable changes to this project will be documented in this file.

## v2.0.0 — Major Architecture Update

This release introduces a complete overhaul of the DiskSpaceService architecture, focusing on reliability, clarity, and real‑world operational behavior. It replaces the legacy v1.0 alerting logic with a clean, state‑driven system designed to eliminate noise, prevent missed alerts, and support multi‑machine monitoring.

### New Features
- Added **NOT_READY** state detection for drives that are unavailable or unmounted.
- Added **machine‑name prefix** to all alert messages for multi‑machine environments.
- Added **state file persistence** to prevent alert spam on reboot.
- Added **network‑ready check** to avoid failed alert sends during early boot.
- Added **SQL reporter skip logic** for unavailable drives.

### Improvements
- Replaced threshold‑only alerting with a **state machine** (NORMAL, ALERT, NOT_READY).
- Eliminated false positives and startup alert noise.
- Improved logging clarity and consistency across all components.
- Simplified alerting architecture with unified message generation.
- Reduced unnecessary config complexity from v1.0.

### Breaking Changes
- v1.0 alert behavior has been fully replaced by the new state‑based system.
- Legacy configuration flags removed or consolidated.
- Alert messages now include the machine name by default.

### Notes
- This release is a full replacement for v1.0.
- The `v2-dev` branch has been merged into `master` and archived.
- Future development will continue on a new `v3-dev` branch.