using System.Collections.Generic;
using Sandbox;

/// <summary>
/// Masque tous les <see cref="SkinnedModelRenderer"/> sous le joueur (corps + fringues Dresser) quand un ragdoll dupliqué est actif.
/// </summary>
public static class RagdollSkinnedVisuals
{
    public static bool IsUnderGameObject( GameObject subject, GameObject ancestor )
    {
        for ( var p = subject; p is not null; p = p.Parent )
        {
            if ( p == ancestor )
                return true;
        }

        return false;
    }

    private static void CollectSkinnedExcludingRagdollSubtree( GameObject go, GameObject ragdollRoot, List<SkinnedModelRenderer> buffer )
    {
        if ( go is null || !go.IsValid() )
            return;

        if ( ragdollRoot is not null && ( go == ragdollRoot || IsUnderGameObject( go, ragdollRoot ) ) )
            return;

        var sm = go.Components.Get<SkinnedModelRenderer>();
        if ( sm is not null && sm.IsValid() )
            buffer.Add( sm );

        foreach ( var child in go.Children )
            CollectSkinnedExcludingRagdollSubtree( child, ragdollRoot, buffer );
    }

    public static void HidePlayerSkinnedForRagdoll(
        GameObject playerRoot,
        GameObject ragdollRoot,
        List<(SkinnedModelRenderer Renderer, bool WasEnabled)> restoreOut )
    {
        restoreOut.Clear();
        if ( playerRoot is null || !playerRoot.IsValid() )
            return;

        var skinnedList = new List<SkinnedModelRenderer>( 8 );
        CollectSkinnedExcludingRagdollSubtree( playerRoot, ragdollRoot, skinnedList );

        foreach ( var skinned in skinnedList )
        {
            restoreOut.Add( (skinned, skinned.Enabled) );
            skinned.Enabled = false;
        }
    }

    public static void RestorePlayerSkinnedAfterRagdoll( List<(SkinnedModelRenderer Renderer, bool WasEnabled)> restore )
    {
        foreach ( var entry in restore )
        {
            if ( entry.Renderer.IsValid() )
                entry.Renderer.Enabled = entry.WasEnabled;
        }

        restore.Clear();
    }
}
