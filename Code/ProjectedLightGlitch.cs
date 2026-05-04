using Sandbox;
using System;

/// <summary>
/// Glitch type MPEG / macroblocs sur la texture du cookie d’un spot (image projetée).
/// Copie l’image source en CPU, applique corruptions puis assigne une render target au <see cref="SpotLight.Cookie"/>.
/// Laisse <see cref="SourceImage"/> vide pour réutiliser le cookie déjà posé sur le spot au premier frame.
/// </summary>
[Title( "Projected Light Glitch" )]
[Category( "Light" )]
[Icon( "broken_image" )]
public sealed class ProjectedLightGlitch : Component, Component.ExecuteInEditor
{
	[Property, Group( "Setup" )]
	[Description( "Image d’origine (même que ton cookie). Si vide, le composant lit une fois le cookie actuel du spot puis le remplace par la version glitchée." )]
	public Texture SourceImage { get; set; }

	[Property, Group( "Setup" )]
	[Description( "Chemin optionnel (ex. maps/ma_map/ma_texture.png) — en jeu le cookie du spot est souvent vide ; ce chemin force le chargement de la source." )]
	public string SourceTexturePath { get; set; } = "";

	[Property, Group( "Setup" )]
	public bool GlitchActive { get; set; } = true;

	[Property, Group( "Setup" )]
	[Description( "Recopie les pixels depuis SourceImage (ou depuis le cookie du spot si source vide)." )]
	public bool RefreshSourcePixelsNow { get; set; }

	[Property, Group( "Quality" ), Range( 64, 2048 )]
	[Description( "Taille max du plus grand côté après mip : plus bas = plus léger en CPU." )]
	public int CpuProcessMaxEdge { get; set; } = 512;

	[Property, Group( "Vitesse" ), Range( 1f, 90f )]
	[Description( "Fréquence moyenne de refresh du glitch (Hz). Le rythme réel varie avec les options ci‑dessous." )]
	public float UpdatesPerSecond { get; set; } = 28f;

	[Property, Group( "Vitesse" ), Range( 0.05f, 4f )]
	[Description( "Multiplicateur global sur la fréquence (2 = environ 2× plus rapide)." )]
	public float SpeedMultiplier { get; set; } = 1f;

	[Property, Group( "Vitesse" ), Range( 0f, 1f )]
	[Description( "0 = intervalle régulier ; 1 = délais très irréguliers entre chaque mise à jour." )]
	public float TimingIrregularity { get; set; } = 0.55f;

	[Property, Group( "Vitesse" ), Range( 0f, 1f )]
	[Description( "La vitesse monte et descend lentement (sinusoïde)." )]
	public float SpeedWobble { get; set; } = 0.35f;

	[Property, Group( "Vitesse" ), Range( 0.15f, 12f )]
	[Description( "Durée d’un cycle de montée/descente de la vitesse (secondes)." )]
	public float SpeedWobblePeriod { get; set; } = 2.2f;

	[Property, Group( "Vitesse" ), Range( 0f, 1f )]
	[Description( "Pauses / rattrapages aléatoires : micro‑gel puis rafales." )]
	public float StutterAmount { get; set; } = 0.25f;

	[Property, Group( "Vitesse" )]
	[Description( "Si activé, l’image redevient parfaitement propre de temps en temps (sans corruption)." )]
	public bool EnableCleanBreaths { get; set; } = true;

	[Property, Group( "Vitesse" ), Range( 0f, 1f )]
	[Description( "Après chaque frame glitchée : chance de passer en phase « image normale »." )]
	public float CleanImageChance { get; set; } = 0.14f;

	[Property, Group( "Vitesse" ), Range( 0.05f, 20f )]
	public float CleanImageDurationMin { get; set; } = 0.3f;

	[Property, Group( "Vitesse" ), Range( 0.05f, 25f )]
	public float CleanImageDurationMax { get; set; } = 1.75f;

	[Property, Group( "Look" ), Range( 0f, 2f )]
	[Description( "0 = image propre, 1 = glitch modéré, au-delà = très sale." )]
	public float GlitchIntensity { get; set; } = 0.85f;

	[Property, Group( "Look" ), Range( 4, 64 )]
	public int MacroblockSize { get; set; } = 16;

	[Property, Group( "Look" ), Range( 0f, 1f )]
	[Description( "Bandes horizontales décalées façon flux H corrompu." )]
	public float SliceShiftWeight { get; set; } = 0.55f;

	[Property, Group( "Look" ), Range( 0f, 1f )]
	[Description( "Blocs déplacés / recopiés depuis un mauvais voisin." )]
	public float BlockMisreadWeight { get; set; } = 0.65f;

	[Property, Group( "Look" ), Range( 0f, 1f )]
	[Description( "Blocs « bitrate manquant » (gris / moyenne / flash )." )]
	public float DroppedBlockWeight { get; set; } = 0.45f;

	[Property, Group( "Look" ), Range( 0f, 1f )]
	public float ChromaticBlockWeight { get; set; } = 0.25f;

	private SpotLight _spot;
	private Texture _originalCookie;
	private Texture _outputTexture;
	private Color32[] _clean;
	private Color32[] _work;
	private Color32[] _rowScratch;
	private int _w, _h;
	private float _nextUpdateTime;
	private Texture _lastSourceRef;
	private bool _cacheBuilt;
	private int _rapidGlitchTicksLeft;
	private Texture _pathResolvedTexture;
	private string _lastSourceTexturePath;
	private float _nextBuildLogTime;
	private bool _loggedBuildFail;
	private float _cleanBreathEnd;

	protected override void OnStart()
	{
		_nextUpdateTime = 0f;
		_pathResolvedTexture = null;
		_loggedBuildFail = false;
		_nextBuildLogTime = 0f;
		_cleanBreathEnd = 0f;
	}

	protected override void OnDestroy()
	{
		_spot ??= Components.Get<SpotLight>();
		RestoreOriginalCookie();
		_outputTexture?.Dispose();
		_outputTexture = null;
		_clean = _work = _rowScratch = null;
	}

	protected override void OnUpdate()
	{
		if ( RefreshSourcePixelsNow )
		{
			RefreshSourcePixelsNow = false;
			InvalidateCache();
		}

		if ( SourceImage != _lastSourceRef )
		{
			_lastSourceRef = SourceImage;
			InvalidateCache();
		}

		var pathNorm = SourceTexturePath?.Trim() ?? "";
		if ( pathNorm != _lastSourceTexturePath )
		{
			_lastSourceTexturePath = pathNorm;
			_pathResolvedTexture = null;
			if ( _cacheBuilt )
				InvalidateCache();
		}

		_spot = Components.Get<SpotLight>();
		if ( _spot is null )
			return;

		if ( !GlitchActive )
		{
			_cleanBreathEnd = 0f;
			_rapidGlitchTicksLeft = 0;
			RestoreOriginalCookie();
			return;
		}

		if ( !_cacheBuilt && !TryBuildCpuCache() )
		{
			TryLogBuildFailure();
			return;
		}

		EnsureCookieAssigned();

		if ( UpdatesPerSecond <= 0f || GlitchIntensity <= 0f )
		{
			_cleanBreathEnd = 0f;
			_rapidGlitchTicksLeft = 0;
			Array.Copy( _clean, _work, _clean.Length );
			_outputTexture?.Update( _work.AsSpan() );
			return;
		}

		if ( Time.Now < _nextUpdateTime )
			return;

		if ( IsInCleanBreath() )
		{
			_rapidGlitchTicksLeft = 0;
			Array.Copy( _clean, _work, _clean.Length );
			_outputTexture?.Update( _work.AsSpan() );
			EnsureCookieAssigned();
			_nextUpdateTime = _cleanBreathEnd;
			return;
		}

		RunGlitchPass();

		if ( EnableCleanBreaths && CleanImageChance > 0f && Game.Random.Float( 0f, 1f ) < CleanImageChance )
		{
			var lo = Math.Min( CleanImageDurationMin, CleanImageDurationMax );
			var hi = Math.Max( CleanImageDurationMin, CleanImageDurationMax );
			_cleanBreathEnd = Time.Now + Game.Random.Float( lo, hi );
			Array.Copy( _clean, _work, _clean.Length );
			_outputTexture?.Update( _work.AsSpan() );
			EnsureCookieAssigned();
			_nextUpdateTime = _cleanBreathEnd;
			return;
		}

		_outputTexture?.Update( _work.AsSpan() );
		EnsureCookieAssigned();
		ScheduleNextGlitchStep();
	}

	private bool IsInCleanBreath()
	{
		if ( !EnableCleanBreaths || CleanImageChance <= 0f )
			return false;
		return Time.Now < _cleanBreathEnd;
	}

	private void TryLogBuildFailure()
	{
		if ( _loggedBuildFail || !Game.IsPlaying )
			return;
		if ( Time.Now < _nextBuildLogTime )
			return;
		_nextBuildLogTime = Time.Now + 2f;

		var hasPath = !string.IsNullOrWhiteSpace( SourceTexturePath );
		var hasSrcProp = SourceImage is not null && SourceImage.IsValid;
		var spot = Components.Get<SpotLight>();
		var hasCookie = spot?.Cookie is not null && spot.Cookie.IsValid && spot.Cookie != _outputTexture;
		if ( hasPath || hasSrcProp || hasCookie )
			return;

		Log.Warning( $"[ProjectedLightGlitch] {GameObject.Name}: impossible de construire la texture glitch (source vide, cookie spot vide en jeu). " +
			"Renseigne **Source Texture Path** (même chemin que le cookie) ou **Source Image** sur ce composant." );
		_loggedBuildFail = true;
	}

	/// <summary>Planifie le prochain tick de glitch (délai irrégulier + oscillation de vitesse).</summary>
	private void ScheduleNextGlitchStep()
	{
		if ( _rapidGlitchTicksLeft > 0 )
		{
			_rapidGlitchTicksLeft--;
			_nextUpdateTime = Time.Now + Game.Random.Float( 0.009f, 0.038f );
			return;
		}

		var period = Math.Max( 0.08f, SpeedWobblePeriod );
		var wobble = 1f;
		if ( SpeedWobble > 0.001f )
			wobble += MathF.Sin( Time.Now * (MathF.Tau / period) ) * SpeedWobble;

		var hz = UpdatesPerSecond * SpeedMultiplier * wobble;
		hz = Math.Clamp( hz, 0.2f, 120f );
		var baseDelay = 1f / hz;

		var jitter = 1f;
		if ( TimingIrregularity > 0.001f )
		{
			var j = TimingIrregularity;
			jitter = Game.Random.Float( 1f - j * 0.62f, 1f + j * 1.05f );
		}

		var delay = baseDelay * jitter;

		if ( StutterAmount > 0.001f && Game.Random.Float( 0f, 1f ) < StutterAmount * 0.24f )
			delay += Game.Random.Float( 0.05f, 0.35f );

		if ( StutterAmount > 0.001f && Game.Random.Float( 0f, 1f ) < StutterAmount * 0.18f )
			_rapidGlitchTicksLeft = Game.Random.Int( 2, 9 );

		_nextUpdateTime = Time.Now + Math.Max( 0.004f, delay );
	}

	private void EnsureCookieAssigned()
	{
		if ( _spot is null || _outputTexture is null )
			return;
		if ( _spot.Cookie != _outputTexture )
			_spot.Cookie = _outputTexture;
	}

	private void RestoreOriginalCookie()
	{
		if ( _spot is null )
			return;
		if ( _outputTexture is null || _spot.Cookie != _outputTexture )
			return;
		if ( _originalCookie is not null && _originalCookie.IsValid )
			_spot.Cookie = _originalCookie;
		else if ( SourceImage is not null && SourceImage.IsValid )
			_spot.Cookie = SourceImage;
		else
			_spot.Cookie = null;
	}

	private void InvalidateCache()
	{
		_spot ??= Components.Get<SpotLight>();
		RestoreOriginalCookie();
		_outputTexture?.Dispose();
		_outputTexture = null;
		_cacheBuilt = false;
		_clean = _work = _rowScratch = null;
		_w = _h = 0;
		_nextUpdateTime = 0f;
		_rapidGlitchTicksLeft = 0;
		_cleanBreathEnd = 0f;
	}

	private bool TryBuildCpuCache()
	{
		if ( _spot is null )
			return false;

		var src = ResolveSourceTexture();
		if ( src is null || !src.IsValid )
			return false;

		if ( !src.IsLoaded )
			return false;

		if ( !TryGetPixelsForCookie( src, out var pixels, out var w, out var h ) )
			return false;

		_originalCookie ??= _spot?.Cookie;

		_loggedBuildFail = false;

		_outputTexture?.Dispose();
		_outputTexture = Texture.CreateRenderTarget(
			$"glitch_cookie_{GameObject.Id}",
			ImageFormat.RGBA8888,
			new Vector2( w, h ) );

		_w = w;
		_h = h;
		_clean = new Color32[w * h];
		_work = new Color32[w * h];
		_rowScratch = new Color32[w];
		Array.Copy( pixels, _clean, pixels.Length );
		Array.Copy( _clean, _work, _clean.Length );

		_cacheBuilt = true;
		_lastSourceRef = SourceImage;
		_spot.Cookie = _outputTexture;
		_outputTexture.Update( _work.AsSpan() );
		ScheduleNextGlitchStep();
		return true;
	}

	private Texture ResolveSourceTexture()
	{
		if ( !string.IsNullOrWhiteSpace( SourceTexturePath ) )
		{
			if ( _pathResolvedTexture is null || !_pathResolvedTexture.IsValid )
				_pathResolvedTexture = Texture.Load( SourceTexturePath, warnOnMissing: false );
			if ( _pathResolvedTexture is not null && _pathResolvedTexture.IsValid )
				return _pathResolvedTexture;
		}

		if ( SourceImage is not null && SourceImage.IsValid )
			return SourceImage;

		if ( _spot?.Cookie is not null && _spot.Cookie.IsValid && _spot.Cookie != _outputTexture )
			return _spot.Cookie;

		return null;
	}

	/// <summary>Lit les pixels sur un mip cohérent (en jeu la taille retournée par <see cref="Texture.GetPixels"/> peut ne pas matcher le mip attendu).</summary>
	private bool TryGetPixelsForCookie( Texture src, out Color32[] pixels, out int w, out int h )
	{
		pixels = null;
		w = h = 0;

		var preferredMip = MipForMaxEdge( src.Width, src.Height, CpuProcessMaxEdge );

		bool TryAtMip( int mip, out Color32[] outPix, out int ow, out int oh )
		{
			outPix = null;
			ow = oh = 0;
			if ( mip < 0 || mip > 12 )
				return false;

			DimForMip( src.Width, src.Height, mip, out ow, out oh );

			Color32[] tryPixels;
			try
			{
				tryPixels = src.GetPixels( mip );
			}
			catch
			{
				return false;
			}

			if ( tryPixels is null || tryPixels.Length == 0 )
				return false;
			if ( ow * oh != tryPixels.Length )
				return false;

			outPix = tryPixels;
			return true;
		}

		for ( var m = preferredMip; m <= 12; m++ )
		{
			if ( TryAtMip( m, out pixels, out w, out h ) )
				return true;
		}

		for ( var m = preferredMip - 1; m >= 0; m-- )
		{
			if ( TryAtMip( m, out pixels, out w, out h ) )
				return true;
		}

		try
		{
			var p0 = src.GetPixels( 0 );
			if ( p0 is not null && p0.Length == src.Width * src.Height )
			{
				pixels = p0;
				w = src.Width;
				h = src.Height;
				return true;
			}
		}
		catch
		{
			/* ignore */
		}

		return false;
	}

	private void RunGlitchPass()
	{
		Array.Copy( _clean, _work, _clean.Length );

		var intensity = GlitchIntensity * (Game.IsEditor && !Game.IsPlaying ? 0.35f : 1f);
		if ( intensity <= 0.001f )
			return;

		var blocksW = Math.Max( 1, (_w + MacroblockSize - 1) / MacroblockSize );
		var blocksH = Math.Max( 1, (_h + MacroblockSize - 1) / MacroblockSize );
		var ops = (int)(6f + intensity * 26f * Game.Random.Float( 0.6f, 1.4f ));

		for ( var i = 0; i < ops; i++ )
		{
			var roll = Game.Random.Float( 0f, 1f );

			if ( roll < SliceShiftWeight * 0.34f )
				ApplyHorizontalSliceShift();
			else if ( roll < SliceShiftWeight * 0.34f + BlockMisreadWeight * 0.38f )
				ApplyBlockMisread( blocksW, blocksH );
			else if ( roll < SliceShiftWeight * 0.34f + BlockMisreadWeight * 0.38f + DroppedBlockWeight * 0.35f )
				ApplyDroppedMacroblock( blocksW, blocksH );
			else if ( roll < SliceShiftWeight * 0.34f + BlockMisreadWeight * 0.38f + DroppedBlockWeight * 0.35f + ChromaticBlockWeight * 0.3f )
				ApplyChromaticBlock( blocksW, blocksH );
			else
				ApplyVerticalJitterStrip();
		}
	}

	private void ApplyHorizontalSliceShift()
	{
		var band = Game.Random.Int( 4, Math.Max( 5, _h / 8 ) );
		var y0 = Game.Random.Int( 0, Math.Max( 0, _h - band ) );
		var shift = Game.Random.Int( -_w / 3, _w / 3 );
		if ( shift == 0 )
			shift = Game.Random.Int( 1, 8 ) * (Game.Random.Float( 0f, 1f ) > 0.5f ? 1 : -1);

		for ( var y = y0; y < y0 + band && y < _h; y++ )
		{
			var row = y * _w;
			for ( var x = 0; x < _w; x++ )
			{
				var sx = x - shift;
				sx = (sx % _w + _w) % _w;
				_rowScratch[x] = _work[row + sx];
			}

			for ( var x = 0; x < _w; x++ )
				_work[row + x] = _rowScratch[x];
		}
	}

	private void ApplyBlockMisread( int blocksW, int blocksH )
	{
		var bx = Game.Random.Int( 0, blocksW - 1 );
		var by = Game.Random.Int( 0, blocksH - 1 );
		var ox = Game.Random.Int( -4, 4 );
		var oy = Game.Random.Int( -4, 4 );
		CopyBlock( bx, by, bx + ox, by + oy );
	}

	private void ApplyDroppedMacroblock( int blocksW, int blocksH )
	{
		var bx = Game.Random.Int( 0, blocksW - 1 );
		var by = Game.Random.Int( 0, blocksH - 1 );
		var mode = Game.Random.Int( 0, 2 );
		var x0 = bx * MacroblockSize;
		var y0 = by * MacroblockSize;

		for ( var dy = 0; dy < MacroblockSize && y0 + dy < _h; dy++ )
		{
			for ( var dx = 0; dx < MacroblockSize && x0 + dx < _w; dx++ )
			{
				var idx = (y0 + dy) * _w + (x0 + dx);
				ref var p = ref _work[idx];
				switch ( mode )
				{
					case 0:
						p = new Color32( 28, 28, 32, 255 );
						break;
					case 1:
						{
							var a = (byte)Game.Random.Int( 40, 220 );
							p = new Color32( a, a, a, 255 );
							break;
						}
					default:
						p = new Color32(
							(byte)Game.Random.Int( 0, 255 ),
							(byte)Game.Random.Int( 0, 255 ),
							(byte)Game.Random.Int( 0, 255 ),
							255 );
						break;
				}
			}
		}
	}

	private void ApplyChromaticBlock( int blocksW, int blocksH )
	{
		var bx = Game.Random.Int( 0, blocksW - 1 );
		var by = Game.Random.Int( 0, blocksH - 1 );
		var x0 = bx * MacroblockSize;
		var y0 = by * MacroblockSize;
		var rs = Game.Random.Int( 1, 5 );
		var gs = Game.Random.Int( -4, 4 );
		var bs = Game.Random.Int( 1, 5 );

		for ( var dy = 0; dy < MacroblockSize && y0 + dy < _h; dy++ )
		{
			for ( var dx = 0; dx < MacroblockSize && x0 + dx < _w; dx++ )
			{
				var xr = Math.Clamp( x0 + dx + rs, 0, _w - 1 );
				var xg = Math.Clamp( x0 + dx + gs, 0, _w - 1 );
				var xb = Math.Clamp( x0 + dx - bs, 0, _w - 1 );
				var y = y0 + dy;
				var cr = _work[y * _w + xr];
				var cg = _work[y * _w + xg];
				var cb = _work[y * _w + xb];
				_work[y * _w + (x0 + dx)] = new Color32( cr.r, cg.g, cb.b, (byte)Math.Max( cr.a, Math.Max( cg.a, cb.a ) ) );
			}
		}
	}

	private void ApplyVerticalJitterStrip()
	{
		var col = Game.Random.Int( 0, _w - 1 );
		var hStrip = Game.Random.Int( 2, Math.Max( 3, _h / 10 ) );
		var y0 = Game.Random.Int( 0, Math.Max( 0, _h - hStrip ) );
		var off = Game.Random.Int( -6, 6 );
		for ( var y = y0; y < y0 + hStrip && y < _h; y++ )
		{
			var srcCol = Math.Clamp( col + off, 0, _w - 1 );
			var t = _work[y * _w + srcCol];
			for ( var k = 0; k < 3 && col + k < _w; k++ )
				_work[y * _w + col + k] = t;
		}
	}

	private void CopyBlock( int bx, int by, int srcBx, int srcBy )
	{
		var bw = Math.Max( 1, (_w + MacroblockSize - 1) / MacroblockSize );
		var bh = Math.Max( 1, (_h + MacroblockSize - 1) / MacroblockSize );
		bx = Math.Clamp( bx, 0, bw - 1 );
		by = Math.Clamp( by, 0, bh - 1 );
		srcBx = Math.Clamp( srcBx, 0, bw - 1 );
		srcBy = Math.Clamp( srcBy, 0, bh - 1 );

		var x0 = bx * MacroblockSize;
		var y0 = by * MacroblockSize;
		var sx0 = srcBx * MacroblockSize;
		var sy0 = srcBy * MacroblockSize;

		for ( var dy = 0; dy < MacroblockSize && y0 + dy < _h && sy0 + dy < _h; dy++ )
		{
			for ( var dx = 0; dx < MacroblockSize && x0 + dx < _w && sx0 + dx < _w; dx++ )
				_work[(y0 + dy) * _w + (x0 + dx)] = _work[(sy0 + dy) * _w + (sx0 + dx)];
		}
	}

	private static void DimForMip( int fullW, int fullH, int mip, out int w, out int h )
	{
		w = fullW;
		h = fullH;
		for ( var i = 0; i < mip; i++ )
		{
			w = Math.Max( 1, (w + 1) / 2 );
			h = Math.Max( 1, (h + 1) / 2 );
		}
	}

	private static int MipForMaxEdge( int fullW, int fullH, int maxEdge )
	{
		var mip = 0;
		var w = fullW;
		var h = fullH;
		maxEdge = Math.Max( 32, maxEdge );

		while ( w > maxEdge || h > maxEdge )
		{
			mip++;
			w = Math.Max( 1, (w + 1) / 2 );
			h = Math.Max( 1, (h + 1) / 2 );
		}

		return mip;
	}
}
