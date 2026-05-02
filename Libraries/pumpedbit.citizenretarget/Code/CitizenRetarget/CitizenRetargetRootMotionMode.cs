namespace Sandbox;

public enum CitizenRetargetRootMotionMode
{
	/// <summary> Source root translation is written to the target pelvis (full root motion). </summary>
	Keep,
	/// <summary> Horizontal root translation removed; vertical (Z) kept for crouch/bounce in the clip. </summary>
	InPlace,
	/// <summary> No root translation from the clip — pelvis stays at rest translation; only rotations apply. </summary>
	FullyInPlace,
}
