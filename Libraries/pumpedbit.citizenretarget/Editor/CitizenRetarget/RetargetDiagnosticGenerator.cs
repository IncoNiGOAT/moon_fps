#nullable enable

namespace Editor.CitizenRetarget;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

[Obsolete( "Deprecated math-audit diagnostic generator retained for historical troubleshooting only.", false )]
internal sealed class RetargetDiagnosticGenerator
{
	private static readonly string[] DiagnosticBones =
	{
		"pelvis",
		"spine_0",
		"arm_upper_L",
		"leg_upper_L",
		"root_IK"
	};

	private readonly SmdAnimationWriter _smdWriter = new();
	private readonly CitizenVmdlWriter _vmdlWriter = new();

	private static readonly (string SequenceName, SmdRotationContract Contract)[] RotationContractDiagnostics =
	{
		("diag_target_rest_pose", SmdRotationContract.RawLocal),
		("diag_target_rest_pose_inverse_local", SmdRotationContract.InverseLocal),
		("diag_target_rest_pose_root_yup", SmdRotationContract.RawLocalRootYUp),
		("diag_target_rest_pose_root_yup_inverse_local", SmdRotationContract.InverseLocalRootYUp),
		("diag_target_rest_pose_legacy_valve", SmdRotationContract.LegacyValve),
		("diag_target_rest_pose_legacy_valve_inverse", SmdRotationContract.LegacyValveInverse),
		("diag_target_rest_pose_legacy_valve_basis", SmdRotationContract.LegacyValveBasis),
		("diag_target_rest_pose_legacy_valve_basis_inverse", SmdRotationContract.LegacyValveBasisInverse)
	};

	public RetargetDiagnosticResult GenerateDiagnostics(
		IReadOnlyList<NativeBoneInfo> targetBones,
		string diagnosticsFolder,
		string diagnosticsVmdlPath )
	{
		var absoluteFolder = CitizenRetargetPaths.GetAssetAbsolutePath( diagnosticsFolder );
		Directory.CreateDirectory( absoluteFolder );
		foreach ( var existing in Directory.GetFiles( absoluteFolder, "diag_*.smd", SearchOption.TopDirectoryOnly ) )
			File.Delete( existing );

		var result = new RetargetDiagnosticResult
		{
			DiagnosticsFolderAbsolutePath = absoluteFolder
		};

		foreach ( var diagnostic in RotationContractDiagnostics )
		{
			var fullSkeleton = BuildFullSkeletonClip( targetBones, diagnostic.SequenceName );
			result.GeneratedClipPaths.Add( _smdWriter.WriteClip( fullSkeleton, diagnosticsFolder, new SmdWriteOptions
			{
				RotationContract = diagnostic.Contract
			} ) );
		}

		var identityRotationSkeleton = BuildIdentityRotationClip( targetBones, "diag_target_rest_identity_rot" );
		result.GeneratedClipPaths.Add( _smdWriter.WriteClip( identityRotationSkeleton, diagnosticsFolder ) );

		foreach ( var boneName in DiagnosticBones )
		{
			foreach ( var axis in new[] { NVector3.UnitX, NVector3.UnitY, NVector3.UnitZ } )
			{
				foreach ( var angle in new[] { 90f, -90f } )
				{
					var diagnosticClip = BuildSingleBoneDiagnostic( targetBones, boneName, axis, angle );
					result.GeneratedClipPaths.Add( _smdWriter.WriteClip( diagnosticClip, diagnosticsFolder ) );
				}
			}
		}

		result.DiagnosticsVmdlAbsolutePath = _vmdlWriter.WriteSharedVmdl( diagnosticsVmdlPath, diagnosticsFolder, ".smd" );
		return result;
	}

	private static RetargetedClip BuildFullSkeletonClip( IReadOnlyList<NativeBoneInfo> targetBones, string sequenceName )
	{
		var clip = BuildClipSkeleton( targetBones, sequenceName );
		clip.Frames.Add( new RetargetedFrame
		{
			Index = 0,
			BoneTransforms = targetBones.Select( bone => bone.LocalTransform ).ToList()
		} );
		return clip;
	}

	private static RetargetedClip BuildIdentityRotationClip( IReadOnlyList<NativeBoneInfo> targetBones, string sequenceName )
	{
		var clip = BuildClipSkeleton( targetBones, sequenceName );
		clip.Frames.Add( new RetargetedFrame
		{
			Index = 0,
			BoneTransforms = targetBones
				.Select( bone => new BoneTransform( bone.LocalTransform.Translation, NQuaternion.Identity ) )
				.ToList()
		} );
		return clip;
	}

	private static RetargetedClip BuildSingleBoneDiagnostic(
		IReadOnlyList<NativeBoneInfo> targetBones,
		string boneName,
		NVector3 axis,
		float angleDegrees )
	{
		var axisLabel = axis == NVector3.UnitX ? "x" : axis == NVector3.UnitY ? "y" : "z";
		var signLabel = angleDegrees > 0f ? "pos90" : "neg90";
		var clip = BuildClipSkeleton( targetBones, $"diag_{boneName}_{axisLabel}_{signLabel}" );
		var frame0 = new RetargetedFrame
		{
			Index = 0,
			BoneTransforms = targetBones.Select( bone => bone.LocalTransform ).ToList()
		};
		var frame1 = new RetargetedFrame
		{
			Index = 1,
			BoneTransforms = targetBones.Select( bone => bone.LocalTransform ).ToList()
		};

		var boneIndex = targetBones
			.Select( ( bone, index ) => new { bone.Name, Index = index } )
			.FirstOrDefault( item => item.Name.Equals( boneName, StringComparison.OrdinalIgnoreCase ) )
			?.Index ?? -1;
		if ( boneIndex >= 0 )
		{
			var rest = frame1.BoneTransforms[boneIndex];
			var delta = NQuaternion.CreateFromAxisAngle( axis, angleDegrees * MathF.PI / 180f );
			frame1.BoneTransforms[boneIndex] = new BoneTransform( rest.Translation, RetargetMath.Normalize( rest.Rotation * delta ) );
		}

		clip.FrameRate = 30;
		clip.Frames.Add( frame0 );
		clip.Frames.Add( frame1 );
		return clip;
	}

	private static RetargetedClip BuildClipSkeleton( IReadOnlyList<NativeBoneInfo> targetBones, string sequenceName )
	{
		return new RetargetedClip
		{
			SourceClipName = sequenceName,
			DisplayName = sequenceName,
			SequenceName = sequenceName,
			Looping = false,
			FrameRate = 30,
			Bones = targetBones
				.Select( ( bone, index ) => new RetargetedBone
				{
					Id = index,
					Name = bone.Name,
					ParentId = string.IsNullOrEmpty( bone.ParentName )
						? -1
						: FindBoneIndex( targetBones, bone.ParentName ),
					RestLocal = bone.LocalTransform
				} )
				.ToList()
		};
	}

	private static int FindBoneIndex( IReadOnlyList<NativeBoneInfo> targetBones, string boneName )
	{
		for ( var index = 0; index < targetBones.Count; ++index )
		{
			if ( targetBones[index].Name.Equals( boneName, StringComparison.OrdinalIgnoreCase ) )
				return index;
		}

		return -1;
	}
}
