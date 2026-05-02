namespace Editor.CitizenRetarget;

internal static class CitizenRetargetPaths
{
	public const string LibraryName = "CitizenRetarget";
	private const string ExternalPathPrefix = "__external__:";
	public const string EditorRunLogFileName = "editor_run_log.txt";
	public static string ProjectRoot => GetProjectRoot();
	public static string AssetsRoot => ProjectAssetsRoot;
	public static string ProjectAssetsRoot => GetAssetsRoot();
	public static string DocsRoot => Path.Combine( ProjectRoot, "docs" );
	public static string TempRoot => Path.Combine( ProjectRoot, ".tmp", "citizen_retarget" );
	public static string PluginRoot => GetPluginRoot();
	public static string PluginAssetsRoot => Path.Combine( PluginRoot, "Assets" );
	public static string PluginDocsRoot => Path.Combine( PluginRoot, "docs" );
	public static string PluginEditorRoot => Path.Combine( PluginRoot, "Editor", "CitizenRetarget" );
	public static string NativeRoot => Path.Combine( PluginEditorRoot, "Native" );
	public static string DataRoot => Path.Combine( PluginEditorRoot, "Data" );
	public static string LocalSettingsRoot => Path.Combine( ProjectRoot, ".sbox", "citizen_retarget" );

	public static string GetAssetAbsolutePath( string relativeAssetPath )
	{
		var relative = relativeAssetPath
			.Replace( '\\', '/' )
			.Trim()
			.TrimStart( '/' );

		if ( relative.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
			relative = relative["Assets/".Length..];

		return Path.Combine( ProjectAssetsRoot, relative.Replace( '/', Path.DirectorySeparatorChar ) );
	}

	public static string GetPluginAssetAbsolutePath( string relativeAssetPath )
	{
		var relative = relativeAssetPath
			.Replace( '\\', '/' )
			.Trim()
			.TrimStart( '/' );

		if ( relative.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
			relative = relative["Assets/".Length..];

		return Path.Combine( PluginAssetsRoot, relative.Replace( '/', Path.DirectorySeparatorChar ) );
	}

	public static string GetProjectAbsolutePath( string relativeProjectPath )
	{
		var relative = relativeProjectPath
			.Replace( '\\', Path.DirectorySeparatorChar )
			.Trim()
			.TrimStart( Path.DirectorySeparatorChar );

		return Path.Combine( ProjectRoot, relative );
	}

	public static string GetPluginAbsolutePath( string relativePluginPath )
	{
		var relative = relativePluginPath
			.Replace( '\\', Path.DirectorySeparatorChar )
			.Trim()
			.TrimStart( Path.DirectorySeparatorChar );

		return Path.Combine( PluginRoot, relative );
	}

	public static string GetTempPath( params string[] parts )
	{
		var segments = new List<string> { TempRoot };
		foreach ( var part in parts )
		{
			if ( string.IsNullOrWhiteSpace( part ) )
				continue;

			var normalized = part
				.Replace( '\\', Path.DirectorySeparatorChar )
				.Replace( '/', Path.DirectorySeparatorChar )
				.Trim()
				.Trim( Path.DirectorySeparatorChar );
			if ( string.IsNullOrWhiteSpace( normalized ) )
				continue;

			segments.Add( normalized );
		}

		return Path.Combine( segments.ToArray() );
	}

	public static string GetAssetResourcePath( string absoluteAssetPath )
	{
		var normalizedAbsolute = absoluteAssetPath.Replace( '\\', '/' );
		var normalizedAssetsRoot = ProjectAssetsRoot.Replace( '\\', '/' ).TrimEnd( '/' );
		if ( !normalizedAbsolute.StartsWith( normalizedAssetsRoot, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( $"Asset path '{absoluteAssetPath}' is outside the project Assets folder." );

		return normalizedAbsolute[normalizedAssetsRoot.Length..].TrimStart( '/' );
	}

	public static string EncodeExternalPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return string.Empty;

		if ( path.StartsWith( ExternalPathPrefix, StringComparison.Ordinal ) )
			return path;

		var normalized = path.Trim();
		if ( !Path.IsPathRooted( normalized ) )
			return normalized;

		return ExternalPathPrefix + Convert.ToBase64String( System.Text.Encoding.UTF8.GetBytes( normalized ) );
	}

	public static string DecodeExternalPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return string.Empty;

		if ( !path.StartsWith( ExternalPathPrefix, StringComparison.Ordinal ) )
			return path;

		var encoded = path[ExternalPathPrefix.Length..];
		try
		{
			return System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( encoded ) );
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string GetProjectRoot()
	{
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrWhiteSpace( root ) )
			throw new InvalidOperationException( "Unable to resolve the current s&box project root" );

		return root;
	}

	private static string GetAssetsRoot()
	{
		var assets = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrWhiteSpace( assets ) )
			throw new InvalidOperationException( "Unable to resolve the current s&box Assets path" );

		return assets;
	}

	private static string GetPluginRoot()
	{
		var libraryRoot = Path.Combine( ProjectRoot, "Libraries", LibraryName );
		if ( Directory.Exists( Path.Combine( libraryRoot, "Editor", "CitizenRetarget" ) ) )
			return libraryRoot;

		var librariesRoot = Path.Combine( ProjectRoot, "Libraries" );
		if ( Directory.Exists( librariesRoot ) )
		{
			foreach ( var candidate in Directory.GetDirectories( librariesRoot ) )
			{
				if ( Directory.Exists( Path.Combine( candidate, "Editor", "CitizenRetarget" ) ) )
					return candidate;
			}
		}

		var legacyRoot = ProjectRoot;
		if ( Directory.Exists( Path.Combine( legacyRoot, "Editor", "CitizenRetarget" ) ) )
			return legacyRoot;

		return libraryRoot;
	}
}
