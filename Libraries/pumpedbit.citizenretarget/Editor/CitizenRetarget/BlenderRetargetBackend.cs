#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.CitizenRetarget;

internal sealed class BlenderRetargetBackend
{
	private const int MaxRetainedBackendRunDirectories = 12;

	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true
	};

	public RetargetImportResult RunRetarget(
		CitizenRetargetJob job,
		RetargetClipDescriptor clip,
		RetargetSourceProfile sourceProfile,
		RetargetMappingProfile mappingProfile,
		IReadOnlyList<RetargetSlotAssignmentState> mappingState,
		Vector3 sourceFacingEulerDegrees = default )
	{
		var settings = RetargetToolSettings.Load();
		var setup = RetargetSetupResolver.Resolve( settings );
		var backendRoot = setup.BackendRootPath;
		var blenderExecutable = setup.BlenderExecutablePath;
		if ( string.IsNullOrWhiteSpace( backendRoot ) || !Directory.Exists( backendRoot ) )
			throw new DirectoryNotFoundException( "Bundled retarget backend is missing. Reinstall or re-sync the plugin; developers can set Backend Override in Diagnostics." );
		if ( string.IsNullOrWhiteSpace( blenderExecutable ) || !File.Exists( blenderExecutable ) )
			throw new FileNotFoundException( "Could not locate blender.exe. Set Blender Path in Diagnostics, set CITIZEN_RETARGET_BLENDER, or install Blender 4.4." );
		if ( string.IsNullOrWhiteSpace( setup.BackendRecipePath ) || !File.Exists( setup.BackendRecipePath ) )
			throw new FileNotFoundException( $"Could not locate the Blender recipe at '{setup.BackendRecipePath}'." );
		if ( string.IsNullOrWhiteSpace( setup.BackendScriptPath ) || !File.Exists( setup.BackendScriptPath ) )
			throw new FileNotFoundException( $"Could not locate the Blender backend script at '{setup.BackendScriptPath}'." );
		if ( string.IsNullOrWhiteSpace( setup.TargetReferencePath ) || !File.Exists( setup.TargetReferencePath ) )
			throw new FileNotFoundException( "Could not resolve a Citizen reference FBX from the current project assets." );

		var outputRoot = CitizenRetargetPaths.GetTempPath( "backend_runs" );
		Directory.CreateDirectory( outputRoot );
		PruneOldBackendRuns( outputRoot );

		var runId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
		var runDirectory = Path.Combine( outputRoot, runId );
		Directory.CreateDirectory( runDirectory );

		var recipePath = WriteResolvedRecipeFile( setup.BackendRecipePath, runDirectory, setup.TargetReferencePath );
		var overridePath = WriteBoneMapOverrideFile( runDirectory, job, sourceProfile, mappingProfile, mappingState, sourceFacingEulerDegrees );
		var result = new RetargetImportResult
		{
			OutputFormat = RetargetOutputFormat.FbxBackend,
			BackendInvocation = new RetargetBackendInvocation
			{
				BackendRootPath = backendRoot,
				BlenderExecutablePath = blenderExecutable,
				RecipePath = recipePath,
				RunId = runId,
				BoneMapOverridePath = overridePath
			}
		};

		var log = new StringBuilder();
		log.AppendLine( $"Using Blender executable: {blenderExecutable}" );
		log.AppendLine( $"Using backend root: {backendRoot}" );
		log.AppendLine( $"Using Citizen target reference: {setup.TargetReferencePath}" );
		var retargetExitCode = RunBlenderScript(
			blenderExecutable,
			backendRoot,
			setup.BackendScriptPath,
			BuildRetargetArguments( recipePath, job, clip, sourceProfile, runId, outputRoot, overridePath ),
			log,
			includeFactoryStartup: false,
			addons: null,
			rokokoAddonPath: settings.RokokoAddonPath,
			throwOnFailure: false );

		var manifestPath = Path.Combine( outputRoot, runId, "result_manifest.json" );
		if ( File.Exists( manifestPath ) )
		{
			result.ManifestAbsolutePath = manifestPath;
			result.Manifest = RetargetManifestJson.Deserialize<RetargetRunManifest>( File.ReadAllText( manifestPath ) );
			result.GeneratedClipAbsolutePath = result.Manifest.Outputs.ExportPath ?? string.Empty;
		}
		else if ( retargetExitCode == 0 )
		{
			throw new InvalidOperationException(
				$"Blender retarget job finished without writing the required result_manifest.json.{Environment.NewLine}{log}" );
		}
		else if ( retargetExitCode != 0 )
		{
			throw new InvalidOperationException(
				$"Blender retarget job failed before writing a manifest.{Environment.NewLine}{log}" );
		}

		result.Log = log.ToString();
		return result;
	}

	private string WriteResolvedRecipeFile( string sourceRecipePath, string runDirectory, string targetReferencePath )
	{
		var root = JsonNode.Parse( File.ReadAllText( sourceRecipePath ) )
			?? throw new InvalidOperationException( $"Recipe '{sourceRecipePath}' is empty or invalid JSON." );
		ReplaceTargetReferencePath( root, targetReferencePath );

		var recipePath = Path.Combine( runDirectory, "resolved_retarget_recipe.json" );
		File.WriteAllText( recipePath, root.ToJsonString( _jsonOptions ) );
		return recipePath;
	}

	private static void ReplaceTargetReferencePath( JsonNode node, string targetReferencePath )
	{
		if ( node is JsonObject obj )
		{
			foreach ( var property in obj.ToList() )
			{
				if ( property.Key.Equals( "targetReferencePath", StringComparison.OrdinalIgnoreCase ) )
					obj[property.Key] = targetReferencePath;
				else if ( property.Value is not null )
					ReplaceTargetReferencePath( property.Value, targetReferencePath );
			}
		}
		else if ( node is JsonArray array )
		{
			foreach ( var child in array )
			{
				if ( child is not null )
					ReplaceTargetReferencePath( child, targetReferencePath );
			}
		}
	}

	private string[] BuildRetargetArguments(
		string recipePath,
		CitizenRetargetJob job,
		RetargetClipDescriptor clip,
		RetargetSourceProfile sourceProfile,
		string runId,
		string outputRoot,
		string overridePath )
	{
		return
		[
			"--recipe", recipePath,
			"--input", job.SourceFbxPath,
			"--output-root", outputRoot,
			"--action", clip.SourceName,
			"--source-profile", sourceProfile.BackendSourceProfileId,
			"--run-id", runId,
			"--target-pose-preset", string.IsNullOrWhiteSpace( job.TargetPosePresetId ) ? CitizenTargetProfile.DefaultTargetPosePresetId : job.TargetPosePresetId,
			"--bone-map-overrides", overridePath
		];
	}

	private int RunBlenderScript(
		string blenderExecutable,
		string workingDirectory,
		string pythonScriptPath,
		IReadOnlyList<string> scriptArguments,
		StringBuilder log,
		bool includeFactoryStartup,
		string? addons,
		string? rokokoAddonPath,
		bool throwOnFailure = true )
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = blenderExecutable,
			WorkingDirectory = workingDirectory,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		startInfo.ArgumentList.Add( "--background" );
		if ( includeFactoryStartup )
			startInfo.ArgumentList.Add( "--factory-startup" );
		if ( !string.IsNullOrWhiteSpace( addons ) )
		{
			startInfo.ArgumentList.Add( "--addons" );
			startInfo.ArgumentList.Add( addons );
		}
		if ( !string.IsNullOrWhiteSpace( rokokoAddonPath ) )
			startInfo.Environment["CITIZEN_RETARGET_ROKOKO_ADDON_PATH"] = rokokoAddonPath.Trim().Trim( '"' );

		startInfo.ArgumentList.Add( "--python" );
		startInfo.ArgumentList.Add( pythonScriptPath );
		startInfo.ArgumentList.Add( "--" );
		foreach ( var argument in scriptArguments )
			startInfo.ArgumentList.Add( argument );

		using var process = new Process { StartInfo = startInfo };
		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		process.OutputDataReceived += (_, args) =>
		{
			if ( !string.IsNullOrWhiteSpace( args.Data ) )
				stdout.AppendLine( args.Data );
		};
		process.ErrorDataReceived += (_, args) =>
		{
			if ( !string.IsNullOrWhiteSpace( args.Data ) )
				stderr.AppendLine( args.Data );
		};
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.WaitForExit();

		if ( stdout.Length > 0 )
			log.AppendLine( stdout.ToString().Trim() );
		if ( stderr.Length > 0 )
			log.AppendLine( stderr.ToString().Trim() );

		if ( process.ExitCode != 0 && throwOnFailure )
		{
			throw new InvalidOperationException(
				$"Blender exited with code {process.ExitCode} while running '{Path.GetFileName( pythonScriptPath )}'." );
		}

		return process.ExitCode;
	}

	private string WriteBoneMapOverrideFile(
		string runDirectory,
		CitizenRetargetJob job,
		RetargetSourceProfile sourceProfile,
		RetargetMappingProfile mappingProfile,
		IReadOnlyList<RetargetSlotAssignmentState> mappingState,
		Vector3 sourceFacingEulerDegrees )
	{
		var payload = new Dictionary<string, object?>
		{
			["metadata"] = new Dictionary<string, object?>
			{
				["sourceProfileId"] = sourceProfile.ProfileId,
				["mappingProfileId"] = mappingProfile.ProfileId,
				["importHands"] = job.ImportHands,
				["targetPosePresetId"] = job.TargetPosePresetId,
				["targetFingerChainRemapMode"] = ResolveTargetFingerChainRemapMode( job, sourceProfile, mappingProfile ),
				["rootMotionMode"] = job.RootMotionMode.ToString(),
				["rootMotionReferenceFrame"] = job.RootMotionReferenceFrame,
				["rootMotionVerticalNudge"] = job.RootMotionVerticalNudge,
				["sourceFacingEulerDegrees"] = new[]
				{
					MathF.Round( sourceFacingEulerDegrees.x, 3 ),
					MathF.Round( sourceFacingEulerDegrees.y, 3 ),
					MathF.Round( sourceFacingEulerDegrees.z, 3 )
				}
			}
		};

		var slots = new Dictionary<string, object?>( StringComparer.OrdinalIgnoreCase );
		foreach ( var slot in mappingState )
		{
			var effectiveSourceBone = slot.EffectiveSourceBone?.Trim() ?? string.Empty;
			var enabled = slot.Enabled && (job.ImportHands || !slot.Group.Contains( "finger", StringComparison.OrdinalIgnoreCase ));
			if ( string.IsNullOrWhiteSpace( effectiveSourceBone ) && enabled )
				continue;

			slots[slot.SlotId] = new Dictionary<string, object?>
			{
				["sourceBone"] = effectiveSourceBone,
				["targetBone"] = slot.TargetBone,
				["required"] = slot.Required,
				["enabled"] = enabled,
				["locked"] = slot.Locked,
				["group"] = slot.Group,
				["userOverridden"] = slot.IsManualOverride,
				["mappingOrigin"] = slot.IsManualOverride ? "editor_manual" : "editor_auto"
			};
		}

		payload["slots"] = slots;

		var overridePath = Path.Combine( runDirectory, "bone_map_overrides.json" );
		File.WriteAllText( overridePath, JsonSerializer.Serialize( payload, _jsonOptions ) );
		return overridePath;
	}

	private static string ResolveTargetFingerChainRemapMode(
		CitizenRetargetJob job,
		RetargetSourceProfile sourceProfile,
		RetargetMappingProfile mappingProfile )
	{
		if ( !job.ImportHands )
			return "none";

		var sourceProfileId = sourceProfile.ProfileId?.Trim() ?? string.Empty;
		var mappingProfileId = mappingProfile.ProfileId?.Trim() ?? string.Empty;
		if ( sourceProfileId.Equals( "quaternius_ual2", StringComparison.OrdinalIgnoreCase )
			&& mappingProfileId.Equals( "ual2_to_citizen", StringComparison.OrdinalIgnoreCase ) )
		{
			return "citizen_012";
		}

		return "none";
	}

	private static void PruneOldBackendRuns( string outputRoot )
	{
		if ( !Directory.Exists( outputRoot ) )
			return;

		var directoryInfo = new DirectoryInfo( outputRoot );
		var runDirectories = directoryInfo
			.EnumerateDirectories()
			.OrderByDescending( directory => directory.LastWriteTimeUtc )
			.ToList();
		if ( runDirectories.Count <= MaxRetainedBackendRunDirectories )
			return;

		foreach ( var staleDirectory in runDirectories.Skip( MaxRetainedBackendRunDirectories ) )
		{
			try
			{
				staleDirectory.Delete( recursive: true );
			}
			catch
			{
				// Keep the run if Windows or Blender still has a handle open.
			}
		}
	}
}
