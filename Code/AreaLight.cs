using Sandbox;
using System;
using System.Collections.Generic;

[Title( "Area Light" )]
[Category( "Lighting" )]
[Icon( "light_mode" )]
public sealed class AreaLight : Component
{
	public enum AreaShape { Rectangle, Square, Disk, Ellipse }

	// ── Shape ─────────────────────────────────────────────────────────────
	[Property, Group( "Shape" )]
	public AreaShape Shape { get; set; } = AreaShape.Rectangle;

	[Property, Group( "Shape" ), Range( 1f, 5000f )]
	public float Width { get; set; } = 200f;

	[Property, Group( "Shape" ), Range( 1f, 5000f )]
	public float Height { get; set; } = 100f;

	// ── Light ─────────────────────────────────────────────────────────────
	[Property, Group( "Light" )]
	public Color LightColor { get; set; } = Color.White;

	[Property, Group( "Light" ), Range( 0f, 100000f )]
	public float Intensity { get; set; } = 2000f;

	[Property, Group( "Light" ), Range( 10f, 100000f )]
	public float Range { get; set; } = 2000f;

	[Property, Group( "Light" ), Range( 0f, 1f )]
	public float Attenuation { get; set; } = 0.0f;

	// ── Shadows ───────────────────────────────────────────────────────────
	[Property, Group( "Shadows" )]
	public bool CastShadows { get; set; } = true;

	[Property, Group( "Shadows" ), Range( 0f, 1f )]
	public float ShadowHardness { get; set; } = 0f;

	// ── Sampling ──────────────────────────────────────────────────────────
	[Property, Group( "Sampling" ), Range( 1, 32 )]
	public int Samples { get; set; } = 6;

	[Property, Group( "Sampling" )]
	public bool SpreadSamples { get; set; } = true;

	// ── Gizmo ─────────────────────────────────────────────────────────────
	[Property, Group( "Gizmo" )]
	public bool ShowGizmo { get; set; } = true;

	// ── Internal ──────────────────────────────────────────────────────────
	private readonly List<GameObject> _lightObjects = new();

	private AreaShape _prevShape;
	private float _prevWidth, _prevHeight, _prevIntensity, _prevRange, _prevAttenuation, _prevHardness;
	private int _prevSamples;
	private Color _prevColor;
	private bool _prevShadows, _prevSpread;

	protected override void OnEnabled()  => Rebuild();
	protected override void OnDisabled() => ClearLights();
	protected override void OnDestroy()  => ClearLights();

	protected override void OnUpdate()
	{
		if ( IsDirty() )
			Rebuild();
	}

	private bool IsDirty()
	{
		return Shape         != _prevShape
			|| Width         != _prevWidth
			|| Height        != _prevHeight
			|| Intensity     != _prevIntensity
			|| Range         != _prevRange
			|| Attenuation   != _prevAttenuation
			|| ShadowHardness != _prevHardness
			|| Samples       != _prevSamples
			|| LightColor    != _prevColor
			|| CastShadows   != _prevShadows
			|| SpreadSamples != _prevSpread;
	}

	private void SaveState()
	{
		_prevShape       = Shape;
		_prevWidth       = Width;
		_prevHeight      = Height;
		_prevIntensity   = Intensity;
		_prevRange       = Range;
		_prevAttenuation = Attenuation;
		_prevHardness    = ShadowHardness;
		_prevSamples     = Samples;
		_prevColor       = LightColor;
		_prevShadows     = CastShadows;
		_prevSpread      = SpreadSamples;
	}

	private void ClearLights()
	{
		foreach ( var go in _lightObjects )
			if ( go.IsValid() ) go.Destroy();
		_lightObjects.Clear();
	}

	private void Rebuild()
	{
		ClearLights();

		var positions = GetSamplePositions();
		int count     = positions.Count;
		float perLight = count > 0 ? Intensity / count : Intensity;

		foreach ( var localPos in positions )
		{
			var go = new GameObject( true, "AreaLight_Sample" );
			go.Parent        = GameObject;
			go.LocalPosition = localPos;

			var pl = go.Components.Create<PointLight>();
			pl.LightColor     = LightColor.WithAlpha( perLight );
			pl.Radius         = Range;
			pl.Attenuation    = Attenuation;
			pl.Shadows        = CastShadows;
			pl.ShadowHardness = ShadowHardness;

			_lightObjects.Add( go );
		}

		SaveState();
	}

	private List<Vector3> GetSamplePositions()
	{
		var pts = new List<Vector3>();
		int n   = Math.Max( Samples, 1 );

		switch ( Shape )
		{
			case AreaShape.Square:   FillGrid( pts, n, Width, Width );               break;
			case AreaShape.Disk:     FillDisk( pts, n, Width * .5f, Width * .5f );   break;
			case AreaShape.Ellipse:  FillDisk( pts, n, Width * .5f, Height * .5f );  break;
			default:                 FillGrid( pts, n, Width, Height );               break;
		}

		return pts;
	}

	private void FillGrid( List<Vector3> pts, int count, float w, float h )
	{
		if ( count == 1 ) { pts.Add( Vector3.Zero ); return; }

		int cols = Math.Max( (int)MathF.Sqrt( count ), 1 );
		int rows = (int)MathF.Ceiling( count / (float)cols );

		for ( int r = 0; r < rows && pts.Count < count; r++ )
		for ( int c = 0; c < cols && pts.Count < count; c++ )
		{
			float x = cols > 1 ? MathX.Lerp( -w * .5f, w * .5f, c / (float)(cols - 1) ) : 0f;
			float y = rows > 1 ? MathX.Lerp( -h * .5f, h * .5f, r / (float)(rows - 1) ) : 0f;

			if ( SpreadSamples )
			{
				x += Game.Random.Float( -w / cols * .35f, w / cols * .35f );
				y += Game.Random.Float( -h / rows * .35f, h / rows * .35f );
			}

			pts.Add( new Vector3( x, y, 0f ) );
		}
	}

	private void FillDisk( List<Vector3> pts, int count, float rx, float ry )
	{
		if ( count == 1 ) { pts.Add( Vector3.Zero ); return; }

		for ( int i = 0; i < count; i++ )
		{
			float t     = i / (float)count;
			float angle = t * MathF.PI * 2f;
			float rad   = SpreadSamples ? MathF.Sqrt( Game.Random.Float() ) : MathF.Sqrt( t );

			pts.Add( new Vector3( MathF.Cos( angle ) * rx * rad,
			                      MathF.Sin( angle ) * ry * rad, 0f ) );
		}
	}

	protected override void DrawGizmos()
	{
		if ( !ShowGizmo ) return;

		Gizmo.Transform = Transform.World;
		Gizmo.Draw.Color = LightColor.WithAlpha( 0.9f );
		Gizmo.Draw.LineThickness = 1.5f;

		switch ( Shape )
		{
			case AreaShape.Rectangle:
			case AreaShape.Square:
			{
				float w = Width;
				float h = Shape == AreaShape.Square ? Width : Height;
				Gizmo.Draw.Line( new Vector3( -w*.5f, -h*.5f, 0 ), new Vector3(  w*.5f, -h*.5f, 0 ) );
				Gizmo.Draw.Line( new Vector3(  w*.5f, -h*.5f, 0 ), new Vector3(  w*.5f,  h*.5f, 0 ) );
				Gizmo.Draw.Line( new Vector3(  w*.5f,  h*.5f, 0 ), new Vector3( -w*.5f,  h*.5f, 0 ) );
				Gizmo.Draw.Line( new Vector3( -w*.5f,  h*.5f, 0 ), new Vector3( -w*.5f, -h*.5f, 0 ) );
				break;
			}
			case AreaShape.Disk:
			case AreaShape.Ellipse:
			{
				float rx = Width  * .5f;
				float ry = Shape == AreaShape.Disk ? rx : Height * .5f;
				int segs = 48;
				for ( int i = 0; i < segs; i++ )
				{
					float a0 = i       / (float)segs * MathF.PI * 2f;
					float a1 = (i + 1) / (float)segs * MathF.PI * 2f;
					Gizmo.Draw.Line(
						new Vector3( MathF.Cos( a0 ) * rx, MathF.Sin( a0 ) * ry, 0f ),
						new Vector3( MathF.Cos( a1 ) * rx, MathF.Sin( a1 ) * ry, 0f ) );
				}
				break;
			}
		}

		Gizmo.Draw.Arrow( Vector3.Zero, Vector3.Forward * 80f, 20f, 5f );
	}
}
