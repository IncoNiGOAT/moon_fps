# CARL UI Domain And Workflow V2

Status as of `2026-04-14`:

- the current `Home / Mapping / Runs` split is a good first pass
- the workstation still mixes selection state, history state, and result state
- the current `single clip list -> source + result preview` model works for one large source pack, but does not scale to multiple libraries, targets, or reruns

## Why This Document Exists

The first UI rework spec focused on hierarchy and reducing noise.

This document focuses on the next layer:

- what the core UI entities are
- which of them are mutable vs historical
- how they should appear in the workstation
- how the workflow should scale beyond a single source FBX

The main problem is not only layout.

The main problem is that several different concepts are currently blended together in the UI.

## Core UI Entities

The workstation should explicitly model the following entities.

### Source Library

A source library is a source asset collection or source FBX context.

Examples:

- `UAL2`
- `Mixamo Pack 01`
- `Purchased Sword Pack`
- `Custom Mocap Session A`

This is the level where the user chooses which animation pack they are browsing.

### Source Clip

A source clip is one specific animation inside a source library.

Examples:

- `Fish_Cast_Idle_Loop`
- `JogToFlip`
- `Bow_RapidShoot_Loop`

This is the unit the user inspects before retargeting.

### Target

A target is the destination character/model contract that will receive retargeted animation output.

Examples:

- `Citizen`
- `Citizen variant with custom full-fork VMDL`
- future non-Citizen humanoid targets

Even if the current tool mostly targets Citizen, the UI should not assume that forever.

### Mapping Setup

A mapping setup is the retarget configuration context used to transform a source clip into a target result.

It includes:

- source profile
- mapping profile
- target pose preset
- root motion policy
- import-hands policy
- future clip-specific options if needed

This is not the same thing as a source clip or a run.

### Queue Item

A queue item is an intent to run retargeting later.

It is a pending operational item, not historical truth.

It should know:

- source clip
- source library
- target
- mapping setup snapshot or reference
- queue status

### Run

A run is one concrete execution record.

This is immutable history.

A run should always preserve:

- run id
- timestamp
- source library
- source clip
- target
- mapping setup used at the time
- stage statuses
- warnings / failures
- artifacts / logs

The UI should never treat a historical run as if it were only a temporary selection.

### Result

A result is the output artifact produced by a run.

This is distinct from the run itself.

A result may include:

- imported animation asset
- generated target model or target model reference
- preview artifacts
- manifest
- raw backend export

Multiple runs may exist for the same source clip.
Those runs may produce different results depending on mapping setup or target.

### Session

A session is the current local editor state.

Examples:

- selected source library
- selected source clip
- selected result for comparison
- currently highlighted run
- active filters
- queue selection

This is mutable UI state and should not be confused with historical records.

## Mutability Rules

The workstation should behave more predictably once each entity has a clear mutability rule.

- `Source Library`: mutable selection, stable asset identity
- `Source Clip`: mutable selection, stable source identity
- `Target`: mutable selection, stable target identity
- `Mapping Setup`: mutable working configuration
- `Queue Item`: mutable operational state
- `Run`: immutable historical record
- `Result`: immutable artifact identity once materialized
- `Session`: mutable local UI state

The biggest current confusion comes from `Run`, `Result`, and `Session` bleeding together.

## Current UX Problem In One Sentence

The current `Home` tab behaves as if `selected source clip`, `active result`, and `latest run` were the same thing.

They are not.

## Desired Compare Model

The compare stage should work from an explicit pair:

- `Compare Source`
- `Compare Result`

The user should always be able to tell:

- which source clip is selected
- which result is selected
- whether the result came from the latest run or from history

The compare stage should not silently depend on whatever run happened to be selected somewhere else unless that is made explicit in the UI.

## Why A Single Clip List Stops Scaling

The current single-list approach is acceptable only when all of the following are true:

- one source library is active
- one target model is active
- one mapping setup is effectively active
- the user mostly wants the latest result for each clip

It starts breaking down when:

- multiple source packs are being explored
- multiple targets are possible
- the same clip is rerun with different mapping setups
- the user wants to compare an old result against a new result
- the user wants to inspect successful and failed variants separately

This is why a single left-side clip list cannot remain the only organizer long-term.

## Recommended Home Model

`Home` should become a compare workspace, not a hidden run/history proxy.

### Home Should Show

- source browser
- result browser
- compare stage
- queue / session status
- compact actions for run / queue / open artifacts

### Source Browser

The source side should support:

- source library selector
- clip filter
- clip list within the selected library
- clip metadata and source-side badges

### Result Browser

The result side should support:

- results for selected source clip
- filters such as `latest`, `successful`, `failed`, `all`
- visible provenance such as target, mapping setup, timestamp, run id

This result browser is the missing counterpart to the source clip list.

### Compare Stage

The stage should compare:

- selected source clip preview
- selected result preview

If there is no selected result yet, the result side should explicitly say so.

This is better than implicitly pulling state from `Runs`.

## Recommended Runs Model

`Runs` should evolve into a run inspector, not remain a thin list.

### Runs Should Become

- a historical browser
- a diagnostics surface
- an artifact explorer
- a provenance viewer

### For A Selected Run, The User Should See

- source library
- source clip
- target
- mapping setup
- run timeline / stage list
- warnings
- errors
- logs
- artifact links
- whether a result was materialized

### Useful Actions For A Selected Run

- open manifest
- open raw export
- open imported asset
- open generated model
- open source preview
- clone this run into current session
- requeue with same settings
- compare this result against current source

This is much stronger than the current `history list + diagnostics text box`.

## Provenance Requirements

Every result shown in the UI should be traceable.

The user should be able to answer:

- which source library did this come from
- which source clip did this come from
- which target did this go to
- which mapping setup produced it
- which run id created it
- when was it created
- did it succeed fully or only partially

If the UI cannot answer those questions, results will become confusing once the project grows.

## Comparison Modes Worth Supporting Later

These do not all need to land immediately, but the data model should allow them.

- `Source vs Result`
- `Result A vs Result B`
- `Latest result vs previous result`
- `Same clip, different mapping setup`
- `Same clip, different target`

If the data model is explicit, these modes are additions.
If the data model stays ambiguous, these modes become hacks.

## Session Rules

The UI should preserve a few clear session rules.

- selecting a source clip should not silently rewrite history state
- selecting a run should not silently become the only way to show a result
- current session compare pair should be explicit
- loading a run from history should be a deliberate action
- latest run should be a convenience, not the only source of truth

## A Better Mental Model

The workstation should feel like:

1. choose a source library
2. choose a source clip
3. choose a result or create one
4. compare source and result
5. inspect the run when needed

It should not feel like:

1. select a clip
2. run something
3. go hunt in history
4. hope the right result appears back on the main screen

## Suggested Surface Responsibilities

### Home

Primary purpose:

- browse source
- browse result
- compare
- queue work

### Mapping

Primary purpose:

- edit and validate mapping setup

### Runs

Primary purpose:

- inspect historical runs
- inspect diagnostics
- inspect artifacts
- recover provenance

## Design Direction For The Next Rework

The next rework should not start from widgets.

It should start from entity boundaries.

The recommended order is:

1. lock the entity model
2. separate `Source Clip`, `Run`, `Result`, and `Session` in the code and UI vocabulary
3. design `Home` around an explicit compare pair
4. design `Runs` as a run inspector
5. add multi-library and multi-result browsing

## What This Unlocks

Once the UI adopts this model, the workstation can scale to:

- multiple source packs
- multiple target variants
- multiple mapping setups
- repeated experiments on the same clip
- reliable history and diagnostics
- richer comparison workflows

## Short Summary

The current workstation already proved that `compare-first` is the right direction.

The next step is to stop treating `selected source clip`, `latest run`, and `active result` as the same thing.

The UI should explicitly model:

- source
- result
- run
- session

That separation is what will make the tool scale from a single-pack demo workflow to a real production workstation.
