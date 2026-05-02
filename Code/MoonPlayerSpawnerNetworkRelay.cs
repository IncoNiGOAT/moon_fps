using Sandbox;

/// <summary>
/// Délègue <see cref="Component.INetworkListener.OnActive"/> au <see cref="MoonPlayerSpawner"/>.
/// Séparer l’interface du gros composant évite des plantages du moteur (« Exception when calling INetworkListener.OnActive »)
/// après recompile / hotload alors que le corps du callback est pourtant dans un try/catch.
/// </summary>
[Title( "Moon Player Spawner — Network Relay" )]
public sealed class MoonPlayerSpawnerNetworkRelay : Component, Component.INetworkListener
{
    [Property] public MoonPlayerSpawner Spawner { get; set; }

    protected override void OnStart()
    {
        ResolveSpawnerIfNeeded();
    }

    public void OnActive( Connection channel )
    {
        try
        {
            ResolveSpawnerIfNeeded();

            if ( Spawner is null )
            {
                Log.Error( "[MoonPlayerSpawnerNetworkRelay] MoonPlayerSpawner introuvable sur ce GameObject." );
                return;
            }

            Spawner.ReceiveConnectionActive( channel );
        }
        catch ( System.Exception e )
        {
            Log.Error( $"[MoonPlayerSpawnerNetworkRelay] OnActive: {e.Message}\n{e.StackTrace}" );
        }
    }

    private void ResolveSpawnerIfNeeded()
    {
        if ( Spawner is not null && Spawner.GameObject is not null && Spawner.GameObject.IsValid() )
            return;

        Spawner = Components.Get<MoonPlayerSpawner>();
    }
}
