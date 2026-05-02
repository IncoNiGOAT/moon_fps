#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Editor.CitizenRetarget;

internal enum RetargetEnvironmentCheckSeverity
{
	Ok,
	Warning,
	Error
}

internal sealed class RetargetEnvironmentCheck
{
	public string Id { get; init; } = string.Empty;
	public string Title { get; init; } = string.Empty;
	public RetargetEnvironmentCheckSeverity Severity { get; init; }
	public bool BlocksRetarget { get; init; }
	public string Detail { get; init; } = string.Empty;
	public string FixHint { get; init; } = string.Empty;
	public string Path { get; init; } = string.Empty;
}

internal sealed class RetargetEnvironmentReport
{
	public DateTime CheckedAt { get; init; } = DateTime.Now;
	public string BackendRootPath { get; init; } = string.Empty;
	public string BackendSource { get; init; } = string.Empty;
	public string BlenderExecutablePath { get; init; } = string.Empty;
	public string BlenderVersion { get; init; } = string.Empty;
	public List<RetargetEnvironmentCheck> Checks { get; init; } = new();

	public bool CanRunRetarget => !Checks.Any( check => check.BlocksRetarget && check.Severity == RetargetEnvironmentCheckSeverity.Error );
	public IEnumerable<RetargetEnvironmentCheck> BlockingIssues => Checks.Where( check => check.BlocksRetarget && check.Severity == RetargetEnvironmentCheckSeverity.Error );
	public IEnumerable<RetargetEnvironmentCheck> Warnings => Checks.Where( check => check.Severity == RetargetEnvironmentCheckSeverity.Warning );

	public string BuildHeadline()
	{
		var blockingCount = BlockingIssues.Count();
		if ( blockingCount > 0 )
			return $"Setup is not ready. {blockingCount} blocking issue(s) need attention.";

		var warningCount = Warnings.Count();
		if ( warningCount > 0 )
			return $"Setup can run, with {warningCount} warning(s).";

		return "Setup is ready for retargeting.";
	}
}

internal sealed class RetargetEnvironmentDiagnosticsService
{
	private const string RokokoAddonName = "rokoko_studio_live_blender";
	private const string RokokoAddonDisplayName = "Rokoko Studio Live for Blender";

	public RetargetEnvironmentReport Check( CitizenRetargetJob job, bool probeBlenderAddon, Action<float, string>? progress = null )
	{
		var checks = new List<RetargetEnvironmentCheck>();
		var settings = RetargetToolSettings.Load();
		var setup = RetargetSetupResolver.Resolve( settings );
		var backendRoot = setup.BackendRootPath;
		var blenderExecutable = setup.BlenderExecutablePath;
		var blenderVersion = string.Empty;

		progress?.Invoke( 0.08f, "Checking backend files..." );
		CheckSettingsFile( checks );
		CheckBackendRoot( checks, setup );
		CheckBackendRecipe( checks, setup );
		CheckBackendPython( checks, setup );
		CheckTargetReference( checks, setup );
		CheckRecipePortability( checks, setup );

		progress?.Invoke( 0.26f, "Checking native FBX helper..." );
		CheckNativeHelper( checks );
		CheckCurrentTarget( checks, job );

		progress?.Invoke( 0.42f, "Detecting Blender..." );
		CheckBlenderExecutable( checks, setup );
		if ( !string.IsNullOrWhiteSpace( blenderExecutable ) )
			blenderVersion = CheckBlenderVersion( checks, blenderExecutable );

		if ( probeBlenderAddon )
		{
			progress?.Invoke( 0.68f, "Checking Rokoko addon in Blender..." );
			CheckRokokoAddon( checks, blenderExecutable, settings.RokokoAddonPath );
		}
		else
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "rokoko_addon",
				Title = "Rokoko Blender addon",
				Severity = RetargetEnvironmentCheckSeverity.Warning,
				BlocksRetarget = true,
				Detail = "Not checked yet. Run setup diagnostics before the first retarget.",
				FixHint = "Press Re-scan Setup in Diagnostics, or start the queue and the plugin will check it automatically."
			} );
		}

		progress?.Invoke( 1f, "Environment diagnostics finished." );
		return new RetargetEnvironmentReport
		{
			CheckedAt = DateTime.Now,
			BackendRootPath = backendRoot,
			BackendSource = setup.BackendSource,
			BlenderExecutablePath = blenderExecutable,
			BlenderVersion = blenderVersion,
			Checks = checks
		};
	}

	private static void CheckSettingsFile( List<RetargetEnvironmentCheck> checks )
	{
		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "tool_settings",
			Title = "Tool settings",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = $"Using settings file {RetargetToolSettings.SettingsAssetPath}.",
			Path = RetargetToolSettings.SettingsAbsolutePath
		} );
	}

	private static void CheckBackendRoot( List<RetargetEnvironmentCheck> checks, RetargetSetupResolution setup )
	{
		if ( string.IsNullOrWhiteSpace( setup.BackendRootPath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "backend_root",
				Title = "Bundled retarget backend",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Bundled backend was not found at {RetargetSetupResolver.BundledBackendRootPath}.",
				FixHint = "Reinstall or re-sync the plugin. Developers can set Backend Override in Diagnostics to use a repo checkout."
			} );
			return;
		}

		if ( !Directory.Exists( setup.BackendRootPath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "backend_root",
				Title = "Retarget backend project",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Backend {setup.BackendSource} folder does not exist: {setup.BackendRootPath}",
				FixHint = string.IsNullOrWhiteSpace( setup.ConfiguredBackendRootPath )
					? "Reinstall or re-sync the plugin so the bundled backend is present."
					: "Clear Backend Override to use the bundled backend, or pick a folder that contains tools/blender/retarget_job.py.",
				Path = setup.BackendRootPath
			} );
			return;
		}

		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "backend_root",
			Title = "Retarget backend project",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = $"Using {setup.BackendSource} at {setup.BackendRootPath}.",
			Path = setup.BackendRootPath
		} );
	}

	private static void CheckBackendRecipe( List<RetargetEnvironmentCheck> checks, RetargetSetupResolution setup )
	{
		if ( string.IsNullOrWhiteSpace( setup.BackendRecipePath ) || !File.Exists( setup.BackendRecipePath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "backend_recipe",
				Title = "Backend recipe",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Missing recipe: {setup.BackendRecipePath}",
				FixHint = string.IsNullOrWhiteSpace( setup.ConfiguredBackendRootPath )
					? "The bundled backend package is incomplete. Reinstall or re-sync the plugin."
					: "Clear Backend Override to use the bundled backend, or point it to a folder that contains tools/blender/config/recipes/retarget_rokoko_citizen.json."
			} );
			return;
		}

		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "backend_recipe",
			Title = "Backend recipe",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = $"Using recipe {CitizenTargetProfile.DefaultBackendRecipeRelativePath}.",
			Path = setup.BackendRecipePath
		} );
	}

	private static void CheckBackendPython( List<RetargetEnvironmentCheck> checks, RetargetSetupResolution setup )
	{
		if ( string.IsNullOrWhiteSpace( setup.BackendScriptPath ) || !File.Exists( setup.BackendScriptPath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "backend_script",
				Title = "Backend Python script",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Missing script: {setup.BackendScriptPath}",
				FixHint = string.IsNullOrWhiteSpace( setup.ConfiguredBackendRootPath )
					? "The bundled backend package is incomplete. Reinstall or re-sync the plugin."
					: "Clear Backend Override to use the bundled backend, or point it to a folder that contains tools/blender/retarget_job.py."
			} );
			return;
		}

		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "backend_script",
			Title = "Backend Python script",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = "Found tools/blender/retarget_job.py.",
			Path = setup.BackendScriptPath
		} );
	}

	private static void CheckTargetReference( List<RetargetEnvironmentCheck> checks, RetargetSetupResolution setup )
	{
		if ( string.IsNullOrWhiteSpace( setup.TargetReferencePath ) || !File.Exists( setup.TargetReferencePath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "target_reference",
				Title = "Citizen reference FBX",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = "Could not resolve models/citizen/citizen_ref.fbx or models/citizen/citizen.fbx from this project.",
				FixHint = "Make sure the project has Citizen model assets installed before retargeting."
			} );
			return;
		}

		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "target_reference",
			Title = "Citizen reference FBX",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = "Retarget runs will inject this project-resolved reference into the backend recipe.",
			Path = setup.TargetReferencePath
		} );
	}

	private static void CheckRecipePortability( List<RetargetEnvironmentCheck> checks, RetargetSetupResolution setup )
	{
		if ( !File.Exists( setup.BackendRecipePath ) )
			return;

		try
		{
			using var document = JsonDocument.Parse( File.ReadAllText( setup.BackendRecipePath ) );
			var targetReferencePath = FindJsonStringProperty( document.RootElement, "targetReferencePath" );
			if ( string.IsNullOrWhiteSpace( targetReferencePath ) )
				return;

			var rooted = Path.IsPathRooted( targetReferencePath );
			if ( rooted )
			{
				checks.Add( new RetargetEnvironmentCheck
				{
					Id = "recipe_target_reference",
					Title = "Recipe target reference",
					Severity = RetargetEnvironmentCheckSeverity.Warning,
					BlocksRetarget = false,
					Detail = $"Recipe uses an absolute target reference path: {targetReferencePath}",
					FixHint = "The plugin will override this per-run with the project-resolved Citizen reference. Clean the backend recipe later to remove this warning."
				} );
			}
		}
		catch ( Exception exception )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "recipe_target_reference",
				Title = "Recipe target reference",
				Severity = RetargetEnvironmentCheckSeverity.Warning,
				Detail = $"Could not inspect recipe portability: {exception.Message}",
				FixHint = "Open the recipe and verify targetReferencePath is not machine-specific."
			} );
		}
	}

	private static void CheckNativeHelper( List<RetargetEnvironmentCheck> checks )
	{
		var dllPath = Path.Combine( CitizenRetargetPaths.NativeRoot, "win-x64", "ual2_ufbx_helper.dll" );
		if ( !File.Exists( dllPath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "native_fbx",
				Title = "Native FBX scanner",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Missing native helper DLL: {dllPath}",
				FixHint = "Press Download Helper in Diagnostics to install the verified GitHub release asset, or build it locally from Editor/CitizenRetarget/Native/build_win_x64.ps1."
			} );
			return;
		}

		try
		{
			using var native = new UfbxNative();
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "native_fbx",
				Title = "Native FBX scanner",
				Severity = RetargetEnvironmentCheckSeverity.Ok,
				Detail = "Native FBX helper DLL loads successfully.",
				Path = dllPath
			} );
		}
		catch ( Exception exception )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "native_fbx",
				Title = "Native FBX scanner",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Native helper failed to load: {exception.Message}",
				FixHint = "Check that the DLL architecture matches the editor process and all native dependencies are present.",
				Path = dllPath
			} );
		}
	}

	private static void CheckCurrentTarget( List<RetargetEnvironmentCheck> checks, CitizenRetargetJob job )
	{
		var targetPath = job.TargetVmdlPath ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( targetPath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "target_model",
				Title = "Current target model",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = "No target VMDL is selected.",
				FixHint = "Create or open a target workspace on Home before retargeting."
			} );
			return;
		}

		var absoluteTargetPath = CitizenRetargetPaths.GetAssetAbsolutePath( targetPath );
		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "target_model",
			Title = "Current target model",
			Severity = File.Exists( absoluteTargetPath ) ? RetargetEnvironmentCheckSeverity.Ok : RetargetEnvironmentCheckSeverity.Warning,
			BlocksRetarget = false,
			Detail = File.Exists( absoluteTargetPath )
				? $"Current target exists: {targetPath}"
				: $"Current target does not exist yet and will be generated: {targetPath}",
			Path = absoluteTargetPath
		} );
	}

	private static void CheckBlenderExecutable( List<RetargetEnvironmentCheck> checks, RetargetSetupResolution setup )
	{
		if ( string.IsNullOrWhiteSpace( setup.BlenderExecutablePath ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "blender",
				Title = "Blender executable",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = "Could not locate blender.exe.",
				FixHint = "Set Blender Path in Diagnostics, install Blender 4.4, add Blender to PATH, or set CITIZEN_RETARGET_BLENDER."
			} );
			return;
		}

		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "blender",
			Title = "Blender executable",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = $"Found Blender via {setup.BlenderSource}: {setup.BlenderExecutablePath}",
			Path = setup.BlenderExecutablePath
		} );
	}

	private static string CheckBlenderVersion( List<RetargetEnvironmentCheck> checks, string blenderExecutable )
	{
		var result = RunProcess( blenderExecutable, new[] { "--version" }, CitizenRetargetPaths.ProjectRoot, 7000 );
		if ( result.TimedOut || result.ExitCode != 0 )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "blender_version",
				Title = "Blender version",
				Severity = RetargetEnvironmentCheckSeverity.Warning,
				Detail = result.TimedOut
					? "Blender version check timed out."
					: $"Blender version check exited with code {result.ExitCode}.",
				FixHint = "Open Blender manually once. If it shows a first-run prompt, finish setup and re-scan."
			} );
			return string.Empty;
		}

		var firstLine = result.Stdout.Split( [ '\r', '\n' ], StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault()?.Trim() ?? "Blender version detected";
		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "blender_version",
			Title = "Blender version",
			Severity = firstLine.Contains( "4.4", StringComparison.OrdinalIgnoreCase )
				? RetargetEnvironmentCheckSeverity.Ok
				: RetargetEnvironmentCheckSeverity.Warning,
			BlocksRetarget = false,
			Detail = firstLine.Contains( "4.4", StringComparison.OrdinalIgnoreCase )
				? firstLine
				: $"{firstLine}. Blender 4.4 is the currently known-good Rokoko backend version.",
			FixHint = firstLine.Contains( "4.4", StringComparison.OrdinalIgnoreCase )
				? string.Empty
				: "If retargeting fails inside Rokoko, install Blender 4.4 and point the plugin to it."
		} );
		return firstLine;
	}

	private static void CheckRokokoAddon( List<RetargetEnvironmentCheck> checks, string blenderExecutable, string configuredAddonPath )
	{
		if ( string.IsNullOrWhiteSpace( blenderExecutable ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "rokoko_addon",
				Title = "Rokoko Blender addon",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = "Skipped because Blender was not found.",
				FixHint = "Install Blender first, then install the Rokoko Blender addon."
			} );
			return;
		}

		if ( !TryResolveConfiguredRokokoAddonPath( configuredAddonPath, out var configuredAddonInitPath, out var configuredPathError ) )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "rokoko_addon_path",
				Title = "Rokoko addon override",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = configuredPathError,
				FixHint = "Clear Rokoko Addon to auto-scan Blender addons, or paste the Rokoko addon folder/__init__.py used by this Blender.",
				Path = configuredAddonPath
			} );
			return;
		}

		var probeDirectory = CitizenRetargetPaths.GetTempPath( "environment" );
		Directory.CreateDirectory( probeDirectory );
		var probePath = Path.Combine( probeDirectory, "rokoko_addon_probe.py" );
		File.WriteAllText( probePath, BuildRokokoProbeScript( configuredAddonInitPath ) );
		var result = RunProcess(
			blenderExecutable,
			new[] { "--background", "--python", probePath },
			CitizenRetargetPaths.ProjectRoot,
			25000 );

		var payload = ParseProbePayload( result.Stdout + Environment.NewLine + result.Stderr );
		if ( result.TimedOut )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "rokoko_addon",
				Title = "Rokoko Blender addon",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = "Blender timed out while checking the Rokoko addon.",
				FixHint = "Open Blender manually, verify Rokoko addon is installed, then re-scan setup."
			} );
			return;
		}

		if ( payload is null )
		{
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "rokoko_addon",
				Title = "Rokoko Blender addon",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = $"Could not read Rokoko probe output. Exit code: {result.ExitCode}.",
				FixHint = "Open Run Log/console output if this repeats. The addon probe script may be incompatible with this Blender install."
			} );
			return;
		}

		if ( !payload.Installed || !payload.Enabled )
		{
			var foundText = string.IsNullOrWhiteSpace( payload.ModuleName )
				? string.Empty
				: $" Found candidate '{payload.AddonName}' as module '{payload.ModuleName}' at {payload.Path}.";
			checks.Add( new RetargetEnvironmentCheck
			{
				Id = "rokoko_addon",
				Title = "Rokoko Blender addon",
				Severity = RetargetEnvironmentCheckSeverity.Error,
				BlocksRetarget = true,
				Detail = string.IsNullOrWhiteSpace( payload.Error )
					? $"Could not find and enable {RokokoAddonDisplayName}.{foundText}"
					: payload.Error,
				FixHint = string.IsNullOrWhiteSpace( configuredAddonInitPath )
					? "Open the same Blender executable shown above, enable Rokoko Studio Live for Blender, then re-scan setup. If this is Blender 5.x and retargeting still fails, install Blender 4.4 and point Blender Path to it."
					: "The manual Rokoko Addon path must belong to the same Blender install. Clear it to auto-scan, or paste the correct addon folder/__init__.py."
			} );
			return;
		}

		var addonLabel = string.IsNullOrWhiteSpace( payload.AddonName ) ? RokokoAddonDisplayName : payload.AddonName;
		var moduleLabel = string.IsNullOrWhiteSpace( payload.ModuleName ) ? RokokoAddonName : payload.ModuleName;
		checks.Add( new RetargetEnvironmentCheck
		{
			Id = "rokoko_addon",
			Title = "Rokoko Blender addon",
			Severity = RetargetEnvironmentCheckSeverity.Ok,
			Detail = string.IsNullOrWhiteSpace( payload.Version )
				? $"{addonLabel} is installed and enabled as '{moduleLabel}'{(string.IsNullOrWhiteSpace( configuredAddonInitPath ) ? " via auto-scan" : " via manual path")}."
				: $"{addonLabel} is installed and enabled as '{moduleLabel}'{(string.IsNullOrWhiteSpace( configuredAddonInitPath ) ? " via auto-scan" : " via manual path")}. Version: {payload.Version}.",
			Path = payload.Path
		} );
	}

	private static bool TryResolveConfiguredRokokoAddonPath( string configuredPath, out string initPath, out string error )
	{
		initPath = string.Empty;
		error = string.Empty;
		var normalizedPath = (configuredPath ?? string.Empty).Trim().Trim( '"' );
		if ( string.IsNullOrWhiteSpace( normalizedPath ) )
			return true;

		if ( Directory.Exists( normalizedPath ) )
		{
			var candidate = Path.Combine( normalizedPath, "__init__.py" );
			if ( File.Exists( candidate ) )
			{
				initPath = Path.GetFullPath( candidate );
				return true;
			}

			error = $"Configured Rokoko addon folder does not contain __init__.py: {normalizedPath}";
			return false;
		}

		if ( File.Exists( normalizedPath ) )
		{
			if ( string.Equals( Path.GetFileName( normalizedPath ), "__init__.py", StringComparison.OrdinalIgnoreCase ) )
			{
				initPath = Path.GetFullPath( normalizedPath );
				return true;
			}

			error = $"Configured Rokoko addon file must be __init__.py, not {Path.GetFileName( normalizedPath )}.";
			return false;
		}

		error = $"Configured Rokoko addon path does not exist: {normalizedPath}";
		return false;
	}

	private static string? FindJsonStringProperty( JsonElement element, string propertyName )
	{
		if ( element.ValueKind == JsonValueKind.Object )
		{
			foreach ( var property in element.EnumerateObject() )
			{
				if ( property.NameEquals( propertyName ) && property.Value.ValueKind == JsonValueKind.String )
					return property.Value.GetString();

				var nested = FindJsonStringProperty( property.Value, propertyName );
				if ( !string.IsNullOrWhiteSpace( nested ) )
					return nested;
			}
		}
		else if ( element.ValueKind == JsonValueKind.Array )
		{
			foreach ( var child in element.EnumerateArray() )
			{
				var nested = FindJsonStringProperty( child, propertyName );
				if ( !string.IsNullOrWhiteSpace( nested ) )
					return nested;
			}
		}

		return null;
	}

	private static ProcessRunResult RunProcess( string fileName, IReadOnlyList<string> arguments, string workingDirectory, int timeoutMilliseconds )
	{
		using var process = new Process();
		process.StartInfo = new ProcessStartInfo
		{
			FileName = fileName,
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach ( var argument in arguments )
			process.StartInfo.ArgumentList.Add( argument );

		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		process.OutputDataReceived += (_, args) =>
		{
			if ( args.Data is not null )
				stdout.AppendLine( args.Data );
		};
		process.ErrorDataReceived += (_, args) =>
		{
			if ( args.Data is not null )
				stderr.AppendLine( args.Data );
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		var completed = process.WaitForExit( timeoutMilliseconds );
		if ( !completed )
		{
			try
			{
				process.Kill();
			}
			catch
			{
				// The timeout status is enough for diagnostics.
			}

			return new ProcessRunResult
			{
				ExitCode = -1,
				TimedOut = true,
				Stdout = stdout.ToString(),
				Stderr = stderr.ToString()
			};
		}

		process.WaitForExit();
		return new ProcessRunResult
		{
			ExitCode = process.ExitCode,
			Stdout = stdout.ToString(),
			Stderr = stderr.ToString()
		};
	}

	private static string BuildRokokoProbeScript( string configuredAddonInitPath )
	{
		var configuredAddonJson = JsonSerializer.Serialize( configuredAddonInitPath ?? string.Empty );
		return $$"""
import addon_utils
import json
import os
import sys

primary_name = "{{RokokoAddonName}}"
configured_addon_init_path = {{configuredAddonJson}}
payload = {
    "installed": False,
    "enabled": False,
    "version": "",
    "moduleName": "",
    "addonName": "",
    "path": "",
    "error": ""
}

def clean(value):
    return str(value or "").strip()

def normalized(value):
    return clean(value).lower().replace("_", "-")

def norm_path(value):
    value = clean(value)
    if not value:
        return ""
    try:
        return os.path.normcase(os.path.abspath(value))
    except Exception:
        return os.path.normcase(value)

def module_info(module):
    info = getattr(module, "bl_info", {}) or {}
    return info if isinstance(info, dict) else {}

def module_init_path(module):
    return norm_path(getattr(module, "__file__", ""))

def module_folder(module):
    path = module_init_path(module)
    return norm_path(os.path.dirname(path)) if path else ""

def matches_configured_path(module):
    if not configured_addon_init_path:
        return False
    configured = norm_path(configured_addon_init_path)
    configured_folder = norm_path(os.path.dirname(configured))
    return module_init_path(module) == configured or module_folder(module) == configured_folder

def is_rokoko_candidate(module):
    module_name = clean(getattr(module, "__name__", ""))
    info = module_info(module)
    addon_name = clean(info.get("name"))
    path = clean(getattr(module, "__file__", ""))
    haystack = normalized(" ".join([module_name, addon_name, path]))
    return "rokoko" in haystack and ("studio" in haystack or "live" in haystack or "rokoko-studio-live-blender" in haystack)

def find_candidate():
    configured_match = None
    exact = None
    rokoko = []
    for module in addon_utils.modules():
        if matches_configured_path(module):
            configured_match = module
            break
        module_name = clean(getattr(module, "__name__", ""))
        if module_name == primary_name:
            exact = module
            break
        if is_rokoko_candidate(module):
            rokoko.append(module)
    return configured_match or exact or (rokoko[0] if rokoko else None)

try:
    module = find_candidate()
    if module is not None:
        module_name = clean(getattr(module, "__name__", ""))
        payload["installed"] = True
        payload["moduleName"] = module_name
        payload["path"] = clean(getattr(module, "__file__", ""))
        info = module_info(module)
        payload["addonName"] = clean(info.get("name"))
        if not addon_utils.check(module_name)[1]:
            addon_utils.enable(module_name, default_set=False, persistent=False)
        payload["enabled"] = addon_utils.check(module_name)[1]
        module = sys.modules.get(module_name) or module
    else:
        info = {}
        if configured_addon_init_path:
            payload["error"] = "Configured Rokoko addon path is valid, but this Blender executable does not list it as an installed addon: " + configured_addon_init_path
    version = info.get("version") if isinstance(info, dict) else None
    if version:
        payload["version"] = ".".join(str(part) for part in version)
except Exception as exception:
    payload["error"] = str(exception)

print("CITIZEN_RETARGET_ENV_JSON:" + json.dumps(payload))
sys.exit(0 if payload["installed"] and payload["enabled"] else 2)
""";
	}

	private static RokokoProbePayload? ParseProbePayload( string output )
	{
		const string prefix = "CITIZEN_RETARGET_ENV_JSON:";
		foreach ( var line in output.Split( [ '\r', '\n' ], StringSplitOptions.RemoveEmptyEntries ) )
		{
			var index = line.IndexOf( prefix, StringComparison.Ordinal );
			if ( index < 0 )
				continue;

			var json = line[(index + prefix.Length)..].Trim();
			try
			{
				return JsonSerializer.Deserialize<RokokoProbePayload>( json, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				} );
			}
			catch
			{
				return null;
			}
		}

		return null;
	}

	private sealed class ProcessRunResult
	{
		public int ExitCode { get; init; }
		public bool TimedOut { get; init; }
		public string Stdout { get; init; } = string.Empty;
		public string Stderr { get; init; } = string.Empty;
	}

	private sealed class RokokoProbePayload
	{
		public bool Installed { get; init; }
		public bool Enabled { get; init; }
		public string Version { get; init; } = string.Empty;
		public string ModuleName { get; init; } = string.Empty;
		public string AddonName { get; init; } = string.Empty;
		public string Path { get; init; } = string.Empty;
		public string Error { get; init; } = string.Empty;
	}
}
