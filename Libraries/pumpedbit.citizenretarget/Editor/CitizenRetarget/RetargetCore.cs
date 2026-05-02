#nullable enable

namespace Editor.CitizenRetarget;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

internal sealed class RetargetCore
{
	public RetargetSolveResult Retarget(
		NativeSampleResult sample,
		List<BoneMapEntry> mappingEntries,
		IReadOnlyDictionary<string, NQuaternion> localCompensation,
		string sequencePrefix,
		RetargetSolveOptions options )
	{
		ArgumentNullException.ThrowIfNull( options );

		if ( sample.SourceBones.Count == 0 || sample.TargetBones.Count == 0 || sample.Frames.Count == 0 )
			throw new InvalidOperationException( "Native sample payload did not include skeleton or frame data" );

		var sourceIndexByName = sample.SourceBones
			.Select( ( bone, index ) => new { bone.Name, Index = index } )
			.ToDictionary( item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase );
		var targetIndexByName = sample.TargetBones
			.Select( ( bone, index ) => new { bone.Name, Index = index } )
			.ToDictionary( item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase );

		var forwardMap = mappingEntries
			.Where( entry => sourceIndexByName.ContainsKey( entry.SourceBone ) && targetIndexByName.ContainsKey( entry.TargetBone ) )
			.ToList();
		var reverseMap = forwardMap.ToDictionary( entry => entry.TargetBone, entry => entry.SourceBone, StringComparer.OrdinalIgnoreCase );
		var mappedTargetNames = new HashSet<string>( reverseMap.Keys, StringComparer.OrdinalIgnoreCase );
		var strippedSourceAncestors = ComputeStrippedSourceAncestors( sample.SourceBones, forwardMap );

		var targetRestWorld = BuildWorldMap( sample.TargetBones, index => sample.TargetBones[index].LocalTransform );
		var sourceRestWorld = BuildWorldMap( sample.SourceBones, index => sample.SourceBones[index].LocalTransform, strippedSourceAncestors );
		var clipAlignment = ComputeClipAlignment( sourceRestWorld, targetRestWorld, reverseMap );
		var alignedSourceRestWorld = ApplyAlignment( sourceRestWorld, clipAlignment );
		var sourceScale = ComputeUniformScale( sample.SourceBones, sample.TargetBones, forwardMap );

		var outputBones = sample.TargetBones.ToList();
		var mappedTargetOrder = sample.TargetBones
			.Where( bone => mappedTargetNames.Contains( bone.Name ) )
			.ToList();

		var sequenceName = sequencePrefix + Slugify( sample.DisplayName );
		var clip = new RetargetedClip
		{
			SourceClipName = sample.ClipName,
			DisplayName = sample.DisplayName,
			SequenceName = sequenceName,
			Looping = sample.DisplayName.Contains( "loop", StringComparison.OrdinalIgnoreCase ),
			FrameRate = sample.FrameRate,
			Bones = outputBones.Select( ( bone, index ) => new RetargetedBone
			{
				Id = index,
				Name = bone.Name,
				ParentId = string.IsNullOrEmpty( bone.ParentName )
					? -1
					: outputBones.FindIndex( candidate => candidate.Name.Equals( bone.ParentName, StringComparison.OrdinalIgnoreCase ) ),
				RestLocal = bone.LocalTransform
			} ).ToList()
		};

		var diagnostics = new RetargetSolveDiagnostics
		{
			SourceClipName = sample.ClipName,
			DisplayName = sample.DisplayName,
			SequenceName = sequenceName,
			SourceProfile = sample.SourceProfile,
			TargetBindAssetPath = sample.TargetPath,
			Stage = options.Stage,
			FrameCount = sample.FrameCount,
			SourceScaleRatio = sourceScale,
			ClipAlignmentTranslation = ToArray( clipAlignment.Translation ),
			ClipAlignmentRotation = ToArray( clipAlignment.Rotation ),
			FullTargetSkeletonWrittenEveryFrame = true,
			OnlyPelvisLocalTranslationAnimated = true,
			UnexpectedScaleValuesDetected = !IsFinite( sourceScale ) || sourceScale <= 0.0001f || sourceScale > 10f
		};
		var maxAngularDeltaByBone = mappedTargetOrder.ToDictionary( bone => bone.Name, _ => 0f, StringComparer.OrdinalIgnoreCase );
		var pelvisRangeInitialized = false;

		for ( var frameIndex = 0; frameIndex < sample.Frames.Count; ++frameIndex )
		{
			var frame = sample.Frames[frameIndex];
			var sourcePoseWorld = BuildWorldMap( sample.SourceBones, index => GetSourceLocalTransform( sample, frame, index ), strippedSourceAncestors );
			var alignedSourcePoseWorld = ApplyAlignment( sourcePoseWorld, clipAlignment );
			var targetLocalPose = new Dictionary<string, BoneTransform>( StringComparer.OrdinalIgnoreCase );
			var targetWorldPose = new Dictionary<string, WorldTransform>( StringComparer.OrdinalIgnoreCase );

			foreach ( var targetBone in mappedTargetOrder )
			{
				var sourceBoneName = reverseMap[targetBone.Name];
				var sourcePose = alignedSourcePoseWorld[sourceBoneName];
				var sourceRest = alignedSourceRestWorld[sourceBoneName];
				var targetRest = targetRestWorld[targetBone.Name];

				WorldTransform? parentWorld = null;
				if ( !string.IsNullOrEmpty( targetBone.ParentName ) )
				{
					if ( targetWorldPose.TryGetValue( targetBone.ParentName, out var computedParent ) )
						parentWorld = computedParent;
					else if ( targetRestWorld.TryGetValue( targetBone.ParentName, out var restParent ) )
						parentWorld = restParent;
				}

				var localDelta = RetargetMath.Normalize( NQuaternion.Inverse( sourceRest.Rotation ) * sourcePose.Rotation );
				maxAngularDeltaByBone[targetBone.Name] = MathF.Max(
					maxAngularDeltaByBone[targetBone.Name],
					RetargetMath.QuaternionAngleDegrees( localDelta ) );

				var targetDesiredWorldRotation = RetargetMath.Normalize( targetRest.Rotation * localDelta );
				var targetDesiredWorldTranslation = targetRest.Translation;
				var isPelvis = targetBone.Name.Equals( "pelvis", StringComparison.OrdinalIgnoreCase );
				if ( isPelvis )
				{
					var pelvisDeltaWorld = (sourcePose.Translation - sourceRest.Translation) * sourceScale;
					if ( options.IncludeRootMotionPolicy )
						pelvisDeltaWorld = ApplyRootMotionToPelvisDelta( pelvisDeltaWorld, options.RootMotionMode );

					targetDesiredWorldTranslation += pelvisDeltaWorld;
					UpdatePelvisRange( diagnostics, pelvisDeltaWorld, ref pelvisRangeInitialized );
				}

				var desiredWorld = new WorldTransform( targetDesiredWorldTranslation, targetDesiredWorldRotation );
				var localPose = RetargetMath.ToLocal( parentWorld, desiredWorld );
				if ( !isPelvis )
					localPose = new BoneTransform( targetBone.LocalTransform.Translation, localPose.Rotation );

				if ( options.EnableLocalCompensation && localCompensation.TryGetValue( targetBone.Name, out var compensation ) )
					localPose = new BoneTransform( localPose.Translation, RetargetMath.Normalize( localPose.Rotation * compensation ) );

				if ( !IsFinite( localPose.Rotation ) )
					diagnostics.AnyAnimatedBoneReceivedNonFiniteQuaternion = true;
				if ( !IsUnitQuaternion( localPose.Rotation ) )
					diagnostics.AnyAnimatedBoneReceivedNonUnitQuaternion = true;
				if ( !isPelvis && !AreEqual( localPose.Translation, targetBone.LocalTransform.Translation ) )
					diagnostics.OnlyPelvisLocalTranslationAnimated = false;

				targetLocalPose[targetBone.Name] = localPose;
				targetWorldPose[targetBone.Name] = RetargetMath.Compose( parentWorld, localPose );
			}

			var rootIkLocal = default( BoneTransform );
			WorldTransform? rootIkWorld = null;
			var ikLocals = new Dictionary<string, BoneTransform>( StringComparer.OrdinalIgnoreCase );
			if ( options.IncludeIkTargets )
			{
				rootIkLocal = BuildRootIkPose( sample.TargetBones, targetRestWorld, targetWorldPose, options.RootMotionMode );
				rootIkWorld = RetargetMath.Compose( null, rootIkLocal );
				ikLocals = BuildIkTargets( targetWorldPose, rootIkWorld.Value );
			}

			var outputFrame = new RetargetedFrame { Index = frame.Index };
			foreach ( var outputBone in outputBones )
			{
				BoneTransform writtenPose;
				if ( targetLocalPose.TryGetValue( outputBone.Name, out var mappedPose ) )
				{
					writtenPose = mappedPose;
				}
				else if ( options.IncludeIkTargets && outputBone.Name.Equals( "root_IK", StringComparison.OrdinalIgnoreCase ) )
				{
					writtenPose = rootIkLocal;
				}
				else if ( options.IncludeIkTargets && ikLocals.TryGetValue( outputBone.Name, out var ikPose ) )
				{
					writtenPose = ikPose;
				}
				else
				{
					writtenPose = outputBone.LocalTransform;
				}

				if ( !mappedTargetNames.Contains( outputBone.Name ) && !AreEqual( writtenPose, outputBone.LocalTransform ) )
					diagnostics.AnyUnmappedBoneReceivedNonRestLocal = true;

				outputFrame.BoneTransforms.Add( writtenPose );
			}

			if ( outputFrame.BoneTransforms.Count != outputBones.Count )
				diagnostics.FullTargetSkeletonWrittenEveryFrame = false;

			clip.Frames.Add( outputFrame );
		}

		diagnostics.MappedBones = maxAngularDeltaByBone
			.OrderByDescending( item => item.Value )
			.Select( item => new RetargetBoneSolveDiagnostics
			{
				BoneName = item.Key,
				MaxAngularDeltaDegrees = item.Value
			} )
			.ToList();

		return new RetargetSolveResult
		{
			Clip = clip,
			Diagnostics = diagnostics
		};
	}

	private static BoneTransform GetSourceLocalTransform( NativeSampleResult sample, NativeFrameInfo frame, int sourceIndex )
	{
		var translation = sourceIndex < frame.Translations.Count
			? frame.Translations[sourceIndex].AsVector3()
			: sample.SourceBones[sourceIndex].LocalTransform.Translation;
		var rotation = sourceIndex < frame.Rotations.Count
			? frame.Rotations[sourceIndex].AsQuaternion()
			: sample.SourceBones[sourceIndex].LocalTransform.Rotation;
		return new BoneTransform( translation, rotation );
	}

	private static Dictionary<string, WorldTransform> BuildWorldMap(
		List<NativeBoneInfo> bones,
		Func<int, BoneTransform> localTransformFactory,
		IReadOnlySet<string>? strippedBoneNames = null )
	{
		var indexByName = bones
			.Select( ( bone, index ) => new { bone.Name, Index = index } )
			.ToDictionary( item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase );
		var worldByName = new Dictionary<string, WorldTransform>( StringComparer.OrdinalIgnoreCase );

		WorldTransform ResolveBone( int index )
		{
			var bone = bones[index];
			if ( worldByName.TryGetValue( bone.Name, out var cached ) )
				return cached;

			WorldTransform? parentWorld = null;
			if ( !string.IsNullOrEmpty( bone.ParentName ) && indexByName.TryGetValue( bone.ParentName, out var parentIndex ) )
				parentWorld = ResolveBone( parentIndex );

			var localTransform = localTransformFactory( index );
			if ( strippedBoneNames is not null && strippedBoneNames.Contains( bone.Name ) )
				localTransform = new BoneTransform( NVector3.Zero, NQuaternion.Identity );

			var world = RetargetMath.Compose( parentWorld, localTransform );
			worldByName[bone.Name] = world;
			return world;
		}

		for ( var index = 0; index < bones.Count; ++index )
			ResolveBone( index );

		return worldByName;
	}

	private static Dictionary<string, WorldTransform> ApplyAlignment(
		Dictionary<string, WorldTransform> sourceWorld,
		WorldTransform alignment )
	{
		return sourceWorld.ToDictionary(
			pair => pair.Key,
			pair => RetargetMath.Transform( alignment, pair.Value ),
			StringComparer.OrdinalIgnoreCase );
	}

	private static WorldTransform ComputeClipAlignment(
		Dictionary<string, WorldTransform> sourceRestWorld,
		Dictionary<string, WorldTransform> targetRestWorld,
		IReadOnlyDictionary<string, string> reverseMap )
	{
		if ( !TryBuildSourceCharacterFrame( sourceRestWorld, reverseMap, out var sourceFrame ) )
			return new WorldTransform( NVector3.Zero, NQuaternion.Identity );
		if ( !TryBuildTargetCharacterFrame( targetRestWorld, out var targetFrame ) )
			return new WorldTransform( NVector3.Zero, NQuaternion.Identity );

		return RetargetMath.Multiply( targetFrame, RetargetMath.Inverse( sourceFrame ) );
	}

	private static bool TryBuildSourceCharacterFrame(
		Dictionary<string, WorldTransform> sourceWorld,
		IReadOnlyDictionary<string, string> reverseMap,
		out WorldTransform frame )
	{
		frame = default;
		if ( !TryGetMappedTranslation( sourceWorld, reverseMap, "pelvis", out var pelvis ) ||
			!TryGetMappedTranslation( sourceWorld, reverseMap, "leg_upper_L", out var leftUpperLeg ) ||
			!TryGetMappedTranslation( sourceWorld, reverseMap, "leg_upper_R", out var rightUpperLeg ) ||
			!TryGetMappedTranslation( sourceWorld, reverseMap, "clavicle_L", out var leftClavicle ) ||
			!TryGetMappedTranslation( sourceWorld, reverseMap, "clavicle_R", out var rightClavicle ) )
		{
			return false;
		}

		frame = BuildCharacterFrame( pelvis, leftUpperLeg, rightUpperLeg, leftClavicle, rightClavicle );
		return true;
	}

	private static bool TryBuildTargetCharacterFrame(
		Dictionary<string, WorldTransform> targetWorld,
		out WorldTransform frame )
	{
		frame = default;
		if ( !TryGetTranslation( targetWorld, "pelvis", out var pelvis ) ||
			!TryGetTranslation( targetWorld, "leg_upper_L", out var leftUpperLeg ) ||
			!TryGetTranslation( targetWorld, "leg_upper_R", out var rightUpperLeg ) ||
			!TryGetTranslation( targetWorld, "clavicle_L", out var leftClavicle ) ||
			!TryGetTranslation( targetWorld, "clavicle_R", out var rightClavicle ) )
		{
			return false;
		}

		frame = BuildCharacterFrame( pelvis, leftUpperLeg, rightUpperLeg, leftClavicle, rightClavicle );
		return true;
	}

	private static WorldTransform BuildCharacterFrame(
		NVector3 pelvis,
		NVector3 leftUpperLeg,
		NVector3 rightUpperLeg,
		NVector3 leftClavicle,
		NVector3 rightClavicle )
	{
		var hipsMid = (leftUpperLeg + rightUpperLeg) * 0.5f;
		var shouldersMid = (leftClavicle + rightClavicle) * 0.5f;
		var up = NormalizeOrFallback( shouldersMid - hipsMid, NVector3.UnitZ );
		var rightHint = NormalizeOrFallback( ((rightClavicle - leftClavicle) + (rightUpperLeg - leftUpperLeg)) * 0.5f, NVector3.UnitX );
		var forward = NormalizeOrFallback( NVector3.Cross( up, rightHint ), NVector3.UnitY );
		var right = NormalizeOrFallback( NVector3.Cross( forward, up ), rightHint );
		var correctedUp = NormalizeOrFallback( NVector3.Cross( right, forward ), up );
		var rotation = RetargetMath.CreateRotationFromBasis( right, forward, correctedUp );
		return new WorldTransform( pelvis, rotation );
	}

	private static bool TryGetMappedTranslation(
		IReadOnlyDictionary<string, WorldTransform> sourceWorld,
		IReadOnlyDictionary<string, string> reverseMap,
		string targetBoneName,
		out NVector3 translation )
	{
		translation = default;
		if ( !reverseMap.TryGetValue( targetBoneName, out var sourceBoneName ) )
			return false;
		if ( !sourceWorld.TryGetValue( sourceBoneName, out var world ) )
			return false;

		translation = world.Translation;
		return true;
	}

	private static bool TryGetTranslation(
		IReadOnlyDictionary<string, WorldTransform> worldByName,
		string boneName,
		out NVector3 translation )
	{
		translation = default;
		if ( !worldByName.TryGetValue( boneName, out var world ) )
			return false;

		translation = world.Translation;
		return true;
	}

	private static NVector3 NormalizeOrFallback( NVector3 value, NVector3 fallback )
	{
		return value.LengthSquared() > 0.000001f ? NVector3.Normalize( value ) : fallback;
	}

	private static void UpdatePelvisRange( RetargetSolveDiagnostics diagnostics, NVector3 pelvisDeltaWorld, ref bool initialized )
	{
		if ( !initialized )
		{
			diagnostics.PelvisTranslationDeltaMin = ToArray( pelvisDeltaWorld );
			diagnostics.PelvisTranslationDeltaMax = ToArray( pelvisDeltaWorld );
			initialized = true;
			return;
		}

		diagnostics.PelvisTranslationDeltaMin[0] = MathF.Min( diagnostics.PelvisTranslationDeltaMin[0], pelvisDeltaWorld.X );
		diagnostics.PelvisTranslationDeltaMin[1] = MathF.Min( diagnostics.PelvisTranslationDeltaMin[1], pelvisDeltaWorld.Y );
		diagnostics.PelvisTranslationDeltaMin[2] = MathF.Min( diagnostics.PelvisTranslationDeltaMin[2], pelvisDeltaWorld.Z );
		diagnostics.PelvisTranslationDeltaMax[0] = MathF.Max( diagnostics.PelvisTranslationDeltaMax[0], pelvisDeltaWorld.X );
		diagnostics.PelvisTranslationDeltaMax[1] = MathF.Max( diagnostics.PelvisTranslationDeltaMax[1], pelvisDeltaWorld.Y );
		diagnostics.PelvisTranslationDeltaMax[2] = MathF.Max( diagnostics.PelvisTranslationDeltaMax[2], pelvisDeltaWorld.Z );
	}

	private static float[] ToArray( NVector3 value ) => [value.X, value.Y, value.Z];
	private static float[] ToArray( NQuaternion value ) => [value.X, value.Y, value.Z, value.W];

	private static bool AreEqual( BoneTransform left, BoneTransform right )
	{
		return AreEqual( left.Translation, right.Translation ) && AreEqual( left.Rotation, right.Rotation );
	}

	private static bool AreEqual( NVector3 left, NVector3 right )
	{
		return NVector3.DistanceSquared( left, right ) <= 0.000001f;
	}

	private static bool AreEqual( NQuaternion left, NQuaternion right )
	{
		var normalizedLeft = RetargetMath.Normalize( left );
		var normalizedRight = RetargetMath.Normalize( right );
		var dot = MathF.Abs( NQuaternion.Dot( normalizedLeft, normalizedRight ) );
		return 1f - dot <= 0.0001f;
	}

	private static bool IsFinite( NQuaternion rotation )
	{
		return float.IsFinite( rotation.X ) &&
			float.IsFinite( rotation.Y ) &&
			float.IsFinite( rotation.Z ) &&
			float.IsFinite( rotation.W );
	}

	private static bool IsFinite( float value ) => float.IsFinite( value );

	private static bool IsUnitQuaternion( NQuaternion rotation )
	{
		var length = rotation.Length();
		return float.IsFinite( length ) && MathF.Abs( length - 1f ) <= 0.001f;
	}

	private static HashSet<string> ComputeStrippedSourceAncestors(
		List<NativeBoneInfo> sourceBones,
		List<BoneMapEntry> mappings )
	{
		var mappedSourceNames = mappings
			.Select( entry => entry.SourceBone )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var sourceByName = sourceBones.ToDictionary( bone => bone.Name, bone => bone, StringComparer.OrdinalIgnoreCase );
		var stripped = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var sourceBoneName in mappedSourceNames )
		{
			if ( !sourceByName.TryGetValue( sourceBoneName, out var bone ) )
				continue;

			var parentName = bone.ParentName;
			while ( !string.IsNullOrEmpty( parentName ) && sourceByName.TryGetValue( parentName, out var parentBone ) )
			{
				if ( mappedSourceNames.Contains( parentBone.Name ) )
					break;

				stripped.Add( parentBone.Name );
				parentName = parentBone.ParentName;
			}
		}

		return stripped;
	}

	private static float ComputeUniformScale(
		List<NativeBoneInfo> sourceBones,
		List<NativeBoneInfo> targetBones,
		List<BoneMapEntry> mappings )
	{
		var sourceByName = sourceBones.ToDictionary( bone => bone.Name, bone => bone, StringComparer.OrdinalIgnoreCase );
		var targetByName = targetBones.ToDictionary( bone => bone.Name, bone => bone, StringComparer.OrdinalIgnoreCase );
		var ratios = new List<float>();

		foreach ( var mapping in mappings )
		{
			if ( mapping.TargetBone.Contains( "finger_", StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( !sourceByName.TryGetValue( mapping.SourceBone, out var sourceBone ) || !targetByName.TryGetValue( mapping.TargetBone, out var targetBone ) )
				continue;

			var sourceLength = RetargetMath.LengthOrZero( sourceBone.LocalTransform.Translation );
			var targetLength = RetargetMath.LengthOrZero( targetBone.LocalTransform.Translation );
			if ( sourceLength <= 0.001f || targetLength <= 0.001f )
				continue;

			ratios.Add( targetLength / sourceLength );
		}

		return ratios.Count > 0 ? ratios.Average() : 1f;
	}

	private static NVector3 ApplyRootMotionToPelvisDelta( NVector3 pelvisDeltaWorld, CitizenRetargetRootMotionMode mode )
	{
		return mode switch
		{
			CitizenRetargetRootMotionMode.Keep => pelvisDeltaWorld,
			CitizenRetargetRootMotionMode.InPlace => new NVector3( 0f, 0f, pelvisDeltaWorld.Z ),
			CitizenRetargetRootMotionMode.FullyInPlace => default,
			_ => pelvisDeltaWorld
		};
	}

	private static BoneTransform BuildRootIkPose(
		List<NativeBoneInfo> targetBones,
		Dictionary<string, WorldTransform> targetRestWorld,
		Dictionary<string, WorldTransform> targetPoseWorld,
		CitizenRetargetRootMotionMode rootMotionMode )
	{
		var rootIkBone = targetBones.First( bone => bone.Name.Equals( "root_IK", StringComparison.OrdinalIgnoreCase ) );
		var pelvisRest = targetRestWorld["pelvis"];
		var pelvisPose = targetPoseWorld["pelvis"];
		var pelvisDelta = pelvisPose.Translation - pelvisRest.Translation;
		pelvisDelta = ApplyRootMotionToPelvisDelta( pelvisDelta, rootMotionMode );

		return new BoneTransform( rootIkBone.LocalTransform.Translation + pelvisDelta, rootIkBone.LocalTransform.Rotation );
	}

	private static Dictionary<string, BoneTransform> BuildIkTargets(
		Dictionary<string, WorldTransform> targetWorldPose,
		WorldTransform rootIkWorld )
	{
		var results = new Dictionary<string, BoneTransform>( StringComparer.OrdinalIgnoreCase );
		foreach ( var pair in CitizenTargetProfile.IkTargets )
		{
			if ( !targetWorldPose.TryGetValue( pair.Value, out var effectorWorld ) )
				continue;

			var localPosition = RetargetMath.TransformPointInverse( rootIkWorld, effectorWorld.Translation );
			var localRotation = RetargetMath.Normalize( NQuaternion.Inverse( rootIkWorld.Rotation ) * effectorWorld.Rotation );
			results[pair.Key] = new BoneTransform( localPosition, localRotation );
		}

		return results;
	}

	private static string Slugify( string value )
	{
		var builder = new System.Text.StringBuilder( value.Length );
		foreach ( var character in value )
		{
			if ( char.IsLetterOrDigit( character ) )
			{
				builder.Append( char.ToLowerInvariant( character ) );
			}
			else if ( builder.Length > 0 && builder[^1] != '_' )
			{
				builder.Append( '_' );
			}
		}

		return builder.ToString().Trim( '_' );
	}
}
