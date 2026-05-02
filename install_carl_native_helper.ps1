$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'Libraries\pumpedbit.citizenretarget\Editor\CitizenRetarget\Native\win-x64'
New-Item -ItemType Directory -Force -Path $proj | Out-Null
$zip = Join-Path $env:TEMP 'carl-native-win-x64-v0.1.0-alpha.2.zip'
$uri = 'https://github.com/pumped-bit/citizen-animation-retargeting-library/releases/download/v0.1.0-alpha.2/carl-native-win-x64-v0.1.0-alpha.2.zip'
Invoke-WebRequest -Uri $uri -OutFile $zip -UseBasicParsing
$bytes = [IO.File]::ReadAllBytes($zip)
$sha = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
$expected = '84c790b5f7bae26467a810ca75c989a1e71d3b82a4adfcc20b1506eb986f5d77'
if ($sha -ne $expected) { throw "SHA256 mismatch: got $sha" }
$ext = Join-Path $env:TEMP 'carl-native-extract'
if (Test-Path $ext) { Remove-Item -Recurse -Force $ext }
Expand-Archive -Path $zip -DestinationPath $ext -Force
$dll = Get-ChildItem -Path $ext -Filter 'ual2_ufbx_helper.dll' -Recurse | Select-Object -First 1
if (-not $dll) { throw 'ual2_ufbx_helper.dll not found in zip' }
Copy-Item $dll.FullName -Destination (Join-Path $proj 'ual2_ufbx_helper.dll') -Force
Write-Host "OK:" (Join-Path $proj 'ual2_ufbx_helper.dll') (Get-Item (Join-Path $proj 'ual2_ufbx_helper.dll')).Length
