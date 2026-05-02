using System.Text;
using System.Text.Json;

#nullable enable

namespace Editor.CitizenRetarget;

using NVector3 = System.Numerics.Vector3;

[Obsolete( "Deprecated pre-release audit service retained for historical troubleshooting only.", false )]
internal sealed class RetargetAuditService
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	private static readonly Dictionary<string, (float MinCm, float MaxCm)> ExpectedTargetBoneLengths = new( StringComparer.OrdinalIgnoreCase )
	{
		["spine_0"] = (4f, 24f),
		["spine_1"] = (4f, 24f),
		["spine_2"] = (4f, 24f),
		["neck_0"] = (2f, 18f),
		["clavicle_L"] = (3f, 20f),
		["clavicle_R"] = (3f, 20f),
		["arm_upper_L"] = (12f, 48f),
		["arm_upper_R"] = (12f, 48f),
		["arm_lower_L"] = (12f, 48f),
		["arm_lower_R"] = (12f, 48f),
		["hand_L"] = (5f, 28f),
		["hand_R"] = (5f, 28f),
		["leg_upper_L"] = (18f, 65f),
		["leg_upper_R"] = (18f, 65f),
		["leg_lower_L"] = (18f, 65f),
		["leg_lower_R"] = (18f, 65f),
		["ankle_L"] = (3f, 20f),
		["ankle_R"] = (3f, 20f),
		["ball_L"] = (2f, 18f),
		["ball_R"] = (2f, 18f),
	};

	private readonly Ual2SourceAdapter _sourceAdapter;
	private readonly Ual2SourceProfile _sourceProfile;

	public RetargetAuditService( Ual2SourceAdapter sourceAdapter, Ual2SourceProfile sourceProfile )
	{
		_sourceAdapter = sourceAdapter;
		_sourceProfile = sourceProfile;
	}

	public RetargetAuditReport RunAudit( RetargetAuditConfig config )
	{
		ArgumentNullException.ThrowIfNull( config );

		var report = new RetargetAuditReport
		{
			SourcePath = config.SourceFbxAbsolutePath,
			AuditJsonPath = CitizenRetargetPaths.GetProjectAbsolutePath( config.AuditJsonPath ),
			AuditMarkdownPath = CitizenRetargetPaths.GetProjectAbsolutePath( config.AuditMarkdownPath ),
			DiagnosticsVmdlAbsolutePath = CitizenTargetProfile.EnableGeneratedDebugAssets
				? CitizenRetargetPaths.GetAssetAbsolutePath( config.DiagnosticsVmdlPath )
				: string.Empty
		};

		Directory.CreateDirectory( Path.GetDirectoryName( report.AuditJsonPath )! );
		Directory.CreateDirectory( Path.GetDirectoryName( report.AuditMarkdownPath )! );

		var sourceCandidates = new List<(TargetBindProfile Profile, RetargetAuditSceneReport Report, NativeSceneAuditResult Native)>();
		foreach ( var profile in config.Profiles )
		{
			var sourceAudit = _sourceAdapter.InspectScene( config.SourceFbxAbsolutePath, profile );
			var sceneReport = CreateSourceSceneReport( config.SourceFbxAbsolutePath, profile, sourceAudit );
			report.SceneReports.Add( sceneReport );
			sourceCandidates.Add( (profile, sceneReport, sourceAudit) );
		}

		var targetCandidates = new[]
		{
			config.TargetBindAssetPath,
			config.FallbackTargetBindAssetPath
		}
		.Where( path => !string.IsNullOrWhiteSpace( path ) )
		.Distinct( StringComparer.OrdinalIgnoreCase )
		.ToList();
		var allTargetCandidates = new List<(string AssetPath, string AbsolutePath, RetargetAuditSceneReport Report, NativeSceneAuditResult Native)>();

		foreach ( var assetPath in targetCandidates )
		{
			var absolutePath = Ual2SourceAdapter.ResolveAssetAbsolutePath( assetPath );
			foreach ( var profile in config.Profiles )
			{
				var nativeAudit = _sourceAdapter.InspectScene( absolutePath, profile );
				var sceneReport = CreateTargetSceneReport( assetPath, absolutePath, profile, nativeAudit );
				report.SceneReports.Add( sceneReport );
				allTargetCandidates.Add( (assetPath, absolutePath, sceneReport, nativeAudit) );
			}
		}

		var bestTargetCandidate = allTargetCandidates
			.Where( item => item.Report.IsBelievable )
			.OrderByDescending( item => item.Report.PlausibilityScore )
			.ThenByDescending( item => GetTargetAssetPreference( item.AssetPath ) )
			.FirstOrDefault();

		NativeSceneAuditResult? lockedNativeScene = null;
		string? lockedAssetPath = null;
		if ( bestTargetCandidate.Report is not null )
		{
			report.LockedTargetBindAssetPath = bestTargetCandidate.AssetPath;
			report.LockedTargetBindAbsolutePath = bestTargetCandidate.AbsolutePath;
			report.LockedTargetProfile = bestTargetCandidate.Report.Profile;
			report.LockedTargetOutputUnitMeters = bestTargetCandidate.Native.OutputUnitMeters;
			var centimetersPerUnit = RetargetMath.CentimetersPerUnit( bestTargetCandidate.Native.OutputUnitMeters );
			report.LockedTargetBones = bestTargetCandidate.Native.Bones.Select( bone => ConvertBone( bone, centimetersPerUnit ) ).ToList();
			report.HasLockedTargetProfile = true;
			report.FallbackTargetWasUsed = !bestTargetCandidate.AssetPath.Equals( config.TargetBindAssetPath, StringComparison.OrdinalIgnoreCase );
			lockedNativeScene = bestTargetCandidate.Native;
			lockedAssetPath = bestTargetCandidate.AssetPath;
		}
		else
		{
			foreach ( var assetPath in targetCandidates )
				report.Warnings.Add( $"No believable target bind profile found for '{assetPath}'." );
		}

		if ( !report.HasLockedTargetProfile )
		{
			var provisional = allTargetCandidates
				.OrderByDescending( item => item.Report.PlausibilityScore )
				.ThenByDescending( item => GetTargetAssetPreference( item.AssetPath ) )
				.FirstOrDefault();
			if ( provisional.Report is null )
				throw new InvalidOperationException( "Target bind audit did not produce any candidate reports." );

			report.LockedTargetBindAssetPath = provisional.AssetPath;
			report.LockedTargetBindAbsolutePath = provisional.AbsolutePath;
			report.LockedTargetProfile = provisional.Report.Profile;
			report.LockedTargetOutputUnitMeters = provisional.Native.OutputUnitMeters;
			var centimetersPerUnit = RetargetMath.CentimetersPerUnit( provisional.Native.OutputUnitMeters );
			report.LockedTargetBones = provisional.Native.Bones.Select( bone => ConvertBone( bone, centimetersPerUnit ) ).ToList();
			report.HasLockedTargetProfile = true;
			report.FallbackTargetWasUsed = !provisional.AssetPath.Equals( config.TargetBindAssetPath, StringComparison.OrdinalIgnoreCase );
			lockedNativeScene = provisional.Native;
			lockedAssetPath = provisional.AssetPath;
			report.Warnings.Add( "No target bind profile passed the plausibility threshold. Continuing with the best provisional profile so the SMD diagnostics can decide whether the bind contract is still usable." );
		}

		var sourceMappings = _sourceProfile.LoadMapping( importHands: true, solveStage: RetargetSolveStage.BodyOnly );
		var targetLengths = report.LockedTargetBones
			.Where( bone => !string.IsNullOrWhiteSpace( bone.Name ) )
			.ToDictionary(
				bone => bone.Name,
				bone => RetargetMath.LengthOrZero( bone.LocalTransform.Translation ),
				StringComparer.OrdinalIgnoreCase );

		foreach ( var candidate in sourceCandidates )
		{
			var score = ScoreSourceSkeleton(
				candidate.Report.CoreBones,
				targetLengths,
				sourceMappings,
				candidate.Report.ApproximateHeightCm,
				candidate.Profile );
			candidate.Report.PlausibilityScore = score;
			candidate.Report.IsBelievable = score >= 0.7f;
		}

		var bestSourceCandidate = sourceCandidates
			.Where( item => item.Report.IsBelievable )
			.OrderByDescending( item => item.Report.PlausibilityScore )
			.ThenByDescending( item => GetSourceProfilePreference( item.Profile ) )
			.FirstOrDefault();

		if ( bestSourceCandidate.Report is not null )
		{
			report.LockedSourceProfile = bestSourceCandidate.Profile;
			report.LockedSourceOutputUnitMeters = bestSourceCandidate.Native.OutputUnitMeters;
			var centimetersPerUnit = RetargetMath.CentimetersPerUnit( bestSourceCandidate.Native.OutputUnitMeters );
			report.LockedSourceBones = bestSourceCandidate.Native.Bones.Select( bone => ConvertBone( bone, centimetersPerUnit ) ).ToList();
			report.HasLockedSourceProfile = true;
		}
		else
		{
			var provisionalSource = sourceCandidates
				.OrderByDescending( item => item.Report.PlausibilityScore )
				.ThenByDescending( item => GetSourceProfilePreference( item.Profile ) )
				.FirstOrDefault();

			if ( provisionalSource.Report is not null )
			{
				report.LockedSourceProfile = provisionalSource.Profile;
				report.LockedSourceOutputUnitMeters = provisionalSource.Native.OutputUnitMeters;
				var centimetersPerUnit = RetargetMath.CentimetersPerUnit( provisionalSource.Native.OutputUnitMeters );
				report.LockedSourceBones = provisionalSource.Native.Bones.Select( bone => ConvertBone( bone, centimetersPerUnit ) ).ToList();
				report.HasLockedSourceProfile = true;
				report.Warnings.Add( "No source ingest profile passed the plausibility threshold. Continuing with the best provisional source profile." );
			}
		}

		if ( lockedNativeScene is not null && CitizenTargetProfile.EnableGeneratedDebugAssets )
		{
			var diagnosticGenerator = new RetargetDiagnosticGenerator();
			var diagnostics = diagnosticGenerator.GenerateDiagnostics(
				report.LockedTargetBones,
				config.DiagnosticsAssetFolder,
				config.DiagnosticsVmdlPath );
			report.DiagnosticsVmdlAbsolutePath = diagnostics.DiagnosticsVmdlAbsolutePath;
		}

		if ( report.FallbackTargetWasUsed )
			report.Warnings.Add( $"Fell back from '{config.TargetBindAssetPath}' to '{lockedAssetPath}' for the target bind contract." );

		WriteAuditOutputs( report );
		return report;
	}

	private RetargetAuditSceneReport CreateSourceSceneReport( string absolutePath, TargetBindProfile profile, NativeSceneAuditResult nativeAudit )
	{
		var mappings = _sourceProfile.LoadMapping( importHands: true, solveStage: RetargetSolveStage.Full );
		var allowedSourceBones = new HashSet<string>(
			mappings
				.Where( entry => CitizenTargetProfile.CoreAuditTargets.Contains( entry.TargetBone, StringComparer.OrdinalIgnoreCase ) )
				.Select( entry => entry.SourceBone ),
			StringComparer.OrdinalIgnoreCase );

		var centimetersPerUnit = RetargetMath.CentimetersPerUnit( nativeAudit.OutputUnitMeters );
		var coreBones = nativeAudit.Bones
			.Where( bone => allowedSourceBones.Contains( bone.Name ) )
			.Select( bone => CreateBoneReport( bone, centimetersPerUnit ) )
			.OrderBy( bone => bone.Name, StringComparer.OrdinalIgnoreCase )
			.ToList();

		return new RetargetAuditSceneReport
		{
			Kind = "Source",
			AssetPath = absolutePath,
			AbsolutePath = absolutePath,
			Profile = profile,
			SceneUnitMeters = nativeAudit.UnitMeters,
			OutputUnitMeters = nativeAudit.OutputUnitMeters,
			Creator = nativeAudit.Metadata.Creator,
			OriginalApplication = FormatApplication( nativeAudit.Metadata.OriginalApplication ),
			LatestApplication = FormatApplication( nativeAudit.Metadata.LatestApplication ),
			ApproximateHeightCm = EstimateHeightCm( nativeAudit.Bones, centimetersPerUnit ),
			PlausibilityScore = 0f,
			IsBelievable = false,
			CoreBones = coreBones
		};
	}

	private static RetargetAuditSceneReport CreateTargetSceneReport( string assetPath, string absolutePath, TargetBindProfile profile, NativeSceneAuditResult nativeAudit )
	{
		var centimetersPerUnit = RetargetMath.CentimetersPerUnit( nativeAudit.OutputUnitMeters );
		var coreBones = nativeAudit.Bones
			.Where( bone => CitizenTargetProfile.CoreAuditTargets.Contains( bone.Name, StringComparer.OrdinalIgnoreCase ) )
			.Select( bone => CreateBoneReport( bone, centimetersPerUnit ) )
			.OrderBy( bone => bone.Name, StringComparer.OrdinalIgnoreCase )
			.ToList();

		var heightCm = EstimateHeightCm( nativeAudit.Bones, centimetersPerUnit );
		var score = ScoreTargetSkeleton( coreBones, heightCm );

		return new RetargetAuditSceneReport
		{
			Kind = "Target",
			AssetPath = assetPath,
			AbsolutePath = absolutePath,
			Profile = profile,
			SceneUnitMeters = nativeAudit.UnitMeters,
			OutputUnitMeters = nativeAudit.OutputUnitMeters,
			Creator = nativeAudit.Metadata.Creator,
			OriginalApplication = FormatApplication( nativeAudit.Metadata.OriginalApplication ),
			LatestApplication = FormatApplication( nativeAudit.Metadata.LatestApplication ),
			ApproximateHeightCm = heightCm,
			PlausibilityScore = score,
			IsBelievable = score >= 0.58f,
			CoreBones = coreBones
		};
	}

	private static RetargetAuditBoneReport CreateBoneReport( NativeAuditBoneInfo bone, float centimetersPerUnit )
	{
		var local = RetargetMath.ScaleTranslation( bone.LocalTransform, centimetersPerUnit );
		var world = RetargetMath.ScaleTranslation( bone.WorldTransform, centimetersPerUnit );
		return new RetargetAuditBoneReport
		{
			Name = bone.Name,
			ParentName = bone.ParentName,
			LocalLengthCm = RetargetMath.LengthOrZero( local.Translation ),
			LocalTranslation = [local.Translation.X, local.Translation.Y, local.Translation.Z],
			LocalRotation = [local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W],
			LocalScale = bone.Scale.ToArray(),
			WorldTranslation = [world.Translation.X, world.Translation.Y, world.Translation.Z],
			WorldRotation = [world.Rotation.X, world.Rotation.Y, world.Rotation.Z, world.Rotation.W]
		};
	}

	private static float ScoreTargetSkeleton( IReadOnlyList<RetargetAuditBoneReport> coreBones, float approximateHeightCm )
	{
		var score = 0f;
		var count = 0;
		var suspiciousNearOne = 0;
		var suspiciousNearHundred = 0;

		foreach ( var bone in coreBones )
		{
			if ( !ExpectedTargetBoneLengths.TryGetValue( bone.Name, out var expected ) )
				continue;

			var length = bone.LocalLengthCm;
			var center = (expected.MinCm + expected.MaxCm) * 0.5f;
			var radius = MathF.Max( (expected.MaxCm - expected.MinCm) * 0.5f, 1f );
			var closeness = MathF.Max( 0f, 1f - MathF.Abs( length - center ) / radius );
			score += closeness;
			count++;

			if ( length >= 0.8f && length <= 1.25f )
				suspiciousNearOne++;
			if ( length >= 80f && length <= 120f )
				suspiciousNearHundred++;
		}

		if ( count == 0 )
			return 0f;

		var normalizedScore = score / count;
		var heightCenter = 175f;
		var heightRadius = 85f;
		var heightScore = MathF.Max( 0f, 1f - MathF.Abs( approximateHeightCm - heightCenter ) / heightRadius );
		normalizedScore = normalizedScore * 0.75f + heightScore * 0.25f;

		if ( suspiciousNearOne >= 8 )
			normalizedScore *= 0.2f;
		if ( suspiciousNearHundred >= 8 )
			normalizedScore *= 0.45f;

		return normalizedScore;
	}

	private static float ScoreSourceSkeleton(
		IReadOnlyList<RetargetAuditBoneReport> sourceBones,
		IReadOnlyDictionary<string, float> targetLengths,
		IReadOnlyList<BoneMapEntry> mappings,
		float approximateHeightCm,
		TargetBindProfile profile )
	{
		var sourceByName = sourceBones.ToDictionary( bone => bone.Name, bone => bone, StringComparer.OrdinalIgnoreCase );
		var mappedCount = 0;
		var plausibleCount = 0;
		var suspiciousNearOne = 0;
		var ratioLogs = new List<float>();

		foreach ( var mapping in mappings )
		{
			if ( !sourceByName.TryGetValue( mapping.SourceBone, out var sourceBone ) )
				continue;
			if ( !targetLengths.TryGetValue( mapping.TargetBone, out var targetLength ) )
				continue;

			var sourceLength = sourceBone.LocalLengthCm;
			if ( sourceLength <= 0.001f || targetLength <= 0.001f )
				continue;

			mappedCount++;
			if ( sourceLength >= 4f && sourceLength <= 120f )
				plausibleCount++;
			if ( sourceLength >= 0.8f && sourceLength <= 1.25f )
				suspiciousNearOne++;

			ratioLogs.Add( MathF.Log( targetLength / sourceLength ) );
		}

		if ( mappedCount == 0 )
			return 0f;

		var plausibleScore = (float)plausibleCount / mappedCount;
		var heightScore = MathF.Max( 0f, 1f - MathF.Abs( approximateHeightCm - 175f ) / 85f );
		var ratioConsistency = 0f;
		if ( ratioLogs.Count > 0 )
		{
			var mean = (float)ratioLogs.Average();
			var variance = (float)(ratioLogs.Sum( value => (value - mean) * (value - mean) ) / ratioLogs.Count);
			ratioConsistency = 1f / (1f + MathF.Sqrt( variance ));
		}

		var score = plausibleScore * 0.7f + ratioConsistency * 0.2f + heightScore * 0.1f;
		if ( suspiciousNearOne >= 5 )
			score *= 0.15f;

		score += GetSourceProfilePreference( profile ) * 0.001f;
		return score;
	}

	private static int GetSourceProfilePreference( TargetBindProfile profile )
	{
		return profile switch
		{
			TargetBindProfile.RawUnits => 4,
			TargetBindProfile.TargetOnlyControl => 3,
			TargetBindProfile.ModifyGeometry => 2,
			_ => 1
		};
	}

	private static int GetTargetAssetPreference( string assetPath )
	{
		if ( assetPath.Equals( CitizenTargetProfile.CitizenAnimationBindAssetPath, StringComparison.OrdinalIgnoreCase ) )
			return 2;
		if ( assetPath.Equals( CitizenTargetProfile.CitizenFallbackBindAssetPath, StringComparison.OrdinalIgnoreCase ) )
			return 1;

		return 0;
	}

	private static float EstimateHeightCm( IReadOnlyList<NativeAuditBoneInfo> bones, float centimetersPerUnit )
	{
		if ( bones.Count == 0 )
			return 0f;

		var positions = bones
			.Where( bone => CitizenTargetProfile.CoreAuditTargets.Contains( bone.Name, StringComparer.OrdinalIgnoreCase ) )
			.Select( bone => RetargetMath.ScaleTranslation( bone.WorldTransform, centimetersPerUnit ).Translation )
			.ToList();

		if ( positions.Count == 0 )
		{
			positions = bones
				.Select( bone => RetargetMath.ScaleTranslation( bone.WorldTransform, centimetersPerUnit ).Translation )
				.ToList();
		}

		var minZ = positions.Min( point => point.Z );
		var maxZ = positions.Max( point => point.Z );
		return maxZ - minZ;
	}

	private static NativeBoneInfo ConvertBone( NativeAuditBoneInfo bone, float centimetersPerUnit )
	{
		var local = RetargetMath.ScaleTranslation( bone.LocalTransform, centimetersPerUnit );
		return new NativeBoneInfo
		{
			Name = bone.Name,
			ParentName = bone.ParentName,
			Translation = [local.Translation.X, local.Translation.Y, local.Translation.Z],
			Rotation = [local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W]
		};
	}

	private static string FormatApplication( NativeApplicationInfo application )
	{
		var parts = new List<string>();
		if ( !string.IsNullOrWhiteSpace( application.Name ) )
			parts.Add( application.Name );
		if ( !string.IsNullOrWhiteSpace( application.Version ) )
			parts.Add( application.Version );
		if ( !string.IsNullOrWhiteSpace( application.Vendor ) )
			parts.Add( application.Vendor );
		return parts.Count > 0 ? string.Join( " / ", parts ) : string.Empty;
	}

	private static void WriteAuditOutputs( RetargetAuditReport report )
	{
		File.WriteAllText( report.AuditJsonPath, JsonSerializer.Serialize( report, JsonOptions ) );
		File.WriteAllText( report.AuditMarkdownPath, BuildMarkdownReport( report ) );
	}

	private static string BuildMarkdownReport( RetargetAuditReport report )
	{
		var builder = new StringBuilder();
		builder.AppendLine( "# CARL Math Audit" );
		builder.AppendLine();
		builder.AppendLine( "This report audits `FBX ingest -> bind/rest contract -> SMD diagnostics` before further solver tuning." );
		builder.AppendLine();
		builder.AppendLine( "Primary references:" );
		builder.AppendLine( "- [ufbx node transforms](https://ufbx.github.io/fbx/node-transforms/)" );
		builder.AppendLine( "- [ufbx nodes and transforms](https://ufbx.github.io/elements/nodes/)" );
		builder.AppendLine( "- [Valve SMD format](https://developer.valvesoftware.com/wiki/SMD)" );
		builder.AppendLine( "- [Blender Source Tools exporter](https://github.com/Artfunkel/BlenderSourceTools)" );
		builder.AppendLine( "- [Godot retargeting 3D skeletons](https://docs.godotengine.org/en/4.1/tutorials/assets_pipeline/retargeting_3d_skeletons.html)" );
		builder.AppendLine( "- [Wicked Engine animation retargeting](https://wickedengine.net/2022/09/animation-retargeting/)" );
		builder.AppendLine( "- [ozz-animation how-tos](https://guillaumeblanc.github.io/ozz-animation/documentation/howtos/)" );
		builder.AppendLine();
		builder.AppendLine( "Diagnostic clips compare raw local export, inverse-local export, root-only `Y-up` correction, and Valve legacy-style `Ry90 @ Rz90` corrections both as simple rotation tweaks and as full local-transform basis remaps before further solver changes." );
		builder.AppendLine();

		if ( report.HasLockedTargetProfile )
		{
			builder.AppendLine( $"Locked target bind: `{report.LockedTargetBindAssetPath}` with profile `{report.LockedTargetProfile}`." );
			builder.AppendLine( "Target bind preference favors `models/citizen/citizen.fbx` because stock Citizen `bindPose` sequences are authored against that source file, while `citizen_REF.fbx` is documented as a clothing/simple-rig reference." );
			builder.AppendLine();
		}
		else
		{
			builder.AppendLine( "No believable target bind profile was found." );
			builder.AppendLine();
		}

		if ( report.HasLockedSourceProfile )
		{
			builder.AppendLine( $"Locked source ingest profile: `{report.LockedSourceProfile}`." );
			builder.AppendLine();
		}
		else
		{
			builder.AppendLine( "No believable source ingest profile was found." );
			builder.AppendLine();
		}

		if ( report.Warnings.Count > 0 )
		{
			builder.AppendLine( "Warnings:" );
			foreach ( var warning in report.Warnings )
				builder.AppendLine( $"- {warning}" );
			builder.AppendLine();
		}

		foreach ( var group in report.SceneReports.GroupBy( item => item.Kind ) )
		{
			builder.AppendLine( $"## {group.Key}" );
			builder.AppendLine();

			foreach ( var scene in group.OrderBy( item => item.AssetPath, StringComparer.OrdinalIgnoreCase ).ThenBy( item => item.Profile ) )
			{
				builder.AppendLine( $"### `{scene.AssetPath}` / `{scene.Profile}`" );
				builder.AppendLine();
				builder.AppendLine( $"- Scene unit meters: `{scene.SceneUnitMeters:0.######}`" );
				builder.AppendLine( $"- Output unit meters: `{scene.OutputUnitMeters:0.######}`" );
				builder.AppendLine( $"- Approximate height (cm): `{scene.ApproximateHeightCm:0.##}`" );
				builder.AppendLine( $"- Plausibility score: `{scene.PlausibilityScore:0.###}`" );
				builder.AppendLine( $"- Believable: `{scene.IsBelievable}`" );
				if ( !string.IsNullOrWhiteSpace( scene.Creator ) )
					builder.AppendLine( $"- Creator: `{scene.Creator}`" );
				if ( !string.IsNullOrWhiteSpace( scene.OriginalApplication ) )
					builder.AppendLine( $"- Original application: `{scene.OriginalApplication}`" );
				if ( !string.IsNullOrWhiteSpace( scene.LatestApplication ) )
					builder.AppendLine( $"- Latest application: `{scene.LatestApplication}`" );
				builder.AppendLine();
				builder.AppendLine( "| Bone | Parent | Local Length (cm) | Local Translation | Local Scale |" );
				builder.AppendLine( "| --- | --- | ---: | --- | --- |" );
				foreach ( var bone in scene.CoreBones )
				{
					builder.AppendLine( $"| `{bone.Name}` | `{bone.ParentName}` | `{bone.LocalLengthCm:0.###}` | `{FormatVector( bone.LocalTranslation )}` | `{FormatVector( bone.LocalScale )}` |" );
				}
				builder.AppendLine();
			}
		}

		return builder.ToString();
	}

	private static string FormatVector( float[] values )
	{
		if ( values is null || values.Length < 3 )
			return "0, 0, 0";

		return $"{values[0]:0.###}, {values[1]:0.###}, {values[2]:0.###}";
	}
}
