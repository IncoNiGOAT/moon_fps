using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace Editor.CitizenRetarget;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

[Obsolete( "Deprecated diagnostic-only DMX spike. Production retarget output uses the template-FBX path.", false )]
internal sealed class DmxOutputWriter : IRetargetOutputWriter
{
	private const string PythonLauncher = "py";
	private const string PythonVersionSwitch = "-3";
	private static readonly string BlenderSourceToolsDataModelDir = Path.Combine(
		CitizenRetargetPaths.GetTempPath( "BlenderSourceTools" ),
		"io_scene_valvesource" );
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};
	private readonly BlenderDmxExportBridge _blenderBridge = new();
	public BlenderForensicArtifactResult? LastForensicArtifactResult { get; private set; }

	public RetargetOutputFormat Format => RetargetOutputFormat.DmxSpike;
	public string FileExtension => ".dmx";

	public BlenderTargetBindCache EnsureTargetBindCache()
	{
		return _blenderBridge.EnsureTargetBindCache();
	}

	public string WriteClip( RetargetedClip clip, string outputFolder )
	{
		var absoluteFolder = CitizenRetargetPaths.GetAssetAbsolutePath( outputFolder );
		Directory.CreateDirectory( absoluteFolder );
		Directory.CreateDirectory( Path.Combine( absoluteFolder, "anims" ) );

		CleanupStaleGeneratedAssets( absoluteFolder );
		var outputAbsolutePath = Path.Combine( absoluteFolder, "anims", $"{clip.SequenceName}.dmx" );
		var payloadPath = WritePayloadJson( clip, CitizenTargetProfile.BlenderGeneratedPayloadRelativeFolder );
		var templateAbsolutePath = Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.ReferenceStockDmxPath );
		LastForensicArtifactResult = null;
		return _blenderBridge.ExportPayloadJsonToDmx( payloadPath, outputAbsolutePath, templateAbsolutePath );
	}

	public string WriteDirectTransferClip(
		string sourceFbxAbsolutePath,
		RetargetClipDescriptor clip,
		string outputFolder,
		string sequencePrefix,
		string mappingJsonAbsolutePath,
		string compensationJsonAbsolutePath,
		bool includeHands )
	{
		var sequenceName = BuildSequenceName( sequencePrefix, clip.DisplayName );
		var absoluteFolder = CitizenRetargetPaths.GetAssetAbsolutePath( outputFolder );
		Directory.CreateDirectory( absoluteFolder );
		Directory.CreateDirectory( Path.Combine( absoluteFolder, "anims" ) );

		CleanupStaleGeneratedAssets( absoluteFolder );
		var outputAbsolutePath = Path.Combine( absoluteFolder, "anims", $"{sequenceName}.dmx" );
		LastForensicArtifactResult = null;
		return _blenderBridge.ExportDirectTransferClipToDmx(
			sourceFbxAbsolutePath,
			clip.SourceName,
			sequenceName,
			mappingJsonAbsolutePath,
			compensationJsonAbsolutePath,
			includeHands,
			outputAbsolutePath );
	}

	private static void CleanupStaleGeneratedAssets( string absoluteFolder )
	{
		var prefix = CitizenTargetProfile.DefaultSequencePrefix;
		foreach ( var candidate in Directory.GetFiles( absoluteFolder, "*.dmx", SearchOption.TopDirectoryOnly ) )
		{
			var fileName = Path.GetFileNameWithoutExtension( candidate );
			if ( fileName.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				File.Delete( candidate );
		}

		var animsFolder = Path.Combine( absoluteFolder, "anims" );
		if ( !Directory.Exists( animsFolder ) )
			return;

		foreach ( var candidate in Directory.GetFiles( animsFolder, "*.dmx", SearchOption.AllDirectories ) )
		{
			var fileName = Path.GetFileNameWithoutExtension( candidate );
			if ( fileName.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				File.Delete( candidate );
		}

		foreach ( var nestedFolder in Directory.GetDirectories( animsFolder, "*", SearchOption.TopDirectoryOnly ) )
		{
			if ( !Directory.EnumerateFileSystemEntries( nestedFolder ).Any() )
				Directory.Delete( nestedFolder, recursive: true );
		}
	}

	public DmxReferenceSpikeResult EnsureReferenceAssets()
	{
		if ( !CitizenTargetProfile.EnableGeneratedDebugAssets )
		{
			return new DmxReferenceSpikeResult
			{
				MarkdownAbsolutePath = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.DmxAuditMarkdownRelativePath ),
				BindCacheJsonAbsolutePath = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.BlenderBindCacheRelativePath )
			};
		}

		var bindCache = EnsureTargetBindCache();
		var tempReferenceFolder = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.BlenderReferencePayloadRelativeFolder );
		Directory.CreateDirectory( tempReferenceFolder );

		var referenceJsonPath = Path.Combine( tempReferenceFolder, "citizen_reference_pose.json" );
		var referenceDmxPath = CitizenRetargetPaths.GetAssetAbsolutePath( $"{CitizenTargetProfile.ReferenceAnimationFolder}/ref_citizen_anim_raw.dmx" );
		var forceMotionJsonPath = Path.Combine( tempReferenceFolder, "citizen_reference_force_motion.json" );
		var forceMotionDmxPath = CitizenRetargetPaths.GetAssetAbsolutePath( $"{CitizenTargetProfile.ReferenceAnimationFolder}/ref_citizen_anim_force_motion.dmx" );
		var stockSummaryJsonPath = CitizenRetargetPaths.GetProjectAbsolutePath( ".tmp/citizen_retarget/dmx/stock_summary.json" );
		var referenceSummaryJsonPath = CitizenRetargetPaths.GetProjectAbsolutePath( ".tmp/citizen_retarget/dmx/reference_summary_generated.json" );
		var markdownPath = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.DmxAuditMarkdownRelativePath );
		var referenceVmdlPath = CitizenRetargetPaths.GetAssetAbsolutePath( CitizenTargetProfile.ReferenceDmxVmdlPath );
		Directory.CreateDirectory( Path.GetDirectoryName( referenceDmxPath )! );
		Directory.CreateDirectory( Path.GetDirectoryName( stockSummaryJsonPath )! );
		Directory.CreateDirectory( Path.GetDirectoryName( markdownPath )! );

		var referencePayload = CreateReferencePayload( bindCache, "ref_citizen_anim_raw", looping: false );
		File.WriteAllText( referenceJsonPath, JsonSerializer.Serialize( referencePayload, JsonOptions ) );
		var exportedReferenceDmxPath = _blenderBridge.ExportPayloadJsonToDmx( referenceJsonPath, referenceDmxPath );

		var stockDmxAbsolutePath = Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.ReferenceStockDmxPath );
		WriteForceMotionReferenceJson( referenceJsonPath, forceMotionJsonPath );
		var exportedForceMotionDmxPath = _blenderBridge.ExportPayloadJsonToDmx( forceMotionJsonPath, forceMotionDmxPath, stockDmxAbsolutePath );
		RunPython( $"\"{GetInspectorScriptPath()}\" --input \"{stockDmxAbsolutePath}\" --output \"{stockSummaryJsonPath}\" --datamodel-dir \"{BlenderSourceToolsDataModelDir}\"" );
		RunPython( $"\"{GetInspectorScriptPath()}\" --input \"{exportedReferenceDmxPath}\" --output \"{referenceSummaryJsonPath}\" --datamodel-dir \"{BlenderSourceToolsDataModelDir}\"" );

		var referenceVmdl = new CitizenVmdlWriter().WriteSharedVmdl( CitizenTargetProfile.ReferenceDmxVmdlPath, new[]
		{
			new GeneratedAnimationSource
			{
				SequenceName = "ref_citizen_anim_raw",
				ResourcePath = CitizenRetargetPaths.GetAssetResourcePath( exportedReferenceDmxPath ),
				Looping = false
			},
			new GeneratedAnimationSource
			{
				SequenceName = "ref_citizen_anim_force_motion",
				ResourcePath = CitizenRetargetPaths.GetAssetResourcePath( exportedForceMotionDmxPath ),
				Looping = true
			}
		} );

		WriteMarkdownSummary( stockSummaryJsonPath, referenceSummaryJsonPath, markdownPath, exportedReferenceDmxPath, referenceVmdl, bindCache.JsonAbsolutePath );

		return new DmxReferenceSpikeResult
		{
			ReferenceDmxAbsolutePath = exportedReferenceDmxPath,
			ReferenceVmdlAbsolutePath = referenceVmdl,
			StockSummaryJsonAbsolutePath = stockSummaryJsonPath,
			ReferenceSummaryJsonAbsolutePath = referenceSummaryJsonPath,
			MarkdownAbsolutePath = markdownPath,
			BindCacheJsonAbsolutePath = bindCache.JsonAbsolutePath
		};
	}

	public (string VmdlAbsolutePath, string AnimationAbsolutePath) EnsureCompensatedReferenceGuide()
	{
		var bindCache = EnsureTargetBindCache();
		var payloadFolder = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.BlenderReferencePayloadRelativeFolder );
		var animationFolder = CitizenRetargetPaths.GetAssetAbsolutePath( CitizenTargetProfile.CompensatedReferenceAnimationFolder );
		Directory.CreateDirectory( payloadFolder );
		Directory.CreateDirectory( animationFolder );
		Directory.CreateDirectory( Path.Combine( animationFolder, "anims" ) );

		var sequenceName = CitizenTargetProfile.CompensatedReferenceSequenceName;
		var payload = CreateReferencePayload( bindCache, sequenceName, looping: false );
		ApplyLocalCompensationToReferencePayload( payload, new Ual2SourceProfile().LoadLocalCompensation() );

		var payloadPath = Path.Combine( payloadFolder, $"{sequenceName}.json" );
		File.WriteAllText( payloadPath, JsonSerializer.Serialize( payload, JsonOptions ) );

		var outputFbxPath = Path.Combine( animationFolder, "anims", $"{sequenceName}.fbx" );
		var exportedAnimationPath = _blenderBridge.ExportPayloadJsonToFbx( payloadPath, outputFbxPath );

		var vmdlPath = new CitizenVmdlWriter().WriteSharedVmdl( CitizenTargetProfile.CompensatedReferenceVmdlPath, new[]
		{
			new GeneratedAnimationSource
			{
				SequenceName = sequenceName,
				ResourcePath = CitizenRetargetPaths.GetAssetResourcePath( exportedAnimationPath ),
				Looping = false
			}
		} );

		return (vmdlPath, exportedAnimationPath);
	}

	public BindPassthroughDebugResult WriteBindPassthroughDebugAssets()
	{
		var bindCache = EnsureTargetBindCache();
		var debugFolder = CitizenRetargetPaths.GetAssetAbsolutePath( CitizenTargetProfile.BindPassthroughAnimationFolder );
		var payloadFolder = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.BlenderBindPassthroughPayloadRelativeFolder );
		Directory.CreateDirectory( debugFolder );
		Directory.CreateDirectory( payloadFolder );
		Directory.CreateDirectory( Path.Combine( debugFolder, "anims" ) );

		var payload = CreateReferencePayload( bindCache, "dbg_citizen_bind_passthrough", looping: false );
		var payloadPath = Path.Combine( payloadFolder, "dbg_citizen_bind_passthrough.json" );
		File.WriteAllText( payloadPath, JsonSerializer.Serialize( payload, JsonOptions ) );

		var outputDmxPath = Path.Combine( debugFolder, "anims", "dbg_citizen_bind_passthrough.dmx" );
		var templateAbsolutePath = Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.ReferenceStockDmxPath );
		var exportedDmxPath = _blenderBridge.ExportPayloadJsonToDmx( payloadPath, outputDmxPath, templateAbsolutePath );

		var vmdlPath = new CitizenVmdlWriter().WriteSharedVmdl( CitizenTargetProfile.BindPassthroughVmdlPath, new[]
		{
			new GeneratedAnimationSource
			{
				SequenceName = "dbg_citizen_bind_passthrough",
				ResourcePath = CitizenRetargetPaths.GetAssetResourcePath( exportedDmxPath ),
				Looping = false
			}
		} );

		return new BindPassthroughDebugResult
		{
			PayloadJsonAbsolutePath = payloadPath,
			DmxAbsolutePath = exportedDmxPath,
			VmdlAbsolutePath = vmdlPath
		};
	}

	public StockRoundTripDebugResult WriteStockRoundTripDebugAssets()
	{
		var debugFolder = CitizenRetargetPaths.GetAssetAbsolutePath( CitizenTargetProfile.StockRoundTripAnimationFolder );
		Directory.CreateDirectory( debugFolder );
		Directory.CreateDirectory( Path.Combine( debugFolder, "anims" ) );

		var sourceDmxPath = Ual2SourceAdapter.ResolveAssetAbsolutePath( CitizenTargetProfile.ReferenceStockDmxPath );
		var outputDmxPath = Path.Combine( debugFolder, "anims", "dbg_citizen_stock_roundtrip.dmx" );
		var exportedDmxPath = _blenderBridge.RoundTripDmx( sourceDmxPath, outputDmxPath );

		var vmdlPath = new CitizenVmdlWriter().WriteSharedVmdl( CitizenTargetProfile.StockRoundTripVmdlPath, new[]
		{
			new GeneratedAnimationSource
			{
				SequenceName = "dbg_citizen_stock_roundtrip",
				ResourcePath = CitizenRetargetPaths.GetAssetResourcePath( exportedDmxPath ),
				Looping = false
			}
		} );

		return new StockRoundTripDebugResult
		{
			SourceDmxAbsolutePath = sourceDmxPath,
			RoundTripDmxAbsolutePath = exportedDmxPath,
			VmdlAbsolutePath = vmdlPath
		};
	}

	private static string WritePayloadJson( RetargetedClip clip, string tempRelativeFolder )
	{
		var tempFolder = CitizenRetargetPaths.GetProjectAbsolutePath( tempRelativeFolder );
		Directory.CreateDirectory( tempFolder );
		var payloadPath = Path.Combine( tempFolder, $"{clip.SequenceName}.json" );

		var payload = new
		{
			sequence_name = clip.SequenceName,
			display_name = clip.DisplayName,
			model_name = CitizenTargetProfile.CitizenAnimationDmxModelName,
			skeleton_name = "citizen_noscale1",
			frame_rate = clip.FrameRate,
			looping = clip.Looping,
			output_unit_meters = 0.01,
			bones = clip.Bones.Select( bone => new
			{
				id = bone.Id,
				name = bone.Name,
				parent_id = bone.ParentId,
				rest_translation = ToArray( bone.RestLocal.Translation ),
				rest_rotation = ToArray( bone.RestLocal.Rotation )
			} ),
			frames = clip.Frames.Select( frame => new
			{
				index = frame.Index,
				bone_transforms = frame.BoneTransforms.Select( transform => new
				{
					translation = ToArray( transform.Translation ),
					rotation = ToArray( transform.Rotation )
				} )
			} )
		};

		File.WriteAllText( payloadPath, JsonSerializer.Serialize( payload, JsonOptions ) );
		return payloadPath;
	}

	private static DmxClipPayload CreateReferencePayload( BlenderTargetBindCache bindCache, string sequenceName, bool looping )
	{
		var bones = bindCache.Bones
			.Select( ( bone, index ) => new DmxBonePayload
			{
				Id = index,
				Name = bone.Name,
				ParentId = string.IsNullOrWhiteSpace( bone.ParentName )
					? -1
					: bindCache.Bones.FindIndex( candidate => candidate.Name.Equals( bone.ParentName, StringComparison.OrdinalIgnoreCase ) ),
				RestTranslation = bone.Translation.ToArray(),
				RestRotation = bone.Rotation.ToArray()
			} )
			.ToList();

		return new DmxClipPayload
		{
			SequenceName = sequenceName,
			DisplayName = sequenceName,
			ModelName = CitizenTargetProfile.CitizenAnimationDmxModelName,
			SkeletonName = "citizen_noscale1",
			FrameRate = 30.0,
			Looping = looping,
			OutputUnitMeters = bindCache.OutputUnitMeters,
			Bones = bones,
			Frames = new List<DmxFramePayload>
			{
				new DmxFramePayload
				{
					Index = 0,
					BoneTransforms = bones.Select( bone => new DmxBoneTransformPayload
					{
						Translation = bone.RestTranslation.ToArray(),
						Rotation = bone.RestRotation.ToArray()
					} ).ToList()
				}
			}
		};
	}

	public static string BuildSequenceName( string sequencePrefix, string clipDisplayName )
	{
		return RetargetSequenceNames.Build( sequencePrefix, clipDisplayName );
	}

	private static float[] ToArray( System.Numerics.Vector3 vector )
	{
		return new[] { vector.X, vector.Y, vector.Z };
	}

	private static string Slugify( string value )
	{
		var builder = new StringBuilder( value.Length );
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

	private static float[] ToArray( System.Numerics.Quaternion quaternion )
	{
		return new[] { quaternion.X, quaternion.Y, quaternion.Z, quaternion.W };
	}

	private static void WriteForceMotionReferenceJson( string inputJsonPath, string outputJsonPath )
	{
		var payload = JsonSerializer.Deserialize<DmxClipPayload>( File.ReadAllText( inputJsonPath ) ) ?? throw new InvalidOperationException( "Could not deserialize reference DMX payload." );
		if ( payload.Frames.Count == 0 )
			throw new InvalidOperationException( "Reference DMX payload does not contain any frames." );

		var referenceFrame = payload.Frames[0];
		var secondFrame = referenceFrame.Clone();
		secondFrame.Index = 15;
		var thirdFrame = referenceFrame.Clone();
		thirdFrame.Index = 60;

		ApplyForceMotion( payload, secondFrame );
		ApplyForceMotion( payload, thirdFrame );

		payload.SequenceName = "ref_citizen_anim_force_motion";
		payload.DisplayName = payload.SequenceName;
		payload.Looping = true;
		payload.Frames = new List<DmxFramePayload>
		{
			referenceFrame.CloneWithIndex( 0 ),
			secondFrame,
			thirdFrame
		};

		File.WriteAllText( outputJsonPath, JsonSerializer.Serialize( payload, JsonOptions ) );
	}

	private static void ApplyForceMotion( DmxClipPayload payload, DmxFramePayload frame )
	{
		var boneMap = payload.Bones.Select( ( bone, index ) => new { bone.Name, Index = index } )
			.ToDictionary( item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase );

		if ( boneMap.TryGetValue( "pelvis", out var pelvisIndex ) )
		{
			var translation = frame.BoneTransforms[pelvisIndex].Translation;
			translation[0] += 20f;
			translation[2] += 8f;
		}

		if ( boneMap.TryGetValue( "arm_upper_L", out var armUpperLIndex ) )
		{
			frame.BoneTransforms[armUpperLIndex].Rotation = MultiplyRotation( frame.BoneTransforms[armUpperLIndex].Rotation, NQuaternion.CreateFromAxisAngle( new NVector3( 0f, 0f, 1f ), MathF.PI * 0.5f ) );
		}

		if ( boneMap.TryGetValue( "arm_lower_L", out var armLowerLIndex ) )
		{
			frame.BoneTransforms[armLowerLIndex].Rotation = MultiplyRotation( frame.BoneTransforms[armLowerLIndex].Rotation, NQuaternion.CreateFromAxisAngle( new NVector3( 0f, 1f, 0f ), -MathF.PI * 0.45f ) );
		}

		if ( boneMap.TryGetValue( "hand_L", out var handLIndex ) )
		{
			frame.BoneTransforms[handLIndex].Rotation = MultiplyRotation( frame.BoneTransforms[handLIndex].Rotation, NQuaternion.CreateFromAxisAngle( new NVector3( 1f, 0f, 0f ), MathF.PI * 0.35f ) );
		}
	}

	private static void ApplyLocalCompensationToReferencePayload( DmxClipPayload payload, IReadOnlyDictionary<string, NQuaternion> localCompensation )
	{
		if ( payload.Frames.Count == 0 || localCompensation.Count == 0 )
			return;

		var boneMap = payload.Bones.Select( ( bone, index ) => new { bone.Name, Index = index } )
			.ToDictionary( item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase );
		var frame = payload.Frames[0];

		foreach ( var (boneName, compensation) in localCompensation )
		{
			if ( !boneMap.TryGetValue( boneName, out var boneIndex ) || boneIndex < 0 || boneIndex >= frame.BoneTransforms.Count )
				continue;

			frame.BoneTransforms[boneIndex].Rotation = PostMultiplyRotation( frame.BoneTransforms[boneIndex].Rotation, compensation );
		}
	}

	private static float[] PostMultiplyRotation( float[] currentRotation, NQuaternion deltaRotation )
	{
		var current = new NQuaternion( currentRotation[0], currentRotation[1], currentRotation[2], currentRotation[3] );
		var next = NQuaternion.Normalize( current * deltaRotation );
		return ToArray( next );
	}

	private static float[] MultiplyRotation( float[] currentRotation, NQuaternion deltaRotation )
	{
		var current = new NQuaternion( currentRotation[0], currentRotation[1], currentRotation[2], currentRotation[3] );
		var next = NQuaternion.Normalize( deltaRotation * current );
		return ToArray( next );
	}

	private static void RunPython( string arguments )
	{
		RunProcess( PythonLauncher, $"{PythonVersionSwitch} {arguments}", "Python DMX helper" );
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

		if ( process.ExitCode == 0 )
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

	private static string GetInspectorScriptPath()
	{
		return CitizenRetargetPaths.GetProjectAbsolutePath( "tools/inspect_dmx_structure.py" );
	}

	private static void WriteMarkdownSummary(
		string stockSummaryJsonPath,
		string referenceSummaryJsonPath,
		string markdownPath,
		string referenceDmxPath,
		string referenceVmdlPath,
		string bindCacheJsonPath )
	{
		var stockSummary = JsonSerializer.Deserialize<DmxSummary>( File.ReadAllText( stockSummaryJsonPath ) ) ?? new DmxSummary();
		var referenceSummary = JsonSerializer.Deserialize<DmxSummary>( File.ReadAllText( referenceSummaryJsonPath ) ) ?? new DmxSummary();
		var builder = new StringBuilder();
		builder.AppendLine( "# Citizen DMX Output Spike" );
		builder.AppendLine();
		builder.AppendLine( "## Summary" );
		builder.AppendLine();
		builder.AppendLine( "- This spike freezes `SMD` as debug-only and validates a Blender Source Tools backed `DMX` path against a shipped Citizen animation." );
		builder.AppendLine( $"- Reference output: `{referenceDmxPath}`" );
		builder.AppendLine( $"- Reference preview model: `{referenceVmdlPath}`" );
		builder.AppendLine( $"- Canonical target bind cache: `{bindCacheJsonPath}`" );
		builder.AppendLine();
		builder.AppendLine( "## Stock Citizen DMX" );
		builder.AppendLine();
		builder.AppendLine( $"- Header: `{stockSummary.Header}`" );
		builder.AppendLine( $"- Root keys: `{string.Join( ", ", stockSummary.RootKeys ?? new List<string>() )}`" );
		builder.AppendLine( $"- Skeleton child count: `{stockSummary.Skeleton?.ChildCount ?? 0}`" );
		builder.AppendLine( $"- Clip channel count: `{stockSummary.Clip?.ChannelCount ?? 0}`" );
		builder.AppendLine();
		builder.AppendLine( "## Generated Reference DMX" );
		builder.AppendLine();
		builder.AppendLine( $"- Header: `{referenceSummary.Header}`" );
		builder.AppendLine( $"- Root keys: `{string.Join( ", ", referenceSummary.RootKeys ?? new List<string>() )}`" );
		builder.AppendLine( $"- Skeleton child count: `{referenceSummary.Skeleton?.ChildCount ?? 0}`" );
		builder.AppendLine( $"- Clip channel count: `{referenceSummary.Clip?.ChannelCount ?? 0}`" );
		builder.AppendLine();
		builder.AppendLine( "## Takeaways" );
		builder.AppendLine();
		builder.AppendLine( "- Stock Citizen animation DMX uses `binary 9 / model 22` with `DmElement -> skeleton + animationList + exportTags`." );
		builder.AppendLine( "- The generated reference DMX now comes from a Blender Source Tools action export instead of the earlier hand-authored channel experiment." );
		builder.AppendLine( "- The canonical target bind for solve and export is the Blender-imported `citizen.fbx` cache, not a separate custom DMX skeleton approximation." );
		builder.AppendLine( "- Manual ModelDoc validation is still required to decide whether the Blender-backed `DMX` path is a sane round-trip path for Citizen." );

		File.WriteAllText( markdownPath, builder.ToString() );
	}

	private sealed class DmxSummary
	{
		public string Header { get; set; } = string.Empty;
		public List<string> RootKeys { get; set; } = new();
		public DmxSkeletonSummary Skeleton { get; set; } = new();
		public DmxClipSummary Clip { get; set; } = new();
	}

	private sealed class DmxClipPayload
	{
		[JsonPropertyName( "sequence_name" )]
		public string SequenceName { get; set; } = string.Empty;
		[JsonPropertyName( "display_name" )]
		public string DisplayName { get; set; } = string.Empty;
		[JsonPropertyName( "model_name" )]
		public string ModelName { get; set; } = string.Empty;
		[JsonPropertyName( "skeleton_name" )]
		public string SkeletonName { get; set; } = string.Empty;
		[JsonPropertyName( "frame_rate" )]
		public double FrameRate { get; set; }
		[JsonPropertyName( "looping" )]
		public bool Looping { get; set; }
		[JsonPropertyName( "output_unit_meters" )]
		public double OutputUnitMeters { get; set; } = 0.01;
		[JsonPropertyName( "bones" )]
		public List<DmxBonePayload> Bones { get; set; } = new();
		[JsonPropertyName( "frames" )]
		public List<DmxFramePayload> Frames { get; set; } = new();
	}

	private sealed class DmxBonePayload
	{
		[JsonPropertyName( "id" )]
		public int Id { get; set; }
		[JsonPropertyName( "name" )]
		public string Name { get; set; } = string.Empty;
		[JsonPropertyName( "parent_id" )]
		public int ParentId { get; set; }
		[JsonPropertyName( "rest_translation" )]
		public float[] RestTranslation { get; set; } = [0f, 0f, 0f];
		[JsonPropertyName( "rest_rotation" )]
		public float[] RestRotation { get; set; } = [0f, 0f, 0f, 1f];
	}

	private sealed class DmxFramePayload
	{
		[JsonPropertyName( "index" )]
		public int Index { get; set; }
		[JsonPropertyName( "bone_transforms" )]
		public List<DmxBoneTransformPayload> BoneTransforms { get; set; } = new();

		public DmxFramePayload Clone()
		{
			return new DmxFramePayload
			{
				Index = Index,
				BoneTransforms = BoneTransforms.Select( transform => transform.Clone() ).ToList()
			};
		}

		public DmxFramePayload CloneWithIndex( int index )
		{
			var clone = Clone();
			clone.Index = index;
			return clone;
		}
	}

	private sealed class DmxBoneTransformPayload
	{
		[JsonPropertyName( "translation" )]
		public float[] Translation { get; set; } = [0f, 0f, 0f];
		[JsonPropertyName( "rotation" )]
		public float[] Rotation { get; set; } = [0f, 0f, 0f, 1f];

		public DmxBoneTransformPayload Clone()
		{
			return new DmxBoneTransformPayload
			{
				Translation = Translation.ToArray(),
				Rotation = Rotation.ToArray()
			};
		}
	}

	private sealed class DmxSkeletonSummary
	{
		public int ChildCount { get; set; }
	}

	private sealed class DmxClipSummary
	{
		public int ChannelCount { get; set; }
	}
}
