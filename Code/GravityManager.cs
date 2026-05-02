using Sandbox;

public sealed class GravityManager : Component
{
	[Property] public Vector3 MoonGravity { get; set; } = new Vector3( 0, 0, -150f );

	protected override void OnStart()
	{
		// Force la gravité de la scène entière au lancement
		Scene.PhysicsWorld.Gravity = MoonGravity;
	}
}