# CARL Workstation v1

CARL is now structured as a reusable s&box library installed at `Libraries/CitizenRetarget`.

## What It Does

- scans a humanoid source `FBX` and lists all clips in the file
- inspects the source skeleton through the existing `ufbx` bridge
- loads a reusable source profile and mapping profile
- builds an editable source-to-Citizen mapping state
- runs the real solve through the bundled Blender backend package
- projects the backend solve onto a stock Citizen animation `FBX` template and imports the generated `FBX` into the project animation folder
- regenerates a shared Citizen `.vmdl` that mirrors the stock Citizen prefab contract instead of using the earlier minimal `Base Model` extension shape
- shows live preview plus Blender-generated preview/comparison videos when available

## Dependencies

- Windows editor/runtime for the current native `win-x64` FBX scanner
- Blender 4.4 recommended
- Rokoko Studio Live for Blender addon installed and enabled in the Blender executable selected by the plugin
- bundled library backend folder containing `tools/blender/retarget_job.py`; optional `Backend Override` is for development only
- project Citizen reference asset, resolved from `models/citizen/citizen_ref.fbx` first, then `models/citizen/citizen.fbx`
- Generic Humanoid, Mixamo, and Quaternius UAL2 source presets

## Library Assets

The library ships these default assets:

- `tools/citizen_retarget/profiles/quaternius_ual2.crtsrc`
- `tools/citizen_retarget/profiles/mixamo_humanoid.crtsrc`
- `tools/citizen_retarget/profiles/ual2_to_citizen.crtmap`

Machine-local setup lives outside the library:

- `.sbox/citizen_retarget/settings.json`

## Setup

1. Open `Editor -> Tools -> CARL`.
2. Open `Diagnostics`.
3. Leave `Backend Override` empty for normal use. The library uses its bundled backend scripts and recipes.
4. Optionally set `Blender Path` to a specific `blender.exe`. If left empty, the plugin checks `CITIZEN_RETARGET_BLENDER`, known Blender folders, then `PATH`.
5. Press `Re-scan Setup`.
6. If setup fails, fix the first blocking issue shown in `Run Health` / `Issues`.
7. Use `Copy Diagnostics` when reporting support issues.

Retargeting is blocked until diagnostics can resolve the bundled backend script, recipe, Blender executable, Rokoko addon, native FBX helper, Citizen reference FBX, and target VMDL that the queue will use.

## Workflow

1. Point the tool at a humanoid `FBX`.
2. Press `Scan`.
3. Pick the best source preset if auto setup needs help.
4. Use `Source Setup` to align source orientation when the skeleton basis is wrong.
5. Review/edit bone mapping on the `Mapping` tab if needed.
6. Select one or more clips and add them to the queue.
7. Press `Run Queue`; setup preflight runs automatically if needed.
8. Review the generated target preview.
9. Open the generated Citizen `.vmdl` in ModelDoc when needed.
10. Open `Diagnostics` and `Copy Diagnostics` only when setup or output looks wrong.

## Notes

- The target stays fixed to Citizen in `v1`.
- Finger chains are optional and can be toggled off per job.
- Queue cancellation is intentionally supported only between jobs, not mid-solve.
- The backend contract now accepts explicit bone-map overrides and a target-pose preset override.
- The backend runner writes a run-local resolved recipe so `targetReferencePath` is project-specific and not a machine hardcode.
- Deleting target animations still has a known refresh/locking issue and is tracked separately.
- The production output path is now `template FBX + generated Citizen full-fork VMDL`.
- The earlier minimal animation-only `Base Model = models/citizen/citizen.vmdl` contract is no longer treated as the production path because it stretched even stock Citizen clips in ModelDoc.
