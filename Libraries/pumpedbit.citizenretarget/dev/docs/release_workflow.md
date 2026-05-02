# CARL Release Workflow

This document describes the intended maintenance workflow for CARL, the Citizen Animation Retargeting Library, as a standalone s&box library.

## Source Of Truth

The canonical development checkout is:

```text
C:\path\to\citizen_retarget
```

This folder is already structured as the standalone library repository:

```text
README.md
CHANGELOG.md
LICENSE
citizenretarget.sbproj
Code/
Editor/
Assets/
tools/
dev/
```

Do not edit installed copies under another project's `Libraries/CitizenRetarget`. Treat them as generated/test installs.

## Package A User Release

From the canonical repository:

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\package.ps1" -Zip
```

Package the Windows native FBX helper as a separate GitHub Release asset:

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\package-native-helper.ps1" -Version "0.1.0-alpha.2"
```

The user release zip contains:

```text
README.md
CHANGELOG.md
LICENSE
install_citizen_retarget_plugin.ps1
Libraries/CitizenRetarget/
```

Users should copy `Libraries/CitizenRetarget` into their s&box project.

When updating an existing install, users should delete the old `Libraries/CitizenRetarget` folder first. Merging a new release over an old folder can leave stale files behind.

The native helper release zip contains:

```text
ual2_ufbx_helper.dll
THIRD_PARTY_NOTICES.md
NATIVE_HELPER_README.md
carl_native_helper_manifest.json
```

Attach both files to the matching GitHub Release:

```text
carl-native-win-x64-v0.1.0-alpha.2.zip
carl-native-win-x64-v0.1.0-alpha.2.sha256
```

The `.sha256` file is the verification source for any future Diagnostics-driven download/install flow.

## Install Into A Test Project

From the canonical repository:

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\install.ps1" -ProjectRoot "C:\Path\To\SboxProject"
```

The installer preserves project-local generated assets and settings. It installs the library under:

```text
Libraries/CitizenRetarget/
```

## Sync To The Live Development Project

Pick any local s&box project for live editor testing:

```text
C:\path\to\SboxProject
```

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\sync-to-project.ps1" -DestinationRoot "C:\path\to\SboxProject"
```

For repeated local testing, set an environment variable and then run sync without arguments:

```powershell
$env:CARL_SYNC_DESTINATION = "C:\path\to\SboxProject"
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\sync-to-project.ps1"
```

Run this after every source change before testing in the editor.

## Prepare A Clean s&box Release Project

Do not publish directly from the git checkout. The s&box library publisher source-publishes the folder recursively, so local ignored files must not be present in the project folder you publish.

Create a clean release project first:

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\prepare-sbox-publish.ps1" -Org "pumpedbit" -Ident "citizenretarget"
```

Then open this folder in s&box and publish from the project title button:

```text
..\release_projects\citizen_retarget
```

The script copies the git-visible project state: tracked files plus untracked files that are not ignored by `.gitignore`. Ignored local files such as `.sbox`, `.vscode`, `_dist`, `obj`, `ProjectSettings`, and generated local scaffold folders stay out of the release project.

Current limitation: s&box source publishing filters out `.dll` files, so `Editor/CitizenRetarget/Native/win-x64/ual2_ufbx_helper.dll` is not uploaded through the package manager. The GitHub release zip remains the native-helper distribution path for s&box package installs.

For package-manager installs, publish the native helper zip above on GitHub Releases. Diagnostics treats that release asset as the official install source: users can press `Download Helper` to download it, verify SHA256, and extract `ual2_ufbx_helper.dll` into the library's native helper folder.

## Local Verification

Before handing a build to another user:

```powershell
dotnet build "Editor\citizenretarget.editor.csproj"
```

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\sync-to-project.ps1" -DestinationRoot "C:\path\to\SboxProject"
```

```powershell
dotnet build "C:\path\to\SboxProject\SboxProject.slnx"
```

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\package.ps1" -Zip
```

```powershell
powershell -ExecutionPolicy Bypass -File ".\dev\scripts\package-native-helper.ps1" -Version "0.1.0-alpha.2"
```

## Clean Project Smoke Test

For each release candidate:

1. Create or open a clean s&box project.
2. Copy `Libraries/CitizenRetarget` from the release package into the project.
3. Restart the editor or run `Compile local`.
4. Confirm the menu appears:

```text
CARL -> Open Retargeter
```

5. Open Diagnostics and press `Re-scan Setup`.
6. Confirm setup errors are actionable if Blender or Rokoko is missing.
7. Run one known-good UAL retarget smoke test.
8. Run one known-good Mixamo retarget smoke test.
9. Confirm generated target results preview and open in ModelDoc.

## Publishing Later

Before a public repository or package-manager release:

- Confirm `citizenretarget.sbproj` has the CARL title and a clear description.
- Confirm the Library Manager shows the README and package description in a clean project.
- Choose the final license and keep `LICENSE` in both the package root and library root.
- Keep private/test-only docs under `dev/` so they are not included in release packages.
- Add release screenshots or a short demo clip.
- Tag the repo version, starting at `v0.1.0`.
- Attach the generated release zip, native helper zip, and native helper `.sha256` to GitHub Releases.
- Keep `CHANGELOG.md` updated for each release.
