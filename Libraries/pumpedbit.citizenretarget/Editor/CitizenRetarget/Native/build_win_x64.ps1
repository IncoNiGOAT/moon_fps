param(
	[string]$OutputDirectory = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputRoot = Join-Path $scriptRoot $OutputDirectory
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
	throw "Missing vcvars64.bat at $vcvars"
}

$source = Join-Path $scriptRoot "ual2_ufbx_helper.cpp"
$vendorSource = Join-Path $scriptRoot "ufbx\ufbx.c"
$outputDll = Join-Path $outputRoot "ual2_ufbx_helper.dll"
$outputLib = Join-Path $outputRoot "ual2_ufbx_helper.lib"
$outputExp = Join-Path $outputRoot "ual2_ufbx_helper.exp"

if (-not (Test-Path $vendorSource)) {
	throw "Missing vendored ufbx.c at $vendorSource"
}

Remove-Item -LiteralPath $outputDll, $outputLib, $outputExp -Force -ErrorAction SilentlyContinue

$batch = [System.IO.Path]::ChangeExtension([System.IO.Path]::GetTempFileName(), ".bat")
@(
	"@echo off"
	"call `"$vcvars`" >nul"
	"cl /std:c++17 /EHsc /MT /LD /O2 /nologo /I`"$scriptRoot`" /Fe:`"$outputDll`" `"$source`" `"$vendorSource`""
) | Set-Content -Path $batch -Encoding ASCII

try {
	cmd /c "`"$batch`""
	if ($LASTEXITCODE -ne 0) {
		throw "Native build failed with exit code $LASTEXITCODE"
	}
}
finally {
	Remove-Item -LiteralPath $batch -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $outputDll)) {
	throw "Build completed without producing $outputDll"
}

Write-Host "Built $outputDll"
