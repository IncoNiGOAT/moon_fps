using System.Text.Json;

namespace Editor.CitizenRetarget;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

internal static class CitizenTargetProfile
{
	public static bool EnableGeneratedDebugAssets => false;
	public const string CitizenAnimationBindAssetPath = "models/citizen/citizen.fbx";
	public const string CitizenFallbackBindAssetPath = "models/citizen/citizen_ref.fbx";
	public const string CitizenBaseModelPath = "models/citizen/citizen.vmdl";
	public const string CitizenTemplateAnimationFbxPath = "models/citizen/animations/Citizen@Airborne_C.fbx";
	public const string CitizenAnimationDmxModelName = "models/citizen/citizen_noscale.vmdl";
	public const string ReferenceStockDmxPath = "models/citizen/animations/Citizen@Idle_Fidget_Large_01.dmx";
	public const string DefaultTargetVmdlPath = "models/citizen_custom/citizen_retarget.vmdl";
	public const string DefaultCitizenAnimGraphPath = "models/citizen/citizen.vanmgrph";
	public const string DefaultOutputAnimationFolder = "models/citizen_custom/animations/citizen_retarget";
	public const string DefaultSequencePrefix = "citizen_retarget_";
	public const string DefaultJobAssetPath = "Assets/tools/citizen_retarget/default.crtjob";
	public const string DefaultSourceProfileAssetPath = "tools/citizen_retarget/profiles/quaternius_ual2.crtsrc";
	public const string MixamoSourceProfileAssetPath = "tools/citizen_retarget/profiles/mixamo_humanoid.crtsrc";
	public const string GenericBackendSourceProfileId = "generic_humanoid";
	public const string DefaultMappingProfileAssetPath = "tools/citizen_retarget/profiles/ual2_to_citizen.crtmap";
	public const string DefaultTargetPosePresetId = "citizen_t_pose_calibrated_v1";
	public const string DefaultBackendRecipeRelativePath = @"tools\blender\config\recipes\retarget_rokoko_citizen.json";
	public const string DiagnosticsAnimationFolder = "models/citizen_custom/debug/diagnostics";
	public const string DiagnosticsVmdlPath = "models/citizen_custom/debug/citizen_diagnostics.vmdl";
	public const string ReferenceAnimationFolder = "models/citizen_custom/debug/reference";
	public const string ReferenceDmxVmdlPath = "models/citizen_custom/debug/citizen_reference_dmx.vmdl";
	public const string CompensatedReferenceAnimationFolder = "models/citizen_custom/debug/compensated_reference";
	public const string CompensatedReferenceVmdlPath = "models/citizen_custom/debug/citizen_compensated_reference.vmdl";
	public const string CompensatedReferenceSequenceName = "ref_citizen_compensated_pose";
	public const string BindPassthroughAnimationFolder = "models/citizen_custom/debug/bind_passthrough";
	public const string BindPassthroughVmdlPath = "models/citizen_custom/debug/citizen_bind_passthrough.vmdl";
	public const string StockRoundTripAnimationFolder = "models/citizen_custom/debug/stock_roundtrip";
	public const string StockRoundTripVmdlPath = "models/citizen_custom/debug/citizen_stock_roundtrip.vmdl";
	public const string DmxAuditJsonRelativePath = ".tmp/citizen_retarget/dmx/reference_summary.json";
	public const string DmxAuditMarkdownRelativePath = "docs/citizen_dmx_output_spike.md";
	public const string BlenderBindCacheRelativePath = ".tmp/citizen_retarget/blender/reference/citizen_bind_cache.json";
	public const string BlenderReferencePayloadRelativeFolder = ".tmp/citizen_retarget/blender/reference";
	public const string BlenderGeneratedPayloadRelativeFolder = ".tmp/citizen_retarget/blender/generated";
	public const string BlenderBindPassthroughPayloadRelativeFolder = ".tmp/citizen_retarget/blender/bind_passthrough";
	public const string AuditJsonRelativePath = ".tmp/citizen_retarget/math_audit/audit_report.json";
	public const string AuditMarkdownRelativePath = "docs/citizen_retarget_math_audit.md";
	public const string SolveDiagnosticsRelativeFolder = ".tmp/citizen_retarget/solve";
	public const string SolveSummaryMarkdownRelativePath = "docs/citizen_retarget_solve_summary.md";

	public static readonly IReadOnlyDictionary<string, string> IkTargets = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
	{
		["hand_L_IK_target"] = "hand_L",
		["hand_R_IK_target"] = "hand_R",
		["foot_L_IK_target"] = "ankle_L",
		["foot_R_IK_target"] = "ankle_R",
	};

	public static readonly IReadOnlyList<string> CoreBodyTargets = new[]
	{
		"pelvis",
		"spine_0",
		"spine_1",
		"spine_2",
		"neck_0",
		"head",
		"clavicle_L",
		"arm_upper_L",
		"arm_lower_L",
		"hand_L",
		"clavicle_R",
		"arm_upper_R",
		"arm_lower_R",
		"hand_R",
		"leg_upper_L",
		"leg_lower_L",
		"ankle_L",
		"ball_L",
		"leg_upper_R",
		"leg_lower_R",
		"ankle_R",
		"ball_R"
	};

	public static readonly IReadOnlyList<string> CoreAuditTargets = new[]
	{
		"pelvis",
		"spine_0",
		"spine_1",
		"spine_2",
		"neck_0",
		"head",
		"clavicle_L",
		"arm_upper_L",
		"arm_lower_L",
		"hand_L",
		"clavicle_R",
		"arm_upper_R",
		"arm_lower_R",
		"hand_R",
		"leg_upper_L",
		"leg_lower_L",
		"ankle_L",
		"ball_L",
		"leg_upper_R",
		"leg_lower_R",
		"ankle_R",
		"ball_R",
		"root_IK",
		"hand_L_IK_target",
		"hand_R_IK_target",
		"foot_L_IK_target",
		"foot_R_IK_target"
	};
}

internal sealed class Ual2SourceProfile
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public string MappingJsonAbsolutePath => Path.Combine( CitizenRetargetPaths.DataRoot, "ual2_to_citizen_mapping.json" );
	public string CompensationJsonAbsolutePath => Path.Combine( CitizenRetargetPaths.DataRoot, "citizen_arm_rest_compensation.json" );

	public List<BoneMapEntry> LoadMapping( bool importHands, RetargetSolveStage solveStage )
	{
		var json = File.ReadAllText( MappingJsonAbsolutePath );
		using var document = JsonDocument.Parse( json );
		var mappingObject = document.RootElement.GetProperty( "mapping" );
		var results = new List<BoneMapEntry>();

		foreach ( var property in mappingObject.EnumerateObject() )
		{
			var targetBone = property.Value.GetString();
			if ( !ShouldIncludeMapping( property.Name, targetBone, importHands, solveStage ) )
				continue;

			results.Add( new BoneMapEntry
			{
				SourceBone = property.Name,
				TargetBone = targetBone
			} );
		}

		return results;
	}

	public Dictionary<string, NQuaternion> LoadLocalCompensation()
	{
		var json = File.ReadAllText( CompensationJsonAbsolutePath );
		using var document = JsonDocument.Parse( json );
		var results = new Dictionary<string, NQuaternion>( StringComparer.OrdinalIgnoreCase );
		var offsetsElement = document.RootElement.GetProperty( "offsets_deg" );

		foreach ( var side in offsetsElement.EnumerateObject() )
		{
			foreach ( var property in side.Value.EnumerateObject() )
			{
				var split = property.Name.Split( '.', 2 );
				if ( split.Length != 2 )
					continue;

				var boneName = split[0];
				var axis = split[1];
				var amountDegrees = property.Value.GetSingle();
				var amountRadians = amountDegrees * MathF.PI / 180f;
				var axisVector = axis switch
				{
					"X" => NVector3.UnitX,
					"Y" => NVector3.UnitY,
					"Z" => NVector3.UnitZ,
					_ => NVector3.Zero
				};

				if ( axisVector == NVector3.Zero )
					continue;

				var rotation = RetargetMath.Normalize( NQuaternion.CreateFromAxisAngle( axisVector, amountRadians ) );
				if ( results.TryGetValue( boneName, out var existing ) )
				{
					results[boneName] = RetargetMath.Normalize( existing * rotation );
				}
				else
				{
					results[boneName] = rotation;
				}
			}
		}

		return results;
	}

	private static bool ShouldIncludeMapping( string sourceBone, string targetBone, bool importHands, RetargetSolveStage solveStage )
	{
		var source = sourceBone ?? string.Empty;
		var target = targetBone ?? string.Empty;

		if ( solveStage == RetargetSolveStage.BodyOnly || solveStage == RetargetSolveStage.BodyAndRootMotion || solveStage == RetargetSolveStage.BodyRootAndIk )
		{
			if ( target.Contains( "finger_", StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		if ( !importHands && IsHandOrFingerMapping( source, target ) )
			return false;

		return true;
	}

	private static bool IsHandOrFingerMapping( string sourceBone, string targetBone )
	{
		return sourceBone.Contains( "hand_", StringComparison.OrdinalIgnoreCase )
			|| sourceBone.Contains( "thumb_", StringComparison.OrdinalIgnoreCase )
			|| sourceBone.Contains( "index_", StringComparison.OrdinalIgnoreCase )
			|| sourceBone.Contains( "middle_", StringComparison.OrdinalIgnoreCase )
			|| sourceBone.Contains( "ring_", StringComparison.OrdinalIgnoreCase )
			|| sourceBone.Contains( "pinky_", StringComparison.OrdinalIgnoreCase )
			|| targetBone.Contains( "hand_", StringComparison.OrdinalIgnoreCase )
			|| targetBone.Contains( "finger_", StringComparison.OrdinalIgnoreCase );
	}

	private static bool IsFingerMapping( string sourceBone, string targetBone )
	{
		var source = sourceBone ?? string.Empty;
		var target = targetBone ?? string.Empty;
		return source.Contains( "thumb_", StringComparison.OrdinalIgnoreCase )
			|| source.Contains( "index_", StringComparison.OrdinalIgnoreCase )
			|| source.Contains( "middle_", StringComparison.OrdinalIgnoreCase )
			|| source.Contains( "ring_", StringComparison.OrdinalIgnoreCase )
			|| source.Contains( "pinky_", StringComparison.OrdinalIgnoreCase )
			|| target.Contains( "finger_", StringComparison.OrdinalIgnoreCase );
	}
}
