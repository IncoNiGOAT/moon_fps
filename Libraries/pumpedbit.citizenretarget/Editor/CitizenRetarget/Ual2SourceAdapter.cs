namespace Editor.CitizenRetarget;

internal sealed class Ual2SourceAdapter : IDisposable
{
	private readonly UfbxNative _ufbx = new();

	public List<RetargetClipDescriptor> ScanClips( string sourceFbxPath )
	{
		var scan = _ufbx.Scan( sourceFbxPath );
		return scan.Clips
			.OrderBy( clip => clip.DisplayName, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	public NativeSampleResult SampleClip( string sourceFbxPath, string clipSourceName )
	{
		var targetFbxPath = ResolveAssetAbsolutePath( CitizenTargetProfile.CitizenAnimationBindAssetPath );
		return SampleClip( sourceFbxPath, clipSourceName, targetFbxPath, TargetBindProfile.Current, TargetBindProfile.Current );
	}

	public NativeSampleResult SampleClip(
		string sourceFbxPath,
		string clipSourceName,
		string targetFbxAbsolutePath,
		TargetBindProfile sourceProfile,
		TargetBindProfile targetProfile )
	{
		return _ufbx.SampleClip( sourceFbxPath, clipSourceName, targetFbxAbsolutePath, sourceProfile, targetProfile );
	}

	public NativeSceneAuditResult InspectScene( string absoluteScenePath, TargetBindProfile profile )
	{
		return _ufbx.InspectScene( absoluteScenePath, profile );
	}

	public void Dispose()
	{
		_ufbx.Dispose();
	}

	public static string ResolveAssetAbsolutePath( string relativeAssetPath )
	{
		var asset = AssetSystem.FindByPath( relativeAssetPath );
		if ( asset is null )
			throw new FileNotFoundException( $"Unable to resolve asset '{relativeAssetPath}'" );

		return asset.AbsolutePath;
	}
}
