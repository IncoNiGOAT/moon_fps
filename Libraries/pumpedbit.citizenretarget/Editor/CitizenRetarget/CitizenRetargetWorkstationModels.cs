#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Editor.CitizenRetarget;

internal sealed class RetargetSourceLibraryRef
{
	public string LibraryId { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string SourceFbxPath { get; set; } = string.Empty;
	public string SourceProfilePath { get; set; } = string.Empty;
}

internal sealed class RetargetResultRef
{
	public string RunId { get; set; } = string.Empty;
	public string SequenceName { get; set; } = string.Empty;
	public string ClipName { get; set; } = string.Empty;
	public string Status { get; set; } = string.Empty;
	public string TargetVmdlPath { get; set; } = string.Empty;
	public string ManifestPath { get; set; } = string.Empty;
	public string ImportedAssetPath { get; set; } = string.Empty;
}

internal sealed class RetargetCompareSelection
{
	public string SelectedSourceLibraryId { get; set; } = string.Empty;
	public string SelectedSourceClipName { get; set; } = string.Empty;
	public string SelectedResultRunId { get; set; } = string.Empty;
}

internal sealed class RetargetSessionState
{
	public RetargetCompareSelection CompareSelection { get; set; } = new();
	public string SelectedHistoryRunId { get; set; } = string.Empty;
	public string HomeResultFilter { get; set; } = "All";
}

internal sealed class RetargetTargetPresetRef
{
	public string Key { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string TargetVmdlPath { get; set; } = string.Empty;
	public string OutputAnimationFolder { get; set; } = string.Empty;
	public string SequencePrefix { get; set; } = string.Empty;
	public bool ExistsOnDisk { get; set; }

	public override string ToString() => DisplayName;
}

internal sealed class RetargetTargetAnimationEntry
{
	public string SequenceName { get; set; } = string.Empty;
	public bool Looping { get; set; }
	public bool IsImported { get; set; }
	public bool IsReadOnly { get; set; }
	public string VmdlResourcePath { get; set; } = string.Empty;
	public string SourceResourcePath { get; set; } = string.Empty;
}

internal static class RetargetSourceLibraryResolver
{
	public static RetargetSourceLibraryRef FromJob( CitizenRetargetJob job )
	{
		var normalizedPath = CitizenRetargetPaths.DecodeExternalPath( job.SourceFbxPath ).Trim().Trim( '"' );
		var displayName = DeriveDisplayName( normalizedPath );

		return new RetargetSourceLibraryRef
		{
			LibraryId = DeriveLibraryId( normalizedPath ),
			DisplayName = string.IsNullOrWhiteSpace( displayName ) ? "Source Library" : displayName,
			SourceFbxPath = job.SourceFbxPath ?? string.Empty,
			SourceProfilePath = job.SourceProfilePath ?? string.Empty
		};
	}

	public static string DeriveLibraryId( string sourceFbxPath )
	{
		var normalizedPath = CitizenRetargetPaths.DecodeExternalPath( sourceFbxPath ).Trim().Trim( '"' );
		if ( string.IsNullOrWhiteSpace( normalizedPath ) )
			return "source_library";

		var stem = DeriveDisplayName( normalizedPath );
		var safeStem = new string( stem
			.Select( character => char.IsLetterOrDigit( character ) ? char.ToLowerInvariant( character ) : '_' )
			.ToArray() )
			.Trim( '_' );
		if ( string.IsNullOrWhiteSpace( safeStem ) )
			safeStem = "source_library";

		var hashBytes = SHA1.HashData( Encoding.UTF8.GetBytes( normalizedPath.ToLowerInvariant() ) );
		var hash = Convert.ToHexString( hashBytes[..4] ).ToLowerInvariant();
		return $"{safeStem}_{hash}";
	}

	public static string DeriveDisplayName( string sourceFbxPath )
	{
		var normalizedPath = CitizenRetargetPaths.DecodeExternalPath( sourceFbxPath ).Trim().Trim( '"' );
		if ( string.IsNullOrWhiteSpace( normalizedPath ) )
			return "Source Library";

		var fileName = Path.GetFileNameWithoutExtension( normalizedPath );
		return string.IsNullOrWhiteSpace( fileName ) ? "Source Library" : fileName;
	}
}

internal sealed class RetargetSourceInspection
{
	public string SourcePath { get; set; } = string.Empty;
	public NativeSceneAuditResult Audit { get; set; } = new();
	public Dictionary<string, NativeAuditBoneInfo> BonesByName { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

internal sealed class RetargetSlotAssignmentState
{
	public string SlotId { get; set; } = string.Empty;
	public string TargetBone { get; set; } = string.Empty;
	public string Group { get; set; } = "body";
	public bool Required { get; set; }
	public bool Enabled { get; set; } = true;
	public bool Locked { get; set; }
	public string MirrorSlotId { get; set; } = string.Empty;
	public string AssignedSourceBone { get; set; } = string.Empty;
	public string AutoSourceBone { get; set; } = string.Empty;
	public string EffectiveSourceBone => string.IsNullOrWhiteSpace( AssignedSourceBone ) ? AutoSourceBone : AssignedSourceBone;
	public bool IsManualOverride { get; set; }
	public float Confidence { get; set; }
	public List<string> CandidateBones { get; set; } = new();
	public List<string> SourceAliases { get; set; } = new();
	public string Notes { get; set; } = string.Empty;
}

internal sealed class RetargetDiagnosticSummary
{
	public List<string> MissingRequiredSlots { get; set; } = new();
	public List<string> DuplicateSourceBones { get; set; } = new();
	public List<string> DuplicateTargetBones { get; set; } = new();
	public List<string> DisabledFingerSlots { get; set; } = new();
}

internal sealed class RetargetQueueItem
{
	public RetargetClipDescriptor Clip { get; set; } = new();
	public string Status { get; set; } = "pending";
	public string Message { get; set; } = string.Empty;
	public RetargetImportResult? Result { get; set; }
}

internal sealed class RetargetRunManifest
{
	public string RunId { get; set; } = string.Empty;
	public string Status { get; set; } = string.Empty;
	public string RecipeId { get; set; } = string.Empty;
	public string SourceProfileId { get; set; } = string.Empty;
	public string TargetProfileId { get; set; } = string.Empty;
	public string SourceFile { get; set; } = string.Empty;
	public string SelectedAction { get; set; } = string.Empty;
	public string RetargetedActionName { get; set; } = string.Empty;
	public string RetargetBackend { get; set; } = string.Empty;
	public string BackendVersion { get; set; } = string.Empty;
	public Dictionary<string, object> BackendSettings { get; set; } = new();
	public List<RetargetGeneratedBoneMapEntry> BackendGeneratedBoneMap { get; set; } = new();
	public RetargetBackendMappingOverrideSummary BackendMappingOverrideSummary { get; set; } = new();
	public List<string> BackendWarnings { get; set; } = new();
	public RetargetPoseNormalizationSummary PoseNormalization { get; set; } = new();
	public RetargetMotionTrajectoryAnalysis MotionTrajectoryAnalysis { get; set; } = new();
	public Dictionary<string, RetargetBoneMapEntry> BoneMap { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	public RetargetMappingCoverageSummary MappingCoverage { get; set; } = new();
	public RetargetBoneMapOverrideSummary BoneMapOverrideSummary { get; set; } = new();
	public List<string> UnmappedRequiredSlots { get; set; } = new();
	public List<string> Warnings { get; set; } = new();
	public List<RetargetStageManifestEntry> Stages { get; set; } = new();
	public RetargetOutputsSummary Outputs { get; set; } = new();
}

internal sealed class RetargetGeneratedBoneMapEntry
{
	public string SourceBone { get; set; } = string.Empty;
	public string TargetBone { get; set; } = string.Empty;
	public string BoneKey { get; set; } = string.Empty;
	public string CanonicalSlot { get; set; } = string.Empty;
	public bool IsCustom { get; set; }
	public bool Required { get; set; }
	public bool UserOverridden { get; set; }
	public string MappingOrigin { get; set; } = string.Empty;
}

internal sealed class RetargetBackendMappingOverrideSummary
{
	public bool Enabled { get; set; }
	public bool Applied { get; set; }
	public string Reason { get; set; } = string.Empty;
	public int ValidatedPairCount { get; set; }
	public List<RetargetValidatedBonePair> ValidatedPairs { get; set; } = new();
	public List<string> MissingSourceBones { get; set; } = new();
	public List<string> MissingTargetBones { get; set; } = new();
}

internal sealed class RetargetValidatedBonePair
{
	public string Slot { get; set; } = string.Empty;
	public string SourceBone { get; set; } = string.Empty;
	public string TargetBone { get; set; } = string.Empty;
}

internal sealed class RetargetBoneMapEntry
{
	public string SourceBone { get; set; } = string.Empty;
	public string TargetBone { get; set; } = string.Empty;
	public bool Required { get; set; }
	public bool Enabled { get; set; }
	public bool Locked { get; set; }
	public bool UserOverridden { get; set; }
	public string Group { get; set; } = string.Empty;
	public string MappingOrigin { get; set; } = string.Empty;
}

internal sealed class RetargetMappingCoverageSummary
{
	public int RequiredSlotCount { get; set; }
	public int MappedRequiredSlotCount { get; set; }
	public int OptionalSlotCount { get; set; }
	public int MappedOptionalSlotCount { get; set; }
	public int ResolvedCandidateSlotCount { get; set; }
	public int TotalMappedSlotCount { get; set; }
	public int UserOverrideSlotCount { get; set; }
	public int LockedSlotCount { get; set; }
}

internal sealed class RetargetBoneMapOverrideSummary
{
	public bool Provided { get; set; }
	public int EnabledSlotCount { get; set; }
	public int RequiredSlotCount { get; set; }
	public int OptionalSlotCount { get; set; }
	public int DisabledSlotCount { get; set; }
	public int LockedSlotCount { get; set; }
	public int UserOverrideSlotCount { get; set; }
	public List<string> DisabledSlots { get; set; } = new();
	public List<string> RequiredSlots { get; set; } = new();
	public List<string> OptionalSlots { get; set; } = new();
	public List<string> LockedSlots { get; set; } = new();
	public List<string> UserOverrideSlots { get; set; } = new();
	public List<string> MissingTargetBoneSlots { get; set; } = new();
	public List<string> InvalidSlots { get; set; } = new();
}

internal sealed class RetargetPoseNormalizationSummary
{
	public string TargetPosePresetId { get; set; } = string.Empty;
	public string TargetPosePresetSource { get; set; } = string.Empty;
}

internal sealed class RetargetMotionTrajectoryAnalysis
{
	public List<string> Warnings { get; set; } = new();
	public List<string> Failures { get; set; } = new();
}

internal sealed class RetargetStageManifestEntry
{
	public string StageId { get; set; } = string.Empty;
	public string Status { get; set; } = string.Empty;
	public List<string> Warnings { get; set; } = new();
	public string Error { get; set; } = string.Empty;
}

internal sealed class RetargetOutputsSummary
{
	public string RunDir { get; set; } = string.Empty;
	public string ExportPath { get; set; } = string.Empty;
	public string ManifestPath { get; set; } = string.Empty;
	public string BoneMapOverridesPath { get; set; } = string.Empty;
}

internal sealed class RetargetPreviewArtifacts
{
	public string PreviewBlendPath { get; set; } = string.Empty;
	public string PreviewVideoPath { get; set; } = string.Empty;
	public string ComparisonBlendPath { get; set; } = string.Empty;
	public string ComparisonVideoPath { get; set; } = string.Empty;
}

internal sealed class RetargetArtifactLinks
{
	public string RunDirectoryPath { get; set; } = string.Empty;
	public string ManifestPath { get; set; } = string.Empty;
	public string ExportPath { get; set; } = string.Empty;
	public string SourcePreviewPath { get; set; } = string.Empty;
	public string ImportedAnimationPath { get; set; } = string.Empty;
	public string SourcePreviewVmdlPath { get; set; } = string.Empty;
	public string VmdlPath { get; set; } = string.Empty;
	public string BoneMapOverridePath { get; set; } = string.Empty;
	public string RunLogPath { get; set; } = string.Empty;
}

internal sealed class RetargetBackendInvocation
{
	public string BlenderExecutablePath { get; set; } = string.Empty;
	public string BackendRootPath { get; set; } = string.Empty;
	public string RecipePath { get; set; } = string.Empty;
	public string RunId { get; set; } = string.Empty;
	public string BoneMapOverridePath { get; set; } = string.Empty;
}

internal static class RetargetManifestJson
{
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	public static T Deserialize<T>( string json ) where T : new()
	{
		var value = JsonSerializer.Deserialize<T>( json, Options ) ?? new T();
		if ( value is RetargetRunManifest manifest )
			NormalizeManifest( manifest );
		return value;
	}

	private static void NormalizeManifest( RetargetRunManifest manifest )
	{
		manifest.RunId ??= string.Empty;
		manifest.Status ??= string.Empty;
		manifest.RecipeId ??= string.Empty;
		manifest.SourceProfileId ??= string.Empty;
		manifest.TargetProfileId ??= string.Empty;
		manifest.SourceFile ??= string.Empty;
		manifest.SelectedAction ??= string.Empty;
		manifest.RetargetedActionName ??= string.Empty;
		manifest.RetargetBackend ??= string.Empty;
		manifest.BackendVersion ??= string.Empty;
		manifest.BackendSettings ??= new Dictionary<string, object>();
		manifest.BackendGeneratedBoneMap ??= new List<RetargetGeneratedBoneMapEntry>();
		foreach ( var entry in manifest.BackendGeneratedBoneMap )
		{
			if ( entry is null )
				continue;

			entry.SourceBone ??= string.Empty;
			entry.TargetBone ??= string.Empty;
			entry.BoneKey ??= string.Empty;
			entry.CanonicalSlot ??= string.Empty;
			entry.MappingOrigin ??= string.Empty;
		}

		manifest.BackendMappingOverrideSummary ??= new RetargetBackendMappingOverrideSummary();
		manifest.BackendMappingOverrideSummary.Reason ??= string.Empty;
		manifest.BackendMappingOverrideSummary.ValidatedPairs ??= new List<RetargetValidatedBonePair>();
		foreach ( var pair in manifest.BackendMappingOverrideSummary.ValidatedPairs )
		{
			if ( pair is null )
				continue;

			pair.Slot ??= string.Empty;
			pair.SourceBone ??= string.Empty;
			pair.TargetBone ??= string.Empty;
		}

		manifest.BackendMappingOverrideSummary.MissingSourceBones ??= new List<string>();
		manifest.BackendMappingOverrideSummary.MissingTargetBones ??= new List<string>();
		manifest.BackendWarnings ??= new List<string>();

		manifest.PoseNormalization ??= new RetargetPoseNormalizationSummary();
		manifest.PoseNormalization.TargetPosePresetId ??= string.Empty;
		manifest.PoseNormalization.TargetPosePresetSource ??= string.Empty;

		manifest.MotionTrajectoryAnalysis ??= new RetargetMotionTrajectoryAnalysis();
		manifest.MotionTrajectoryAnalysis.Warnings ??= new List<string>();
		manifest.MotionTrajectoryAnalysis.Failures ??= new List<string>();

		manifest.BoneMap = manifest.BoneMap is null
			? new Dictionary<string, RetargetBoneMapEntry>( StringComparer.OrdinalIgnoreCase )
			: new Dictionary<string, RetargetBoneMapEntry>( manifest.BoneMap, StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in manifest.BoneMap.Values )
		{
			if ( entry is null )
				continue;

			entry.SourceBone ??= string.Empty;
			entry.TargetBone ??= string.Empty;
			entry.Group ??= string.Empty;
			entry.MappingOrigin ??= string.Empty;
		}

		manifest.MappingCoverage ??= new RetargetMappingCoverageSummary();
		manifest.BoneMapOverrideSummary ??= new RetargetBoneMapOverrideSummary();
		manifest.BoneMapOverrideSummary.DisabledSlots ??= new List<string>();
		manifest.BoneMapOverrideSummary.RequiredSlots ??= new List<string>();
		manifest.BoneMapOverrideSummary.OptionalSlots ??= new List<string>();
		manifest.BoneMapOverrideSummary.LockedSlots ??= new List<string>();
		manifest.BoneMapOverrideSummary.UserOverrideSlots ??= new List<string>();
		manifest.BoneMapOverrideSummary.MissingTargetBoneSlots ??= new List<string>();
		manifest.BoneMapOverrideSummary.InvalidSlots ??= new List<string>();
		manifest.UnmappedRequiredSlots ??= new List<string>();
		manifest.Warnings ??= new List<string>();
		manifest.Stages ??= new List<RetargetStageManifestEntry>();
		foreach ( var stage in manifest.Stages )
		{
			if ( stage is null )
				continue;

			stage.StageId ??= string.Empty;
			stage.Status ??= string.Empty;
			stage.Warnings ??= new List<string>();
			stage.Error ??= string.Empty;
		}

		manifest.Outputs ??= new RetargetOutputsSummary();
		manifest.Outputs.RunDir ??= string.Empty;
		manifest.Outputs.ExportPath ??= string.Empty;
		manifest.Outputs.ManifestPath ??= string.Empty;
		manifest.Outputs.BoneMapOverridesPath ??= string.Empty;
	}
}
