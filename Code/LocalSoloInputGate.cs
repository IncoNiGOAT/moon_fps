using Sandbox;

/// <summary>
/// En solo local : un seul <see cref="PlayerController"/> reçoit clavier / souris / caméra.
/// Mets ce composant sur un objet (ex. GameMode), assigne <see cref="ActivePlayerRoot"/> au joueur voulu.
/// Désactive ce composant ou l'objet en multijoueur réel.
/// </summary>
public sealed class LocalSoloInputGate : Component
{
    [Property] public GameObject ActivePlayerRoot { get; set; }
    [Property] public bool ApplyOnStart { get; set; } = true;
    [Property] public bool EnabledGate { get; set; } = true;

    protected override void OnStart()
    {
        if ( ApplyOnStart && EnabledGate )
            Apply();
    }

    [Button]
    public void Apply()
    {
        if ( !EnabledGate )
            return;

        var active = ResolveActiveController();

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            var on = active is not null && pc == active;
            pc.UseInputControls = on;
            pc.UseLookControls = on;
            pc.UseCameraControls = on;
        }
    }

    private PlayerController ResolveActiveController()
    {
        if ( ActivePlayerRoot is not null )
        {
            return ActivePlayerRoot.Components.Get<PlayerController>()
                   ?? ActivePlayerRoot.Components.GetInChildren<PlayerController>( true );
        }

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is not null )
                return pc;
        }

        return null;
    }
}
