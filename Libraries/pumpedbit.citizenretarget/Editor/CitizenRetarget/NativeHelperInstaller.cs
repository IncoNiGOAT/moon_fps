#nullable enable

using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Editor.CitizenRetarget;

internal sealed class NativeHelperInstallResult
{
	public bool Installed { get; init; }
	public Uri PackageUri { get; init; } = null!;
	public string PackageSha256 { get; init; } = string.Empty;
	public string DllPath { get; init; } = string.Empty;
	public long PackageBytes { get; init; }
	public long DllBytes { get; init; }
}

internal static class NativeHelperInstaller
{
	public const string Version = "0.1.0-alpha.2";
	public const string DllFileName = "ual2_ufbx_helper.dll";
	public const string PackageAssetName = "carl-native-win-x64-v0.1.0-alpha.2.zip";
	public const string PackageUrl = "https://github.com/pumped-bit/citizen-animation-retargeting-library/releases/download/v0.1.0-alpha.2/carl-native-win-x64-v0.1.0-alpha.2.zip";
	public const string ExpectedPackageSha256 = "84c790b5f7bae26467a810ca75c989a1e71d3b82a4adfcc20b1506eb986f5d77";

	private static readonly HttpClient Http = new();

	public static Task<NativeHelperInstallResult> InstallDefaultWinX64Async(
		string targetDirectory,
		Action<float, string>? progress = null,
		CancellationToken cancellationToken = default )
	{
		return InstallFromPackageAsync(
			new Uri( PackageUrl ),
			ExpectedPackageSha256,
			targetDirectory,
			progress: progress,
			cancellationToken: cancellationToken );
	}

	public static async Task<NativeHelperInstallResult> InstallFromPackageAsync(
		Uri packageUri,
		string expectedPackageSha256,
		string targetDirectory,
		string dllFileName = DllFileName,
		Action<float, string>? progress = null,
		Func<Uri, CancellationToken, Task<Stream>>? openPackageAsync = null,
		CancellationToken cancellationToken = default )
	{
		if ( packageUri is null )
			throw new ArgumentNullException( nameof( packageUri ) );
		if ( string.IsNullOrWhiteSpace( expectedPackageSha256 ) )
			throw new ArgumentException( "Expected package SHA256 is required.", nameof( expectedPackageSha256 ) );
		if ( string.IsNullOrWhiteSpace( targetDirectory ) )
			throw new ArgumentException( "Target directory is required.", nameof( targetDirectory ) );
		if ( string.IsNullOrWhiteSpace( dllFileName ) )
			throw new ArgumentException( "DLL filename is required.", nameof( dllFileName ) );

		progress?.Invoke( 0.04f, $"Downloading {PackageAssetName}..." );
		var packageBytes = openPackageAsync is null
			? await DownloadPackageBytesAsync( packageUri, progress, cancellationToken )
			: await ReadPackageBytesAsync( packageUri, openPackageAsync, cancellationToken );

		progress?.Invoke( 0.74f, "Verifying native helper package checksum..." );
		var actualSha = Sha256Hex( packageBytes );
		var expectedSha = NormalizeSha256( expectedPackageSha256 );
		if ( !actualSha.Equals( expectedSha, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( $"Downloaded package SHA256 mismatch. Expected {expectedSha}, got {actualSha}." );

		progress?.Invoke( 0.86f, "Extracting native FBX helper..." );
		var dllPath = await ExtractDllAsync( packageBytes, targetDirectory, dllFileName, cancellationToken );

		progress?.Invoke( 1f, "Native FBX helper installed." );
		return new NativeHelperInstallResult
		{
			Installed = true,
			PackageUri = packageUri,
			PackageSha256 = actualSha,
			DllPath = dllPath,
			PackageBytes = packageBytes.LongLength,
			DllBytes = new FileInfo( dllPath ).Length
		};
	}

	private static async Task<byte[]> ReadPackageBytesAsync(
		Uri packageUri,
		Func<Uri, CancellationToken, Task<Stream>> openPackageAsync,
		CancellationToken cancellationToken )
	{
		await using var packageStream = await openPackageAsync( packageUri, cancellationToken );
		if ( packageStream is null )
			throw new InvalidOperationException( "Package stream provider returned null." );

		using var buffer = new MemoryStream();
		await packageStream.CopyToAsync( buffer, cancellationToken );
		return buffer.ToArray();
	}

	private static async Task<byte[]> DownloadPackageBytesAsync(
		Uri packageUri,
		Action<float, string>? progress,
		CancellationToken cancellationToken )
	{
		using var request = new HttpRequestMessage( HttpMethod.Get, packageUri );
		request.Headers.UserAgent.ParseAdd( $"CARL/{Version}" );

		using var response = await Http.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, cancellationToken );
		response.EnsureSuccessStatusCode();

		var contentLength = response.Content.Headers.ContentLength;
		await using var remoteStream = await response.Content.ReadAsStreamAsync( cancellationToken );
		using var buffer = new MemoryStream( contentLength is > 0 and <= int.MaxValue ? (int)contentLength.Value : 0 );
		var chunk = new byte[64 * 1024];
		long totalRead = 0;

		while ( true )
		{
			var read = await remoteStream.ReadAsync( chunk.AsMemory( 0, chunk.Length ), cancellationToken );
			if ( read <= 0 )
				break;

			await buffer.WriteAsync( chunk.AsMemory( 0, read ), cancellationToken );
			totalRead += read;

			if ( contentLength is > 0 )
			{
				var downloadProgress = Math.Clamp( (float)totalRead / contentLength.Value, 0f, 1f );
				progress?.Invoke( 0.08f + downloadProgress * 0.62f, $"Downloading native helper package ({totalRead / 1024:N0} KB)..." );
			}
			else
			{
				progress?.Invoke( 0.36f, $"Downloading native helper package ({totalRead / 1024:N0} KB)..." );
			}
		}

		return buffer.ToArray();
	}

	private static async Task<string> ExtractDllAsync(
		byte[] packageBytes,
		string targetDirectory,
		string dllFileName,
		CancellationToken cancellationToken )
	{
		using var archive = new ZipArchive( new MemoryStream( packageBytes, writable: false ), ZipArchiveMode.Read );
		var entry = archive.Entries.FirstOrDefault( candidate =>
			Path.GetFileName( candidate.FullName ).Equals( dllFileName, StringComparison.OrdinalIgnoreCase ) );
		if ( entry is null )
			throw new InvalidDataException( $"Native helper package does not contain {dllFileName}." );
		if ( entry.Length <= 0 )
			throw new InvalidDataException( $"{dllFileName} in the native helper package is empty." );

		Directory.CreateDirectory( targetDirectory );
		var dllPath = Path.Combine( targetDirectory, dllFileName );
		var tempPath = Path.Combine( targetDirectory, $"{dllFileName}.download" );

		try
		{
			await using ( var entryStream = entry.Open() )
			await using ( var output = File.Create( tempPath ) )
			{
				await entryStream.CopyToAsync( output, cancellationToken );
			}

			File.Move( tempPath, dllPath, overwrite: true );
			return dllPath;
		}
		catch
		{
			TryDelete( tempPath );
			throw;
		}
	}

	private static string NormalizeSha256( string value )
	{
		var trimmed = value.Trim();
		var firstToken = trimmed.Split( [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? string.Empty;
		return firstToken.ToLowerInvariant();
	}

	private static string Sha256Hex( byte[] bytes )
	{
		return Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
	}

	private static void TryDelete( string path )
	{
		try
		{
			if ( File.Exists( path ) )
				File.Delete( path );
		}
		catch
		{
			// Best-effort cleanup of a partial download.
		}
	}
}
