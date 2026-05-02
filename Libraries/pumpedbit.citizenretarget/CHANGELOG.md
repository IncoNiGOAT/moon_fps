# Changelog

## Unreleased

### Fixed

- Fixed Rokoko-backed retargeting for Mixamo clips where hand bones exist in the skeleton but have no animation curves, such as No Fingers exports.

## 0.1.0-alpha.3 - 2026-04-30

### Added

- Added a Diagnostics `Download Helper` flow that downloads the Windows native FBX helper from GitHub Releases, verifies SHA256, extracts `ual2_ufbx_helper.dll`, and re-runs setup diagnostics.
- Added release-project preparation for s&box publishing without ignored local files.

### Changed

- Switched the project license to MIT for public collaboration.
- Marked historical DMX/SMD/audit paths as deprecated diagnostic-only code.
- Added the CARL logo as bundled library branding.
- Hydrate the local Library Manager package thumbnail from the bundled CARL logo.
- Use a local filesystem thumbnail path for Library Manager rendering.
- Rebranded public-facing UI and documentation to CARL, the Citizen Animation Retargeting Library.

## 0.1.0 - 2026-04-28

Initial local-library release under the original project name.

### Added

- s&box editor library layout under `Libraries/CitizenRetarget`.
- Home workflow for scanning FBX sources, queueing clips, retargeting, and previewing target results.
- Mapping workflow with source preset selection, source orientation calibration, automatic mapping, manual mapping edits, and profile saving.
- Diagnostics workflow for setup checks, run health, issues, artifacts, logs, and support copy.
- Bundled Blender backend scripts, recipes, profiles, native FBX helper, and Citizen compensation data.
- Local settings stored in `.sbox/citizen_retarget/settings.json`.
- Package/install scripts for copy-paste distribution.

### Known Issues

- Target-animation delete can require a manual UI refresh in some cases.
- Windows is the only supported release target.
- Blender and the Rokoko Studio Live Blender addon must be installed separately.
