using System.Diagnostics;
using System.Text;
using System.Text.Json;

#nullable enable

namespace Editor.CitizenRetarget;

internal sealed class BlenderDmxExportBridge
{
	private static readonly string BlenderSourceToolsRoot = CitizenRetargetPaths.GetTempPath( "BlenderSourceTools" );
	private static readonly string[] ForensicFocusBones =
	{
		"pelvis",
		"spine_0",
		"spine_2",
		"clavicle_L",
		"arm_upper_L",
		"leg_upper_L"
	};
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	public BlenderTargetBindCache EnsureTargetBindCache()
	{
		var bindCachePath = CitizenRetargetPaths.GetTempPath( "blender", "reference", "citizen_bind_cache.json" );
		Directory.CreateDirectory( Path.GetDirectoryName( bindCachePath )! );

		if ( !File.Exists( bindCachePath ) )
		{
			RunBlender(
				$"--mode dump-bind --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --template-dmx \"{GetStockTemplateDmxAbsolutePath()}\" --output \"{bindCachePath}\" --bst-root \"{BlenderSourceToolsRoot}\"",
				"Blender bind cache export" );
		}

		if ( !File.Exists( bindCachePath ) )
			throw new FileNotFoundException( $"Blender bind cache export did not produce '{bindCachePath}'" );

		return LoadBindCache( bindCachePath );
	}

	public string ExportPayloadJsonToDmx(
		string payloadJsonPath,
		string outputAbsolutePath,
		string? templateDmxAbsolutePath = null,
		string? preExportPoseJsonAbsolutePath = null )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );
		var templatePath = string.IsNullOrWhiteSpace( templateDmxAbsolutePath )
			? GetStockTemplateDmxAbsolutePath()
			: templateDmxAbsolutePath;
		var templateArgument = $" --template-dmx \"{templatePath}\"";
		var preExportArgument = string.IsNullOrWhiteSpace( preExportPoseJsonAbsolutePath )
			? string.Empty
			: $" --pre-export-json \"{preExportPoseJsonAbsolutePath}\"";

		RunBlender(
			$"--mode export-payload --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --input-json \"{payloadJsonPath}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"{templateArgument}{preExportArgument}",
			"Blender DMX export" );

		var actualOutputPath = ResolveActualOutputPath( outputAbsolutePath );
		if ( string.IsNullOrWhiteSpace( actualOutputPath ) )
			throw new FileNotFoundException( $"Blender export did not produce '{outputAbsolutePath}'" );

		return actualOutputPath;
	}

	public string ExportPayloadJsonToFbx(
		string payloadJsonPath,
		string outputAbsolutePath,
		string? preExportPoseJsonAbsolutePath = null )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );
		var preExportArgument = string.IsNullOrWhiteSpace( preExportPoseJsonAbsolutePath )
			? string.Empty
			: $" --pre-export-json \"{preExportPoseJsonAbsolutePath}\"";

		RunBlender(
			$"--mode export-payload-fbx --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --input-json \"{payloadJsonPath}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"{preExportArgument}",
			"Blender FBX payload export" );

		if ( !File.Exists( outputAbsolutePath ) )
			throw new FileNotFoundException( $"Blender FBX payload export did not produce '{outputAbsolutePath}'" );

		return outputAbsolutePath;
	}

	public BlenderForensicArtifactResult CaptureForensicRoundTrip(
		string sequenceName,
		string payloadJsonPath,
		string outputAbsolutePath,
		string? templateDmxAbsolutePath = null )
	{
		var forensicFolder = CitizenRetargetPaths.GetTempPath( "blender", "forensics", sequenceName );
		Directory.CreateDirectory( forensicFolder );
		var payloadCopyPath = Path.Combine( forensicFolder, "payload.json" );
		var appliedPosePath = Path.Combine( forensicFolder, "applied_pose.json" );
		var reimportedPosePath = Path.Combine( forensicFolder, "reimported_pose.json" );
		var summaryJsonPath = Path.Combine( forensicFolder, "summary.json" );
		var summaryMarkdownPath = Path.Combine( forensicFolder, "summary.md" );

		File.Copy( payloadJsonPath, payloadCopyPath, overwrite: true );
		var actualOutputPath = ExportPayloadJsonToDmx( payloadCopyPath, outputAbsolutePath, templateDmxAbsolutePath, appliedPosePath );

		RunBlender(
			$"--mode dump-pose --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --input-dmx \"{actualOutputPath}\" --output \"{reimportedPosePath}\" --bst-root \"{BlenderSourceToolsRoot}\"",
			"Blender DMX post-import pose dump" );

		WriteForensicSummary( payloadCopyPath, appliedPosePath, reimportedPosePath, summaryJsonPath, summaryMarkdownPath );

		return new BlenderForensicArtifactResult
		{
			PayloadJsonAbsolutePath = payloadCopyPath,
			AppliedPoseJsonAbsolutePath = appliedPosePath,
			ReimportedPoseJsonAbsolutePath = reimportedPosePath,
			SummaryJsonAbsolutePath = summaryJsonPath,
			SummaryMarkdownAbsolutePath = summaryMarkdownPath,
			GeneratedDmxAbsolutePath = actualOutputPath
		};
	}

	public string RoundTripDmx( string inputDmxAbsolutePath, string outputAbsolutePath )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );

		RunBlender(
			$"--mode roundtrip-dmx --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --input-dmx \"{inputDmxAbsolutePath}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"",
			"Blender DMX roundtrip" );

		var actualOutputPath = ResolveActualOutputPath( outputAbsolutePath );
		if ( string.IsNullOrWhiteSpace( actualOutputPath ) )
			throw new FileNotFoundException( $"Blender roundtrip did not produce '{outputAbsolutePath}'" );

		return actualOutputPath;
	}

	public string ExportDirectTransferClipToDmx(
		string sourceFbxAbsolutePath,
		string clipSourceName,
		string sequenceName,
		string mappingJsonAbsolutePath,
		string compensationJsonAbsolutePath,
		bool includeHands,
		string outputAbsolutePath,
		string? templateDmxAbsolutePath = null )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );
		var templatePath = string.IsNullOrWhiteSpace( templateDmxAbsolutePath )
			? GetStockTemplateDmxAbsolutePath()
			: templateDmxAbsolutePath;
		var includeHandsFlag = includeHands ? "1" : "0";

		RunBlender(
			$"--mode retarget-clip --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --source-fbx \"{sourceFbxAbsolutePath}\" --clip-name \"{clipSourceName}\" --sequence-name \"{sequenceName}\" --mapping-json \"{mappingJsonAbsolutePath}\" --compensation-json \"{compensationJsonAbsolutePath}\" --include-hands \"{includeHandsFlag}\" --template-dmx \"{templatePath}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"",
			"Blender direct-transfer DMX export" );

		var actualOutputPath = ResolveActualOutputPath( outputAbsolutePath );
		if ( string.IsNullOrWhiteSpace( actualOutputPath ) )
			throw new FileNotFoundException( $"Blender direct-transfer export did not produce '{outputAbsolutePath}'" );

		return actualOutputPath;
	}

	public string ExportSolvedCitizenFbxToDmx(
		string sourceFbxAbsolutePath,
		string sequenceName,
		string outputAbsolutePath,
		string? sourceActionName = null,
		string? templateDmxAbsolutePath = null )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );
		var templatePath = string.IsNullOrWhiteSpace( templateDmxAbsolutePath )
			? GetStockTemplateDmxAbsolutePath()
			: templateDmxAbsolutePath;
		var sourceActionArgument = string.IsNullOrWhiteSpace( sourceActionName )
			? string.Empty
			: $" --source-action \"{sourceActionName}\"";

		RunBlender(
			$"--mode copy-fbx-to-dmx --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --source-fbx \"{sourceFbxAbsolutePath}\" --sequence-name \"{sequenceName}\" --template-dmx \"{templatePath}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"{sourceActionArgument}",
			"Blender solved-FBX to DMX export" );

		var actualOutputPath = ResolveActualOutputPath( outputAbsolutePath );
		if ( string.IsNullOrWhiteSpace( actualOutputPath ) )
			throw new FileNotFoundException( $"Blender solved-FBX export did not produce '{outputAbsolutePath}'" );

		return actualOutputPath;
	}

	public string ExportSolvedCitizenFbxToTemplateFbx(
		string sourceFbxAbsolutePath,
		string sequenceName,
		string outputAbsolutePath,
		string? sourceActionName = null,
		string? templateFbxAbsolutePath = null )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );
		var templatePath = string.IsNullOrWhiteSpace( templateFbxAbsolutePath )
			? GetStockTemplateAnimationFbxAbsolutePath()
			: templateFbxAbsolutePath;
		var sourceActionArgument = string.IsNullOrWhiteSpace( sourceActionName )
			? string.Empty
			: $" --source-action \"{sourceActionName}\"";

		RunBlender(
			$"--mode copy-fbx-to-fbx-template --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --source-fbx \"{sourceFbxAbsolutePath}\" --template-fbx \"{templatePath}\" --sequence-name \"{sequenceName}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"{sourceActionArgument}",
			"Blender solved-FBX to template-FBX export" );

		if ( !File.Exists( outputAbsolutePath ) )
			throw new FileNotFoundException( $"Blender solved-FBX template export did not produce '{outputAbsolutePath}'" );

		return outputAbsolutePath;
	}

	public string ExportSourceActionToPreviewFbx(
		string sourceFbxAbsolutePath,
		string sourceActionName,
		string sequenceName,
		string outputAbsolutePath )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( outputAbsolutePath )! );
		var sourceActionArgument = string.IsNullOrWhiteSpace( sourceActionName )
			? string.Empty
			: $" --source-action \"{sourceActionName}\"";
		var sequenceArgument = string.IsNullOrWhiteSpace( sequenceName )
			? string.Empty
			: $" --sequence-name \"{sequenceName}\"";

		RunBlender(
			$"--mode copy-source-action-to-fbx --citizen-fbx \"{GetCitizenFbxAbsolutePath()}\" --source-fbx \"{sourceFbxAbsolutePath}\" --output \"{outputAbsolutePath}\" --bst-root \"{BlenderSourceToolsRoot}\"{sourceActionArgument}{sequenceArgument}",
			"Blender source-action preview export" );

		if ( !File.Exists( outputAbsolutePath ) )
			throw new FileNotFoundException( $"Blender source-action preview export did not produce '{outputAbsolutePath}'" );

		return outputAbsolutePath;
	}

	private static void WriteForensicSummary(
		string payloadJsonPath,
		string appliedPoseJsonPath,
		string reimportedPoseJsonPath,
		string summaryJsonPath,
		string summaryMarkdownPath )
	{
		var payload = LoadPoseDump( payloadJsonPath );
		var applied = LoadPoseDump( appliedPoseJsonPath );
		var reimported = LoadPoseDump( reimportedPoseJsonPath );
		var comparisons = ForensicFocusBones
			.Select( boneName => BuildBoneComparison( boneName, payload, applied, reimported ) )
			.Where( comparison => comparison is not null )
			.Cast<object>()
			.ToList();

		var summary = new
		{
			payload_json = payloadJsonPath,
			applied_pose_json = appliedPoseJsonPath,
			reimported_pose_json = reimportedPoseJsonPath,
			payload_armature_object = payload.ObjectTransform,
			applied_armature_object = applied.ObjectTransform,
			reimported_armature_object = reimported.ObjectTransform,
			focus_bones = comparisons
		};

		File.WriteAllText( summaryJsonPath, JsonSerializer.Serialize( summary, JsonOptions ) );

		var builder = new StringBuilder();
		builder.AppendLine( "# Blender Forensic Round-Trip" );
		builder.AppendLine();
		builder.AppendLine( $"- Payload: `{payloadJsonPath}`" );
		builder.AppendLine( $"- Applied pose dump: `{appliedPoseJsonPath}`" );
		builder.AppendLine( $"- Re-imported pose dump: `{reimportedPoseJsonPath}`" );
		builder.AppendLine();
		builder.AppendLine( "## Object Transform" );
		builder.AppendLine();
		builder.AppendLine( $"- Payload armature object: `{FormatObjectTransform( payload.ObjectTransform )}`" );
		builder.AppendLine( $"- Applied armature object: `{FormatObjectTransform( applied.ObjectTransform )}`" );
		builder.AppendLine( $"- Re-imported armature object: `{FormatObjectTransform( reimported.ObjectTransform )}`" );
		builder.AppendLine();
		builder.AppendLine( "## Focus Bones" );
		builder.AppendLine();

		foreach ( var boneName in ForensicFocusBones )
		{
			var comparison = BuildBoneComparison( boneName, payload, applied, reimported );
			if ( comparison is null )
				continue;

			builder.AppendLine( $"### {boneName}" );
			builder.AppendLine();
			builder.AppendLine( $"- Payload translation: `{FormatArray( comparison.PayloadTranslation )}`" );
			builder.AppendLine( $"- Applied translation: `{FormatArray( comparison.AppliedTranslation )}`" );
			builder.AppendLine( $"- Re-imported translation: `{FormatArray( comparison.ReimportedTranslation )}`" );
			builder.AppendLine( $"- Payload rotation: `{FormatArray( comparison.PayloadRotation )}`" );
			builder.AppendLine( $"- Applied rotation: `{FormatArray( comparison.AppliedRotation )}`" );
			builder.AppendLine( $"- Re-imported rotation: `{FormatArray( comparison.ReimportedRotation )}`" );
			builder.AppendLine( $"- Payload->Applied translation delta: `{comparison.PayloadToAppliedTranslationError:0.####}`" );
			builder.AppendLine( $"- Applied->Re-imported translation delta: `{comparison.AppliedToReimportedTranslationError:0.####}`" );
			builder.AppendLine( $"- Payload->Applied rotation delta: `{comparison.PayloadToAppliedRotationDegrees:0.####} deg`" );
			builder.AppendLine( $"- Applied->Re-imported rotation delta: `{comparison.AppliedToReimportedRotationDegrees:0.####} deg`" );
			builder.AppendLine();
		}

		File.WriteAllText( summaryMarkdownPath, builder.ToString() );
	}

	private static BlenderBoneComparison? BuildBoneComparison( string boneName, BlenderPoseDump payload, BlenderPoseDump applied, BlenderPoseDump reimported )
	{
		if ( !TryGetFrameZeroTransform( payload, boneName, out var payloadTransform ) ||
			!TryGetFrameZeroTransform( applied, boneName, out var appliedTransform ) ||
			!TryGetFrameZeroTransform( reimported, boneName, out var reimportedTransform ) )
		{
			return null;
		}

		return new BlenderBoneComparison
		{
			BoneName = boneName,
			PayloadTranslation = payloadTransform!.Translation,
			PayloadRotation = payloadTransform.Rotation,
			AppliedTranslation = appliedTransform!.Translation,
			AppliedRotation = appliedTransform.Rotation,
			ReimportedTranslation = reimportedTransform!.Translation,
			ReimportedRotation = reimportedTransform.Rotation,
			PayloadToAppliedTranslationError = Distance( payloadTransform.Translation, appliedTransform.Translation ),
			AppliedToReimportedTranslationError = Distance( appliedTransform.Translation, reimportedTransform.Translation ),
			PayloadToAppliedRotationDegrees = QuaternionAngleDegrees( payloadTransform.Rotation, appliedTransform.Rotation ),
			AppliedToReimportedRotationDegrees = QuaternionAngleDegrees( appliedTransform.Rotation, reimportedTransform.Rotation )
		};
	}

	private static bool TryGetFrameZeroTransform( BlenderPoseDump dump, string boneName, out BlenderPoseTransform? transform )
	{
		transform = null;
		if ( dump.Frames.Count == 0 )
			return false;

		var index = dump.Bones.FindIndex( bone => bone.Name.Equals( boneName, StringComparison.OrdinalIgnoreCase ) );
		if ( index < 0 || index >= dump.Frames[0].BoneTransforms.Count )
			return false;

		transform = dump.Frames[0].BoneTransforms[index];
		return true;
	}

	private static BlenderPoseDump LoadPoseDump( string jsonPath )
	{
		return JsonSerializer.Deserialize<BlenderPoseDump>( File.ReadAllText( jsonPath ) ) ?? new BlenderPoseDump();
	}

	private static float Distance( IReadOnlyList<float> first, IReadOnlyList<float> second )
	{
		if ( first.Count < 3 || second.Count < 3 )
			return float.NaN;

		var dx = first[0] - second[0];
		var dy = first[1] - second[1];
		var dz = first[2] - second[2];
		return MathF.Sqrt( dx * dx + dy * dy + dz * dz );
	}

	private static float QuaternionAngleDegrees( IReadOnlyList<float> first, IReadOnlyList<float> second )
	{
		if ( first.Count < 4 || second.Count < 4 )
			return float.NaN;

		var firstQuaternion = RetargetMath.Normalize( new System.Numerics.Quaternion( first[0], first[1], first[2], first[3] ) );
		var secondQuaternion = RetargetMath.Normalize( new System.Numerics.Quaternion( second[0], second[1], second[2], second[3] ) );
		var dot = Math.Clamp( MathF.Abs( System.Numerics.Quaternion.Dot( firstQuaternion, secondQuaternion ) ), 0f, 1f );
		return MathF.Acos( dot ) * 2f * (180f / MathF.PI);
	}

	private static string FormatArray( IReadOnlyList<float> values )
	{
		return string.Join( ", ", values.Select( value => value.ToString( "0.####" ) ) );
	}

	private static string FormatObjectTransform( BlenderObjectTransform transform )
	{
		return $"T=({FormatArray( transform.Translation )}) R=({FormatArray( transform.Rotation )}) S=({FormatArray( transform.Scale )})";
	}

	private static string ResolveActualOutputPath( string requestedOutputPath )
	{
		if ( File.Exists( requestedOutputPath ) )
			return requestedOutputPath;

		var outputDirectory = Path.GetDirectoryName( requestedOutputPath )!;
		var fileName = Path.GetFileName( requestedOutputPath );
		var candidatePaths = new[]
		{
			Path.Combine( outputDirectory, "anims", fileName ),
			Path.Combine( outputDirectory, "anims", "anims", fileName )
		};

		foreach ( var candidatePath in candidatePaths )
		{
			if ( !File.Exists( candidatePath ) )
				continue;

			var canonicalPath = requestedOutputPath;
			Directory.CreateDirectory( Path.GetDirectoryName( canonicalPath )! );
			if ( !candidatePath.Equals( canonicalPath, StringComparison.OrdinalIgnoreCase ) )
			{
				if ( File.Exists( canonicalPath ) )
					File.Delete( canonicalPath );

				File.Move( candidatePath, canonicalPath );
				var candidateDirectory = Path.GetDirectoryName( candidatePath );
				if ( !string.IsNullOrWhiteSpace( candidateDirectory ) &&
					Directory.Exists( candidateDirectory ) &&
					!Directory.EnumerateFileSystemEntries( candidateDirectory ).Any() )
				{
					Directory.Delete( candidateDirectory, recursive: true );
				}
			}

			return canonicalPath;
		}

		var recursiveMatch = Directory.Exists( outputDirectory )
			? Directory.GetFiles( outputDirectory, fileName, SearchOption.AllDirectories ).FirstOrDefault()
			: null;
		if ( !string.IsNullOrWhiteSpace( recursiveMatch ) && File.Exists( recursiveMatch ) )
		{
			var canonicalPath = requestedOutputPath;
			Directory.CreateDirectory( Path.GetDirectoryName( canonicalPath )! );
			if ( !recursiveMatch.Equals( canonicalPath, StringComparison.OrdinalIgnoreCase ) )
			{
				if ( File.Exists( canonicalPath ) )
					File.Delete( canonicalPath );

				File.Move( recursiveMatch, canonicalPath );
			}

			return canonicalPath;
		}

		return string.Empty;
	}

	private static BlenderTargetBindCache LoadBindCache( string jsonPath )
	{
		using var document = JsonDocument.Parse( File.ReadAllText( jsonPath ) );
		var root = document.RootElement;
		var cache = new BlenderTargetBindCache
		{
			JsonAbsolutePath = jsonPath,
			OutputUnitMeters = root.TryGetProperty( "output_unit_meters", out var unitMeters )
				? unitMeters.GetDouble()
				: 0.01
		};

		if ( !root.TryGetProperty( "bones", out var bones ) )
			throw new InvalidOperationException( $"Blender bind cache '{jsonPath}' is missing a bones array." );

		foreach ( var bone in bones.EnumerateArray() )
		{
			cache.Bones.Add( new NativeBoneInfo
			{
				Name = bone.GetProperty( "name" ).GetString() ?? string.Empty,
				ParentName = ResolveParentName( bone, bones ),
				Translation = ReadVector3( bone.GetProperty( "rest_translation" ) ),
				Rotation = ReadQuaternion( bone.GetProperty( "rest_rotation" ) )
			} );
		}

		return cache;
	}

	private static string ResolveParentName( JsonElement bone, JsonElement allBones )
	{
		var parentId = bone.GetProperty( "parent_id" ).GetInt32();
		if ( parentId < 0 )
			return string.Empty;

		foreach ( var candidate in allBones.EnumerateArray() )
		{
			if ( candidate.GetProperty( "id" ).GetInt32() == parentId )
				return candidate.GetProperty( "name" ).GetString() ?? string.Empty;
		}

		return string.Empty;
	}

	private static float[] ReadVector3( JsonElement element )
	{
		return new[]
		{
			element[0].GetSingle(),
			element[1].GetSingle(),
			element[2].GetSingle()
		};
	}

	private static float[] ReadQuaternion( JsonElement element )
	{
		return new[]
		{
			element[0].GetSingle(),
			element[1].GetSingle(),
			element[2].GetSingle(),
			element[3].GetSingle()
		};
	}

	private static string GetCitizenFbxAbsolutePath()
	{
		return Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.CitizenAnimationBindAssetPath );
	}

	private static string GetStockTemplateDmxAbsolutePath()
	{
		return Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.ReferenceStockDmxPath );
	}

	private static string GetStockTemplateAnimationFbxAbsolutePath()
	{
		return Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.CitizenTemplateAnimationFbxPath );
	}

	private static void RunBlender( string scriptArguments, string stepName )
	{
		var blenderPath = RetargetSetupResolver.Resolve( RetargetToolSettings.Load() ).BlenderExecutablePath;
		if ( string.IsNullOrWhiteSpace( blenderPath ) )
			throw new FileNotFoundException( "Could not locate blender.exe. Set Blender Path in Diagnostics, set CITIZEN_RETARGET_BLENDER, or install Blender 4.4." );

		var scriptPath = CitizenRetargetPaths.GetPluginAbsolutePath( "tools/blender_export_citizen_dmx.py" );
		var arguments = $"--background --factory-startup --python \"{scriptPath}\" -- {scriptArguments}";
		RunProcess( blenderPath, arguments, stepName );
	}

	private static void RunProcess( string fileName, string arguments, string stepName )
	{
		using var process = new Process();
		process.StartInfo = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			WorkingDirectory = CitizenRetargetPaths.ProjectRoot,
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		process.Start();
		var standardOutput = process.StandardOutput.ReadToEnd();
		var standardError = process.StandardError.ReadToEnd();
		process.WaitForExit();

		var combinedOutput = $"{standardOutput}{Environment.NewLine}{standardError}";
		if ( process.ExitCode == 0 && !LooksLikePythonFailure( combinedOutput ) )
			return;

		var builder = new StringBuilder();
		builder.AppendLine( $"{stepName} failed with exit code {process.ExitCode}." );
		if ( !string.IsNullOrWhiteSpace( standardOutput ) )
		{
			builder.AppendLine( "stdout:" );
			builder.AppendLine( standardOutput.Trim() );
		}

		if ( !string.IsNullOrWhiteSpace( standardError ) )
		{
			builder.AppendLine( "stderr:" );
			builder.AppendLine( standardError.Trim() );
		}

		throw new InvalidOperationException( builder.ToString().TrimEnd() );
	}

	private static bool LooksLikePythonFailure( string output )
	{
		return output.Contains( "Traceback (most recent call last)", StringComparison.OrdinalIgnoreCase )
			|| output.Contains( "Error: Python:", StringComparison.OrdinalIgnoreCase )
			|| output.Contains( "ImportError:", StringComparison.OrdinalIgnoreCase )
			|| output.Contains( "RuntimeError:", StringComparison.OrdinalIgnoreCase );
	}

	private sealed class BlenderPoseDump
	{
		public BlenderObjectTransform ObjectTransform { get; set; } = new();
		public List<BlenderPoseBone> Bones { get; set; } = new();
		public List<BlenderPoseFrame> Frames { get; set; } = new();
	}

	private sealed class BlenderObjectTransform
	{
		public float[] Translation { get; set; } = new[] { 0f, 0f, 0f };
		public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
		public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
	}

	private sealed class BlenderPoseBone
	{
		public string Name { get; set; } = string.Empty;
	}

	private sealed class BlenderPoseFrame
	{
		public int Index { get; set; }
		public List<BlenderPoseTransform> BoneTransforms { get; set; } = new();
	}

	private sealed class BlenderPoseTransform
	{
		public float[] Translation { get; set; } = new[] { 0f, 0f, 0f };
		public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
	}

	private sealed class BlenderBoneComparison
	{
		public string BoneName { get; set; } = string.Empty;
		public IReadOnlyList<float> PayloadTranslation { get; set; } = Array.Empty<float>();
		public IReadOnlyList<float> PayloadRotation { get; set; } = Array.Empty<float>();
		public IReadOnlyList<float> AppliedTranslation { get; set; } = Array.Empty<float>();
		public IReadOnlyList<float> AppliedRotation { get; set; } = Array.Empty<float>();
		public IReadOnlyList<float> ReimportedTranslation { get; set; } = Array.Empty<float>();
		public IReadOnlyList<float> ReimportedRotation { get; set; } = Array.Empty<float>();
		public float PayloadToAppliedTranslationError { get; set; }
		public float AppliedToReimportedTranslationError { get; set; }
		public float PayloadToAppliedRotationDegrees { get; set; }
		public float AppliedToReimportedRotationDegrees { get; set; }
	}
}
