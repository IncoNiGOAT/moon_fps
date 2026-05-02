using System.Collections.Generic;

namespace Sandbox;

[AssetType( Name = "Retarget Source Profile", Extension = "crtsrc", Category = "Tools" )]
public sealed class RetargetSourceProfile : GameResource
{
	[Property] public string ProfileId { get; set; } = "generic_humanoid";
	[Property] public string BackendSourceProfileId { get; set; } = "generic_humanoid";
	[Property] public string DisplayName { get; set; } = "Generic Humanoid";
	[Property] public string SampleReferenceFbxPath { get; set; } = string.Empty;
	[Property] public string DefaultMappingProfilePath { get; set; } = "tools/citizen_retarget/profiles/ual2_to_citizen.crtmap";
	[Property] public string DefaultPoseReferenceAction { get; set; } = string.Empty;
	[Property] public int DefaultPoseReferenceFrame { get; set; }
	[Property] public List<string> RootBoneCandidates { get; set; } = new();
	[Property] public List<RetargetSlotAliasSet> CanonicalSourceAliases { get; set; } = new();
	[Property] public List<RetargetFingerChainHint> FingerChainHints { get; set; } = new();
	[Property] public List<string> Notes { get; set; } = new();
}

[AssetType( Name = "Retarget Mapping Profile", Extension = "crtmap", Category = "Tools" )]
public sealed class RetargetMappingProfile : GameResource
{
	[Property] public string ProfileId { get; set; } = "ual2_to_citizen";
	[Property] public string DisplayName { get; set; } = "Source -> Citizen";
	[Property] public string TargetProfileId { get; set; } = "citizen";
	[Property] public string TargetPosePresetId { get; set; } = "citizen_t_pose_calibrated_v1";
	[Property] public List<RetargetSlotMappingRule> Slots { get; set; } = new();
	[Property] public List<string> ValidationSuppressions { get; set; } = new();
	[Property] public List<string> Notes { get; set; } = new();
}

public sealed class RetargetSlotAliasSet
{
	[Property] public string SlotId { get; set; } = string.Empty;
	[Property] public string Group { get; set; } = "body";
	[Property] public bool Required { get; set; }
	[Property] public List<string> Aliases { get; set; } = new();
}

public sealed class RetargetFingerChainHint
{
	[Property] public string FingerId { get; set; } = string.Empty;
	[Property] public string HandSide { get; set; } = string.Empty;
	[Property] public List<string> Bones { get; set; } = new();
}

public sealed class RetargetSlotMappingRule
{
	[Property] public string SlotId { get; set; } = string.Empty;
	[Property] public string TargetBone { get; set; } = string.Empty;
	[Property] public string Group { get; set; } = "body";
	[Property] public bool Required { get; set; }
	[Property] public bool Enabled { get; set; } = true;
	[Property] public bool Locked { get; set; }
	[Property] public string MirrorSlotId { get; set; } = string.Empty;
	[Property] public string AssignedSourceBone { get; set; } = string.Empty;
	[Property] public List<string> SourceAliases { get; set; } = new();
	[Property] public string Notes { get; set; } = string.Empty;
}

public sealed class RetargetRunHistoryEntry
{
	[Property] public string RunId { get; set; } = string.Empty;
	[Property] public string ClipName { get; set; } = string.Empty;
	[Property] public string SequenceName { get; set; } = string.Empty;
	[Property] public string Status { get; set; } = string.Empty;
	[Property] public string TargetVmdlPath { get; set; } = string.Empty;
	[Property] public string ManifestPath { get; set; } = string.Empty;
	[Property] public string ExportPath { get; set; } = string.Empty;
	[Property] public string PreviewVideoPath { get; set; } = string.Empty;
	[Property] public string ComparisonVideoPath { get; set; } = string.Empty;
	[Property] public string ImportedAssetPath { get; set; } = string.Empty;
	[Property] public string CreatedUtc { get; set; } = string.Empty;
}
