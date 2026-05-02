param(
	[string]$DestinationRoot = ""
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

$sourceRoot = Get-SourceRoot
if ( [string]::IsNullOrWhiteSpace( $DestinationRoot ) )
{
	$DestinationRoot = $env:CARL_SYNC_DESTINATION
}

if ( [string]::IsNullOrWhiteSpace( $DestinationRoot ) )
{
	throw "Missing destination project. Pass -DestinationRoot or set CARL_SYNC_DESTINATION."
}

$installer = $null
foreach ( $candidate in @(
	(Join-Path $sourceRoot "install_citizen_retarget_plugin.ps1"),
	(Join-Path $sourceRoot "dev\scripts\install.ps1")
) )
{
	if ( Test-Path -LiteralPath $candidate )
	{
		$installer = $candidate
		break
	}
}

if ( [string]::IsNullOrWhiteSpace( $installer ) )
{
	throw "Missing CARL installer under $sourceRoot"
}

& powershell -ExecutionPolicy Bypass -File $installer -ProjectRoot $DestinationRoot -PluginRoot $sourceRoot -PruneStaleFiles

if ( $LASTEXITCODE -ne 0 )
{
	exit $LASTEXITCODE
}

Write-Host "CARL workstation synced to $DestinationRoot as Libraries\CitizenRetarget"
