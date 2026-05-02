# CARL UI Rework Spec

Status as of `2026-04-13`:

- core retarget flow works
- source preview can be generated before retarget
- compare preview exists in MVP form
- the current workstation still shows too much information too early

Companion reading:

- [citizen_retarget_ui_domain_workflow_v2.md](citizen_retarget_ui_domain_workflow_v2.md)

This spec covers layout and UX direction.
The companion document covers domain entities such as `Source Library`, `Source Clip`, `Run`, `Result`, and `Session`.

## Design Intent

The workstation should feel simple on first open and deep only when needed.

The default experience should answer only three questions:

1. What source file am I working with?
2. Which clip am I looking at?
3. How does the source compare to the Citizen result?

Everything else should be progressive disclosure.

## Core UX Principles

- `Compare first`: the main screen should lead with source vs result, not forms.
- `Source first`: selecting a clip should show the source preview before any retarget run.
- `Progressive disclosure`: mapping, queue management, diagnostics, and advanced settings should stay out of the way until asked for.
- `Icon-led UI`: frequent actions should prefer icons with tooltips over repeated text labels.
- `Panel-level loading`: individual panels can be loading or unavailable without freezing the entire window.
- `State-based layout`: the app should visibly change between empty, ready, running, and review states.
- `Low-noise status`: useful status should be compressed into chips, icons, and short summaries instead of long blocks of text.

## UI Implementation Rules

- Use native editor affordances first. Prefer `IconButton` with Material-style icon names and a tooltip for compact controls instead of text placeholders like `v`, `>`, or `...`.
- Repeated icon-only actions must have tooltips. A user should never need to guess what a small button does.
- Collapsible sections belong on vertical, scrollable inspector-style screens such as Diagnostics. Avoid collapsible panels inside split/stretch layouts unless the layout is explicitly designed for collapsed state.
- Collapsed sections should remove their body from layout flow and collapse toward the top of the scroll stack. Do not use maximum-height hacks to fight stretch layout.
- Home should prioritize stable task flow over configurability. If a Home panel is important enough to anchor the workflow, keep it visible instead of making it collapsible.
- If a screen can exceed the viewport, make the whole screen scroll rather than relying on individual oversized cards.
- Verify every UI change at a compact window size, not only on a maximized editor.

## Problems In The Current Layout

- too much configuration is visible on first open
- the first screen mixes setup, queue, runs, diagnostics, and preview at once
- the user has to parse implementation details before they can do the simple task of selecting a clip and pressing run
- preview is present, but it does not yet visually dominate the workflow
- queue and diagnostics occupy space that should belong to clip browsing and comparison
- the app still feels "tool internals first" instead of "animation review first"

## Proposed Information Architecture

The workstation should be reorganized into three primary surfaces:

1. `Home`
2. `Mapping`
3. `Runs`

`Home` is the default and should cover almost every normal use case.

## Home Surface

`Home` should contain only:

- source file header
- clip browser
- compare preview
- main action controls
- compact status/progress
- collapsible advanced settings

### Home Layout

- left rail: source + clip browser
- main stage: compare preview
- top of stage: preview mode and camera controls
- bottom of stage: playback controls and queue progress
- advanced settings: collapsed by default

### Left Rail

The left rail should be narrow, stable, and scannable.

It should contain:

- source file chip or compact header
- source actions: `open`, `rescan`, `recent`
- clip search
- clip list
- per-clip status markers

Clip rows should use:

- status dot or icon
- clip name
- optional small badges such as `loop`, `queued`, `done`, `failed`

The selected clip should become the clear working context for the whole screen.

### Main Stage

The main stage should default to `Compare`.

Before retarget:

- left pane: source preview
- right pane: placeholder "Run retarget to see result"

After retarget:

- left pane: source preview
- right pane: result preview

The stage should be visually dominant and resize well with the window.

### Preview Controls

Preview controls should be mostly icon-driven.

Primary controls:

- play / pause
- loop
- step backward / step forward
- speed
- frame character
- reset camera
- orbit left / right
- tilt up / down
- zoom in / out
- fit to window

Secondary controls:

- source only
- result only
- compare split
- sync cameras
- follow target

All of these should use tooltips, not long button labels.

### Bottom Status Strip

The bottom strip should be compact and persistent.

It should contain:

- queue progress bar
- current clip name
- current phase such as `building source preview`, `retargeting`, `importing`, `done`
- small job counters such as `2/3 complete`

This replaces large diagnostic walls on the main screen.

## Mapping Surface

Mapping should be treated as an advanced workflow.

It should not compete with the Home surface.

The `Mapping` surface should contain:

- source bone list
- slot assignment editor
- filters
- mirror/reset/save actions
- a compact coverage summary

The first open experience should not expose this unless the user explicitly navigates here.

## Runs Surface

`Runs` should group operational detail:

- queue list
- run history
- diagnostics
- artifact links
- retry / clear actions

This keeps operational noise off the main review screen.

## Advanced Settings

Advanced settings should be collapsed by default on `Home`.

The default collapsed state should hide:

- source profile
- mapping profile
- target pose preset
- output folder
- sequence prefix
- root motion mode
- import hands
- auto-open ModelDoc
- debug artifact toggles

The user should be able to retarget a normal clip without opening this section.

## Progressive Loading

The workstation is heavy. It should communicate partial readiness instead of blocking the whole window.

### Desired Loading Model

At startup:

- render the shell immediately
- show clip/browser area even if content is not ready yet
- show preview placeholders immediately
- defer expensive work

When the source is scanned:

- clips can load first
- skeleton inspection can finish next
- mapping state can finish after that

When a clip is selected:

- source preview should build lazily for that clip only
- the rest of the UI should remain interactive

When retarget runs:

- the whole UI should not appear frozen
- disable only unsafe actions
- keep history, preview tab switching, and status readable

### Panel States

Each panel should support explicit states:

- `empty`
- `loading`
- `ready`
- `error`
- `disabled because another step is required`

Examples:

- compare result pane: `Run retarget to see result`
- source pane: `Generating source preview...`
- mapping tab: `Scan source to build mapping`
- runs tab: `No runs yet`

## Iconography Direction

The UI should reduce label noise by using icons for repeated actions.

Good icon candidates:

- source file actions
- queue actions
- run actions
- preview camera controls
- artifact open actions
- view mode toggles
- status markers

Text should remain in:

- section titles
- tooltips
- empty states
- short summary labels

## Preview Behavior Requirements

- preview should scale with window size
- camera should frame the character automatically
- small windows should still keep the character in view
- user camera changes should persist until explicitly reset
- compare should stay aligned enough to read motion differences
- source preview should be available before retarget when possible
- result preview should update after import without requiring ModelDoc

## Status Language

Main-screen text should be reduced to short, useful language.

Preferred examples:

- `Source ready`
- `3 clips selected`
- `Generating source preview`
- `Retargeting in Blender`
- `Importing result`
- `Completed`
- `1 failed`

Avoid long paragraphs on the main screen.

Detailed logs belong in `Runs`.

## MVP Rework Scope

The first UI rework pass should deliver:

- new `Home / Mapping / Runs` structure
- compare-first home layout
- advanced settings collapsed by default
- source preview available before retarget
- icon-driven preview controls
- compact bottom progress/status strip
- reduced text noise on the initial screen
- visible loading placeholders instead of blank areas

## Not In The First Rework Pass

- final visual polish
- full async background architecture
- perfect non-blocking Blender process integration
- final icon set
- complete diagnostics redesign

The first pass is about hierarchy, clarity, and perceived responsiveness.

## Recommended Implementation Order

1. Rebuild `Home` as clip-browser + compare-stage + bottom status strip.
2. Move advanced settings behind a collapsed section.
3. Keep `Mapping` and `Runs` as separate surfaces.
4. Replace text-heavy preview controls with icon-first controls.
5. Add explicit loading and empty states to source/result panels.
6. Reduce blocking sensations by avoiding whole-window busy states where possible.

## Product Reading

The tool should feel like:

- choose clip
- inspect source
- run retarget
- compare result

It should not feel like:

- fill out a form
- read diagnostics
- learn the backend
- then maybe preview something
