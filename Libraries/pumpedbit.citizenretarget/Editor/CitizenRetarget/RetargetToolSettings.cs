#nullable enable

using System.Diagnostics;
using System.Text.Json;

namespace Editor.CitizenRetarget;

internal sealed class RetargetToolSettings
{
	public string BackendRootPath { get; set; } = string.Empty;
	public string BlenderExecutablePath { get; set; } = string.Empty;
	public string RokokoAddonPath { get; set; } = string.Empty;

	public static string SettingsAssetPath => ".sbox/citizen_retarget/settings.json";
	public static string SettingsAbsolutePath => Path.Combine( CitizenRetargetPaths.LocalSettingsRoot, "settings.json" );
	private static string LegacySettingsAbsolutePath => CitizenRetargetPaths.GetAssetAbsolutePath( "tools/citizen_retarget/settings.json" );

	public static RetargetToolSettings Load()
	{
		try
		{
			var path = SettingsAbsolutePath;
			if ( !File.Exists( path ) )
			{
				var legacyPath = LegacySettingsAbsolutePath;
				if ( File.Exists( legacyPath ) )
				{
					var migrated = LoadFromFile( legacyPath );
					migrated.Save();
					return migrated;
				}

				return new RetargetToolSettings();
			}

			return LoadFromFile( path );
		}
		catch
		{
			return new RetargetToolSettings();
		}
	}

	public void Save()
	{
		var path = SettingsAbsolutePath;
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllText( path, JsonSerializer.Serialize( this, new JsonSerializerOptions
		{
			WriteIndented = true
		} ) );
	}

	private static RetargetToolSettings LoadFromFile( string path )
	{
		return JsonSerializer.Deserialize<RetargetToolSettings>( File.ReadAllText( path ), new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			AllowTrailingCommas = true,
			ReadCommentHandling = JsonCommentHandling.Skip
		} ) ?? new RetargetToolSettings();
	}
}

internal sealed class RetargetSetupResolution
{
	public string BackendRootPath { get; init; } = string.Empty;
	public string BackendSource { get; init; } = string.Empty;
	public string ConfiguredBackendRootPath { get; init; } = string.Empty;
	public string BackendRecipePath { get; init; } = string.Empty;
	public string BackendScriptPath { get; init; } = string.Empty;
	public string BlenderExecutablePath { get; init; } = string.Empty;
	public string BlenderSource { get; init; } = string.Empty;
	public string TargetReferencePath { get; init; } = string.Empty;
}

internal static class RetargetSetupResolver
{
	private const string BundledBackendAssetPath = "tools/citizen_retarget/backend";
	private const string KnownGoodRokokoBlenderPath = @"C:\Program Files\Blender Foundation\Blender 4.4\blender.exe";
	private static readonly string[] BlenderCandidatePaths =
	{
		KnownGoodRokokoBlenderPath,
		@"C:\Program Files\Blender Foundation\Blender 4.3\blender.exe",
		@"C:\Program Files\Blender Foundation\Blender 5.0\blender.exe",
		@"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"
	};

	public static RetargetSetupResolution Resolve( RetargetToolSettings settings )
	{
		var configuredBackendRoot = NormalizePath( settings.BackendRootPath );
		var backendRoot = ResolveBackendRoot( configuredBackendRoot, out var backendSource );
		return new RetargetSetupResolution
		{
			BackendRootPath = backendRoot,
			BackendSource = backendSource,
			ConfiguredBackendRootPath = configuredBackendRoot,
			BackendRecipePath = string.IsNullOrWhiteSpace( backendRoot )
				? string.Empty
				: Path.Combine( backendRoot, CitizenTargetProfile.DefaultBackendRecipeRelativePath ),
			BackendScriptPath = string.IsNullOrWhiteSpace( backendRoot )
				? string.Empty
				: Path.Combine( backendRoot, "tools", "blender", "retarget_job.py" ),
			BlenderExecutablePath = ResolveBlenderExecutable( settings, out var blenderSource ),
			BlenderSource = blenderSource,
			TargetReferencePath = ResolveTargetReferencePath()
		};
	}

	public static string BundledBackendRootPath => CitizenRetargetPaths.GetPluginAssetAbsolutePath( BundledBackendAssetPath );

	private static string ResolveBackendRoot( string configuredBackendRoot, out string source )
	{
		if ( !string.IsNullOrWhiteSpace( configuredBackendRoot ) )
		{
			source = "developer override";
			return configuredBackendRoot;
		}

		var bundledBackendRoot = BundledBackendRootPath;
		if ( Directory.Exists( bundledBackendRoot ) )
		{
			source = "bundled plugin backend";
			return bundledBackendRoot;
		}

		source = string.Empty;
		return string.Empty;
	}

	public static string ResolveTargetReferencePath()
	{
		foreach ( var assetPath in new[]
		{
			CitizenTargetProfile.CitizenFallbackBindAssetPath,
			CitizenTargetProfile.CitizenAnimationBindAssetPath
		} )
		{
			try
			{
				var asset = AssetSystem.FindByPath( assetPath );
				if ( asset is not null && File.Exists( asset.AbsolutePath ) )
					return asset.AbsolutePath;
			}
			catch
			{
				// The diagnostics layer reports the missing reference with a user-facing hint.
			}
		}

		return string.Empty;
	}

	private static string ResolveBlenderExecutable( RetargetToolSettings settings, out string source )
	{
		var envOverride = Environment.GetEnvironmentVariable( "CITIZEN_RETARGET_BLENDER" );
		if ( IsExistingFile( envOverride ) )
		{
			source = "CITIZEN_RETARGET_BLENDER";
			return envOverride!.Trim().Trim( '"' );
		}

		if ( IsExistingFile( settings.BlenderExecutablePath ) )
		{
			source = "tool settings";
			return settings.BlenderExecutablePath.Trim().Trim( '"' );
		}

		foreach ( var candidate in BlenderCandidatePaths )
		{
			if ( File.Exists( candidate ) )
			{
				source = "known install path";
				return candidate;
			}
		}

		try
		{
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "where.exe",
					Arguments = "blender",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			process.Start();
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			var resolved = output
				.Split( [ '\r', '\n' ], StringSplitOptions.RemoveEmptyEntries )
				.FirstOrDefault( File.Exists );
			if ( !string.IsNullOrWhiteSpace( resolved ) )
			{
				source = "PATH";
				return resolved;
			}
		}
		catch
		{
			// Caller reports the friendly setup error.
		}

		source = string.Empty;
		return string.Empty;
	}

	private static bool IsExistingFile( string? path )
	{
		return !string.IsNullOrWhiteSpace( path ) && File.Exists( path.Trim().Trim( '"' ) );
	}

	private static string NormalizePath( string? path )
	{
		return (path ?? string.Empty).Trim().Trim( '"' );
	}
}
