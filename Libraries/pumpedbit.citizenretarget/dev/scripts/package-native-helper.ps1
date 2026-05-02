param(
	[string]$OutputRoot = "",
	[string]$Version = "0.1.0-alpha.2",
	[string]$PackageName = "carl-native-win-x64"
)

$ErrorActionPreference = "Stop"

function Get-SourceRoot
{
	$candidate = $PSScriptRoot
	for ( $i = 0; $i -lt 6; $i++ )
	{
		if ( Test-Path -LiteralPath (Join-Path $candidate "citizenretarget.sbproj") )
		{
			return $candidate
		}

		$parent = Split-Path -Parent $candidate
		if ( [string]::IsNullOrWhiteSpace( $parent ) -or $parent -eq $candidate )
		{
			break
		}

		$candidate = $parent
	}

	throw "Could not locate CARL source root from $PSScriptRoot"
}

function Copy-RequiredFile
{
	param(
		[string]$Source,
		[string]$Destination
	)

	if ( -not (Test-Path -LiteralPath $Source -PathType Leaf) )
	{
		throw "Missing required native helper release file: $Source"
	}

	$destinationParent = Split-Path -Parent $Destination
	if ( -not (Test-Path -LiteralPath $destinationParent) )
	{
		New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
	}

	Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$sourceRoot = Get-SourceRoot
if ( [string]::IsNullOrWhiteSpace( $OutputRoot ) )
{
	$OutputRoot = Join-Path $sourceRoot "_dist"
}

$outputRootFull = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $OutputRoot )
$assetBaseName = "$PackageName-v$Version"
$stagingRoot = Join-Path $outputRootFull $assetBaseName
$zipPath = Join-Path $outputRootFull "$assetBaseName.zip"
$shaPath = Join-Path $outputRootFull "$assetBaseName.sha256"

if ( Test-Path -LiteralPath $stagingRoot )
{
	Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$dllRelativePath = "Editor\CitizenRetarget\Native\win-x64\ual2_ufbx_helper.dll"
$dllSourcePath = Join-Path $sourceRoot $dllRelativePath
$dllPackagePath = Join-Path $stagingRoot "ual2_ufbx_helper.dll"

Copy-RequiredFile -Source $dllSourcePath -Destination $dllPackagePath
Copy-RequiredFile -Source (Join-Path $sourceRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $stagingRoot "THIRD_PARTY_NOTICES.md")
Copy-RequiredFile -Source (Join-Path $sourceRoot "Editor\CitizenRetarget\Native\README.md") -Destination (Join-Path $stagingRoot "NATIVE_HELPER_README.md")

$dllHash = (Get-FileHash -LiteralPath $dllPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
	Name = "CARL Native Helper"
	Version = $Version
	Platform = "win-x64"
	Dll = "ual2_ufbx_helper.dll"
	DllSha256 = $dllHash
	SourcePath = $dllRelativePath.Replace( '\', '/' )
	Contains = @(
		"CARL native FBX scanner bridge",
		"ufbx vendored source"
	)
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stagingRoot "carl_native_helper_manifest.json") -Encoding UTF8

if ( Test-Path -LiteralPath $zipPath )
{
	Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $shaPath -Encoding ASCII -Value "$zipHash  $assetBaseName.zip"

Write-Host "CARL native helper package created: $zipPath"
Write-Host "SHA256 written to: $shaPath"
