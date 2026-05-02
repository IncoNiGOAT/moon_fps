param(
	[Parameter(Mandatory = $true)]
	[string]$ProjectRoot,

	[string]$PluginRoot = "",

	[switch]$ForceOverwriteSettings,

	[switch]$PruneStaleFiles
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath( [string]$PathValue )
{
	$executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $PathValue )
}

function Copy-ManagedDirectory
{
	param(
		[string]$Source,
		[string]$Destination,
		[string[]]$PreserveExistingRelativePaths = @(),
		[string[]]$PreserveExtensions = @()
	)

	if ( -not (Test-Path -LiteralPath $Source) )
	{
		throw "Missing plugin package directory: $Source"
	}

	if ( -not (Test-Path -LiteralPath $Destination) )
	{
		New-Item -ItemType Directory -Force -Path $Destination | Out-Null
	}

	$warnings = @()
	$sourceFiles = Get-ChildItem -LiteralPath $Source -Recurse -File
	$sourceRelativePaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

	foreach ( $sourceFile in $sourceFiles )
	{
		if ( $sourceFile.FullName -match "\\(bin|obj|__pycache__)\\" )
		{
			continue
		}

		$relativePath = $sourceFile.FullName.Substring( $Source.Length ).TrimStart( '\', '/' )
		$null = $sourceRelativePaths.Add( $relativePath )
		$destinationFile = Join-Path $Destination $relativePath
		$destinationParent = Split-Path -Parent $destinationFile
		if ( -not (Test-Path -LiteralPath $destinationParent) )
		{
			New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
		}

		if ( ( -not $ForceOverwriteSettings ) -and ( $PreserveExistingRelativePaths -contains $relativePath ) -and ( Test-Path -LiteralPath $destinationFile ) )
		{
			continue
		}

		try
		{
			Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
		}
		catch
		{
			$warnings += "Skipped locked or inaccessible file: $destinationFile"
		}
	}

	if ( $PruneStaleFiles )
	{
		$destinationFiles = Get-ChildItem -LiteralPath $Destination -Recurse -File
		foreach ( $destinationFile in $destinationFiles )
		{
			$relativePath = $destinationFile.FullName.Substring( $Destination.Length ).TrimStart( '\', '/' )
			if ( $sourceRelativePaths.Contains( $relativePath ) )
			{
				continue
			}

			if ( $PreserveExtensions -contains $destinationFile.Extension.ToLowerInvariant() )
			{
				continue
			}

			try
			{
				Remove-Item -LiteralPath $destinationFile.FullName -Force
			}
			catch
			{
				$warnings += "Skipped deleting stale file: $($destinationFile.FullName)"
			}
		}
	}

	return $warnings
}

function Copy-ManagedFile
{
	param(
		[string]$Source,
		[string]$Destination
	)

	if ( -not (Test-Path -LiteralPath $Source) )
	{
		throw "Missing plugin package file: $Source"
	}

	$destinationParent = Split-Path -Parent $Destination
	if ( -not (Test-Path -LiteralPath $destinationParent) )
	{
		New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
	}

	Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-SourceLibraryRoot
{
	param(
		[string]$PluginRootFull
	)

	$packagedLibraryRoot = Join-Path $PluginRootFull "Libraries\CitizenRetarget"
	if ( Test-Path -LiteralPath (Join-Path $packagedLibraryRoot "Editor\CitizenRetarget") )
	{
		return $packagedLibraryRoot
	}

	if ( Test-Path -LiteralPath (Join-Path $PluginRootFull "Editor\CitizenRetarget") )
	{
		return $PluginRootFull
	}

	throw "Plugin root does not contain a CitizenRetarget library: $PluginRootFull"
}

function Migrate-LocalSettings
{
	param(
		[string]$ProjectRootFull
	)

	$newSettingsPath = Join-Path $ProjectRootFull ".sbox\citizen_retarget\settings.json"
	if ( Test-Path -LiteralPath $newSettingsPath )
	{
		return
	}

	$oldSettingsPath = Join-Path $ProjectRootFull "Assets\tools\citizen_retarget\settings.json"
	if ( -not (Test-Path -LiteralPath $oldSettingsPath) )
	{
		return
	}

	$newSettingsParent = Split-Path -Parent $newSettingsPath
	if ( -not (Test-Path -LiteralPath $newSettingsParent) )
	{
		New-Item -ItemType Directory -Force -Path $newSettingsParent | Out-Null
	}

	Copy-Item -LiteralPath $oldSettingsPath -Destination $newSettingsPath -Force
}

function Ensure-LocalSettings
{
	param(
		[string]$ProjectRootFull
	)

	$settingsPath = Join-Path $ProjectRootFull ".sbox\citizen_retarget\settings.json"
	if ( Test-Path -LiteralPath $settingsPath )
	{
		return
	}

	$settingsParent = Split-Path -Parent $settingsPath
	if ( -not (Test-Path -LiteralPath $settingsParent) )
	{
		New-Item -ItemType Directory -Force -Path $settingsParent | Out-Null
	}

	@"
{
  "BackendRootPath": "",
  "BlenderExecutablePath": ""
}
"@ | Set-Content -Path $settingsPath -Encoding UTF8
}

function Remove-LegacyRootInstall
{
	param(
		[string]$ProjectRootFull
	)

	foreach ( $legacyPath in @(
		"Code\CitizenRetarget",
		"Editor\CitizenRetarget",
		"Assets\tools\citizen_retarget",
		"tools\blender_export_citizen_dmx.py"
	) )
	{
		$absoluteLegacyPath = Join-Path $ProjectRootFull $legacyPath
		if ( Test-Path -LiteralPath $absoluteLegacyPath )
		{
			Remove-Item -LiteralPath $absoluteLegacyPath -Recurse -Force
		}
	}

	$legacyMenuShim = Join-Path $ProjectRootFull "Editor\MyEditorMenu.cs"
	if ( Test-Path -LiteralPath $legacyMenuShim )
	{
		$legacyMenuText = Get-Content -LiteralPath $legacyMenuShim -Raw
		if ( $legacyMenuText -like "*Editor.CitizenRetarget.CitizenRetargetWindow*" )
		{
			Remove-Item -LiteralPath $legacyMenuShim -Force
		}
	}
}

function Remove-StaleLibraryPayload
{
	param(
		[string]$DestinationLibraryRoot
	)

	foreach ( $staleRelativePath in @(
		"docs",
		"dev",
		"examples",
		"scripts"
	) )
	{
		$absoluteStalePath = Join-Path $DestinationLibraryRoot $staleRelativePath
		if ( Test-Path -LiteralPath $absoluteStalePath )
		{
			Remove-Item -LiteralPath $absoluteStalePath -Recurse -Force
		}
	}
}

$projectRootFull = Resolve-FullPath $ProjectRoot
if ( [string]::IsNullOrWhiteSpace( $PluginRoot ) )
{
	$PluginRoot = $PSScriptRoot
	$candidate = $PSScriptRoot
	for ( $i = 0; $i -lt 6; $i++ )
	{
		if ( Test-Path -LiteralPath (Join-Path $candidate "citizenretarget.sbproj") )
		{
			$PluginRoot = $candidate
			break
		}

		$packagedLibraryRoot = Join-Path $candidate "Libraries\CitizenRetarget"
		if ( Test-Path -LiteralPath (Join-Path $packagedLibraryRoot "citizenretarget.sbproj") )
		{
			$PluginRoot = $candidate
			break
		}

		$parent = Split-Path -Parent $candidate
		if ( [string]::IsNullOrWhiteSpace( $parent ) -or $parent -eq $candidate )
		{
			break
		}

		$candidate = $parent
	}
}

$pluginRootFull = Resolve-FullPath $PluginRoot
$sourceLibraryRoot = Get-SourceLibraryRoot $pluginRootFull
$destinationLibraryRoot = Join-Path $projectRootFull "Libraries\CitizenRetarget"

if ( -not (Test-Path -LiteralPath $projectRootFull) )
{
	throw "Target s&box project root does not exist: $projectRootFull"
}

if ( -not (Test-Path -LiteralPath (Join-Path $projectRootFull "Assets")) )
{
	throw "Target does not look like an s&box project. Missing Assets folder: $projectRootFull"
}

$copyWarnings = @()
$preserveExtensions = @('.dll', '.pdb', '.exp', '.lib')

Migrate-LocalSettings $projectRootFull
Ensure-LocalSettings $projectRootFull

$copyWarnings += Copy-ManagedDirectory `
	-Source (Join-Path $sourceLibraryRoot "Code") `
	-Destination (Join-Path $destinationLibraryRoot "Code") `
	-PreserveExtensions $preserveExtensions

$copyWarnings += Copy-ManagedDirectory `
	-Source (Join-Path $sourceLibraryRoot "Editor\CitizenRetarget") `
	-Destination (Join-Path $destinationLibraryRoot "Editor\CitizenRetarget") `
	-PreserveExtensions $preserveExtensions

$editorProjectPath = Join-Path $sourceLibraryRoot "Editor\citizenretarget.editor.csproj"
if ( Test-Path -LiteralPath $editorProjectPath )
{
	Copy-ManagedFile `
		-Source $editorProjectPath `
		-Destination (Join-Path $destinationLibraryRoot "Editor\citizenretarget.editor.csproj")
}

$editorAssemblyPath = Join-Path $sourceLibraryRoot "Editor\Assembly.cs"
if ( Test-Path -LiteralPath $editorAssemblyPath )
{
	Copy-ManagedFile `
		-Source $editorAssemblyPath `
		-Destination (Join-Path $destinationLibraryRoot "Editor\Assembly.cs")
}

$libraryProjectPath = Join-Path $sourceLibraryRoot "citizenretarget.sbproj"
if ( Test-Path -LiteralPath $libraryProjectPath )
{
	Copy-ManagedFile `
		-Source $libraryProjectPath `
		-Destination (Join-Path $destinationLibraryRoot "citizenretarget.sbproj")
}

foreach ( $metadataFileName in @(
	"README.md",
	"CHANGELOG.md",
	"LICENSE",
	"THIRD_PARTY_NOTICES.md"
) )
{
	$metadataPath = Join-Path $sourceLibraryRoot $metadataFileName
	if ( Test-Path -LiteralPath $metadataPath )
	{
		Copy-ManagedFile `
			-Source $metadataPath `
			-Destination (Join-Path $destinationLibraryRoot $metadataFileName)
	}
}

$copyWarnings += Copy-ManagedDirectory `
	-Source (Join-Path $sourceLibraryRoot "Assets\tools\citizen_retarget") `
	-Destination (Join-Path $destinationLibraryRoot "Assets\tools\citizen_retarget") `
	-PreserveExtensions $preserveExtensions

if ( Test-Path -LiteralPath (Join-Path $sourceLibraryRoot "branding") )
{
	$copyWarnings += Copy-ManagedDirectory `
		-Source (Join-Path $sourceLibraryRoot "branding") `
		-Destination (Join-Path $destinationLibraryRoot "branding")
}

Copy-ManagedFile `
	-Source (Join-Path $sourceLibraryRoot "tools\blender_export_citizen_dmx.py") `
	-Destination (Join-Path $destinationLibraryRoot "tools\blender_export_citizen_dmx.py")

$manifestPath = Join-Path $sourceLibraryRoot "citizen_retarget_library_manifest.json"
if ( Test-Path -LiteralPath $manifestPath )
{
	Copy-ManagedFile -Source $manifestPath -Destination (Join-Path $destinationLibraryRoot "citizen_retarget_library_manifest.json")
}

Remove-LegacyRootInstall $projectRootFull
Remove-StaleLibraryPayload $destinationLibraryRoot

Write-Host "CARL library installed to $destinationLibraryRoot"
if ( $copyWarnings.Count -gt 0 )
{
	Write-Warning ( $copyWarnings -join [Environment]::NewLine )
}
