using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox;

/// <summary>
/// Menu principal minimal pour prototyper le flow:
/// - Creer
/// - Rejoindre
/// - Parametres (placeholder)
/// 
/// Place ce composant sur un GameObject dans ta scene menu.
/// </summary>
[Title( "Main Menu Controller" )]
public sealed class MainMenuController : Component
{
    [Property] public bool ShowHudMenu { get; set; } = true;
    [Property] public bool ForceMouseVisible { get; set; } = true;
    [Property] public SceneFile ArenaScene { get; set; }

    /// <summary>
    /// Chemin de secours si <see cref="ArenaScene"/> est vide. En éditeur <c>Assets/Arena.scene</c> marche souvent ;
    /// en package / serveur dédié le VFS expose plutôt <c>Arena.scene</c> (sans préfixe Assets/).
    /// </summary>
    [Property] public string ArenaScenePathFallback { get; set; } = "Arena.scene";
    [Property] public string CreateAction { get; set; } = "Slot1";
    [Property] public string JoinAction { get; set; } = "Slot2";
    [Property] public string SettingsAction { get; set; } = "Slot3";
    [Property] public string ClickAction { get; set; } = "attack1";
    [Property] public bool AutoCreateIfJoinFails { get; set; } = false;
    [Property] public bool TryDirectConnectOnJoinFail { get; set; } = true;
    [Property] public string DirectConnectAddress { get; set; } = "127.0.0.1";

    public MenuState State { get; private set; } = MenuState.Main;

    private bool _quickJoinBusy;
    private bool _noLobbyDialogOpen;

    protected override void OnStart()
    {
        if ( ForceMouseVisible )
            Mouse.Visibility = MouseVisibility.Visible;
    }

    protected override void OnDestroy()
    {
        if ( ForceMouseVisible )
            Mouse.Visibility = MouseVisibility.Auto;
    }

    protected override void OnUpdate()
    {
        if ( ForceMouseVisible && Mouse.Visibility != MouseVisibility.Visible )
            Mouse.Visibility = MouseVisibility.Visible;

        var clickPressed = Input.Pressed( ClickAction );
        HandleMouseClick( clickPressed );

        if ( _noLobbyDialogOpen && Input.EscapePressed )
        {
            _noLobbyDialogOpen = false;
            State = MenuState.Main;
        }
        else if ( !_noLobbyDialogOpen && !_quickJoinBusy )
        {
            if ( Input.Pressed( CreateAction ) )
                OnCreatePressed();
            else if ( Input.Pressed( JoinAction ) )
                OnJoinPressed();
            else if ( Input.Pressed( SettingsAction ) )
                OnSettingsPressed();
        }

        if ( ShowHudMenu )
            DrawMenuHud();
    }

    [Button]
    public void OnCreatePressed()
    {
        if ( _quickJoinBusy )
            return;

        State = MenuState.Create;
        StartCreateFlow();
    }

    private async void StartCreateFlow()
    {
        _quickJoinBusy = true;
        try
        {
            await Task.Delay( 50 );

            if ( !TryLoadArenaScene() )
            {
                State = MenuState.Main;
                Log.Warning( "[Menu] Impossible de charger l'arene. Verifie Arena.scene / Assets/Arena.scene." );
                return;
            }

            Log.Info( "[Menu] Arene chargee (solo / local)." );
        }
        finally
        {
            _quickJoinBusy = false;
        }
    }

    [Button]
    public void OnJoinPressed()
    {
        if ( _quickJoinBusy || _noLobbyDialogOpen )
            return;

        State = MenuState.Join;
        StartQuickJoin();
    }

    private async void StartQuickJoin()
    {
        _quickJoinBusy = true;
        try
        {
            await Task.Delay( 50 );

            if ( TryLoadArenaScene() )
            {
                Log.Info( "[Menu] Arene chargee (rejoindre = meme flux local que Creer)." );
                return;
            }

            Log.Warning( "[Menu] Impossible de charger l'arene." );

            if ( AutoCreateIfJoinFails )
            {
                OnCreatePressed();
                return;
            }

            _noLobbyDialogOpen = true;
            State = MenuState.Main;
        }
        finally
        {
            _quickJoinBusy = false;
        }
    }

    [Button]
    public void OnSettingsPressed()
    {
        State = MenuState.Settings;
        Log.Info( "[Menu] Parametres selectionnes (placeholder)." );
    }

    [Button]
    public void BackToMain()
    {
        State = MenuState.Main;
    }

    private void DrawMenuHud()
    {
        if ( Scene?.Camera is null )
            return;

        var hud = Scene.Camera.Hud;

        var boxX = Screen.Width * 0.5f - 220f;
        var boxY = Screen.Height * 0.25f;
        var boxW = 440f;
        var boxH = 220f;

        hud.DrawRect( new Rect( boxX, boxY, boxW, boxH ), new Color( 0f, 0f, 0f, 0.55f ) );

        hud.DrawText( new TextRendering.Scope( "MOON FPS", Color.White, 34 ),
            new Vector2( boxX + 120f, boxY + 20f ) );

        DrawMenuLine( hud, 0, $"[{CreateAction}] Creer", State == MenuState.Create, IsMouseOverLine( 0 ) );
        DrawMenuLine( hud, 1, $"[{JoinAction}] Rejoindre", State == MenuState.Join, IsMouseOverLine( 1 ) );
        DrawMenuLine( hud, 2, $"[{SettingsAction}] Parametres", State == MenuState.Settings, IsMouseOverLine( 2 ) );

        var footer = State switch
        {
            MenuState.Create => "Etat: Chargement de l'arene...",
            MenuState.Join => "Etat: Recherche du meilleur lobby...",
            MenuState.Settings => "Etat: Parametres (vide).",
            _ => "Etat: Menu principal"
        };

        hud.DrawText( new TextRendering.Scope( footer, new Color( 0.85f, 0.9f, 1f ), 15 ),
            new Vector2( boxX + 20f, boxY + 180f ) );

        if ( _noLobbyDialogOpen )
            DrawNoLobbyDialog( hud );
    }

    private void DrawMenuLine( Sandbox.Rendering.HudPainter hud, int index, string text, bool active, bool hovered )
    {
        var rect = GetLineRect( index );
        var color = active ? new Color( 1f, 0.9f, 0.4f ) : Color.White;

        if ( hovered )
        {
            hud.DrawRect( rect, new Color( 1f, 1f, 1f, 0.08f ) );
            if ( !active )
                color = new Color( 0.9f, 0.95f, 1f );
        }

        hud.DrawText( new TextRendering.Scope( text, color, 22 ), new Vector2( rect.Left + 12f, rect.Top + 1f ) );
    }

    private void HandleMouseClick( bool clickPressed )
    {
        if ( !clickPressed )
            return;

        if ( _noLobbyDialogOpen )
        {
            if ( IsMouseOverNoLobbyYes() )
            {
                _noLobbyDialogOpen = false;
                OnCreatePressed();
            }
            else if ( IsMouseOverNoLobbyNo() )
            {
                _noLobbyDialogOpen = false;
                State = MenuState.Main;
            }

            return;
        }

        if ( IsMouseOverLine( 0 ) )
            OnCreatePressed();
        else if ( IsMouseOverLine( 1 ) )
            OnJoinPressed();
        else if ( IsMouseOverLine( 2 ) )
            OnSettingsPressed();
    }

    private bool IsMouseOverLine( int index )
    {
        var mouse = Mouse.Position;
        var rect = GetLineRect( index );
        return mouse.x >= rect.Left && mouse.x <= rect.Right &&
               mouse.y >= rect.Top && mouse.y <= rect.Bottom;
    }

    private Rect GetLineRect( int index )
    {
        var x = Screen.Width * 0.5f - 170f;
        var y = Screen.Height * 0.25f + 80f + index * 30f;
        return new Rect( x, y - 2f, 300f, 28f );
    }

    private void DrawNoLobbyDialog( Sandbox.Rendering.HudPainter hud )
    {
        hud.DrawRect( new Rect( 0f, 0f, Screen.Width, Screen.Height ), new Color( 0f, 0f, 0f, 0.65f ) );

        var w = 480f;
        var h = 200f;
        var dx = (Screen.Width - w) * 0.5f;
        var dy = (Screen.Height - h) * 0.5f;
        hud.DrawRect( new Rect( dx, dy, w, h ), new Color( 0.12f, 0.12f, 0.14f, 0.98f ) );
        hud.DrawRect( new Rect( dx, dy, w, 2f ), new Color( 1f, 0.85f, 0.25f, 0.9f ) );

        hud.DrawText( new TextRendering.Scope( "Aucune partie trouvée", Color.White, 26 ),
            new Vector2( dx + 24f, dy + 20f ) );
        hud.DrawText( new TextRendering.Scope( "Voulez-vous en créer une ?", new Color( 0.85f, 0.9f, 1f ), 18 ),
            new Vector2( dx + 24f, dy + 58f ) );

        var yesRect = GetNoLobbyYesRect();
        var noRect = GetNoLobbyNoRect();
        var yesHover = IsMouseOverNoLobbyYes();
        var noHover = IsMouseOverNoLobbyNo();

        hud.DrawRect( yesRect, yesHover ? new Color( 0.2f, 0.55f, 0.3f, 0.95f ) : new Color( 0.15f, 0.45f, 0.25f, 0.9f ) );
        hud.DrawRect( noRect, noHover ? new Color( 0.45f, 0.2f, 0.2f, 0.95f ) : new Color( 0.35f, 0.15f, 0.15f, 0.9f ) );

        hud.DrawText( new TextRendering.Scope( "Oui", Color.White, 20 ), new Vector2( yesRect.Left + 52f, yesRect.Top + 10f ) );
        hud.DrawText( new TextRendering.Scope( "Non", Color.White, 20 ), new Vector2( noRect.Left + 52f, noRect.Top + 10f ) );
    }

    private Rect GetNoLobbyYesRect()
    {
        var w = 480f;
        var h = 200f;
        var dx = (Screen.Width - w) * 0.5f;
        var dy = (Screen.Height - h) * 0.5f;
        return new Rect( dx + 40f, dy + 110f, 180f, 44f );
    }

    private Rect GetNoLobbyNoRect()
    {
        var w = 480f;
        var h = 200f;
        var dx = (Screen.Width - w) * 0.5f;
        var dy = (Screen.Height - h) * 0.5f;
        return new Rect( dx + 260f, dy + 110f, 180f, 44f );
    }

    private bool IsMouseOverNoLobbyYes()
    {
        var m = Mouse.Position;
        var r = GetNoLobbyYesRect();
        return m.x >= r.Left && m.x <= r.Right && m.y >= r.Top && m.y <= r.Bottom;
    }

    private bool IsMouseOverNoLobbyNo()
    {
        var m = Mouse.Position;
        var r = GetNoLobbyNoRect();
        return m.x >= r.Left && m.x <= r.Right && m.y >= r.Top && m.y <= r.Bottom;
    }

    /// <summary>
    /// Charge l'arène : <see cref="ArenaScene"/> si assigné, sinon plusieurs chemins (éditeur vs package dédié).
    /// </summary>
    private bool TryLoadArenaScene()
    {
        if ( ArenaScene is not null && Scene.Load( ArenaScene ) )
            return true;

        var candidates = new List<string>();
        if ( !string.IsNullOrWhiteSpace( ArenaScenePathFallback ) )
            candidates.Add( ArenaScenePathFallback.Trim() );

        foreach ( var p in new[] { "Arena.scene", "arena.scene", "Assets/Arena.scene", "assets/arena.scene" } )
        {
            if ( !candidates.Exists( c => string.Equals( c, p, StringComparison.OrdinalIgnoreCase ) ) )
                candidates.Add( p );
        }

        var tried = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var path in candidates )
        {
            if ( string.IsNullOrWhiteSpace( path ) || !tried.Add( path ) )
                continue;

            if ( Scene.LoadFromFile( path ) )
            {
                Log.Info( $"[Menu] Arène chargée : {path}" );
                return true;
            }
        }

        return false;
    }
}

public enum MenuState
{
    Main,
    Create,
    Join,
    Settings
}
