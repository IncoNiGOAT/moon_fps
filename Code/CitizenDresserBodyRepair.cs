using System.Linq;
using Sandbox;

/// <summary> Détruit les enfants orphelins sous le corps (souvent laissés par le Dresser) et peut réactiver le Dresser. </summary>
[Title( "Citizen — nettoyage Body / Dresser" )]
public sealed class CitizenDresserBodyRepair : Component
{
    [Property]
    public SkinnedModelRenderer BodyRenderer { get; set; }

    [Property]
    public bool DestroyClothingChildObjects { get; set; } = true;

    /// <summary> Ex. <c>models/citizen/citizen.vmdl</c> — laisse vide pour ne pas changer le modèle. </summary>
    [Property]
    public string ResetBodyModel { get; set; } = string.Empty;

    [Property]
    public Dresser DresserToEnable { get; set; }

    protected override void OnStart()
    {
        var renderer = BodyRenderer;
        if ( !renderer.IsValid() )
            renderer = Components.GetInChildren<SkinnedModelRenderer>( true );

        if ( !renderer.IsValid() )
            return;

        var bodyGo = renderer.GameObject;
        if ( bodyGo is null || !bodyGo.IsValid() )
            return;

        if ( DestroyClothingChildObjects )
        {
            foreach ( var child in bodyGo.Children.ToArray() )
            {
                if ( child is not null && child.IsValid() )
                    child.Destroy();
            }
        }

        if ( !string.IsNullOrWhiteSpace( ResetBodyModel ) )
        {
            var m = Model.Load( ResetBodyModel );
            if ( m is not null && !m.IsError )
                renderer.Model = m;
        }

        var dresser = DresserToEnable;
        if ( !dresser.IsValid() )
            dresser = Components.Get<Dresser>() ?? Components.GetInChildren<Dresser>( true );

        if ( dresser.IsValid() && !dresser.Enabled )
            dresser.Enabled = true;
    }
}
