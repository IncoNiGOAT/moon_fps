param(
	[string]$OutputRoot = "",
	[string]$PackageName = "citizen_retarget",
	[string]$Org = "pumpedbit",
	[string]$Ident = "citizenretarget"
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

function Get-GitReleaseFiles
{
	param(
		[string]$SourceRoot
	)

	$gitRoot = (& git -C $SourceRoot rev-parse --show-toplevel).Trim()
	if ( $LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace( $gitRoot ) )
	{
		throw "CARL source root must be a git checkout: $SourceRoot"
	}

	$resolvedGitRoot = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $gitRoot )
	$resolvedSourceRoot = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $SourceRoot )
	if ( -not $resolvedGitRoot.Equals( $resolvedSourceRoot, [System.StringComparison]::OrdinalIgnoreCase ) )
	{
		throw "Run this from the CARL repository root. Git root was $resolvedGitRoot"
	}

	$files = & git -C $SourceRoot ls-files --cached --others --exclude-standard
	if ( $LASTEXITCODE -ne 0 )
	{
		throw "git ls-files failed."
	}

	return $files | Where-Object { -not [string]::IsNullOrWhiteSpace( $_ ) } | Sort-Object -Unique
}

function Copy-ReleaseFiles
{
	param(
		[string]$SourceRoot,
		[string]$PublishRoot,
		[string[]]$RelativeFiles
	)

	foreach ( $relativePath in $RelativeFiles )
	{
		$sourceFile = Join-Path $SourceRoot $relativePath
		if ( -not (Test-Path -LiteralPath $sourceFile -PathType Leaf) )
		{
			throw "Release file disappeared before copy: $relativePath"
		}

		$destinationFile = Join-Path $PublishRoot $relativePath
		$destinationParent = Split-Path -Parent $destinationFile
		if ( -not (Test-Path -LiteralPath $destinationParent) )
		{
			New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
		}

		Copy-Item -LiteralPath $sourceFile -Destination $destinationFile -Force
	}
}

function Set-PublishProjectIdentity
{
	param(
		[string]$ProjectFile,
		[string]$Org,
		[string]$Ident
	)

	$config = Get-Content -LiteralPath $ProjectFile -Raw | ConvertFrom-Json
	Set-JsonProperty -Object $config -Name "Org" -Value $Org
	Set-JsonProperty -Object $config -Name "Ident" -Value $Ident
	Set-JsonProperty -Object $config -Name "Type" -Value "library"
	Set-JsonProperty -Object $config -Name "IncludeSourceFiles" -Value $false

	$config | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $ProjectFile -Encoding UTF8
}

function Set-JsonProperty
{
	param(
		[object]$Object,
		[string]$Name,
		[object]$Value
	)

	if ( $Object.PSObject.Properties[$Name] )
	{
		$Object.$Name = $Value
		return
	}

	$Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
}

function Test-SboxLooseFileAllowed
{
	param(
		[string]$RelativePath,
		[bool]$AllowSourceFiles
	)

	$file = $RelativePath.Replace( '\', '/' )
	if ( $file.IndexOf( "/obj/", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
	if ( $file.IndexOf( "/.git", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
	if ( $file.IndexOf( "/.addon", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
	if ( $file.IndexOf( "/.editorconfig", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
	if ( $file.IndexOf( "/.vs/", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
	if ( $file.IndexOf( "_bakeresourcecache", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
	if ( $file.IndexOf( "launchsettings.json", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }

	if ( -not $AllowSourceFiles )
	{
		if ( $file.IndexOf( ".sbproj", [StringComparison]::OrdinalIgnoreCase ) -ge 0 ) { return $false }
		if ( $file.EndsWith( ".cs", [StringComparison]::OrdinalIgnoreCase ) ) { return $false }
		if ( $file.EndsWith( ".razor", [StringComparison]::OrdinalIgnoreCase ) ) { return $false }
		if ( $file.EndsWith( ".fbx", [StringComparison]::OrdinalIgnoreCase ) ) { return $false }
	}

	foreach ( $extension in @( ".dll", ".exe", ".csproj", ".sln", ".user", ".slnx", ".pdb" ) )
	{
		if ( $file.EndsWith( $extension, [StringComparison]::OrdinalIgnoreCase ) )
		{
			return $false
		}
	}

	return $true
}

function Test-ReleaseWorkspace
{
	param(
		[string]$PublishRoot
	)

	$blockedPrefixes = @(
		".git/",
		".sbox/",
		".vscode/",
		"_dist/",
		"_sbox_publish/",
		"_standalone/",
		".tmp/",
		"Code/obj/",
		"Editor/obj/",
		"dev/backend_config_archive/",
		"ProjectSettings/",
		"Libraries/"
	)

	$badFiles = New-Object System.Collections.Generic.List[string]
	$candidateCount = 0
	$dllPath = "Editor/CitizenRetarget/Native/win-x64/ual2_ufbx_helper.dll"
	$dllPresent = $false
	$dllPublishable = $false

	foreach ( $file in Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Force )
	{
		$relative = $file.FullName.Substring( $PublishRoot.Length ).TrimStart( '\', '/' ).Replace( '\', '/' )
		foreach ( $prefix in $blockedPrefixes )
		{
			if ( $relative.StartsWith( $prefix, [StringComparison]::OrdinalIgnoreCase ) )
			{
				$badFiles.Add( $relative )
			}
		}

		if ( $relative.Equals( $dllPath, [StringComparison]::OrdinalIgnoreCase ) )
		{
			$dllPresent = $true
		}

		if ( (Test-SboxLooseFileAllowed $relative $true) -and $file.Length -gt 0 )
		{
			$candidateCount++
			if ( $relative.Equals( $dllPath, [StringComparison]::OrdinalIgnoreCase ) )
			{
				$dllPublishable = $true
			}
		}
	}

	if ( $badFiles.Count -gt 0 )
	{
		throw "Release workspace contains ignored/generated files:`n - $($badFiles -join "`n - ")"
	}

	Write-Host "CARL release project created: $PublishRoot"
	Write-Host "Files copied from git-visible project state: $((Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Force | Measure-Object).Count)"
	Write-Host "Approximate s&box source-publish candidate files: $candidateCount"

	if ( $dllPresent -and -not $dllPublishable )
	{
		Write-Warning "s&box source publishing filters out .dll files, including $dllPath. Publish the native helper zip on GitHub Releases; package-manager users can install it from Diagnostics > Download Helper."
	}
}

$sourceRoot = Get-SourceRoot
if ( [string]::IsNullOrWhiteSpace( $OutputRoot ) )
{
	$OutputRoot = Join-Path (Split-Path -Parent $sourceRoot) "release_projects"
}

$outputRootFull = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $OutputRoot )
$publishRoot = Join-Path $outputRootFull $PackageName
$publishRootFull = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $publishRoot )
$sourceRootFull = $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath( $sourceRoot )

if ( $publishRootFull.Equals( $sourceRootFull, [System.StringComparison]::OrdinalIgnoreCase ) )
{
	throw "Refusing to publish over the source repository."
}

if ( -not $publishRootFull.StartsWith( $outputRootFull, [System.StringComparison]::OrdinalIgnoreCase ) )
{
	throw "Refusing to write outside output root: $publishRootFull"
}

if ( Test-Path -LiteralPath $publishRootFull )
{
	Remove-Item -LiteralPath $publishRootFull -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishRootFull | Out-Null

$releaseFiles = @( Get-GitReleaseFiles -SourceRoot $sourceRoot )
Copy-ReleaseFiles -SourceRoot $sourceRoot -PublishRoot $publishRootFull -RelativeFiles $releaseFiles
Set-PublishProjectIdentity -ProjectFile (Join-Path $publishRootFull "citizenretarget.sbproj") -Org $Org -Ident $Ident
Test-ReleaseWorkspace -PublishRoot $publishRootFull
