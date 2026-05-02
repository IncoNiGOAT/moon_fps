using Sandbox;

/// <summary>
/// En mode édition, appelle <see cref="ParticleEffect.Step"/> chaque frame pour afficher les particules dans le viewport
/// (sans lancer Play). À placer sur la racine de la balle / FX ou sur chaque objet qui porte un <see cref="ParticleEffect"/>.
/// </summary>
public sealed class ParticleEffectEditorPreview : Component, Component.ExecuteInEditor
{
	[Property]
	public bool PreviewInEditor { get; set; } = true;

	/// <summary> Si vrai, tous les <see cref="ParticleEffect"/> sous ce GameObject (enfants inclus) sont stepped. </summary>
	[Property]
	public bool IncludeDescendants { get; set; } = true;

	protected override void OnUpdate()
	{
		if ( !PreviewInEditor || !Game.IsEditor )
			return;

		var delta = Time.Delta;
		if ( delta <= 0f )
			return;

		if ( IncludeDescendants )
			StepRecursive( GameObject, delta );
		else
			StepOn( GameObject, delta );
	}

	static void StepRecursive( GameObject go, float delta )
	{
		if ( go is null || !go.IsValid() )
			return;

		StepOn( go, delta );

		foreach ( var child in go.Children )
			StepRecursive( child, delta );
	}

	static void StepOn( GameObject go, float delta )
	{
		var fx = go.Components.Get<ParticleEffect>();
		if ( fx is null || !fx.IsValid() || !fx.Enabled )
			return;

		fx.Step( delta );
	}
}
