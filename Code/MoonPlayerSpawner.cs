using Sandbox;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Spawn dynamique des joueurs : 1 perso en solo (pas de session réseau), 1 par connexion en multijoueur.
/// Calqué sur le NetworkHelper du moteur : OnActive + GameObject.NetworkSpawn.
/// <para><b>Setup :</b> garde UN joueur complet dans la scène comme modèle (désactivé), assigne-le à <see cref="PlayerPrefab"/>.
/// Mets les anciens Player Controller de test dans <see cref="RemoveFromSceneOnStart"/> pour les détruire au lancement.
/// </summary>
[Title( "Moon Player Spawner" )]
public sealed class MoonPlayerSpawner : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }
    [Property] public TeamSpawnManager ArenaSpawns { get; set; }
    [Property] public bool HostGetsRedTeam { get; set; } = true;
    [Property] public bool EnforceTeamBalance { get; set; } = true;
    [Property] public int MaxTeamSizeDelta { get; set; } = 1;
    [Property] public bool RequireOfflineTeamChoice { get; set; } = true;
    [Property] public bool RequireOnlineTeamChoice { get; set; } = true;
    [Property] public string ChooseRedAction { get; set; } = "Slot1";
    [Property] public string ChooseBlueAction { get; set; } = "Slot2";
    [Property] public bool ShowOfflineTeamHud { get; set; } = true;
    [Property] public bool ShowOnlineTeamHud { get; set; } = true;
    [Property] public TeamId OfflinePreferredTeam { get; set; } = TeamId.Red;
    [Property] public List<GameObject> RemoveFromSceneOnStart { get; set; } = new();
    [Property] public int MaxPlayers { get; set; } = 10;
    /// <summary> Solo / éditeur : après ton spawn, remplit l'arène de bots pour tester (désactivé en multijoueur). </summary>
    [Property] public bool SpawnOfflineTestBots { get; set; } = true;
    [Property] public int OfflineTestBotsPerTeam { get; set; } = 3;

    private int _spawnedCount;
    private readonly Dictionary<Connection, TeamId> _preferredTeams = new();
    private readonly Dictionary<Connection, GameObject> _spawnedByConnection = new();
    private readonly HashSet<Connection> _pendingOnlineChoices = new();
    private bool _offlineWaitingForChoice;
    /// <summary> Solo : perso humain après choix d'équipe (pour ne pas piloter la caméra depuis les bots). </summary>
    private GameObject _offlineHumanRoot;

    protected override void OnStart()
    {
        Invoke( 0.05f, BootstrapOfflineIfNeeded );
    }

    protected override void OnUpdate()
    {
        if ( _offlineWaitingForChoice )
        {
            if ( ShowOfflineTeamHud )
                DrawTeamChoiceHud();

            if ( Input.Pressed( ChooseRedAction ) )
                TrySpawnOffline( TeamId.Red );
            else if ( Input.Pressed( ChooseBlueAction ) )
                TrySpawnOffline( TeamId.Blue );
        }

        if ( Networking.IsActive )
        {
            TickPendingOnlineChoices();

            if ( ShowOnlineTeamHud && IsLocalWaitingOnlineChoice() )
                DrawTeamChoiceHud();
        }
    }

    /// <summary> Solo / éditeur : pas de session <see cref="Networking"/>. </summary>
    private void BootstrapOfflineIfNeeded()
    {
        foreach ( var go in RemoveFromSceneOnStart )
        {
            if ( go is not null && go.IsValid() )
                go.Destroy();
        }

        if ( Networking.IsActive )
            return;

        if ( !PlayerPrefab.IsValid() )
            return;

        if ( _spawnedCount >= MaxPlayers )
            return;

        if ( RequireOfflineTeamChoice )
        {
            _offlineWaitingForChoice = true;
            return;
        }

        TrySpawnOffline( OfflinePreferredTeam );
    }

    /// <summary> Appelé sur l'hôte quand une connexion est prête (comme NetworkHelper). </summary>
    public void OnActive( Connection channel )
    {
        if ( !PlayerPrefab.IsValid() )
            return;

        if ( _spawnedCount >= MaxPlayers )
            return;

        var host = Connection.Host;
        if ( host is null )
            return;

        if ( RequireOnlineTeamChoice )
        {
            _pendingOnlineChoices.Add( channel );
            return;
        }

        SpawnOnlineForConnection( channel, host );
    }

    /// <summary>
    /// API à brancher sur un UI/menu plus tard : enregistre l'équipe préférée d'une connexion.
    /// Le prochain spawn de cette connexion utilisera ce choix si l'équilibrage l'autorise.
    /// </summary>
    public void SetPreferredTeam( Connection channel, TeamId preferredTeam )
    {
        if ( channel is null )
            return;

        _preferredTeams[channel] = preferredTeam;

        if ( Networking.IsHost && _pendingOnlineChoices.Contains( channel ) )
            TrySpawnPendingConnection( channel );
    }

    [Button]
    public void ChooseRedOfflineNow() => TrySpawnOffline( TeamId.Red );

    [Button]
    public void ChooseBlueOfflineNow() => TrySpawnOffline( TeamId.Blue );

    private GameObject SpawnCloneAtTeam( TeamId team, int arenaSlot, bool offline, bool setAsOfflineHumanRoot = false )
    {
        GameObject clone;
        if ( TryGetSpawnTransform( team, arenaSlot, out var spawnTx ) )
            clone = PlayerPrefab.Clone( spawnTx );
        else
            clone = PlayerPrefab.Clone( WorldPosition, WorldRotation );

        if ( clone is null || !clone.IsValid() )
            return null;

        clone.Enabled = true;
        if ( offline && setAsOfflineHumanRoot )
            _offlineHumanRoot = clone;

        FaceTowardsArenaCenter( clone );
        // Certains controllers écrasent la rotation juste après le spawn.
        // On réapplique brièvement pour stabiliser orientation perso + caméra.
        Invoke( 0.02f, () => FaceTowardsArenaCenter( clone ) );
        Invoke( 0.08f, () => FaceTowardsArenaCenter( clone ) );

        if ( !offline )
            return clone;

        ApplyTeam( clone, team, arenaSlot );

        var pc = clone.Components.Get<PlayerController>();
        if ( pc?.Body is not null )
        {
            pc.Body.Velocity = Vector3.Zero;
            pc.Body.AngularVelocity = Vector3.Zero;
        }

        return clone;
    }

    private void FaceTowardsArenaCenter( GameObject root )
    {
        if ( root is null || !root.IsValid() )
            return;

        if ( !TryGetArenaCenter( out var center ) )
        {
            Log.Warning( $"[MoonPlayerSpawner] FaceTowardsArenaCenter: no arena center for {root.Name}." );
            return;
        }

        var dir = (center - root.WorldPosition).WithZ( 0f );
        if ( dir.LengthSquared < 0.0001f )
        {
            Log.Warning( $"[MoonPlayerSpawner] FaceTowardsArenaCenter: direction too small for {root.Name}. root={root.WorldPosition} center={center}" );
            return;
        }

        var lookRot = Rotation.LookAt( dir.Normal, Vector3.Up );
        Log.Info( $"[MoonPlayerSpawner] FaceTowardsArenaCenter: root={root.Name} pos={root.WorldPosition} center={center} rot={lookRot}" );
        root.WorldRotation = lookRot;

        var pc = root.Components.Get<PlayerController>() ?? root.Components.GetInChildren<PlayerController>( true );
        if ( pc?.Body is not null )
        {
            pc.Body.WorldRotation = lookRot;
            Log.Info( $"[MoonPlayerSpawner] Body rotation applied on {root.Name}." );
        }
        else
        {
            Log.Warning( $"[MoonPlayerSpawner] No PlayerController.Body found on {root.Name}." );
        }

        // Confort local : aligne la caméra seulement pour le joueur humain (pas chaque bot en solo).
        if ( Scene?.Camera is not null && ShouldApplyCameraLookFromSpawn( root ) )
        {
            Scene.Camera.WorldRotation = lookRot;
            Log.Info( $"[MoonPlayerSpawner] Camera rotation applied for {root.Name}." );
        }
        else
        {
            Log.Info( $"[MoonPlayerSpawner] Camera rotation skipped for {root.Name}. hasCamera={(Scene?.Camera is not null)} applyCam={ShouldApplyCameraLookFromSpawn( root )}" );
        }
    }

    private bool ShouldApplyCameraLookFromSpawn( GameObject root )
    {
        if ( root is null || !root.IsValid() )
            return false;

        if ( Networking.IsActive )
            return IsLocalOwnedPlayer( root );

        return root == _offlineHumanRoot;
    }

    private bool TryGetArenaCenter( out Vector3 center )
    {
        var mgr = ArenaSpawns ?? Scene.GetAllComponents<TeamSpawnManager>().FirstOrDefault();
        Log.Info( $"[MoonPlayerSpawner] TryGetArenaCenter: manager={(mgr is null ? "null" : mgr.GameObject?.Name)} explicitAssigned={(ArenaSpawns is not null)}" );
        if ( mgr is not null && mgr.TryGetArenaCenter( out center ) )
        {
            Log.Info( $"[MoonPlayerSpawner] TryGetArenaCenter: center={center}" );
            return true;
        }

        center = WorldPosition;
        Log.Warning( $"[MoonPlayerSpawner] TryGetArenaCenter failed. fallback={center}" );
        return false;
    }

    private bool TryGetSpawnTransform( TeamId team, int arenaSlot, out Transform tx )
    {
        tx = default;
        var mgr = ArenaSpawns ?? Scene.GetAllComponents<TeamSpawnManager>().FirstOrDefault();
        if ( mgr is null )
        {
            Log.Warning( $"[MoonPlayerSpawner] TryGetSpawnTransform: no TeamSpawnManager for team={team}, slot={arenaSlot}." );
            return false;
        }

        GameObject pt = null;
        if ( mgr.UseSlotBasedArenaSpawns && arenaSlot >= 0 )
            pt = mgr.GetArenaSpawnForSlot( team, arenaSlot );

        if ( pt is null )
            pt = mgr.GetSpawnPoint( team );

        if ( pt is null )
        {
            Log.Warning( $"[MoonPlayerSpawner] TryGetSpawnTransform: no spawn point found for team={team}, slot={arenaSlot} on manager={mgr.GameObject?.Name}." );
            return false;
        }

        tx = pt.WorldTransform;
        Log.Info( $"[MoonPlayerSpawner] TryGetSpawnTransform: team={team}, slot={arenaSlot}, point={pt.Name}, pos={tx.Position}, rot={tx.Rotation}" );
        return true;
    }

    /// <summary> Premier index libre dans la liste de spawns de l'équipe (0..Count-1). </summary>
    private int AllocateArenaSlot( TeamId team )
    {
        var mgr = ArenaSpawns ?? Scene.GetAllComponents<TeamSpawnManager>().FirstOrDefault();
        var cap = 0;
        if ( mgr is not null )
            cap = team == TeamId.Red ? mgr.RedSpawnPoints.Count : mgr.BlueSpawnPoints.Count;

        if ( cap <= 0 )
            return -1;

        var used = new HashSet<int>();
        foreach ( var m in Scene.GetAllComponents<TeamMember>() )
        {
            if ( m is null )
                continue;
            if ( m.Team == team && m.ArenaSlotIndex >= 0 )
                used.Add( m.ArenaSlotIndex );
        }

        for ( var i = 0; i < cap; i++ )
        {
            if ( !used.Contains( i ) )
                return i;
        }

        return -1;
    }

    private static TeamId TeamForConnection( Connection channel, Connection host, bool hostGetsRed )
    {
        var isHost = channel == host;
        return hostGetsRed
            ? (isHost ? TeamId.Red : TeamId.Blue)
            : (isHost ? TeamId.Blue : TeamId.Red);
    }

    private TeamId ResolveTeamForNewConnection( Connection channel, Connection host )
    {
        var counts = TeamCountsFromConnections();
        var defaultTeam = TeamForConnection( channel, host, HostGetsRedTeam );

        if ( !_preferredTeams.TryGetValue( channel, out var preferredTeam ) )
            preferredTeam = defaultTeam;

        return SelectTeamForCounts( preferredTeam, counts );
    }

    private TeamId SelectTeamForCounts( TeamId preferredTeam, (int red, int blue) counts )
    {
        if ( !EnforceTeamBalance )
            return preferredTeam;

        var opposite = preferredTeam == TeamId.Red ? TeamId.Blue : TeamId.Red;

        if ( CanJoinTeam( preferredTeam, counts ) )
            return preferredTeam;

        if ( CanJoinTeam( opposite, counts ) )
            return opposite;

        // Cas extrême (delta trop strict) : on choisit quand même l'équipe la moins peuplée.
        return counts.red <= counts.blue ? TeamId.Red : TeamId.Blue;
    }

    private bool CanJoinTeam( TeamId team, (int red, int blue) counts )
    {
        var redAfter = counts.red + (team == TeamId.Red ? 1 : 0);
        var blueAfter = counts.blue + (team == TeamId.Blue ? 1 : 0);
        return System.Math.Abs( redAfter - blueAfter ) <= MaxTeamSizeDelta;
    }

    private (int red, int blue) TeamCountsFromConnections()
    {
        _spawnedByConnection.RemoveAll( kv => kv.Key is null || kv.Value is null || !kv.Value.IsValid() );

        var red = 0;
        var blue = 0;

        foreach ( var kv in _spawnedByConnection )
        {
            var team = FindTeamOnRoot( kv.Value );
            if ( team == TeamId.Red ) red++;
            else blue++;
        }

        return (red, blue);
    }

    private (int red, int blue) TeamCountsFromScene()
    {
        var red = 0;
        var blue = 0;

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            var team = FindTeamOnRoot( pc.GameObject );
            if ( team == TeamId.Red ) red++;
            else blue++;
        }

        return (red, blue);
    }

    private static TeamId FindTeamOnRoot( GameObject root )
    {
        var member = root.Components.Get<TeamMember>() ?? root.Components.GetInChildren<TeamMember>( true );
        return member?.Team ?? TeamId.Red;
    }

    private void TickPendingOnlineChoices()
    {
        if ( !Networking.IsHost || _pendingOnlineChoices.Count == 0 )
            return;

        _pendingOnlineChoices.RemoveWhere( c => c is null || !c.IsActive );

        // On lit les touches de CHAQUE connexion sur l'hote.
        // Ainsi chaque joueur choisit sa team avec les memes binds (Slot1/Slot2).
        var pendingSnapshot = _pendingOnlineChoices.ToList();
        foreach ( var channel in pendingSnapshot )
        {
            if ( _spawnedCount >= MaxPlayers )
                return;

            if ( channel.Pressed( ChooseRedAction ) )
            {
                SetPreferredTeam( channel, TeamId.Red );
                continue;
            }

            if ( channel.Pressed( ChooseBlueAction ) )
            {
                SetPreferredTeam( channel, TeamId.Blue );
            }
        }
    }

    private void TrySpawnPendingConnection( Connection channel )
    {
        if ( channel is null || !channel.IsActive || _spawnedCount >= MaxPlayers )
            return;

        var host = Connection.Host;
        if ( host is null )
            return;

        SpawnOnlineForConnection( channel, host );
    }

    private void SpawnOnlineForConnection( Connection channel, Connection host )
    {
        if ( channel is null || _spawnedByConnection.ContainsKey( channel ) )
            return;

        var team = ResolveTeamForNewConnection( channel, host );
        var slot = AllocateArenaSlot( team );
        var clone = SpawnCloneAtTeam( team, slot, offline: false );
        if ( clone is null )
            return;

        clone.Name = $"Player [{channel.DisplayName}]";
        ApplyTeam( clone, team, slot );
        clone.NetworkSpawn( channel );
        _spawnedByConnection[channel] = clone;
        _pendingOnlineChoices.Remove( channel );
        _spawnedCount++;
        NotifyLocalInputGates();
    }

    private void TrySpawnOffline( TeamId preferredTeam )
    {
        if ( Networking.IsActive || !PlayerPrefab.IsValid() || _spawnedCount >= MaxPlayers )
            return;

        var team = EnforceTeamBalance
            ? SelectTeamForCounts( preferredTeam, TeamCountsFromScene() )
            : preferredTeam;

        var slot = AllocateArenaSlot( team );
        var clone = SpawnCloneAtTeam( team, slot, offline: true, setAsOfflineHumanRoot: true );
        if ( clone is null )
            return;

        _spawnedCount++;
        _offlineWaitingForChoice = false;

        PinOfflineHumanAsActive( clone );

        if ( SpawnOfflineTestBots )
            SpawnOfflineTestBotsFill();

        NotifyLocalInputGates();
        EnsureOfflineLookControlsIfNoGate( clone );
        Invoke( 0.12f, NotifyLocalInputGates );
    }

    private void SpawnOfflineTestBotsFill()
    {
        if ( OfflineTestBotsPerTeam <= 0 || !PlayerPrefab.IsValid() )
            return;

        foreach ( var sideTeam in new[] { TeamId.Red, TeamId.Blue } )
        {
            for ( var i = 0; i < OfflineTestBotsPerTeam; i++ )
            {
                if ( _spawnedCount >= MaxPlayers )
                    return;

                var botSlot = AllocateArenaSlot( sideTeam );
                var bot = SpawnCloneAtTeam( sideTeam, botSlot, offline: true, setAsOfflineHumanRoot: false );
                if ( bot is null )
                    continue;

                bot.Name = $"Bot {sideTeam} {i + 1}";
                _spawnedCount++;
            }
        }
    }

    private static void PinOfflineHumanAsActive( GameObject humanRoot )
    {
        if ( humanRoot is null || !humanRoot.IsValid() )
            return;

        var scene = Game.ActiveScene;
        if ( scene is null )
            return;

        foreach ( var gate in scene.GetAllComponents<LocalSoloInputGate>() )
        {
            if ( gate is null )
                continue;

            gate.ActivePlayerRoot = humanRoot;
        }
    }

    /// <summary>
    /// Sans <see cref="LocalSoloInputGate"/> dans la scène, les clones peuvent rester avec UseLookControls désactivé
    /// (ex. ordre d'init). On force le humain actif pour la souris / caméra TPS.
    /// </summary>
    private void EnsureOfflineLookControlsIfNoGate( GameObject humanRoot )
    {
        if ( Networking.IsActive || humanRoot is null || !humanRoot.IsValid() )
            return;

        foreach ( var gate in Scene.GetAllComponents<LocalSoloInputGate>() )
        {
            if ( gate is not null && gate.EnabledGate )
                return;
        }

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            var isHuman = IsUnderRoot( pc.GameObject, humanRoot );
            pc.UseInputControls = isHuman;
            pc.UseLookControls = isHuman;
            pc.UseCameraControls = isHuman;
        }
    }

    private static bool IsUnderRoot( GameObject go, GameObject root )
    {
        var c = go;
        while ( c is not null )
        {
            if ( c == root )
                return true;
            c = c.Parent;
        }

        return false;
    }

    private bool IsLocalWaitingOnlineChoice()
    {
        if ( !Networking.IsActive )
            return false;

        var local = Connection.Local;
        if ( local is not null && _pendingOnlineChoices.Contains( local ) )
            return true;

        // Fallback visuel client : pas encore de joueur possédé localement.
        return !HasOwnedPlayer();
    }

    private bool HasOwnedPlayer()
    {
        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            if ( TryGetNetworkRoot( pc.GameObject, out var root ) && root.Network.IsOwner )
                return true;
        }

        return false;
    }

    private static bool TryGetNetworkRoot( GameObject start, out GameObject root )
    {
        var go = start;
        while ( go is not null )
        {
            if ( go.Network.Active )
            {
                root = go.Network.RootGameObject ?? go;
                return true;
            }

            go = go.Parent;
        }

        root = null;
        return false;
    }

    private static bool IsLocalOwnedPlayer( GameObject root )
    {
        if ( !Networking.IsActive )
            return true;

        return TryGetNetworkRoot( root, out var netRoot ) && netRoot.Network.IsOwner;
    }

    private void DrawTeamChoiceHud()
    {
        if ( Scene?.Camera is null )
            return;

        var hud = Scene.Camera.Hud;
        var redAction = string.IsNullOrWhiteSpace( ChooseRedAction ) ? "Slot1" : ChooseRedAction;
        var blueAction = string.IsNullOrWhiteSpace( ChooseBlueAction ) ? "Slot2" : ChooseBlueAction;
        var label = $"Choisis ton equipe: [{redAction}] Rouge  |  [{blueAction}] Bleu";

        var x = Screen.Width * 0.5f - 260f;
        var y = Screen.Height * 0.2f;
        hud.DrawRect( new Rect( x - 12f, y - 8f, 560f, 36f ), new Color( 0f, 0f, 0f, 0.5f ) );
        hud.DrawText( new TextRendering.Scope( label, Color.White, 18 ), new Vector2( x, y ) );
    }

    private void ApplyTeam( GameObject root, TeamId team, int arenaSlot = -1 )
    {
        var member = root.Components.Get<TeamMember>() ?? root.Components.GetInChildren<TeamMember>( true );
        if ( member is null )
            return;

        member.Team = team;
        if ( arenaSlot >= 0 )
            member.ArenaSlotIndex = arenaSlot;
        member.ApplyTeamVisual();

        if ( SoftMidlineBarrier.SceneHasPlayerBlockingSoftWall( root.Scene ) )
        {
            var pc = root.Components.Get<PlayerController>() ?? root.Components.GetInChildren<PlayerController>( true );
            if ( pc?.Body is not null )
                SoftMidlineBarrier.ConfigurePlayerBody( this, pc.Body );
        }
    }

    private static void NotifyLocalInputGates()
    {
        var scene = Game.ActiveScene;
        if ( scene is null )
            return;

        foreach ( var gate in scene.GetAllComponents<LocalSoloInputGate>() )
            gate?.Apply();
    }
}

public static class MoonPlayerSpawnerDictionaryExtensions
{
    public static void RemoveAll<TKey, TValue>( this Dictionary<TKey, TValue> dictionary, System.Func<KeyValuePair<TKey, TValue>, bool> predicate )
    {
        var keys = new List<TKey>();
        foreach ( var pair in dictionary )
        {
            if ( predicate( pair ) )
                keys.Add( pair.Key );
        }

        foreach ( var key in keys )
            dictionary.Remove( key );
    }
}
