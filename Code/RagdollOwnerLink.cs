using Sandbox;

/// <summary>
/// Lien entre un ragdoll temporaire et le joueur source.
/// Permet de remonter au bon joueur quand une collision touche le ragdoll.
/// </summary>
public sealed class RagdollOwnerLink : Component
{
    public GameObject OwnerRoot { get; set; }
}
