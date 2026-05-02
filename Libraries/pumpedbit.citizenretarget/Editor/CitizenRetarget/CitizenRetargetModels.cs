#nullable enable

namespace Editor.CitizenRetarget;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

internal sealed class RetargetClipDescriptor
{
	public string SourceName { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public double TimeBegin { get; set; }
	public double TimeEnd { get; set; }
	public double FrameRate { get; set; }
	public int FrameCount { get; set; }

	public override string ToString() => $"{DisplayName} ({FrameCount}f @ {FrameRate:0.##}fps)";
}

internal sealed class RetargetImportResult
{
	public string SequenceName { get; set; } = string.Empty;
	public string GeneratedClipAbsolutePath { get; set; } = string.Empty;
	public string SourcePreviewClipAbsolutePath { get; set; } = string.Empty;
	public string PostImportError { get; set; } = string.Empty;
	public RetargetOutputFormat OutputFormat { get; set; }
	public string VmdlAbsolutePath { get; set; } = string.Empty;
	public string VmdlResourcePath { get; set; } = string.Empty;
	public string SourcePreviewVmdlAbsolutePath { get; set; } = string.Empty;
	public string SourcePreviewVmdlResourcePath { get; set; } = string.Empty;
	public string JobAbsolutePath { get; set; } = string.Empty;
	public string AuditMarkdownPath { get; set; } = string.Empty;
	public string SolveDiagnosticsJsonPath { get; set; } = string.Empty;
	public string SolveSummaryMarkdownPath { get; set; } = string.Empty;
	public string DiagnosticsVmdlAbsolutePath { get; set; } = string.Empty;
	public string ManifestAbsolutePath { get; set; } = string.Empty;
	public RetargetRunManifest Manifest { get; set; } = new();
	public RetargetPreviewArtifacts PreviewArtifacts { get; set; } = new();
	public RetargetArtifactLinks ArtifactLinks { get; set; } = new();
	public RetargetBackendInvocation BackendInvocation { get; set; } = new();
	public string Log { get; set; } = string.Empty;
}

internal enum RetargetOutputFormat
{
	// Deprecated diagnostic path retained for historical SMD experiments.
	SmdDebug,
	// Deprecated diagnostic path retained for historical DMX experiments.
	DmxSpike,
	FbxBackend
}

internal sealed class GeneratedAnimationSource
{
	public string SequenceName { get; set; } = string.Empty;
	public string ResourcePath { get; set; } = string.Empty;
	public bool Looping { get; set; }
	/// <summary> ModelDoc AnimFile start_frame, or -1 for default (full take). </summary>
	public int StartFrame { get; set; } = -1;
	/// <summary> ModelDoc AnimFile end_frame, or -1 for default (full take). </summary>
	public int EndFrame { get; set; } = -1;
}

internal sealed class NativeScanResult
{
	public string Error { get; set; } = string.Empty;
	public string SourcePath { get; set; } = string.Empty;
	public double UnitMeters { get; set; }
	public double OutputUnitMeters { get; set; }
	public double FrameRate { get; set; }
	public List<RetargetClipDescriptor> Clips { get; set; } = new();
}

internal sealed class NativeSampleResult
{
	public string Error { get; set; } = string.Empty;
	public string SourcePath { get; set; } = string.Empty;
	public string TargetPath { get; set; } = string.Empty;
	public string ClipName { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string SourceProfile { get; set; } = string.Empty;
	public string TargetProfile { get; set; } = string.Empty;
	public double SourceUnitMeters { get; set; }
	public double SourceOutputUnitMeters { get; set; }
	public double TargetUnitMeters { get; set; }
	public double TargetOutputUnitMeters { get; set; }
	public double FrameRate { get; set; }
	public int FrameCount { get; set; }
	public List<NativeBoneInfo> SourceBones { get; set; } = new();
	public List<NativeBoneInfo> TargetBones { get; set; } = new();
	public List<NativeFrameInfo> Frames { get; set; } = new();
}

internal sealed class NativeSceneAuditResult
{
	public string Error { get; set; } = string.Empty;
	public string ScenePath { get; set; } = string.Empty;
	public string Profile { get; set; } = string.Empty;
	public double UnitMeters { get; set; }
	public double OutputUnitMeters { get; set; }
	public double FrameRate { get; set; }
	public NativeSceneMetadata Metadata { get; set; } = new();
	public List<NativeAuditBoneInfo> Bones { get; set; } = new();
}

internal sealed class NativeSceneMetadata
{
	public string Creator { get; set; } = string.Empty;
	public NativeApplicationInfo OriginalApplication { get; set; } = new();
	public NativeApplicationInfo LatestApplication { get; set; } = new();
}

internal sealed class NativeApplicationInfo
{
	public string Name { get; set; } = string.Empty;
	public string Vendor { get; set; } = string.Empty;
	public string Version { get; set; } = string.Empty;
}

internal sealed class NativeAuditBoneInfo
{
	public string Name { get; set; } = string.Empty;
	public string ParentName { get; set; } = string.Empty;
	public float[] Translation { get; set; } = new[] { 0f, 0f, 0f };
	public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
	public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
	public float[] WorldTranslation { get; set; } = new[] { 0f, 0f, 0f };
	public float[] WorldRotation { get; set; } = new[] { 0f, 0f, 0f, 1f };

	public BoneTransform LocalTransform => new( Translation.AsVector3(), Rotation.AsQuaternion() );
	public WorldTransform WorldTransform => new( WorldTranslation.AsVector3(), WorldRotation.AsQuaternion() );
}

internal sealed class NativeBoneInfo
{
	public string Name { get; set; } = string.Empty;
	public string ParentName { get; set; } = string.Empty;
	public float[] Translation { get; set; } = new[] { 0f, 0f, 0f };
	public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };

	public BoneTransform LocalTransform => new( Translation.AsVector3(), Rotation.AsQuaternion() );
}

internal sealed class NativeFrameInfo
{
	public int Index { get; set; }
	public double Time { get; set; }
	public List<float[]> Translations { get; set; } = new();
	public List<float[]> Rotations { get; set; } = new();
}

internal sealed class BoneMapEntry
{
	public string SourceBone { get; set; } = string.Empty;
	public string TargetBone { get; set; } = string.Empty;
}

internal enum TargetBindProfile
{
	Current,
	ModifyGeometry,
	RawUnits,
	TargetOnlyControl
}

internal enum RetargetSolveStage
{
	BodyOnly,
	BodyAndRootMotion,
	BodyRootAndIk,
	Full
}

internal sealed class RetargetSolveOptions
{
	public RetargetSolveStage Stage { get; set; } = RetargetSolveStage.BodyOnly;
	public CitizenRetargetRootMotionMode RootMotionMode { get; set; } = CitizenRetargetRootMotionMode.Keep;
	public bool EnableLocalCompensation { get; set; }

	public bool IncludeIkTargets => Stage >= RetargetSolveStage.BodyRootAndIk;
	public bool IncludeFingers => Stage == RetargetSolveStage.Full;
	public bool IncludeRootMotionPolicy => Stage >= RetargetSolveStage.BodyAndRootMotion;
}

internal sealed class RetargetAuditConfig
{
	public string SourceFbxAbsolutePath { get; set; } = string.Empty;
	public string TargetBindAssetPath { get; set; } = string.Empty;
	public string FallbackTargetBindAssetPath { get; set; } = string.Empty;
	public string DiagnosticsAssetFolder { get; set; } = string.Empty;
	public string DiagnosticsVmdlPath { get; set; } = string.Empty;
	public string AuditJsonPath { get; set; } = string.Empty;
	public string AuditMarkdownPath { get; set; } = string.Empty;
	public List<TargetBindProfile> Profiles { get; set; } = new()
	{
		TargetBindProfile.Current,
		TargetBindProfile.ModifyGeometry,
		TargetBindProfile.RawUnits,
		TargetBindProfile.TargetOnlyControl
	};
}

internal sealed class RetargetAuditReport
{
	public string SourcePath { get; set; } = string.Empty;
	public TargetBindProfile LockedSourceProfile { get; set; }
	public bool HasLockedSourceProfile { get; set; }
	public double LockedSourceOutputUnitMeters { get; set; }
	public string LockedTargetBindAssetPath { get; set; } = string.Empty;
	public string LockedTargetBindAbsolutePath { get; set; } = string.Empty;
	public TargetBindProfile LockedTargetProfile { get; set; }
	public bool FallbackTargetWasUsed { get; set; }
	public bool HasLockedTargetProfile { get; set; }
	public string AuditJsonPath { get; set; } = string.Empty;
	public string AuditMarkdownPath { get; set; } = string.Empty;
	public string DiagnosticsVmdlAbsolutePath { get; set; } = string.Empty;
	public List<RetargetAuditSceneReport> SceneReports { get; set; } = new();
	public List<string> Warnings { get; set; } = new();
	public List<NativeBoneInfo> LockedSourceBones { get; set; } = new();
	public List<NativeBoneInfo> LockedTargetBones { get; set; } = new();
	public double LockedTargetOutputUnitMeters { get; set; }
}

internal sealed class RetargetDiagnosticResult
{
	public string DiagnosticsFolderAbsolutePath { get; set; } = string.Empty;
	public string DiagnosticsVmdlAbsolutePath { get; set; } = string.Empty;
	public List<string> GeneratedClipPaths { get; set; } = new();
}

internal sealed class DmxReferenceSpikeResult
{
	public string ReferenceDmxAbsolutePath { get; set; } = string.Empty;
	public string ReferenceVmdlAbsolutePath { get; set; } = string.Empty;
	public string StockSummaryJsonAbsolutePath { get; set; } = string.Empty;
	public string ReferenceSummaryJsonAbsolutePath { get; set; } = string.Empty;
	public string MarkdownAbsolutePath { get; set; } = string.Empty;
	public string BindCacheJsonAbsolutePath { get; set; } = string.Empty;
}

internal sealed class BlenderTargetBindCache
{
	public string JsonAbsolutePath { get; set; } = string.Empty;
	public double OutputUnitMeters { get; set; } = 0.01;
	public List<NativeBoneInfo> Bones { get; set; } = new();
}

internal sealed class BlenderForensicArtifactResult
{
	public string PayloadJsonAbsolutePath { get; set; } = string.Empty;
	public string AppliedPoseJsonAbsolutePath { get; set; } = string.Empty;
	public string ReimportedPoseJsonAbsolutePath { get; set; } = string.Empty;
	public string SummaryJsonAbsolutePath { get; set; } = string.Empty;
	public string SummaryMarkdownAbsolutePath { get; set; } = string.Empty;
	public string GeneratedDmxAbsolutePath { get; set; } = string.Empty;
}

internal sealed class BindPassthroughDebugResult
{
	public string PayloadJsonAbsolutePath { get; set; } = string.Empty;
	public string DmxAbsolutePath { get; set; } = string.Empty;
	public string VmdlAbsolutePath { get; set; } = string.Empty;
}

internal sealed class StockRoundTripDebugResult
{
	public string SourceDmxAbsolutePath { get; set; } = string.Empty;
	public string RoundTripDmxAbsolutePath { get; set; } = string.Empty;
	public string VmdlAbsolutePath { get; set; } = string.Empty;
}

internal sealed class RetargetAuditSceneReport
{
	public string Kind { get; set; } = string.Empty;
	public string AssetPath { get; set; } = string.Empty;
	public string AbsolutePath { get; set; } = string.Empty;
	public TargetBindProfile Profile { get; set; }
	public double SceneUnitMeters { get; set; }
	public double OutputUnitMeters { get; set; }
	public string Creator { get; set; } = string.Empty;
	public string OriginalApplication { get; set; } = string.Empty;
	public string LatestApplication { get; set; } = string.Empty;
	public float ApproximateHeightCm { get; set; }
	public float PlausibilityScore { get; set; }
	public bool IsBelievable { get; set; }
	public List<RetargetAuditBoneReport> CoreBones { get; set; } = new();
}

internal sealed class RetargetAuditBoneReport
{
	public string Name { get; set; } = string.Empty;
	public string ParentName { get; set; } = string.Empty;
	public float LocalLengthCm { get; set; }
	public float[] LocalTranslation { get; set; } = new[] { 0f, 0f, 0f };
	public float[] LocalRotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
	public float[] LocalScale { get; set; } = new[] { 1f, 1f, 1f };
	public float[] WorldTranslation { get; set; } = new[] { 0f, 0f, 0f };
	public float[] WorldRotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
}

internal sealed class RetargetedClip
{
	public string SourceClipName { get; set; } = string.Empty;
	public string SequenceName { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public bool Looping { get; set; }
	public double FrameRate { get; set; }
	public List<RetargetedBone> Bones { get; set; } = new();
	public List<RetargetedFrame> Frames { get; set; } = new();
}

internal sealed class RetargetSolveResult
{
	public RetargetedClip Clip { get; set; } = new();
	public RetargetSolveDiagnostics Diagnostics { get; set; } = new();
}

internal sealed class RetargetSolveDiagnostics
{
	public string SourceClipName { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string SequenceName { get; set; } = string.Empty;
	public string SourceProfile { get; set; } = string.Empty;
	public string TargetBindAssetPath { get; set; } = string.Empty;
	public RetargetSolveStage Stage { get; set; }
	public int FrameCount { get; set; }
	public float SourceScaleRatio { get; set; }
	public float[] ClipAlignmentTranslation { get; set; } = new[] { 0f, 0f, 0f };
	public float[] ClipAlignmentRotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
	public float[] PelvisTranslationDeltaMin { get; set; } = new[] { 0f, 0f, 0f };
	public float[] PelvisTranslationDeltaMax { get; set; } = new[] { 0f, 0f, 0f };
	public bool FullTargetSkeletonWrittenEveryFrame { get; set; }
	public bool AnyUnmappedBoneReceivedNonRestLocal { get; set; }
	public bool AnyAnimatedBoneReceivedNonFiniteQuaternion { get; set; }
	public bool AnyAnimatedBoneReceivedNonUnitQuaternion { get; set; }
	public bool OnlyPelvisLocalTranslationAnimated { get; set; }
	public bool UnexpectedScaleValuesDetected { get; set; }
	public List<RetargetBoneSolveDiagnostics> MappedBones { get; set; } = new();
}

internal sealed class RetargetBoneSolveDiagnostics
{
	public string BoneName { get; set; } = string.Empty;
	public float MaxAngularDeltaDegrees { get; set; }
}

internal sealed class RetargetSolveArtifactResult
{
	public string JsonAbsolutePath { get; set; } = string.Empty;
	public string MarkdownAbsolutePath { get; set; } = string.Empty;
}

internal sealed class RetargetedBone
{
	public int Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public int ParentId { get; set; }
	public BoneTransform RestLocal { get; set; }
}

internal sealed class RetargetedFrame
{
	public int Index { get; set; }
	public List<BoneTransform> BoneTransforms { get; set; } = new();
}

internal enum SmdRotationContract
{
	RawLocal,
	InverseLocal,
	LegacyValve,
	LegacyValveInverse,
	LegacyValveBasis,
	LegacyValveBasisInverse,
	RawLocalRootYUp,
	InverseLocalRootYUp
}

internal sealed class SmdWriteOptions
{
	public SmdRotationContract RotationContract { get; set; } = SmdRotationContract.RawLocal;
}

internal interface IRetargetOutputWriter
{
	RetargetOutputFormat Format { get; }
	string FileExtension { get; }
	string WriteClip( RetargetedClip clip, string outputFolder );
}

internal readonly struct BoneTransform
{
	public BoneTransform( NVector3 translation, NQuaternion rotation )
	{
		Translation = translation;
		Rotation = rotation;
	}

	public NVector3 Translation { get; }
	public NQuaternion Rotation { get; }
}

internal readonly struct WorldTransform
{
	public WorldTransform( NVector3 translation, NQuaternion rotation )
	{
		Translation = translation;
		Rotation = rotation;
	}

	public NVector3 Translation { get; }
	public NQuaternion Rotation { get; }
}

internal static class RetargetMath
{
	public static NQuaternion Normalize( NQuaternion rotation )
	{
		return rotation.LengthSquared() <= 0.000001f
			? NQuaternion.Identity
			: NQuaternion.Normalize( rotation );
	}

	public static WorldTransform Compose( WorldTransform? parent, BoneTransform local )
	{
		if ( parent is null )
			return new WorldTransform( local.Translation, Normalize( local.Rotation ) );

		var parentValue = parent.Value;
		var worldRotation = Normalize( parentValue.Rotation * local.Rotation );
		var worldTranslation = parentValue.Translation + NVector3.Transform( local.Translation, parentValue.Rotation );
		return new WorldTransform( worldTranslation, worldRotation );
	}

	public static BoneTransform ToLocal( WorldTransform? parent, WorldTransform world )
	{
		if ( parent is null )
			return new BoneTransform( world.Translation, Normalize( world.Rotation ) );

		var parentValue = parent.Value;
		var inverseParentRotation = Normalize( NQuaternion.Inverse( parentValue.Rotation ) );
		var localTranslation = NVector3.Transform( world.Translation - parentValue.Translation, inverseParentRotation );
		var localRotation = Normalize( inverseParentRotation * world.Rotation );
		return new BoneTransform( localTranslation, localRotation );
	}

	public static NVector3 TransformPointInverse( WorldTransform transform, NVector3 point )
	{
		return NVector3.Transform( point - transform.Translation, Normalize( NQuaternion.Inverse( transform.Rotation ) ) );
	}

	public static float LengthOrZero( NVector3 value ) => value.LengthSquared() > 0.000001f ? value.Length() : 0f;

	public static float CentimetersPerUnit( double outputUnitMeters )
	{
		return outputUnitMeters > 0.000001
			? (float)(outputUnitMeters / 0.01)
			: 1f;
	}

	public static BoneTransform ScaleTranslation( BoneTransform transform, float centimetersPerUnit )
	{
		return centimetersPerUnit == 1f
			? transform
			: new BoneTransform( transform.Translation * centimetersPerUnit, transform.Rotation );
	}

	public static WorldTransform ScaleTranslation( WorldTransform transform, float centimetersPerUnit )
	{
		return centimetersPerUnit == 1f
			? transform
			: new WorldTransform( transform.Translation * centimetersPerUnit, transform.Rotation );
	}

	public static WorldTransform Multiply( WorldTransform first, WorldTransform second )
	{
		var translation = first.Translation + NVector3.Transform( second.Translation, first.Rotation );
		var rotation = Normalize( first.Rotation * second.Rotation );
		return new WorldTransform( translation, rotation );
	}

	public static WorldTransform Inverse( WorldTransform transform )
	{
		var inverseRotation = Normalize( NQuaternion.Inverse( transform.Rotation ) );
		var inverseTranslation = NVector3.Transform( -transform.Translation, inverseRotation );
		return new WorldTransform( inverseTranslation, inverseRotation );
	}

	public static WorldTransform Transform( WorldTransform frame, WorldTransform value )
	{
		return Multiply( frame, value );
	}

	public static NQuaternion CreateRotationFromBasis( NVector3 right, NVector3 forward, NVector3 up )
	{
		var matrix = new System.Numerics.Matrix4x4(
			right.X, forward.X, up.X, 0f,
			right.Y, forward.Y, up.Y, 0f,
			right.Z, forward.Z, up.Z, 0f,
			0f, 0f, 0f, 1f );
		return Normalize( NQuaternion.CreateFromRotationMatrix( matrix ) );
	}

	public static float QuaternionAngleDegrees( NQuaternion rotation )
	{
		var normalized = Normalize( rotation );
		var halfAngle = MathF.Acos( Math.Clamp( MathF.Abs( normalized.W ), 0f, 1f ) );
		return halfAngle * 2f * (180f / MathF.PI);
	}
}

internal static class RetargetArrayExtensions
{
	public static NVector3 AsVector3( this float[] values )
	{
		if ( values is null || values.Length < 3 )
			return NVector3.Zero;

		return new NVector3( values[0], values[1], values[2] );
	}

	public static NQuaternion AsQuaternion( this float[] values )
	{
		if ( values is null || values.Length < 4 )
			return NQuaternion.Identity;

		return RetargetMath.Normalize( new NQuaternion( values[0], values[1], values[2], values[3] ) );
	}
}
