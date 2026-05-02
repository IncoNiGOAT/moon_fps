using System.Text.Json;

#nullable enable

namespace Editor.CitizenRetarget;

internal sealed class CitizenRetargetPipeline : IDisposable
{
	private const int MaxRecentRuns = 40;
	private static readonly JsonSerializerOptions ProfileJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};
	private readonly Ual2SourceAdapter _sourceAdapter = new();
	private readonly CitizenVmdlWriter _vmdlWriter = new();
	private readonly BlenderRetargetBackend _backend = new();
	private readonly BlenderDmxExportBridge _dmxBridge = new();

	private readonly record struct StandardizedBoneName( string Original, string Standardized );

	public CitizenRetargetJob LoadOrCreateJob()
	{
		EnsureSeedProfilesExist();

		var relativePath = NormalizeResourcePath( CitizenTargetProfile.DefaultJobAssetPath );
		var absolutePath = ToAbsoluteAssetPath( relativePath );
		Directory.CreateDirectory( Path.GetDirectoryName( absolutePath )! );

		if ( File.Exists( absolutePath ) )
		{
			AssetSystem.RegisterFile( absolutePath );
			var existing = ResourceLibrary.Get<CitizenRetargetJob>( relativePath );
			if ( existing is not null )
			{
				var decoded = CloneJobForRuntime( existing );
				var migratedDefaultSourceProfile = IsDefaultSourceProfilePath( decoded.SourceProfilePath );
				var migratedLegacySourcePath = IsLegacyDefaultSourceFbxPath( decoded.SourceFbxPath );
				if ( migratedDefaultSourceProfile )
					decoded.SourceProfilePath = string.Empty;
				if ( migratedLegacySourcePath )
					decoded.SourceFbxPath = string.Empty;
				if ( migratedDefaultSourceProfile || migratedLegacySourcePath || NeedsJobPersistenceMigration( existing ) )
					SaveJob( decoded );
				EnsureGeneratedTargetExists( decoded );
				return decoded;
			}
		}

		var asset = AssetSystem.CreateResource( "crtjob", absolutePath );
		var job = new CitizenRetargetJob
		{
			SourceFbxPath = string.Empty,
			SourceProfilePath = string.Empty,
			MappingProfilePath = CitizenTargetProfile.DefaultMappingProfileAssetPath,
			TargetVmdlPath = CitizenTargetProfile.DefaultTargetVmdlPath,
			OutputAnimationFolder = CitizenTargetProfile.DefaultOutputAnimationFolder,
			SequencePrefix = CitizenTargetProfile.DefaultSequencePrefix,
			TargetPosePresetId = CitizenTargetProfile.DefaultTargetPosePresetId,
			RootMotionMode = CitizenRetargetRootMotionMode.Keep,
			ImportHands = true
		};
		asset.SaveToDisk( PrepareJobForPersistence( job ) );
		AssetSystem.RegisterFile( absolutePath );
		EnsureGeneratedTargetExists( job );
		return CloneJobForRuntime( job );
	}

	public RetargetSourceProfile LoadSourceProfile( string? resourcePath )
	{
		EnsureSeedProfilesExist();
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
			return BuildGenericSourceProfile();

		var normalized = NormalizeResourcePath( resourcePath );
		var absolutePath = ResolveAssetOrPluginAssetPath( normalized );
		RegisterFileIfExists( absolutePath );
		var loaded = ResourceLibrary.Get<RetargetSourceProfile>( normalized ) ?? TryLoadJsonProfile<RetargetSourceProfile>( absolutePath );
		if ( loaded is null )
			throw new FileNotFoundException( $"Unable to load source profile '{normalized}'." );
		var decoded = CloneSourceProfileForRuntime( loaded );
		if ( NeedsSourceProfilePersistenceMigration( loaded ) )
			SaveSourceProfile( decoded, normalized );
		return decoded;
	}

	public RetargetMappingProfile LoadMappingProfile( string? resourcePath )
	{
		EnsureSeedProfilesExist();
		var normalized = NormalizeResourcePath( string.IsNullOrWhiteSpace( resourcePath ) ? CitizenTargetProfile.DefaultMappingProfileAssetPath : resourcePath );
		var absolutePath = ResolveAssetOrPluginAssetPath( normalized );
		RegisterFileIfExists( absolutePath );
		return ResourceLibrary.Get<RetargetMappingProfile>( normalized )
			?? TryLoadJsonProfile<RetargetMappingProfile>( absolutePath )
			?? throw new FileNotFoundException( $"Unable to load mapping profile '{normalized}'." );
	}

	public IReadOnlyList<RetargetClipDescriptor> ScanClips( CitizenRetargetJob job )
	{
		var normalizedSourcePath = NormalizeSourceFilePath( job.SourceFbxPath );
		return _sourceAdapter.ScanClips( normalizedSourcePath );
	}

	public string ResolveSourceFbxPath( CitizenRetargetJob job )
	{
		return NormalizeSourceFilePath( job.SourceFbxPath );
	}

	public RetargetSourceInspection InspectSourceSkeleton( CitizenRetargetJob job )
	{
		var normalizedSourcePath = NormalizeSourceFilePath( job.SourceFbxPath );
		var audit = _sourceAdapter.InspectScene( normalizedSourcePath, TargetBindProfile.Current );
		return new RetargetSourceInspection
		{
			SourcePath = normalizedSourcePath,
			Audit = audit,
			BonesByName = audit.Bones.ToDictionary( bone => bone.Name, StringComparer.OrdinalIgnoreCase )
		};
	}

	public string DetectSourceProfilePath( CitizenRetargetJob job, RetargetSourceInspection inspection, IEnumerable<RetargetClipDescriptor>? clips = null )
	{
		var sourcePath = job.SourceFbxPath ?? string.Empty;
		var boneNames = inspection?.Audit?.Bones?.Select( bone => bone.Name ).Where( name => !string.IsNullOrWhiteSpace( name ) ).ToList()
			?? new List<string>();
		var clipNames = clips?
			.SelectMany( clip => new[] { clip.SourceName, clip.DisplayName } )
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.ToList()
			?? new List<string>();

		if ( !string.IsNullOrWhiteSpace( sourcePath ) && sourcePath.Contains( "mixamo", StringComparison.OrdinalIgnoreCase ) )
			return CitizenTargetProfile.MixamoSourceProfileAssetPath;

		if ( boneNames.Any( name => name.StartsWith( "mixamorig:", StringComparison.OrdinalIgnoreCase ) ) )
			return CitizenTargetProfile.MixamoSourceProfileAssetPath;

		if ( clipNames.Any( name => name.Contains( "mixamo.com", StringComparison.OrdinalIgnoreCase ) ) )
			return CitizenTargetProfile.MixamoSourceProfileAssetPath;

		return string.Empty;
	}

	private static bool IsDefaultSourceProfilePath( string? resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
			return false;

		return NormalizeResourcePath( resourcePath ).Equals(
			NormalizeResourcePath( CitizenTargetProfile.DefaultSourceProfileAssetPath ),
			StringComparison.OrdinalIgnoreCase );
	}

	private static bool IsLegacyDefaultSourceFbxPath( string? sourceFbxPath )
	{
		var decoded = CitizenRetargetPaths.DecodeExternalPath( sourceFbxPath ?? string.Empty )
			.Replace( '\\', '/' )
			.Trim();
		return decoded.EndsWith( "Universal Animation Library 2[Source]/Unity/UAL2.fbx", StringComparison.OrdinalIgnoreCase );
	}

	private static RetargetSourceProfile BuildGenericSourceProfile()
	{
		return new RetargetSourceProfile
		{
			ProfileId = "generic_humanoid",
			BackendSourceProfileId = CitizenTargetProfile.GenericBackendSourceProfileId,
			DisplayName = "Generic Humanoid",
			DefaultMappingProfilePath = CitizenTargetProfile.DefaultMappingProfileAssetPath,
			DefaultPoseReferenceAction = string.Empty,
			DefaultPoseReferenceFrame = 0,
			RootBoneCandidates = new List<string>
			{
				"mixamorig:Root",
				"mixamorig:Hips",
				"B-root",
				"B-hips",
				"root",
				"Root",
				"hips",
				"Hips",
				"pelvis",
				"Pelvis",
				"Armature"
			},
			CanonicalSourceAliases = new List<RetargetSlotAliasSet>(),
			FingerChainHints = new List<RetargetFingerChainHint>(),
			Notes = new List<string>
			{
				"Runtime default. Uses Rokoko-style auto detection plus manual overrides."
			}
		};
	}

	public List<RetargetSlotAssignmentState> BuildMappingState(
		CitizenRetargetJob job,
		RetargetSourceProfile sourceProfile,
		RetargetMappingProfile mappingProfile,
		RetargetSourceInspection inspection )
	{
		var aliasLookup = sourceProfile.CanonicalSourceAliases.ToDictionary( alias => alias.SlotId, StringComparer.OrdinalIgnoreCase );
		var sourceBoneLookup = inspection.BonesByName;
		var detectedCandidatesBySlot = BuildRokokoStyleAutoMapCandidates( sourceBoneLookup.Keys.ToList() );
		var states = new List<RetargetSlotAssignmentState>();

		foreach ( var rule in mappingProfile.Slots.OrderBy( slot => slot.Required ? 0 : 1 ).ThenBy( slot => slot.SlotId, StringComparer.OrdinalIgnoreCase ) )
		{
			var sourceAliasCandidates = new List<string>();
			if ( !string.IsNullOrWhiteSpace( rule.AssignedSourceBone ) )
				sourceAliasCandidates.Add( rule.AssignedSourceBone );
			if ( rule.SourceAliases is { Count: > 0 } )
				sourceAliasCandidates.AddRange( rule.SourceAliases );
			if ( aliasLookup.TryGetValue( rule.SlotId, out var aliasSet ) && aliasSet.Aliases.Count > 0 )
				sourceAliasCandidates.AddRange( aliasSet.Aliases );

			var combinedCandidates = new List<string>( sourceAliasCandidates );
			if ( detectedCandidatesBySlot.TryGetValue( rule.SlotId, out var detectedCandidates ) && detectedCandidates.Count > 0 )
				combinedCandidates.AddRange( detectedCandidates );

			var candidates = combinedCandidates
				.Where( alias => !string.IsNullOrWhiteSpace( alias ) )
				.Distinct( StringComparer.OrdinalIgnoreCase )
				.Where( alias => sourceBoneLookup.ContainsKey( alias ) )
				.ToList();

			var assignedExists = !string.IsNullOrWhiteSpace( rule.AssignedSourceBone ) && sourceBoneLookup.ContainsKey( rule.AssignedSourceBone );
			var effectiveGroup = string.IsNullOrWhiteSpace( rule.Group ) ? InferSlotGroup( rule.SlotId ) : rule.Group;
			var enabled = rule.Enabled && (job.ImportHands || !IsFingerSlot( effectiveGroup, rule.SlotId ));

			states.Add( new RetargetSlotAssignmentState
			{
				SlotId = rule.SlotId,
				TargetBone = string.IsNullOrWhiteSpace( rule.TargetBone ) ? rule.SlotId : rule.TargetBone,
				Group = effectiveGroup,
				Required = rule.Required,
				Enabled = enabled,
				Locked = rule.Locked,
				MirrorSlotId = rule.MirrorSlotId,
				AssignedSourceBone = assignedExists ? rule.AssignedSourceBone : string.Empty,
				AutoSourceBone = candidates.FirstOrDefault() ?? string.Empty,
				IsManualOverride = assignedExists,
				Confidence = assignedExists ? 1.0f : candidates.Count > 0 ? 0.85f : 0.0f,
				CandidateBones = candidates,
				SourceAliases = sourceAliasCandidates.Distinct( StringComparer.OrdinalIgnoreCase ).ToList(),
				Notes = rule.Notes ?? string.Empty
			} );
		}

		return states;
	}

	private static Dictionary<string, List<string>> BuildRokokoStyleAutoMapCandidates( IReadOnlyList<string> sourceBoneNames )
	{
		var candidatesBySlot = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );
		var standardizedBones = sourceBoneNames
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Select( name => new StandardizedBoneName( name, StandardizeRokokoBoneName( name ) ) )
			.ToList();

		void AddCandidate( string slotId, string boneName )
		{
			if ( string.IsNullOrWhiteSpace( slotId ) || string.IsNullOrWhiteSpace( boneName ) )
				return;

			if ( !candidatesBySlot.TryGetValue( slotId, out var list ) )
			{
				list = new List<string>();
				candidatesBySlot[slotId] = list;
			}

			if ( !list.Contains( boneName, StringComparer.OrdinalIgnoreCase ) )
				list.Add( boneName );
		}

		foreach ( var bone in standardizedBones )
		{
			if ( ScoreExactNameMatch( bone.Standardized, "hips", "hip", "pelvis", "waist", "lowerbody", "lower_body" ) )
				AddCandidate( "pelvis", bone.Original );
			if ( ScoreExactNameMatch( bone.Standardized, "neck", "necklower", "headneck", "neck00" ) )
				AddCandidate( "neck_0", bone.Original );
			if ( ScoreExactNameMatch( bone.Standardized, "head", "head01", "head001" ) )
				AddCandidate( "head", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "clavicle", "collar", "shoulder" ) )
				AddCandidate( "clavicle_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "clavicle", "collar", "shoulder" ) )
				AddCandidate( "clavicle_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "upperarm", "uparm", "arm" ) && !ContainsAny( bone.Standardized, "forearm", "lowerarm", "hand", "clavicle", "shoulder", "finger", "thumb" ) )
				AddCandidate( "arm_upper_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "upperarm", "uparm", "arm" ) && !ContainsAny( bone.Standardized, "forearm", "lowerarm", "hand", "clavicle", "shoulder", "finger", "thumb" ) )
				AddCandidate( "arm_upper_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "forearm", "lowerarm", "elbow" ) )
				AddCandidate( "arm_lower_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "forearm", "lowerarm", "elbow" ) )
				AddCandidate( "arm_lower_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "hand", "wrist", "palm" ) && !ContainsAny( bone.Standardized, "finger", "thumb", "index", "middle", "ring", "pinky", "prop" ) )
				AddCandidate( "hand_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "hand", "wrist", "palm" ) && !ContainsAny( bone.Standardized, "finger", "thumb", "index", "middle", "ring", "pinky", "prop" ) )
				AddCandidate( "hand_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "thigh", "upleg", "upperleg", "leg" ) && !ContainsAny( bone.Standardized, "calf", "lowerleg", "foot", "toe", "knee", "shin" ) )
				AddCandidate( "leg_upper_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "thigh", "upleg", "upperleg", "leg" ) && !ContainsAny( bone.Standardized, "calf", "lowerleg", "foot", "toe", "knee", "shin" ) )
				AddCandidate( "leg_upper_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "calf", "lowerleg", "shin", "knee", "leg" ) && !ContainsAny( bone.Standardized, "thigh", "upleg", "upperleg", "foot", "toe" ) )
				AddCandidate( "leg_lower_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "calf", "lowerleg", "shin", "knee", "leg" ) && !ContainsAny( bone.Standardized, "thigh", "upleg", "upperleg", "foot", "toe" ) )
				AddCandidate( "leg_lower_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "foot" ) && !ContainsAny( bone.Standardized, "toe", "ball" ) )
				AddCandidate( "ankle_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "foot" ) && !ContainsAny( bone.Standardized, "toe", "ball" ) )
				AddCandidate( "ankle_R", bone.Original );

			if ( MatchesSideBone( bone.Standardized, left: true, "toe", "ball" ) )
				AddCandidate( "ball_L", bone.Original );
			if ( MatchesSideBone( bone.Standardized, left: false, "toe", "ball" ) )
				AddCandidate( "ball_R", bone.Original );

			AddFingerCandidates( candidatesBySlot, bone );
		}

		var spineBones = standardizedBones
			.Where( bone => IsSpineLikeBone( bone.Standardized ) )
			.OrderBy( bone => GetSpineSortOrder( bone.Standardized ) )
			.Select( bone => bone.Original )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToList();
		if ( spineBones.Count > 0 )
			AddCandidate( "spine_0", spineBones[0] );
		if ( spineBones.Count > 1 )
			AddCandidate( "spine_1", spineBones[Math.Min( 1, spineBones.Count - 1 )] );
		if ( spineBones.Count > 2 )
			AddCandidate( "spine_2", spineBones[Math.Min( 2, spineBones.Count - 1 )] );
		else if ( spineBones.Count > 1 )
			AddCandidate( "spine_2", spineBones[^1] );

		return candidatesBySlot;
	}

	private static void AddFingerCandidates( IDictionary<string, List<string>> candidatesBySlot, StandardizedBoneName bone )
	{
		var fingerId = GetFingerId( bone.Standardized );
		if ( string.IsNullOrWhiteSpace( fingerId ) )
			return;

		var side = GetBoneSide( bone.Standardized );
		if ( side is null )
			return;

		var segment = GetFingerSegmentIndex( bone.Standardized );
		if ( segment is null )
			return;

		var slotId = $"finger_{fingerId}_{segment.Value}_{(side.Value ? "L" : "R")}";
		if ( !candidatesBySlot.TryGetValue( slotId, out var list ) )
		{
			list = new List<string>();
			candidatesBySlot[slotId] = list;
		}

		if ( !list.Contains( bone.Original, StringComparer.OrdinalIgnoreCase ) )
			list.Add( bone.Original );
	}

	private static string StandardizeRokokoBoneName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return string.Empty;

		var normalized = name.Trim()
			.Replace( ' ', '_' )
			.Replace( '-', '_' )
			.Replace( '.', '_' );

		while ( normalized.Contains( "__", StringComparison.Ordinal ) )
			normalized = normalized.Replace( "__", "_", StringComparison.Ordinal );

		foreach ( var replacement in new[]
		{
			("_", string.Empty),
			("B_", string.Empty),
			("ValveBiped_", string.Empty),
			("Valvebiped_", string.Empty),
			("Bip1_", "Bip_"),
			("Bip01_", "Bip_"),
			("Bip001_", "Bip_"),
			("Character1_", string.Empty),
			("HLP_", string.Empty),
			("JD_", string.Empty),
			("JU_", string.Empty),
			("Armature|", string.Empty),
			("Bone_", string.Empty),
			("C_", string.Empty),
			("Cf_S_", string.Empty),
			("Cf_J_", string.Empty),
			("G_", string.Empty),
			("Joint_", string.Empty),
			("DEF_", string.Empty),
			("CC_Base_", string.Empty)
		})
		{
			if ( normalized.StartsWith( replacement.Item1, StringComparison.OrdinalIgnoreCase ) )
			{
				normalized = replacement.Item2 + normalized[replacement.Item1.Length..];
				break;
			}
		}

		var split = normalized.Split( '_', StringSplitOptions.RemoveEmptyEntries );
		if ( split.Length > 1 && int.TryParse( split[0], out _ ) )
			normalized = string.Join( "_", split.Skip( 1 ) );

		if ( normalized.Contains( ':', StringComparison.Ordinal ) )
			normalized = string.Concat( normalized.Split( ':', StringSplitOptions.RemoveEmptyEntries ).Skip( 1 ) );

		if ( normalized.EndsWith( "S0", StringComparison.OrdinalIgnoreCase ) )
			normalized = normalized[..^2];
		if ( normalized.EndsWith( "_Jnt", StringComparison.OrdinalIgnoreCase ) )
			normalized = normalized[..^4];

		return normalized.ToLowerInvariant();
	}

	private static T? TryLoadJsonProfile<T>( string absolutePath ) where T : class
	{
		if ( string.IsNullOrWhiteSpace( absolutePath ) || !File.Exists( absolutePath ) )
			return null;

		try
		{
			var json = File.ReadAllText( absolutePath );
			return JsonSerializer.Deserialize<T>( json, ProfileJsonOptions );
		}
		catch
		{
			return null;
		}
	}

	private static bool ScoreExactNameMatch( string standardizedName, params string[] aliases )
	{
		foreach ( var alias in aliases )
		{
			if ( standardizedName.Equals( StandardizeRokokoBoneName( alias ), StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	private static bool MatchesSideBone( string standardizedName, bool left, params string[] aliases )
	{
		var side = GetBoneSide( standardizedName );
		if ( side is null || side.Value != left )
			return false;

		var sideTokens = left
			? new[] { "left", "_l", "l_", "l" }
			: new[] { "right", "_r", "r_", "r" };
		foreach ( var alias in aliases )
		{
			var rawCandidates = left
				? new[]
				{
					$"left{alias}",
					$"{alias}left",
					$"{alias}_l",
					$"l_{alias}",
					$"{alias}l",
					$"l{alias}"
				}
				: new[]
				{
					$"right{alias}",
					$"{alias}right",
					$"{alias}_r",
					$"r_{alias}",
					$"{alias}r",
					$"r{alias}"
				};

			if ( rawCandidates.Any( candidate =>
				standardizedName.Equals( StandardizeRokokoBoneName( candidate ), StringComparison.OrdinalIgnoreCase ) ) )
				return true;

			if ( standardizedName.Contains( alias, StringComparison.OrdinalIgnoreCase )
				&& sideTokens.Any( token => standardizedName.Contains( token, StringComparison.OrdinalIgnoreCase ) ) )
				return true;
		}

		return false;
	}

	private static bool ContainsAny( string standardizedName, params string[] fragments )
	{
		return fragments.Any( fragment => standardizedName.Contains( fragment, StringComparison.OrdinalIgnoreCase ) );
	}

	private static bool IsSpineLikeBone( string standardizedName )
	{
		return ContainsAny( standardizedName, "spine", "chest", "upperchest", "abdomen", "torso", "spineproxy" );
	}

	private static int GetSpineSortOrder( string standardizedName )
	{
		if ( standardizedName.Contains( "spineproxy", StringComparison.OrdinalIgnoreCase ) )
			return 15;
		if ( standardizedName.Contains( "upperchest", StringComparison.OrdinalIgnoreCase ) )
			return 40;
		if ( standardizedName.Contains( "chest", StringComparison.OrdinalIgnoreCase ) )
			return 30;
		if ( standardizedName.Contains( "abdomen", StringComparison.OrdinalIgnoreCase ) )
			return 5;

		var digitMatch = standardizedName.Reverse().FirstOrDefault( char.IsDigit );
		if ( digitMatch != default && char.IsDigit( digitMatch ) )
			return digitMatch - '0';

		return standardizedName.Contains( "spine", StringComparison.OrdinalIgnoreCase ) ? 10 : 20;
	}

	private static string GetFingerId( string standardizedName )
	{
		if ( ContainsAny( standardizedName, "thumb" ) )
			return "thumb";
		if ( ContainsAny( standardizedName, "indexfinger", "index" ) )
			return "index";
		if ( ContainsAny( standardizedName, "middlefinger", "middle" ) )
			return "middle";
		if ( ContainsAny( standardizedName, "ringfinger", "ring" ) )
			return "ring";
		if ( ContainsAny( standardizedName, "pinky", "littlefinger", "pinkie" ) )
			return "pinky";
		return string.Empty;
	}

	private static int? GetFingerSegmentIndex( string standardizedName )
	{
		if ( ContainsAny( standardizedName, "01", "1" ) )
			return 0;
		if ( ContainsAny( standardizedName, "02", "2" ) )
			return 1;
		if ( ContainsAny( standardizedName, "03", "3" ) )
			return 2;
		return null;
	}

	private static bool? GetBoneSide( string standardizedName )
	{
		if ( standardizedName.Contains( "left", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.Contains( "_l", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "l_", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lhand", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "larm", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lleg", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lshoulder", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lthigh", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lforearm", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lupperarm", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "lfoot", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "ltoe", StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( standardizedName.Contains( "right", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.Contains( "_r", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "r_", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rhand", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rarm", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rleg", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rshoulder", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rthigh", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rforearm", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rupperarm", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rfoot", StringComparison.OrdinalIgnoreCase )
			|| standardizedName.StartsWith( "rtoe", StringComparison.OrdinalIgnoreCase ) )
			return false;

		return null;
	}

	public RetargetDiagnosticSummary BuildDiagnostics( IReadOnlyList<RetargetSlotAssignmentState> mappingState )
	{
		var duplicateSourceBones = mappingState
			.Where( slot => slot.Enabled && !string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ) )
			.GroupBy( slot => slot.EffectiveSourceBone, StringComparer.OrdinalIgnoreCase )
			.Where( group => group.Count() > 1 && !IsAllowedSharedSourceAssignment( group ) )
			.Select( group => $"{group.Key} -> {string.Join( ", ", group.Select( slot => slot.SlotId ) )}" )
			.OrderBy( text => text, StringComparer.OrdinalIgnoreCase )
			.ToList();

		var duplicateTargetBones = mappingState
			.Where( slot => slot.Enabled && !string.IsNullOrWhiteSpace( slot.TargetBone ) )
			.GroupBy( slot => slot.TargetBone, StringComparer.OrdinalIgnoreCase )
			.Where( group => group.Count() > 1 )
			.Select( group => $"{group.Key} -> {string.Join( ", ", group.Select( slot => slot.SlotId ) )}" )
			.OrderBy( text => text, StringComparer.OrdinalIgnoreCase )
			.ToList();

		return new RetargetDiagnosticSummary
		{
			MissingRequiredSlots = mappingState
				.Where( slot => slot.Enabled && slot.Required && string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ) )
				.Select( slot => slot.SlotId )
				.OrderBy( slot => slot, StringComparer.OrdinalIgnoreCase )
				.ToList(),
			DuplicateSourceBones = duplicateSourceBones,
			DuplicateTargetBones = duplicateTargetBones,
			DisabledFingerSlots = mappingState
				.Where( slot => !slot.Enabled && IsFingerSlot( slot.Group, slot.SlotId ) )
				.Select( slot => slot.SlotId )
				.OrderBy( slot => slot, StringComparer.OrdinalIgnoreCase )
				.ToList()
		};
	}

	public string SaveJob( CitizenRetargetJob job )
	{
		EnsureSeedProfilesExist();
		var relativePath = NormalizeResourcePath( CitizenTargetProfile.DefaultJobAssetPath );
		var absolutePath = ToAbsoluteAssetPath( relativePath );
		Directory.CreateDirectory( Path.GetDirectoryName( absolutePath )! );

		var asset = AssetSystem.FindByPath( relativePath ) ?? AssetSystem.CreateResource( "crtjob", absolutePath );
		asset.SaveToDisk( PrepareJobForPersistence( job ) );
		AssetSystem.RegisterFile( absolutePath );
		return absolutePath;
	}

	public string SaveMappingProfile( RetargetMappingProfile mappingProfile, IReadOnlyList<RetargetSlotAssignmentState> mappingState, string? resourcePath = null )
	{
		var bySlot = mappingState.ToDictionary( slot => slot.SlotId, StringComparer.OrdinalIgnoreCase );
		foreach ( var rule in mappingProfile.Slots )
		{
			if ( !bySlot.TryGetValue( rule.SlotId, out var state ) )
				continue;

			rule.AssignedSourceBone = state.IsManualOverride ? state.AssignedSourceBone : string.Empty;
			rule.Enabled = state.Enabled;
			rule.Locked = state.Locked;
			if ( state.SourceAliases.Count > 0 )
				rule.SourceAliases = state.SourceAliases.ToList();
		}

		var normalized = NormalizeResourcePath( string.IsNullOrWhiteSpace( resourcePath ) ? CitizenTargetProfile.DefaultMappingProfileAssetPath : resourcePath );
		var absolute = ToAbsoluteAssetPath( normalized );
		Directory.CreateDirectory( Path.GetDirectoryName( absolute )! );
		var asset = AssetSystem.FindByPath( normalized ) ?? AssetSystem.CreateResource( "crtmap", absolute );
		asset.SaveToDisk( mappingProfile );
		AssetSystem.RegisterFile( absolute );
		return absolute;
	}

	public RetargetImportResult ImportClip(
		CitizenRetargetJob job,
		RetargetClipDescriptor clip,
		IReadOnlyList<RetargetSlotAssignmentState> mappingState,
		Vector3 sourceFacingEulerDegrees = default )
	{
		var sourceProfile = LoadSourceProfile( job.SourceProfilePath );
		var mappingProfile = LoadMappingProfile( job.MappingProfilePath );
		var diagnostics = ValidateImportDiagnostics( mappingState );
		var runtimeJob = CreateRuntimeJobSnapshot( job );
		var result = RunBackendRetargetOnly( runtimeJob, clip, sourceProfile, mappingProfile, mappingState, sourceFacingEulerDegrees );
		return FinalizeImportedResult( job, clip, result, diagnostics );
	}

	public CitizenRetargetJob CreateRuntimeJobSnapshot( CitizenRetargetJob job )
	{
		return CloneJobForRuntime( job );
	}

	public RetargetDiagnosticSummary ValidateImportDiagnostics( IReadOnlyList<RetargetSlotAssignmentState> mappingState )
	{
		var diagnostics = BuildDiagnostics( mappingState );
		if ( diagnostics.MissingRequiredSlots.Count > 0 )
			throw new InvalidOperationException( $"Missing required slots: {string.Join( ", ", diagnostics.MissingRequiredSlots )}" );
		if ( diagnostics.DuplicateSourceBones.Count > 0 )
			throw new InvalidOperationException( $"Duplicate source-bone assignments detected: {diagnostics.DuplicateSourceBones[0]}" );
		return diagnostics;
	}

	public RetargetImportResult RunBackendRetargetOnly(
		CitizenRetargetJob job,
		RetargetClipDescriptor clip,
		RetargetSourceProfile sourceProfile,
		RetargetMappingProfile mappingProfile,
		IReadOnlyList<RetargetSlotAssignmentState> mappingState,
		Vector3 sourceFacingEulerDegrees = default )
	{
		PrepareRuntimeJobForImport( job, mappingProfile );
		var result = _backend.RunRetarget( job, clip, sourceProfile, mappingProfile, mappingState, sourceFacingEulerDegrees );
		result.SequenceName = RetargetSequenceNames.Build( job.SequencePrefix, clip.DisplayName );
		result.VmdlResourcePath = job.TargetVmdlPath;
		return result;
	}

	public RetargetImportResult FinalizeImportedResult(
		CitizenRetargetJob job,
		RetargetClipDescriptor clip,
		RetargetImportResult result,
		RetargetDiagnosticSummary diagnostics )
	{
		var runtimeJob = CreateRuntimeJobSnapshot( job );
		PrepareRuntimeJobForImport( runtimeJob, null );

		var importedAnimationAbsolutePath = string.Empty;
		var vmdlAbsolutePath = string.Empty;
		if ( !string.IsNullOrWhiteSpace( result.Manifest.Outputs.ExportPath ) && File.Exists( result.Manifest.Outputs.ExportPath ) )
		{
			try
			{
				importedAnimationAbsolutePath = MaterializeImportedAnimation(
					runtimeJob.OutputAnimationFolder,
					runtimeJob.SequencePrefix,
					clip,
					result.Manifest.Outputs.ExportPath,
					result.Manifest.RetargetedActionName );
				result.OutputFormat = RetargetOutputFormat.FbxBackend;
				var generatedSources = _vmdlWriter.EnumerateSourcesFromFolder( runtimeJob.OutputAnimationFolder, runtimeJob.SequencePrefix, ".fbx" );
				ApplyModelDocSequenceFrameRange( generatedSources, runtimeJob );
				vmdlAbsolutePath = _vmdlWriter.WriteSharedVmdl( runtimeJob.TargetVmdlPath, generatedSources, NormalizeOptionalAnimGraphResourcePath( runtimeJob ) );

				var importedAnimationAsset = AssetSystem.RegisterFile( importedAnimationAbsolutePath );
				var vmdlAsset = AssetSystem.RegisterFile( vmdlAbsolutePath );
				importedAnimationAsset?.Compile( true );
				vmdlAsset?.Compile( true );
			}
			catch ( Exception exception )
			{
				result.PostImportError = $"{exception.GetType().Name}: {exception.Message}";
			}
		}

		result.GeneratedClipAbsolutePath = importedAnimationAbsolutePath;
		result.VmdlAbsolutePath = vmdlAbsolutePath;
		job.SourceFbxPath = runtimeJob.SourceFbxPath;
		job.TargetVmdlPath = runtimeJob.TargetVmdlPath;
		job.OutputAnimationFolder = runtimeJob.OutputAnimationFolder;
		job.SequencePrefix = runtimeJob.SequencePrefix;
		job.TargetPosePresetId = runtimeJob.TargetPosePresetId;
		result.JobAbsolutePath = SaveJob( UpdateJobWithResult( job, clip, result, importedAnimationAbsolutePath ) );
		result.ArtifactLinks = new RetargetArtifactLinks
		{
			RunDirectoryPath = result.Manifest.Outputs.RunDir,
			ManifestPath = result.ManifestAbsolutePath,
			ExportPath = result.Manifest.Outputs.ExportPath,
			ImportedAnimationPath = importedAnimationAbsolutePath,
			VmdlPath = vmdlAbsolutePath,
			BoneMapOverridePath = result.Manifest.Outputs.BoneMapOverridesPath
		};

		result.Log = BuildImportLog( clip, result, diagnostics, importedAnimationAbsolutePath, vmdlAbsolutePath );
		result.ArtifactLinks.RunLogPath = WriteRunLogArtifact( result.Manifest.Outputs.RunDir, result.Log );
		return result;
	}

	public void OpenResultInModelDoc( CitizenRetargetJob job )
	{
		if ( !HasImportedAnimations( job ) )
			throw new InvalidOperationException( "No imported Citizen animation sources are available yet. Run a successful retarget first." );

		EnsureGeneratedTargetExists( job );
		OpenModelAsset( job.TargetVmdlPath );
	}

	public void OpenModelAsset( string assetPath )
	{
		var relativePath = NormalizeResourcePath( assetPath );
		var asset = AssetSystem.FindByPath( relativePath );
		if ( asset is null )
			throw new FileNotFoundException( $"Unable to locate generated VMDL asset '{relativePath}'." );

		asset.OpenInEditor();
	}

	public bool HasImportedAnimations( CitizenRetargetJob job )
	{
		try
		{
			var normalizedAnimationFolder = NormalizeResourcePath( job.OutputAnimationFolder );
			var normalizedPrefix = string.IsNullOrWhiteSpace( job.SequencePrefix ) ? CitizenTargetProfile.DefaultSequencePrefix : job.SequencePrefix.Trim();
			return _vmdlWriter.EnumerateSourcesFromFolder( normalizedAnimationFolder, normalizedPrefix, ".fbx" ).Count > 0;
		}
		catch
		{
			return false;
		}
	}

	public List<GeneratedAnimationSource> GetTargetAnimationSources( CitizenRetargetJob job )
	{
		try
		{
			var normalizedAnimationFolder = NormalizeResourcePath( job.OutputAnimationFolder );
			var normalizedPrefix = string.IsNullOrWhiteSpace( job.SequencePrefix ) ? CitizenTargetProfile.DefaultSequencePrefix : job.SequencePrefix.Trim();
			var clips = _vmdlWriter.EnumerateSourcesFromFolder( normalizedAnimationFolder, normalizedPrefix, ".fbx" );
			ApplyModelDocSequenceFrameRange( clips, job );
			return clips;
		}
		catch
		{
			return new List<GeneratedAnimationSource>();
		}
	}

	public void EnsureTargetAssetExists( CitizenRetargetJob job )
	{
		EnsureGeneratedTargetExists( job );
	}

	public void RefreshTargetAssetDefinition( CitizenRetargetJob job )
	{
		var normalizedTargetVmdlPath = NormalizeResourcePath( job.TargetVmdlPath );
		var normalizedAnimationFolder = NormalizeResourcePath( job.OutputAnimationFolder );
		var normalizedPrefix = string.IsNullOrWhiteSpace( job.SequencePrefix ) ? CitizenTargetProfile.DefaultSequencePrefix : job.SequencePrefix.Trim();
		var clips = _vmdlWriter.EnumerateSourcesFromFolder( normalizedAnimationFolder, normalizedPrefix, ".fbx" );
		ApplyModelDocSequenceFrameRange( clips, job );
		var vmdlAbsolutePath = _vmdlWriter.WriteSharedVmdl( normalizedTargetVmdlPath, clips, NormalizeOptionalAnimGraphResourcePath( job ) );
		AssetSystem.RegisterFile( vmdlAbsolutePath );
	}

	public string ResolvePreviewableTargetVmdlPath( CitizenRetargetJob job )
	{
		try
		{
			var normalizedTargetVmdlPath = NormalizeResourcePath( job.TargetVmdlPath );
			var absoluteTargetVmdlPath = ToAbsoluteAssetPath( normalizedTargetVmdlPath );
			return File.Exists( absoluteTargetVmdlPath )
				? normalizedTargetVmdlPath
				: CitizenTargetProfile.CitizenBaseModelPath;
		}
		catch
		{
			return CitizenTargetProfile.CitizenBaseModelPath;
		}
	}

	public List<string> GetModelAnimationNames( string vmdlResourcePath )
	{
		if ( string.IsNullOrWhiteSpace( vmdlResourcePath ) )
			return new List<string>();

		try
		{
			var model = Model.Load( vmdlResourcePath );
			if ( model is null || model.IsError || model.AnimationCount <= 0 )
				return new List<string>();

			var names = new List<string>();
			for ( var animationIndex = 0; animationIndex < model.AnimationCount; animationIndex++ )
			{
				var name = model.GetAnimationName( animationIndex ) ?? string.Empty;
				if ( string.IsNullOrWhiteSpace( name ) )
					continue;

				if ( names.Contains( name, StringComparer.OrdinalIgnoreCase ) )
					continue;

				names.Add( name );
			}

			return names;
		}
		catch
		{
			return new List<string>();
		}
	}

	public void OpenPath( string absolutePath )
	{
		if ( string.IsNullOrWhiteSpace( absolutePath ) )
			return;

		if ( File.Exists( absolutePath ) )
		{
			EditorUtility.OpenFile( absolutePath );
			return;
		}

		if ( Directory.Exists( absolutePath ) )
		{
			EditorUtility.OpenFileFolder( absolutePath );
		}
	}

	public void Dispose()
	{
		_sourceAdapter.Dispose();
	}

	private void EnsureSeedProfilesExist()
	{
		EnsureSeedSourceProfileExists();
		EnsureSeedMappingProfileExists();
	}

	private void EnsureSeedSourceProfileExists()
	{
		var normalized = NormalizeResourcePath( CitizenTargetProfile.DefaultSourceProfileAssetPath );
		var absolute = ResolveAssetOrPluginAssetPath( normalized );
		if ( File.Exists( absolute ) )
		{
			RegisterFileIfExists( absolute );
			return;
		}

		absolute = ToAbsoluteAssetPath( normalized );
		Directory.CreateDirectory( Path.GetDirectoryName( absolute )! );
		var asset = AssetSystem.CreateResource( "crtsrc", absolute );
		asset.SaveToDisk( PrepareSourceProfileForPersistence( BuildDefaultSourceProfile() ) );
		RegisterFileIfExists( absolute );
	}

	private void SaveSourceProfile( RetargetSourceProfile sourceProfile, string resourcePath )
	{
		var normalized = NormalizeResourcePath( resourcePath );
		var absolute = ToAbsoluteAssetPath( normalized );
		Directory.CreateDirectory( Path.GetDirectoryName( absolute )! );
		var asset = AssetSystem.FindByPath( normalized ) ?? AssetSystem.CreateResource( "crtsrc", absolute );
		asset.SaveToDisk( PrepareSourceProfileForPersistence( sourceProfile ) );
		AssetSystem.RegisterFile( absolute );
	}

	private void EnsureSeedMappingProfileExists()
	{
		var normalized = NormalizeResourcePath( CitizenTargetProfile.DefaultMappingProfileAssetPath );
		var absolute = ResolveAssetOrPluginAssetPath( normalized );
		if ( File.Exists( absolute ) )
		{
			RegisterFileIfExists( absolute );
			return;
		}

		absolute = ToAbsoluteAssetPath( normalized );
		Directory.CreateDirectory( Path.GetDirectoryName( absolute )! );
		var asset = AssetSystem.CreateResource( "crtmap", absolute );
		asset.SaveToDisk( BuildDefaultMappingProfile() );
		RegisterFileIfExists( absolute );
	}

	private RetargetSourceProfile BuildDefaultSourceProfile()
	{
		var mappingPairs = ApplyCitizenFingerChainIndexShift( LoadLegacySeedMapping() );
		var aliasSets = mappingPairs
			.Select( pair => new RetargetSlotAliasSet
			{
				SlotId = pair.Value,
				Group = InferSlotGroup( pair.Value ),
				Required = IsRequiredBodySlot( pair.Value, InferSlotGroup( pair.Value ) ),
				Aliases = new List<string> { pair.Key }
			} )
			.OrderBy( alias => alias.Required ? 0 : 1 )
			.ThenBy( alias => alias.SlotId, StringComparer.OrdinalIgnoreCase )
			.ToList();

		return new RetargetSourceProfile
		{
			ProfileId = "quaternius_ual2",
			BackendSourceProfileId = "quaternius_ual2",
			DisplayName = "Quaternius UAL2 (Unity FBX)",
			SampleReferenceFbxPath = string.Empty,
			DefaultMappingProfilePath = CitizenTargetProfile.DefaultMappingProfileAssetPath,
			DefaultPoseReferenceAction = "A_TPose",
			DefaultPoseReferenceFrame = 0,
			RootBoneCandidates = new List<string> { "B-root", "B-hips", "root", "hips", "pelvis" },
			CanonicalSourceAliases = aliasSets,
			FingerChainHints = BuildFingerChainHints( mappingPairs ),
			Notes = new List<string>
			{
				"Seed profile generated from the legacy UAL2 -> Citizen mapping JSON.",
				"Core body slots are required. Neck and clavicle slots stay optional for simplified humanoid rigs.",
				"UAL2 finger chains use the Citizen 0/1/2 shift so source proximal bones skip the Citizen meta bones."
			}
		};
	}

	private RetargetMappingProfile BuildDefaultMappingProfile()
	{
		var mappingPairs = ApplyCitizenFingerChainIndexShift( LoadLegacySeedMapping() );
		var slots = mappingPairs
			.Select( pair =>
			{
				var group = InferSlotGroup( pair.Value );
				return new RetargetSlotMappingRule
				{
					SlotId = pair.Value,
					TargetBone = pair.Value,
					Group = group,
					Required = IsRequiredBodySlot( pair.Value, group ),
					Enabled = true,
					Locked = !IsFingerSlot( group, pair.Value ),
					MirrorSlotId = InferMirrorSlot( pair.Value ),
					AssignedSourceBone = pair.Key,
					SourceAliases = new List<string> { pair.Key }
				};
			} )
			.OrderBy( slot => slot.Required ? 0 : 1 )
			.ThenBy( slot => slot.SlotId, StringComparer.OrdinalIgnoreCase )
			.ToList();

		return new RetargetMappingProfile
		{
			ProfileId = "ual2_to_citizen",
			DisplayName = "UAL2 -> Citizen",
			TargetProfileId = "citizen",
			TargetPosePresetId = CitizenTargetProfile.DefaultTargetPosePresetId,
			Slots = slots,
			Notes = new List<string>
			{
				"Seed mapping generated from the legacy UAL2 -> Citizen mapping JSON.",
				"Finger chains are optional and can be toggled from the workstation UI.",
				"UAL2 finger chains map onto Citizen finger 0/1/2 segments and intentionally skip Citizen meta finger bones."
			}
		};
	}

	private static bool IsRequiredBodySlot( string slotId, string? group = null )
	{
		if ( IsFingerSlot( group ?? InferSlotGroup( slotId ), slotId ) )
			return false;

		return !slotId.Equals( "neck_0", StringComparison.OrdinalIgnoreCase )
			&& !slotId.Equals( "clavicle_L", StringComparison.OrdinalIgnoreCase )
			&& !slotId.Equals( "clavicle_R", StringComparison.OrdinalIgnoreCase );
	}

	private static bool IsAllowedSharedSourceAssignment( IGrouping<string, RetargetSlotAssignmentState> group )
	{
		var slotIds = group
			.Select( slot => slot.SlotId )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToList();

		if ( slotIds.Count < 2 )
			return true;

		return slotIds.All( slotId =>
			slotId.Equals( "spine_0", StringComparison.OrdinalIgnoreCase )
			|| slotId.Equals( "spine_1", StringComparison.OrdinalIgnoreCase )
			|| slotId.Equals( "spine_2", StringComparison.OrdinalIgnoreCase ) );
	}

	private static List<RetargetFingerChainHint> BuildFingerChainHints( IReadOnlyDictionary<string, string> mappingPairs )
	{
		return mappingPairs
			.Where( pair => pair.Value.Contains( "finger_", StringComparison.OrdinalIgnoreCase ) )
			.GroupBy( pair => pair.Value.Contains( "_L", StringComparison.OrdinalIgnoreCase ) ? "L" : "R" )
			.SelectMany( sideGroup =>
				sideGroup
					.GroupBy( pair => pair.Value.Split( '_' )[1], StringComparer.OrdinalIgnoreCase )
					.Select( fingerGroup => new RetargetFingerChainHint
					{
						HandSide = sideGroup.Key,
						FingerId = fingerGroup.Key,
						Bones = fingerGroup.Select( pair => pair.Key ).OrderBy( bone => bone, StringComparer.OrdinalIgnoreCase ).ToList()
					} ) )
			.OrderBy( hint => hint.HandSide, StringComparer.OrdinalIgnoreCase )
			.ThenBy( hint => hint.FingerId, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	private static Dictionary<string, string> LoadLegacySeedMapping()
	{
		var absolutePath = Path.Combine( CitizenRetargetPaths.DataRoot, "ual2_to_citizen_mapping.json" );
		if ( !File.Exists( absolutePath ) )
			throw new FileNotFoundException( $"Missing legacy seed mapping at '{absolutePath}'." );

		using var document = JsonDocument.Parse( File.ReadAllText( absolutePath ) );
		var mapping = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var property in document.RootElement.GetProperty( "mapping" ).EnumerateObject() )
		{
			var targetBone = property.Value.GetString();
			if ( string.IsNullOrWhiteSpace( targetBone ) )
				continue;

			mapping[property.Name] = targetBone;
		}

		return mapping;
	}

	private static Dictionary<string, string> ApplyCitizenFingerChainIndexShift( IReadOnlyDictionary<string, string> mappingPairs )
	{
		var remappedTargets = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["finger_index_meta_L"] = "finger_index_0_L",
			["finger_index_meta_R"] = "finger_index_0_R",
			["finger_index_0_L"] = "finger_index_1_L",
			["finger_index_0_R"] = "finger_index_1_R",
			["finger_index_1_L"] = "finger_index_2_L",
			["finger_index_1_R"] = "finger_index_2_R",
			["finger_middle_meta_L"] = "finger_middle_0_L",
			["finger_middle_meta_R"] = "finger_middle_0_R",
			["finger_middle_0_L"] = "finger_middle_1_L",
			["finger_middle_0_R"] = "finger_middle_1_R",
			["finger_middle_1_L"] = "finger_middle_2_L",
			["finger_middle_1_R"] = "finger_middle_2_R",
			["finger_ring_meta_L"] = "finger_ring_0_L",
			["finger_ring_meta_R"] = "finger_ring_0_R",
			["finger_ring_0_L"] = "finger_ring_1_L",
			["finger_ring_0_R"] = "finger_ring_1_R",
			["finger_ring_1_L"] = "finger_ring_2_L",
			["finger_ring_1_R"] = "finger_ring_2_R",
		};

		var shifted = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var pair in mappingPairs )
			shifted[pair.Key] = remappedTargets.TryGetValue( pair.Value, out var targetBone ) ? targetBone : pair.Value;

		return shifted;
	}

	private CitizenRetargetJob UpdateJobWithResult(
		CitizenRetargetJob job,
		RetargetClipDescriptor clip,
		RetargetImportResult result,
		string importedAnimationAbsolutePath )
	{
		job.LastManifestPath = result.ManifestAbsolutePath;
		job.SelectedClipNames = new List<string> { clip.DisplayName };
		var importedSuccessfully =
			result.Manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase )
			&& !string.IsNullOrWhiteSpace( importedAnimationAbsolutePath )
			&& File.Exists( importedAnimationAbsolutePath );
		if ( importedSuccessfully )
		{
			job.LastImportedClip = clip.DisplayName;
			job.LastSuccessfulRunId = result.Manifest.RunId;
			job.LastSuccessfulSequenceName = result.SequenceName;
		}

		job.RecentRuns ??= new List<RetargetRunHistoryEntry>();
		job.RecentRuns.Insert( 0, new RetargetRunHistoryEntry
		{
			RunId = result.Manifest.RunId,
			ClipName = clip.DisplayName,
			SequenceName = result.SequenceName,
			Status = importedSuccessfully
				? result.Manifest.Status
				: string.IsNullOrWhiteSpace( result.PostImportError ) ? result.Manifest.Status : "import_failed",
			TargetVmdlPath = job.TargetVmdlPath,
			ManifestPath = result.ManifestAbsolutePath,
			ExportPath = result.Manifest.Outputs.ExportPath,
			PreviewVideoPath = result.PreviewArtifacts.PreviewVideoPath,
			ComparisonVideoPath = result.PreviewArtifacts.ComparisonVideoPath,
			ImportedAssetPath = importedAnimationAbsolutePath,
			CreatedUtc = DateTime.UtcNow.ToString( "O" )
		} );

		if ( job.RecentRuns.Count > MaxRecentRuns )
			job.RecentRuns = job.RecentRuns.Take( MaxRecentRuns ).ToList();

		return job;
	}

	private static string FormatStageNameForLog( string stageId )
	{
		if ( string.IsNullOrWhiteSpace( stageId ) )
			return "unknown stage";

		var readable = stageId.Replace( '_', ' ' ).Replace( '-', ' ' ).Trim();
		return string.IsNullOrWhiteSpace( readable ) ? stageId : readable;
	}

	private static string BuildPipelineRunSummary( RetargetImportResult result )
	{
		var failedStage = result.Manifest.Stages.FirstOrDefault( stage => stage.Status.Equals( "failed", StringComparison.OrdinalIgnoreCase ) );
		if ( !string.IsNullOrWhiteSpace( result.PostImportError ) )
			return $"Failed during target import for '{result.SequenceName}'.";
		if ( failedStage is not null )
			return $"Failed during stage '{FormatStageNameForLog( failedStage.StageId )}' for '{result.SequenceName}'.";
		if ( result.Manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			return $"Completed with motion validation failures for '{result.SequenceName}'.";
		if ( result.Manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
			return $"Completed successfully for '{result.SequenceName}'.";

		return string.IsNullOrWhiteSpace( result.Manifest.Status )
			? $"Run '{result.SequenceName}' finished with an unknown status."
			: $"Run '{result.SequenceName}' finished with status '{result.Manifest.Status}'.";
	}

	private static string BuildPipelineNextStep( RetargetImportResult result, RetargetDiagnosticSummary diagnostics )
	{
		var failedStage = result.Manifest.Stages.FirstOrDefault( stage => stage.Status.Equals( "failed", StringComparison.OrdinalIgnoreCase ) );
		if ( !string.IsNullOrWhiteSpace( result.PostImportError ) )
			return "Open Raw Export and Manifest first, then inspect the target import step and generated asset paths.";
		if ( failedStage is not null )
			return $"Inspect the '{FormatStageNameForLog( failedStage.StageId )}' stage and its error/warnings first.";
		if ( diagnostics.MissingRequiredSlots.Count > 0 || result.Manifest.UnmappedRequiredSlots.Count > 0 )
			return "Fix the required mapping coverage first, then run the retarget again.";
		if ( result.Manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			return "Review the motion failures and verify pose preset, mapping, and root motion settings.";
		if ( result.Manifest.BackendWarnings.Count > 0 || result.Manifest.Warnings.Count > 0 )
			return "Review the warnings in this log if the animation quality looks wrong; otherwise preview the result in the generated model.";

		return "Preview the animation in the generated model. Use this log only if the result looks wrong.";
	}

	private static void AppendLogSection( List<string> lines, string title, bool addLeadingSpacing = true )
	{
		if ( addLeadingSpacing && lines.Count > 0 )
			lines.Add( string.Empty );

		lines.Add( title );
		lines.Add( new string( '-', title.Length ) );
	}

	private string BuildImportLog(
		RetargetClipDescriptor clip,
		RetargetImportResult result,
		RetargetDiagnosticSummary diagnostics,
		string importedAnimationAbsolutePath,
		string vmdlAbsolutePath )
	{
		var lines = new List<string>();
		AppendLogSection( lines, "Run Status", addLeadingSpacing: false );
		lines.AddRange(
		[
			$"Summary: {BuildPipelineRunSummary( result )}",
			$"Run ID: {result.Manifest.RunId}",
			$"Source clip: {clip.DisplayName}",
			$"Generated sequence: {result.SequenceName}",
			$"Backend status: {result.Manifest.Status}"
		] );

		if ( !string.IsNullOrWhiteSpace( result.PostImportError ) )
			lines.Add( $"Primary failure: {result.PostImportError}" );

		AppendLogSection( lines, "Prerequisites" );
		if ( !string.IsNullOrWhiteSpace( result.Manifest.SourceFile ) )
			lines.Add( $"Source file: {result.Manifest.SourceFile}" );
		if ( !string.IsNullOrWhiteSpace( result.VmdlResourcePath ) )
			lines.Add( $"Target VMDL: {result.VmdlResourcePath}" );
		if ( !string.IsNullOrWhiteSpace( result.Manifest.PoseNormalization.TargetPosePresetId ) )
			lines.Add( $"Pose preset: {result.Manifest.PoseNormalization.TargetPosePresetId}" );
		lines.Add( $"Required mapping: {result.Manifest.MappingCoverage.MappedRequiredSlotCount}/{result.Manifest.MappingCoverage.RequiredSlotCount}" );
		lines.Add( $"Optional mapping: {result.Manifest.MappingCoverage.MappedOptionalSlotCount}/{result.Manifest.MappingCoverage.OptionalSlotCount}" );
		lines.Add( $"User overrides: {result.Manifest.MappingCoverage.UserOverrideSlotCount}" );
		if ( diagnostics.DisabledFingerSlots.Count > 0 )
			lines.Add( $"Disabled finger slots: {string.Join( ", ", diagnostics.DisabledFingerSlots )}" );
		if ( result.Manifest.UnmappedRequiredSlots.Count > 0 )
			lines.Add( $"Unmapped required slots: {string.Join( ", ", result.Manifest.UnmappedRequiredSlots )}" );

		AppendLogSection( lines, "Process" );
		if ( result.Manifest.Stages.Count == 0 )
		{
			lines.Add( "- No stage data was recorded for this run." );
		}
		else
		{
			foreach ( var stage in result.Manifest.Stages )
			{
				lines.Add( $"- {FormatStageNameForLog( stage.StageId )}: {stage.Status}" );
				if ( stage.Warnings.Count > 0 )
					lines.AddRange( stage.Warnings.Select( warning => $"  warning: {warning}" ) );
				if ( !string.IsNullOrWhiteSpace( stage.Error ) )
					lines.Add( $"  error: {stage.Error}" );
			}
		}

		AppendLogSection( lines, "Outputs" );
		lines.Add( $"Run dir: {result.Manifest.Outputs.RunDir}" );
		lines.Add( $"Manifest: {result.ManifestAbsolutePath}" );
		lines.Add( $"Raw export: {(string.IsNullOrWhiteSpace( result.Manifest.Outputs.ExportPath ) ? "missing" : result.Manifest.Outputs.ExportPath)}" );
		lines.Add( $"Imported FBX: {(string.IsNullOrWhiteSpace( importedAnimationAbsolutePath ) ? "missing" : importedAnimationAbsolutePath)}" );
		lines.Add( $"Generated VMDL: {(string.IsNullOrWhiteSpace( vmdlAbsolutePath ) ? "missing" : vmdlAbsolutePath)}" );

		var warningLines = new List<string>();
		if ( result.Manifest.BackendWarnings.Count > 0 )
			warningLines.AddRange( result.Manifest.BackendWarnings.Select( warning => $"- Backend: {warning}" ) );
		if ( result.Manifest.Warnings.Count > 0 )
			warningLines.AddRange( result.Manifest.Warnings.Select( warning => $"- Run: {warning}" ) );
		if ( result.Manifest.MotionTrajectoryAnalysis.Warnings.Count > 0 )
			warningLines.AddRange( result.Manifest.MotionTrajectoryAnalysis.Warnings.Select( warning => $"- Motion warning: {warning}" ) );
		if ( result.Manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			warningLines.AddRange( result.Manifest.MotionTrajectoryAnalysis.Failures.Select( failure => $"- Motion failure: {failure}" ) );

		if ( warningLines.Count > 0 )
		{
			AppendLogSection( lines, "Warnings" );
			lines.AddRange( warningLines );
		}

		AppendLogSection( lines, "What To Check Next" );
		lines.Add( BuildPipelineNextStep( result, diagnostics ) );

		if ( !string.IsNullOrWhiteSpace( result.BackendInvocation.BlenderExecutablePath ) )
		{
			AppendLogSection( lines, "Log Context" );
			lines.Add( $"Blender executable: {result.BackendInvocation.BlenderExecutablePath}" );
		}

		return string.Join( Environment.NewLine, lines );
	}

	private static string WriteRunLogArtifact( string runDirectoryPath, string log )
	{
		if ( string.IsNullOrWhiteSpace( runDirectoryPath ) || string.IsNullOrWhiteSpace( log ) )
			return string.Empty;

		try
		{
			Directory.CreateDirectory( runDirectoryPath );
			var logPath = Path.Combine( runDirectoryPath, CitizenRetargetPaths.EditorRunLogFileName );
			File.WriteAllText( logPath, log );
			return logPath;
		}
		catch
		{
			return string.Empty;
		}
	}

	private string MaterializeImportedAnimation(
		string animationFolder,
		string sequencePrefix,
		RetargetClipDescriptor clip,
		string exportPath,
		string sourceActionName )
	{
		if ( string.IsNullOrWhiteSpace( exportPath ) || !File.Exists( exportPath ) )
			throw new FileNotFoundException( $"The backend export FBX does not exist: '{exportPath}'." );

		var sequenceName = RetargetSequenceNames.Build( sequencePrefix, clip.DisplayName );
		var outputAnimationFolder = NormalizeResourcePath( $"{animationFolder}/anims" );
		DeleteStaleGeneratedAnimationVariants( outputAnimationFolder, sequenceName );
		var absoluteFolder = CitizenRetargetPaths.GetAssetAbsolutePath( outputAnimationFolder );
		Directory.CreateDirectory( absoluteFolder );
		var absolutePath = Path.Combine( absoluteFolder, $"{sequenceName}.fbx" );
		return _dmxBridge.ExportSolvedCitizenFbxToTemplateFbx( exportPath, sequenceName, absolutePath, sourceActionName );
	}

	private void EnsureGeneratedTargetExists( CitizenRetargetJob job )
	{
		var normalizedTargetVmdlPath = NormalizeResourcePath( job.TargetVmdlPath );
		var normalizedAnimationFolder = NormalizeResourcePath( job.OutputAnimationFolder );
		var normalizedPrefix = string.IsNullOrWhiteSpace( job.SequencePrefix ) ? CitizenTargetProfile.DefaultSequencePrefix : job.SequencePrefix.Trim();
		var clips = _vmdlWriter.EnumerateSourcesFromFolder( normalizedAnimationFolder, normalizedPrefix, ".fbx" );
		ApplyModelDocSequenceFrameRange( clips, job );
		var vmdlAbsolutePath = _vmdlWriter.WriteSharedVmdl( normalizedTargetVmdlPath, clips, NormalizeOptionalAnimGraphResourcePath( job ) );
		AssetSystem.RegisterFile( vmdlAbsolutePath )?.Compile( true );
	}

	private static void ApplyModelDocSequenceFrameRange( List<GeneratedAnimationSource> clips, CitizenRetargetJob job )
	{
		if ( clips.Count == 0 )
			return;
		if ( job.ModelDocSequenceStartFrame < 0 || job.ModelDocSequenceEndFrame < 0 )
			return;
		if ( job.ModelDocSequenceEndFrame < job.ModelDocSequenceStartFrame )
			return;
		foreach ( var clip in clips )
		{
			clip.StartFrame = job.ModelDocSequenceStartFrame;
			clip.EndFrame = job.ModelDocSequenceEndFrame;
		}
	}

	private static void DeleteStaleGeneratedAnimationVariants( string outputAnimationFolder, string sequenceName )
	{
		var absoluteFolder = CitizenRetargetPaths.GetAssetAbsolutePath( outputAnimationFolder );
		Directory.CreateDirectory( absoluteFolder );
		foreach ( var extension in new[] { ".fbx", ".smd", ".dmx" } )
		{
			var candidate = Path.Combine( absoluteFolder, $"{sequenceName}{extension}" );
			if ( File.Exists( candidate ) )
				File.Delete( candidate );
		}
	}

	private void PrepareRuntimeJobForImport( CitizenRetargetJob job, RetargetMappingProfile? mappingProfile )
	{
		job.SourceFbxPath = NormalizeSourceFilePath( job.SourceFbxPath );
		job.TargetVmdlPath = NormalizeResourcePath( job.TargetVmdlPath );
		job.OutputAnimationFolder = NormalizeResourcePath( job.OutputAnimationFolder );
		job.SequencePrefix = string.IsNullOrWhiteSpace( job.SequencePrefix ) ? CitizenTargetProfile.DefaultSequencePrefix : job.SequencePrefix.Trim();
		job.TargetPosePresetId = string.IsNullOrWhiteSpace( job.TargetPosePresetId )
			? (mappingProfile?.TargetPosePresetId ?? CitizenTargetProfile.DefaultTargetPosePresetId)
			: job.TargetPosePresetId.Trim();
	}

	private static string SanitizeResourceToken( string value )
	{
		var token = string.IsNullOrWhiteSpace( value ) ? "default" : value.Trim().TrimEnd( '_' );
		if ( string.IsNullOrWhiteSpace( token ) )
			token = "default";

		var builder = new StringBuilder();
		foreach ( var character in token )
			builder.Append( char.IsLetterOrDigit( character ) || character is '_' or '-' ? character : '_' );

		return builder.ToString();
	}

	private static CitizenRetargetJob PrepareJobForPersistence( CitizenRetargetJob source )
	{
		return new CitizenRetargetJob
		{
			SourceFbxPath = CitizenRetargetPaths.EncodeExternalPath( source.SourceFbxPath ),
			SourceProfilePath = source.SourceProfilePath,
			MappingProfilePath = source.MappingProfilePath,
			TargetVmdlPath = source.TargetVmdlPath,
			TargetAnimGraphPath = source.TargetAnimGraphPath,
			OutputAnimationFolder = source.OutputAnimationFolder,
			SequencePrefix = source.SequencePrefix,
			TargetPosePresetId = source.TargetPosePresetId,
			RootMotionMode = source.RootMotionMode,
			RootMotionReferenceFrame = source.RootMotionReferenceFrame,
			RootMotionVerticalNudge = source.RootMotionVerticalNudge,
			ModelDocSequenceStartFrame = source.ModelDocSequenceStartFrame,
			ModelDocSequenceEndFrame = source.ModelDocSequenceEndFrame,
			ImportHands = source.ImportHands,
			SelectedClipNames = source.SelectedClipNames?.ToList() ?? new List<string>(),
			LastImportedClip = source.LastImportedClip,
			LastSuccessfulRunId = source.LastSuccessfulRunId,
			LastSuccessfulSequenceName = source.LastSuccessfulSequenceName,
			LastManifestPath = CitizenRetargetPaths.EncodeExternalPath( source.LastManifestPath ),
			RecentRuns = source.RecentRuns?.Select( PrepareHistoryForPersistence ).ToList() ?? new List<RetargetRunHistoryEntry>()
		};
	}

	private static CitizenRetargetJob CloneJobForRuntime( CitizenRetargetJob source )
	{
		return new CitizenRetargetJob
		{
			SourceFbxPath = CitizenRetargetPaths.DecodeExternalPath( source.SourceFbxPath ),
			SourceProfilePath = source.SourceProfilePath,
			MappingProfilePath = source.MappingProfilePath,
			TargetVmdlPath = source.TargetVmdlPath,
			TargetAnimGraphPath = source.TargetAnimGraphPath,
			OutputAnimationFolder = source.OutputAnimationFolder,
			SequencePrefix = source.SequencePrefix,
			TargetPosePresetId = source.TargetPosePresetId,
			RootMotionMode = source.RootMotionMode,
			RootMotionReferenceFrame = source.RootMotionReferenceFrame,
			RootMotionVerticalNudge = source.RootMotionVerticalNudge,
			ModelDocSequenceStartFrame = source.ModelDocSequenceStartFrame,
			ModelDocSequenceEndFrame = source.ModelDocSequenceEndFrame,
			ImportHands = source.ImportHands,
			SelectedClipNames = source.SelectedClipNames?.ToList() ?? new List<string>(),
			LastImportedClip = source.LastImportedClip,
			LastSuccessfulRunId = source.LastSuccessfulRunId,
			LastSuccessfulSequenceName = source.LastSuccessfulSequenceName,
			LastManifestPath = CitizenRetargetPaths.DecodeExternalPath( source.LastManifestPath ),
			RecentRuns = source.RecentRuns?.Select( CloneHistoryForRuntime ).ToList() ?? new List<RetargetRunHistoryEntry>()
		};
	}

	private static RetargetSourceProfile PrepareSourceProfileForPersistence( RetargetSourceProfile source )
	{
		return new RetargetSourceProfile
		{
			ProfileId = source.ProfileId,
			BackendSourceProfileId = source.BackendSourceProfileId,
			DisplayName = source.DisplayName,
			SampleReferenceFbxPath = CitizenRetargetPaths.EncodeExternalPath( source.SampleReferenceFbxPath ),
			DefaultMappingProfilePath = source.DefaultMappingProfilePath,
			DefaultPoseReferenceAction = source.DefaultPoseReferenceAction,
			DefaultPoseReferenceFrame = source.DefaultPoseReferenceFrame,
			RootBoneCandidates = source.RootBoneCandidates?.ToList() ?? new List<string>(),
			CanonicalSourceAliases = source.CanonicalSourceAliases?.Select( CloneAliasSet ).ToList() ?? new List<RetargetSlotAliasSet>(),
			FingerChainHints = source.FingerChainHints?.Select( CloneFingerHint ).ToList() ?? new List<RetargetFingerChainHint>(),
			Notes = source.Notes?.ToList() ?? new List<string>()
		};
	}

	private static RetargetSourceProfile CloneSourceProfileForRuntime( RetargetSourceProfile source )
	{
		return new RetargetSourceProfile
		{
			ProfileId = source.ProfileId,
			BackendSourceProfileId = source.BackendSourceProfileId,
			DisplayName = source.DisplayName,
			SampleReferenceFbxPath = CitizenRetargetPaths.DecodeExternalPath( source.SampleReferenceFbxPath ),
			DefaultMappingProfilePath = source.DefaultMappingProfilePath,
			DefaultPoseReferenceAction = source.DefaultPoseReferenceAction,
			DefaultPoseReferenceFrame = source.DefaultPoseReferenceFrame,
			RootBoneCandidates = source.RootBoneCandidates?.ToList() ?? new List<string>(),
			CanonicalSourceAliases = source.CanonicalSourceAliases?.Select( CloneAliasSet ).ToList() ?? new List<RetargetSlotAliasSet>(),
			FingerChainHints = source.FingerChainHints?.Select( CloneFingerHint ).ToList() ?? new List<RetargetFingerChainHint>(),
			Notes = source.Notes?.ToList() ?? new List<string>()
		};
	}

	private static RetargetRunHistoryEntry PrepareHistoryForPersistence( RetargetRunHistoryEntry entry )
	{
		return new RetargetRunHistoryEntry
		{
			RunId = entry.RunId,
			ClipName = entry.ClipName,
			SequenceName = entry.SequenceName,
			Status = entry.Status,
			TargetVmdlPath = entry.TargetVmdlPath,
			ManifestPath = CitizenRetargetPaths.EncodeExternalPath( entry.ManifestPath ),
			ExportPath = CitizenRetargetPaths.EncodeExternalPath( entry.ExportPath ),
			PreviewVideoPath = CitizenRetargetPaths.EncodeExternalPath( entry.PreviewVideoPath ),
			ComparisonVideoPath = CitizenRetargetPaths.EncodeExternalPath( entry.ComparisonVideoPath ),
			ImportedAssetPath = CitizenRetargetPaths.EncodeExternalPath( entry.ImportedAssetPath ),
			CreatedUtc = entry.CreatedUtc
		};
	}

	private static RetargetRunHistoryEntry CloneHistoryForRuntime( RetargetRunHistoryEntry entry )
	{
		return new RetargetRunHistoryEntry
		{
			RunId = entry.RunId,
			ClipName = entry.ClipName,
			SequenceName = entry.SequenceName,
			Status = entry.Status,
			TargetVmdlPath = entry.TargetVmdlPath,
			ManifestPath = CitizenRetargetPaths.DecodeExternalPath( entry.ManifestPath ),
			ExportPath = CitizenRetargetPaths.DecodeExternalPath( entry.ExportPath ),
			PreviewVideoPath = CitizenRetargetPaths.DecodeExternalPath( entry.PreviewVideoPath ),
			ComparisonVideoPath = CitizenRetargetPaths.DecodeExternalPath( entry.ComparisonVideoPath ),
			ImportedAssetPath = CitizenRetargetPaths.DecodeExternalPath( entry.ImportedAssetPath ),
			CreatedUtc = entry.CreatedUtc
		};
	}

	private static RetargetSlotAliasSet CloneAliasSet( RetargetSlotAliasSet alias )
	{
		return new RetargetSlotAliasSet
		{
			SlotId = alias.SlotId,
			Group = alias.Group,
			Required = alias.Required,
			Aliases = alias.Aliases?.ToList() ?? new List<string>()
		};
	}

	private static RetargetFingerChainHint CloneFingerHint( RetargetFingerChainHint hint )
	{
		return new RetargetFingerChainHint
		{
			FingerId = hint.FingerId,
			HandSide = hint.HandSide,
			Bones = hint.Bones?.ToList() ?? new List<string>()
		};
	}

	private static string? NormalizeOptionalAnimGraphResourcePath( CitizenRetargetJob job )
	{
		if ( string.IsNullOrWhiteSpace( job.TargetAnimGraphPath ) )
			return null;

		return NormalizeResourcePath( job.TargetAnimGraphPath.Trim() );
	}

	private static bool NeedsJobPersistenceMigration( CitizenRetargetJob job )
	{
		return IsPlainAbsolutePath( job.SourceFbxPath )
			|| IsPlainAbsolutePath( job.LastManifestPath )
			|| (job.RecentRuns?.Any( entry =>
				IsPlainAbsolutePath( entry.ManifestPath )
				|| IsPlainAbsolutePath( entry.ExportPath )
				|| IsPlainAbsolutePath( entry.PreviewVideoPath )
				|| IsPlainAbsolutePath( entry.ComparisonVideoPath )
				|| IsPlainAbsolutePath( entry.ImportedAssetPath )) ?? false);
	}

	private static bool NeedsSourceProfilePersistenceMigration( RetargetSourceProfile sourceProfile )
	{
		return IsPlainAbsolutePath( sourceProfile.SampleReferenceFbxPath );
	}

	private static bool IsPlainAbsolutePath( string? path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		var trimmed = path.Trim();
		return Path.IsPathRooted( trimmed ) && !trimmed.StartsWith( "__external__:", StringComparison.Ordinal );
	}

	private static string InferSlotGroup( string slotId )
	{
		return slotId.Contains( "finger_", StringComparison.OrdinalIgnoreCase ) ? "fingers" : "body";
	}

	private static bool IsFingerSlot( string? group, string slotId )
	{
		return (group ?? string.Empty).Contains( "finger", StringComparison.OrdinalIgnoreCase )
			|| slotId.Contains( "finger_", StringComparison.OrdinalIgnoreCase );
	}

	private static string InferMirrorSlot( string slotId )
	{
		if ( slotId.EndsWith( "_L", StringComparison.OrdinalIgnoreCase ) )
			return slotId[..^2] + "_R";
		if ( slotId.EndsWith( "_R", StringComparison.OrdinalIgnoreCase ) )
			return slotId[..^2] + "_L";
		return string.Empty;
	}

	private static string NormalizeSourceFilePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			throw new InvalidOperationException( "Source FBX path is empty." );

		var normalized = CitizenRetargetPaths.DecodeExternalPath( path ).Trim().Trim( '"' );
		if ( !Path.IsPathRooted( normalized ) )
			normalized = Path.GetFullPath( Path.Combine( CitizenRetargetPaths.ProjectRoot, normalized ) );

		if ( !File.Exists( normalized ) )
			throw new FileNotFoundException( $"Could not find source FBX '{normalized}'." );

		return normalized;
	}

	private static string NormalizeResourcePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			throw new InvalidOperationException( "Expected a non-empty asset path." );

		var normalized = path.Replace( '\\', '/' ).Trim();
		if ( Path.IsPathRooted( normalized ) )
		{
			var assetsRoot = CitizenRetargetPaths.ProjectAssetsRoot.Replace( '\\', '/' );
			if ( normalized.StartsWith( assetsRoot, StringComparison.OrdinalIgnoreCase ) )
				return normalized[assetsRoot.Length..].TrimStart( '/' );

			var pluginAssetsRoot = CitizenRetargetPaths.PluginAssetsRoot.Replace( '\\', '/' );
			if ( normalized.StartsWith( pluginAssetsRoot, StringComparison.OrdinalIgnoreCase ) )
				return normalized[pluginAssetsRoot.Length..].TrimStart( '/' );

			throw new InvalidOperationException( $"Asset path '{path}' is outside the project Assets folder." );
		}

		if ( normalized.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
			return normalized["Assets/".Length..];

		return normalized.TrimStart( '/' );
	}

	private static string ToAbsoluteAssetPath( string relativeAssetPath )
	{
		return CitizenRetargetPaths.GetAssetAbsolutePath( relativeAssetPath );
	}

	private static string ResolveAssetOrPluginAssetPath( string relativeAssetPath )
	{
		var projectPath = CitizenRetargetPaths.GetAssetAbsolutePath( relativeAssetPath );
		if ( File.Exists( projectPath ) )
			return projectPath;

		var pluginPath = CitizenRetargetPaths.GetPluginAssetAbsolutePath( relativeAssetPath );
		if ( File.Exists( pluginPath ) )
			return pluginPath;

		return projectPath;
	}

	private static void RegisterFileIfExists( string absolutePath )
	{
		if ( string.IsNullOrWhiteSpace( absolutePath ) || !File.Exists( absolutePath ) )
			return;

		try
		{
			AssetSystem.RegisterFile( absolutePath );
		}
		catch
		{
			// Library files may already be mounted. JSON fallback loading still works.
		}
	}
}
