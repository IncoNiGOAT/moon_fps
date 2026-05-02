using Sandbox;

public sealed class JumpManager : Component
{
	// Cette valeur apparaîtra dans ton Inspector pour un réglage facile
	[Property] public float Power { get; set; } = 150f;

	protected override void OnUpdate()
	{
		// On cherche le composant PlayerController sur cet objet
		var controller = Components.Get<PlayerController>();

		if ( controller != null )
		{
			// On force la vitesse du saut avec notre valeur personnalisée
			controller.JumpSpeed = Power;
		}
	}
}