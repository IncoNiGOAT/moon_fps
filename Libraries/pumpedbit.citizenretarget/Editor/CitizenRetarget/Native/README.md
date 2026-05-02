`ual2_ufbx_helper.dll` is a tiny Windows-only native bridge around [ufbx](https://github.com/ufbx/ufbx).

It is intentionally narrow:

- scan animation stacks from `UAL2.fbx`
- sample one selected clip into parent-relative local TRS
- normalize both source and Citizen reference FBX into the same right-handed Z-up centimeter basis

Build locally with:

```powershell
./build_win_x64.ps1
```

Expected output:

- `Editor/CitizenRetarget/Native/win-x64/ual2_ufbx_helper.dll`

Vendored source snapshot:

- `ufbx/ufbx.h`
- `ufbx/ufbx.c`
