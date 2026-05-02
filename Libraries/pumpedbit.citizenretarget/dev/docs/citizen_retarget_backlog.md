# CARL Backlog

Status as of `2026-04-13`:

- Blender-side solve is considered working.
- Production ingestion into `s&box` now works through `template FBX + generated Citizen full-fork VMDL`.
- This is the first stable baseline, not the finished tool.

## Baseline Kept

- keep the sibling Blender backend as the real solver
- keep the canonical Citizen target pose preset
- keep `template FBX` as the authored animation source that lands in `Assets/models/citizen_custom/animations/...`
- keep the generated `citizen_ual2.vmdl` on the stock Citizen prefab contract

## Immediate Stabilization

- remove or quarantine misleading dead-end output paths from the main workstation flow
- tighten status text and diagnostics so the tool reports the real production path
- keep generated output naming and folder layout predictable across repeated runs
- make sure `Open ModelDoc` always targets the generated full-fork Citizen VMDL

## UX Follow-Ups

- execute the UI direction in [citizen_retarget_ui_rework_spec.md](citizen_retarget_ui_rework_spec.md)
- use [citizen_retarget_ui_domain_workflow_v2.md](citizen_retarget_ui_domain_workflow_v2.md) as the entity model for the next workstation refactor
- improve live preview so animation review is possible without relying on ModelDoc
- make preview state clearer when a run succeeded but no local preview asset is available
- improve queue feedback so clip enqueue, run start, run finish, and post-import stages are obvious
- make the artifact area clearer about what is raw backend output vs imported project output
- continue simplifying the workstation layout now that the core output path is known
- separate `Source Clip`, `Run`, `Result`, and `Session` in both UI vocabulary and interaction flow
- redesign `Runs` toward a run inspector instead of a thin history list
- redesign `Home` around an explicit `source + result` compare pair instead of one implicit clip-driven result

## Mapping And Editing

- improve the bone-map editor readability for large humanoid rigs
- make manual override states more obvious
- reduce noisy unmapped helper-bone warnings when required and optional slots are already satisfied
- add better mirror/reset flows for finger-heavy mapping edits

## Pipeline Polish

- decide whether the generated Citizen VMDL should remain shared-per-folder or become per-job/per-profile
- review whether stock Citizen `AnimFile` defaults such as activities, looping, and compression need clip-specific tuning
- keep motion-quality checks as warnings unless they are proven to protect a real shipping failure
- revisit root-motion handling once the preview path is stronger

## Cleanup Later

- retire or archive obsolete `DMX`/`SMD` spike docs after the new path is documented cleanly
- prune debug-only assets and experiments that no longer inform the production path
- replace legacy wording that still assumes an animation-only `Base Model` extension path
