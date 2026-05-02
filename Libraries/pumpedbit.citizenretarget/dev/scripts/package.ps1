param(
	[string]$OutputRoot = "",
	[string]$PackageName = "citizen_retarget_plugin",
	[switch]$Zip
)

$ErrorActionPreference = "Stop"

function Copy-PackageDirectory
{
	param(
		[string]$Source,
		[string]$Destination
	)

	if ( -not (Test-Path -LiteralPath $Source) )
	{
		throw "Missing package source directory: $Source"
	}

	$files = Get-ChildItem -LiteralPath $Source -Recurse -File
	foreach ( $file in $files )
	{
		if ( $file.FullName -match "\\(bin|obj|__pycache__)\\" )
		{
			continue
		}

		$relativePath = $file.FullName.Substring( $Source.Length ).TrimStart( '\', '/' )
		$destinationFile = Join-Path $Destination $relativePath
		$destinationParent = Split-Path -Parent $destinationFile
		if ( -not (Test-Path -LiteralPath $destinationParent) )
		{
			New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
		}

		Copy-Item -LiteralPath $file.FullName -Destination $destinationFile -Force
	}
}

function Copy-PackageFile
{
	param(
		[string]$Source,
		[string]$Destination
	)

	if ( -not (Test-Path -LiteralPath $Source) )
	{
		throw "Missing package source file: $Source"
	}

	$destinationParent = Split-Path -Parent $Destination
	if ( -not (Test-Path -LiteralPath $destinationParent) )
	{
		New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
	}

	Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

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

function Get-InstallScriptPath
{
	param(
		[string]$SourceRoot
	)

	foreach ( $candidate in @(
		(Join-Path $SourceRoot "install_citizen_retarget_plugin.ps1"),
		(Join-Path $SourceRoot "dev\scripts\install.ps1")
	) )
	{
		if ( Test-Path -LiteralPath $candidate )
		{
			return $candidate
		}
	}

	throw "Could not locate CARL installer script under $SourceRoot"
}

function Test-PackageOutput
{
	param(
		[string]$PackageRoot,
		[string]$LibraryRoot
	)

	$requiredFiles = @(
		(Join-Path $PackageRoot "README.md"),
		(Join-Path $PackageRoot "branding\carl_logo.jpg"),
		(Join-Path $PackageRoot "THIRD_PARTY_NOTICES.md"),
		(Join-Path $PackageRoot "install_citizen_retarget_plugin.ps1"),
		(Join-Path $LibraryRoot "citizenretarget.sbproj"),
		(Join-Path $LibraryRoot "Code\citizenretarget.csproj"),
		(Join-Path $LibraryRoot "Editor\citizenretarget.editor.csproj"),
		(Join-Path $LibraryRoot "Editor\Assembly.cs"),
		(Join-Path $LibraryRoot "Editor\CitizenRetarget\CitizenRetargetWindow.cs"),
		(Join-Path $LibraryRoot "branding\carl_logo.jpg"),
		(Join-Path $LibraryRoot "Editor\CitizenRetarget\Native\win-x64\ual2_ufbx_helper.dll"),
		(Join-Path $LibraryRoot "Assets\tools\citizen_retarget\backend\tools\blender\retarget_job.py"),
		(Join-Path $LibraryRoot "Assets\tools\citizen_retarget\backend\tools\blender\config\recipes\retarget_rokoko_citizen.json"),
		(Join-Path $LibraryRoot "README.md"),
		(Join-Path $LibraryRoot "THIRD_PARTY_NOTICES.md"),
		(Join-Path $LibraryRoot "citizen_retarget_library_manifest.json")
	)

	$missing = @()
	foreach ( $file in $requiredFiles )
	{
		if ( -not (Test-Path -LiteralPath $file -PathType Leaf) )
		{
			$missing += $file
		}
	}

	$forbiddenPaths = @(
		(Join-Path $PackageRoot "Editor\CitizenRetarget"),
		(Join-Path $PackageRoot "Assets\tools\citizen_retarget"),
		(Join-Path $PackageRoot "tools\blender_export_citizen_dmx.py"),
		(Join-Path $PackageRoot "dev"),
		(Join-Path $LibraryRoot "dev"),
		(Join-Path $LibraryRoot "docs")
	)

	$forbiddenPresent = @()
	foreach ( $path in $forbiddenPaths )
	{
		if ( Test-Path -LiteralPath $path )
		{
			$forbiddenPresent += $path
		}
	}

	if ( $missing.Count -gt 0 -or $forbiddenPresent.Count -gt 0 )
	{
		$message = New-Object System.Collections.Generic.List[string]
		$message.Add( "CARL package self-check failed." )
		if ( $missing.Count -gt 0 )
		{
			$message.Add( "" )
			$message.Add( "Missing required files:" )
			foreach ( $file in $missing )
			{
				$message.Add( " - $file" )
			}
		}
		if ( $forbiddenPresent.Count -gt 0 )
		{
			$message.Add( "" )
			$message.Add( "Stale root-install files were found in the package:" )
			foreach ( $path in $forbiddenPresent )
			{
				$message.Add( " - $path" )
			}
		}

		throw ($message -join [Environment]::NewLine)
	}

	Write-Host "Package self-check passed."
}

$sourceRoot = Get-SourceRoot
if ( [string]::IsNullOrWhiteSpace( $OutputRoot ) )
{
	$OutputRoot = Join-Path $sourceRoot "_dist"
}

$outputRootFull = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $OutputRoot )
$packageRoot = Join-Path $outputRootFull $PackageName

if ( Test-Path -LiteralPath $packageRoot )
{
	Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$libraryRoot = Join-Path $packageRoot "Libraries\CitizenRetarget"

Copy-PackageFile -Source (Join-Path $sourceRoot "citizenretarget.sbproj") -Destination (Join-Path $libraryRoot "citizenretarget.sbproj")
Copy-PackageDirectory -Source (Join-Path $sourceRoot "Code") -Destination (Join-Path $libraryRoot "Code")
Copy-PackageDirectory -Source (Join-Path $sourceRoot "Editor\CitizenRetarget") -Destination (Join-Path $libraryRoot "Editor\CitizenRetarget")
Copy-PackageFile -Source (Join-Path $sourceRoot "Editor\citizenretarget.editor.csproj") -Destination (Join-Path $libraryRoot "Editor\citizenretarget.editor.csproj")
Copy-PackageFile -Source (Join-Path $sourceRoot "Editor\Assembly.cs") -Destination (Join-Path $libraryRoot "Editor\Assembly.cs")
Copy-PackageDirectory -Source (Join-Path $sourceRoot "Assets\tools\citizen_retarget") -Destination (Join-Path $libraryRoot "Assets\tools\citizen_retarget")
Copy-PackageDirectory -Source (Join-Path $sourceRoot "branding") -Destination (Join-Path $libraryRoot "branding")
Copy-PackageDirectory -Source (Join-Path $sourceRoot "branding") -Destination (Join-Path $packageRoot "branding")
Copy-PackageFile -Source (Join-Path $sourceRoot "tools\blender_export_citizen_dmx.py") -Destination (Join-Path $libraryRoot "tools\blender_export_citizen_dmx.py")
Copy-PackageFile -Source (Get-InstallScriptPath $sourceRoot) -Destination (Join-Path $packageRoot "install_citizen_retarget_plugin.ps1")
Copy-PackageFile -Source (Join-Path $sourceRoot "README.md") -Destination (Join-Path $packageRoot "README.md")
Copy-PackageFile -Source (Join-Path $sourceRoot "README.md") -Destination (Join-Path $libraryRoot "README.md")
Copy-PackageFile -Source (Join-Path $sourceRoot "CHANGELOG.md") -Destination (Join-Path $packageRoot "CHANGELOG.md")
Copy-PackageFile -Source (Join-Path $sourceRoot "CHANGELOG.md") -Destination (Join-Path $libraryRoot "CHANGELOG.md")
Copy-PackageFile -Source (Join-Path $sourceRoot "LICENSE") -Destination (Join-Path $packageRoot "LICENSE")
Copy-PackageFile -Source (Join-Path $sourceRoot "LICENSE") -Destination (Join-Path $libraryRoot "LICENSE")
Copy-PackageFile -Source (Join-Path $sourceRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $packageRoot "THIRD_PARTY_NOTICES.md")
Copy-PackageFile -Source (Join-Path $sourceRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $libraryRoot "THIRD_PARTY_NOTICES.md")

$manifest = [ordered]@{
	Name = "CARL"
	Version = "0.1.0-alpha.3"
	PackageId = "citizen_retarget_library"
	SchemaVersion = 1
	CreatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
	InstallScript = "install_citizen_retarget_plugin.ps1"
	ProjectInstallRoots = @(
		"Libraries/CitizenRetarget/citizenretarget.sbproj",
		"Libraries/CitizenRetarget/Code/citizenretarget.csproj",
		"Libraries/CitizenRetarget/Editor/citizenretarget.editor.csproj",
		"Libraries/CitizenRetarget/Editor/Assembly.cs",
		"Libraries/CitizenRetarget/Editor/CitizenRetarget",
		"Libraries/CitizenRetarget/branding",
		"Libraries/CitizenRetarget/Assets/tools/citizen_retarget",
		"Libraries/CitizenRetarget/tools/blender_export_citizen_dmx.py",
		"Libraries/CitizenRetarget/README.md",
		"Libraries/CitizenRetarget/CHANGELOG.md",
		"Libraries/CitizenRetarget/LICENSE",
		"Libraries/CitizenRetarget/THIRD_PARTY_NOTICES.md"
	)
	ExternalDependencies = @(
		"Windows editor/runtime",
		"Blender 4.4 recommended",
		"Blender addon: Rokoko Studio Live for Blender",
		"s&box Citizen assets in the target project"
	)
	KnownIssues = @(
		"Target animation delete can require manual UI refresh in some cases."
	)
}

$manifestPath = Join-Path $libraryRoot "citizen_retarget_library_manifest.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

Test-PackageOutput -PackageRoot $packageRoot -LibraryRoot $libraryRoot

if ( $Zip )
{
	$zipPath = Join-Path $outputRootFull "$PackageName.zip"
	if ( Test-Path -LiteralPath $zipPath )
	{
		Remove-Item -LiteralPath $zipPath -Force
	}

	Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force
	Write-Host "CARL plugin package created: $zipPath"
}
else
{
	Write-Host "CARL plugin package created: $packageRoot"
}
