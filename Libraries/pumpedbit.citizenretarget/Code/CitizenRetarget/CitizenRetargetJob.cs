using System.Collections.Generic;

namespace Sandbox;

[AssetType( Name = "CARL Retarget Job", Extension = "crtjob", Category = "Tools" )]
public sealed class CitizenRetargetJob : GameResource
{
	[Property] public string SourceFbxPath { get; set; } = string.Empty;
	[Property] public string SourceProfilePath { get; set; } = string.Empty;
	[Property] public string MappingProfilePath { get; set; } = "tools/citizen_retarget/profiles/ual2_to_citizen.crtmap";
	[Property] public string TargetVmdlPath { get; set; } = "models/citizen_custom/citizen_retarget.vmdl";
	[Property] public string TargetAnimGraphPath { get; set; } = string.Empty;
	[Property] public string OutputAnimationFolder { get; set; } = "models/citizen_custom/animations/citizen_retarget";
	[Property] public string SequencePrefix { get; set; } = "citizen_retarget_";
	[Property] public string TargetPosePresetId { get; set; } = "citizen_t_pose_calibrated_v1";
	[Property] public CitizenRetargetRootMotionMode RootMotionMode { get; set; } = CitizenRetargetRootMotionMode.Keep;
	/// <summary> Blender frame used as horizontal (and Fully-In-Place) reference for the pelvis. -1 = first frame of the baked clip. Pick a frame where the feet are on the ground if the clip starts in the air. </summary>
	[Property] public int RootMotionReferenceFrame { get; set; } = -1;
	/// <summary> Added to pelvis world Z after root policy (Blender space, Z is up). Use a small negative value (e.g. -0.02 to -0.05) to sink the whole animation toward the floor without opening Blender. Does not remove jump height deltas when using In Place. </summary>
	[Property] public float RootMotionVerticalNudge { get; set; }
	/// <summary> Inclusive start frame for every generated AnimFile on the target VMDL (FBX timeline). -1 with <see cref="ModelDocSequenceEndFrame"/> -1 = use full clip. Example: 50 with end 66 plays only those frames. </summary>
	[Property] public int ModelDocSequenceStartFrame { get; set; } = -1;
	/// <summary> Inclusive end frame for AnimFile entries. Must be &gt;= <see cref="ModelDocSequenceStartFrame"/> when both are set. </summary>
	[Property] public int ModelDocSequenceEndFrame { get; set; } = -1;
	[Property] public bool ImportHands { get; set; } = true;
	[Property] public bool GeneratePreviewVideo { get; set; }
	[Property] public bool GenerateComparisonVideo { get; set; }
	[Property] public bool AutoOpenModelDoc { get; set; }
	[Property] public List<string> SelectedClipNames { get; set; } = new();
	[Property] public string LastImportedClip { get; set; } = string.Empty;
	[Property] public string LastSuccessfulRunId { get; set; } = string.Empty;
	[Property] public string LastSuccessfulSequenceName { get; set; } = string.Empty;
	[Property] public string LastManifestPath { get; set; } = string.Empty;
	[Property] public List<RetargetRunHistoryEntry> RecentRuns { get; set; } = new();
}
