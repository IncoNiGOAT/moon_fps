using Sandbox;
using System;

/// <summary>
/// Wall jump unique par saut (réinitialisé au sol) + IK mains / pieds collés au mur en l’air.
/// À placer sur le même GameObject que <see cref="PlayerController"/> (souvent la racine joueur).
/// </summary>
[Title( "Wall Jump & Wall Cling (IK)" )]
[Category( "Player" )]
[Icon( "directions_run" )]
public sealed class WallJumpAndWallCling : Component
{
	[Property, Group( "Wall jump" )]
	public float WallCheckDistance { get; set; } = 42f;

	[Property, Group( "Wall jump" )]
	public float TraceRadius { get; set; } = 14f;

	[Property, Group( "Wall jump" ), Range( 0f, 0.9f )]
	[Description( "Mur quasi vertical : |normal.z| doit rester sous ce seuil." )]
	public float MaxWallSlopeZ { get; set; } = 0.35f;

	[Property, Group( "Wall jump" )]
	public float WallJumpHorizontalSpeed { get; set; } = 320f;

	[Property, Group( "Wall jump" )]
	public float WallJumpUpSpeed { get; set; } = 220f;

	[Property, Group( "Wall jump" )]
	[Description( "Si 1, pousse selon -HitNormal ; si le saut part dans le mur, mets -1." )]
	public float WallNormalPushSign { get; set; } = 1f;

	[Property, Group( "Wall jump" )]
	public float ChestTraceHeight { get; set; } = 48f;

	[Property, Group( "IK — noms AnimGraph" )]
	public string IkLeftHand { get; set; } = "hand_left";

	[Property, Group( "IK — noms AnimGraph" )]
	public string IkRightHand { get; set; } = "hand_right";

	[Property, Group( "IK — noms AnimGraph" )]
	public string IkLeftFoot { get; set; } = "foot_left";

	[Property, Group( "IK — noms AnimGraph" )]
	public string IkRightFoot { get; set; } = "foot_right";

	[Property, Group( "IK" )]
	public SkinnedModelRenderer BodyRenderer { get; set; }

	[Property, Group( "IK" )]
	public bool EnableWallClingIk { get; set; } = true;

	[Property, Group( "IK" )]
	public float HandSpread { get; set; } = 28f;

	[Property, Group( "IK" )]
	public float HandHeightOffset { get; set; } = 12f;

	[Property, Group( "IK" )]
	public float FootSpread { get; set; } = 22f;

	[Property, Group( "IK" )]
	public float FootHeightOffset { get; set; } = -8f;

	[Property, Group( "IK" )]
	public float IkWallOffset { get; set; } = 4f;

	private PlayerController _pc;
	private bool _wallJumpAvailable = true;
	private Vector3 _lastWallNormal;
	private bool _hasWallContact;

	protected override void OnUpdate()
	{
		_pc ??= Components.Get<PlayerController>() ?? Components.GetInChildren<PlayerController>( true );
		if ( _pc is null || !_pc.UseInputControls )
			return;

		if ( !_pc.Body.IsValid() || !_pc.Body.MotionEnabled )
			return;

		if ( _pc.IsOnGround )
		{
			_wallJumpAvailable = true;
			_hasWallContact = false;
			ClearAllIk();
			return;
		}

		UpdateWallContact();
		UpdateWallClingIk();

		if ( !Input.Pressed( "Jump" ) )
			return;

		if ( !_wallJumpAvailable || !_hasWallContact )
			return;

		var away = (_lastWallNormal * WallNormalPushSign).WithZ( 0 );
		if ( away.Length < 0.05f )
			return;
		away = away.Normal;

		var impulse = away * WallJumpHorizontalSpeed + Vector3.Up * WallJumpUpSpeed;
		_pc.Jump( impulse );
		_wallJumpAvailable = false;

		try
		{
			Input.ReleaseAction( "Jump" );
		}
		catch
		{
			/* certaines versions / contextes */
		}
	}

	private void UpdateWallContact()
	{
		_hasWallContact = false;
		var chest = WorldPosition + Vector3.Up * ChestTraceHeight;
		var yaw = _pc.EyeAngles.yaw;
		var basis = Rotation.FromYaw( yaw );
		var dirs = new[]
		{
			basis * Vector3.Forward,
			basis * Vector3.Right,
			basis * -Vector3.Forward,
			basis * -Vector3.Right
		};

		var bestDist = float.MaxValue;
		Vector3 bestNormal = default;

		foreach ( var dir in dirs )
		{
			var flat = dir.WithZ( 0 ).Normal;
			if ( flat.Length < 0.01f )
				continue;

			var tr = Scene.Trace
				.Sphere( TraceRadius, chest, chest + flat * WallCheckDistance )
				.IgnoreGameObject( GameObject )
				.Run();

			if ( !tr.Hit )
				continue;

			if ( MathF.Abs( tr.Normal.z ) > MaxWallSlopeZ )
				continue;

			if ( tr.Distance < bestDist )
			{
				bestDist = tr.Distance;
				bestNormal = tr.Normal;
			}
		}

		if ( bestDist < float.MaxValue )
		{
			_hasWallContact = true;
			_lastWallNormal = bestNormal.WithZ( 0 ).Normal;
			if ( _lastWallNormal.Length < 0.05f )
				_hasWallContact = false;
		}
	}

	private void UpdateWallClingIk()
	{
		if ( !EnableWallClingIk )
			return;

		var skinned = BodyRenderer
			?? ( _pc.Renderer as SkinnedModelRenderer )
			?? Components.GetInChildren<SkinnedModelRenderer>( true );

		if ( skinned is null || !skinned.IsValid() || !skinned.UseAnimGraph )
			return;

		if ( !_hasWallContact )
		{
			ClearIkOn( skinned );
			return;
		}

		var n = _lastWallNormal;
		var chest = WorldPosition + Vector3.Up * ( ChestTraceHeight + HandHeightOffset );
		var hip = WorldPosition + Vector3.Up * ( ChestTraceHeight * 0.35f + FootHeightOffset );
		var right = Vector3.Cross( Vector3.Up, n ).Normal;
		if ( right.Length < 0.1f )
			right = Vector3.Right;

		var wallPointChest = ProjectToWall( chest, n );
		var wallPointHip = ProjectToWall( hip, n );

		var handL = wallPointChest - right * HandSpread * 0.5f + n * IkWallOffset;
		var handR = wallPointChest + right * HandSpread * 0.5f + n * IkWallOffset;
		var footL = wallPointHip - right * FootSpread * 0.35f + n * IkWallOffset;
		var footR = wallPointHip + right * FootSpread * 0.35f + n * IkWallOffset;

		var palmRot = Rotation.LookAt( n, Vector3.Up );

		skinned.SetIk( IkLeftHand, new Transform( handL, palmRot ) );
		skinned.SetIk( IkRightHand, new Transform( handR, palmRot ) );
		skinned.SetIk( IkLeftFoot, new Transform( footL, palmRot ) );
		skinned.SetIk( IkRightFoot, new Transform( footR, palmRot ) );
	}

	private Vector3 ProjectToWall( Vector3 worldPoint, Vector3 wallNormalFlat )
	{
		var n = wallNormalFlat.Normal;
		var chest = WorldPosition + Vector3.Up * ChestTraceHeight;
		var tr = Scene.Trace
			.Ray( worldPoint + n * 24f, worldPoint - n * WallCheckDistance * 2f )
			.IgnoreGameObject( GameObject )
			.Run();

		if ( tr.Hit )
			return tr.HitPosition + n * IkWallOffset;

		return worldPoint + n * IkWallOffset;
	}

	private void ClearAllIk()
	{
		var skinned = BodyRenderer
			?? ( _pc?.Renderer as SkinnedModelRenderer )
			?? Components.GetInChildren<SkinnedModelRenderer>( true );
		if ( skinned is not null && skinned.IsValid() )
			ClearIkOn( skinned );
	}

	private void ClearIkOn( SkinnedModelRenderer skinned )
	{
		skinned.ClearIk( IkLeftHand );
		skinned.ClearIk( IkRightHand );
		skinned.ClearIk( IkLeftFoot );
		skinned.ClearIk( IkRightFoot );
	}

	protected override void OnDestroy()
	{
		_pc ??= Components.Get<PlayerController>() ?? Components.GetInChildren<PlayerController>( true );
		ClearAllIk();
	}
}
