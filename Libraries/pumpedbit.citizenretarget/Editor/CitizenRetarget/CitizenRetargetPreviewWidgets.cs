#nullable enable
using Editor.Widgets;
using NVector3 = System.Numerics.Vector3;

namespace Editor.CitizenRetarget;

internal sealed class RetargetLivePreviewWidget : Widget
{
	private readonly SceneCanvas _sceneCanvas;
	private readonly Label _statusLabel;
	private readonly FloatSlider? _timeline;
	private readonly FloatSlider? _zoomSlider;
	private readonly ComboBox? _speedCombo;
	private readonly Checkbox? _followTarget;
	private readonly bool _compactMode;
	private float _playbackRate = 1.0f;
	private bool _isPaused;
	private bool _isSeeking;
	private bool _syncingCameraControls;

	public RetargetLivePreviewWidget( Widget parent, bool compactMode = false ) : base( parent )
	{
		_compactMode = compactMode;
		var rootLayout = Layout.Column();
		Layout = rootLayout;
		rootLayout.Margin = 0;
		rootLayout.Spacing = compactMode ? 4 : 6;

		_sceneCanvas = new SceneCanvas( this );

		if ( !compactMode )
		{
			var toolbar = new ToolBar( this );
			toolbar.SetIconSize( 16 );
			toolbar.AddOption( new Option( "Play/Pause", "play_arrow", () => TogglePlayPause() ) );
			toolbar.AddOption( new Option( "Restart", "replay", Restart ) );
			toolbar.AddOption( new Option( "Frame Character", "center_focus_strong", FrameCharacter ) );
			toolbar.AddOption( new Option( "Reset View", "home", ResetCamera ) );
			toolbar.AddSeparator();
			toolbar.AddOption( new Option( "Orbit Left", "rotate_left", OrbitLeft ) );
			toolbar.AddOption( new Option( "Orbit Right", "rotate_right", OrbitRight ) );
			toolbar.AddOption( new Option( "Tilt Up", "keyboard_arrow_up", TiltUp ) );
			toolbar.AddOption( new Option( "Tilt Down", "keyboard_arrow_down", TiltDown ) );
			toolbar.AddOption( new Option( "Zoom In", "zoom_in", ZoomIn ) );
			toolbar.AddOption( new Option( "Zoom Out", "zoom_out", ZoomOut ) );
			toolbar.AddSeparator();

			var speedHost = new Widget( toolbar );
			var speedLayout = Layout.Row();
			speedHost.Layout = speedLayout;
			speedLayout.Spacing = 4;
			speedLayout.Add( new Label( "Speed" ) );
			_speedCombo = speedLayout.Add( new ComboBox() );
			_speedCombo.AddItem( "0.25x" );
			_speedCombo.AddItem( "0.5x" );
			_speedCombo.AddItem( "1.0x" );
			_speedCombo.CurrentIndex = 2;
			_speedCombo.ItemChanged += ApplyPlaybackRate;
			toolbar.AddWidget( speedHost );

			rootLayout.Add( toolbar );

			var cameraControls = new Widget( this );
			var cameraLayout = Layout.Row();
			cameraControls.Layout = cameraLayout;
			cameraLayout.Spacing = 8;
			cameraLayout.Add( new Label( "Camera" ) );
			_followTarget = cameraLayout.Add( new Checkbox( "Follow" ) );
			TrySetFollowTargetState( CheckState.On );
			_followTarget.StateChanged += _ =>
			{
				if ( _syncingCameraControls )
					return;

				ApplyFollowTargetToggle();
			};
			cameraLayout.Add( new Label( "Zoom" ) );
			_zoomSlider = cameraLayout.Add( new FloatSlider( cameraControls ), 1 );
			_zoomSlider.Minimum = SceneCanvas.MinimumZoomFactor;
			_zoomSlider.Maximum = SceneCanvas.MaximumZoomFactor;
			TrySetZoomSliderValue( SceneCanvas.DefaultZoomFactor );
			_zoomSlider.OnValueEdited = () =>
			{
				if ( _syncingCameraControls )
					return;

				ApplyZoomSlider();
			};
			rootLayout.Add( cameraControls );
		}
		else
		{
			_speedCombo = null;
			_followTarget = null;
			_zoomSlider = null;
		}

		_sceneCanvas.CameraStateChanged += SyncCameraControls;
		rootLayout.Add( _sceneCanvas, 1 );

		var footer = new Widget( this );
		var footerLayout = Layout.Column();
		footer.Layout = footerLayout;
		footerLayout.Spacing = compactMode ? 2 : 4;
		rootLayout.Add( footer );

		_statusLabel = footerLayout.Add( new Label.Body( "Live preview is waiting for a compiled Citizen animation." ) { Color = Theme.Text.WithAlpha( 0.78f ) } );
		if ( compactMode )
		{
			_timeline = null;
		}
		else
		{
			_timeline = footerLayout.Add( new FloatSlider( footer ) );
			_timeline.Minimum = 0;
			_timeline.Maximum = 1;
			_timeline.OnValueEdited = () =>
			{
				_isSeeking = true;
				_sceneCanvas.SeekNormalized( _timeline.Value );
				_isSeeking = false;
			};
		}
	}

	public void LoadAnimation( string vmdlResourcePath, string sequenceName )
	{
		_sceneCanvas.LoadAnimation( vmdlResourcePath, sequenceName );
		_statusLabel.Text = string.IsNullOrWhiteSpace( sequenceName )
			? $"Loaded {vmdlResourcePath}"
			: $"Loaded {sequenceName} from {vmdlResourcePath}";
		_isPaused = false;
		ApplyPlaybackRate();
		TrySetTimelineValue( 0f );
		SyncCameraControls();
	}

	public void ClearAnimation( string message )
	{
		_sceneCanvas.ClearAnimation();
		_statusLabel.Text = string.IsNullOrWhiteSpace( message )
			? "Live preview is waiting for a compiled Citizen animation."
			: message;
		_isPaused = false;
		TrySetTimelineValue( 0f );
		SyncCameraControls();
	}

	public void Restart()
	{
		_sceneCanvas.SeekNormalized( 0 );
		TrySetTimelineValue( 0f );
	}

	public void ResetCamera()
	{
		_sceneCanvas.ResetCamera();
	}

	public void FrameCharacter()
	{
		_sceneCanvas.FrameCharacter();
	}

	public void TogglePlayPause()
	{
		_isPaused = !_isPaused;
		if ( _isPaused )
		{
			_sceneCanvas.SetPaused( true );
			return;
		}

		ApplyPlaybackRate();
	}

	private void ApplyPlaybackRate()
	{
		var speedText = _speedCombo?.CurrentText ?? "1.0x";
		_playbackRate = speedText switch
		{
			"0.25x" => 0.25f,
			"0.5x" => 0.5f,
			_ => 1.0f
		};

		if ( _isPaused )
			return;

		_sceneCanvas.SetPlaybackRate( _playbackRate );
	}

	private void OrbitLeft() => _sceneCanvas.OrbitBy( -18f, 0f );
	private void OrbitRight() => _sceneCanvas.OrbitBy( 18f, 0f );
	private void TiltUp() => _sceneCanvas.OrbitBy( 0f, 8f );
	private void TiltDown() => _sceneCanvas.OrbitBy( 0f, -8f );
	private void ZoomIn() => _sceneCanvas.AdjustZoom( 0.88f );
	private void ZoomOut() => _sceneCanvas.AdjustZoom( 1.12f );
	private void ApplyFollowTargetToggle()
	{
		if ( _followTarget is null )
			return;

		_sceneCanvas.SetFollowTarget( TryGetFollowTargetState() == CheckState.On );
	}
	private void ApplyZoomSlider() => _sceneCanvas.SetZoomFactor( TryGetZoomSliderValue() );

	[EditorEvent.Frame]
	private void Frame()
	{
		if ( _isSeeking )
			return;

		TrySetTimelineValue( _sceneCanvas.TimeNormalized );
	}

	private void SyncCameraControls()
	{
		if ( _compactMode )
			return;

		_syncingCameraControls = true;
		TrySetZoomSliderValue( _sceneCanvas.ZoomFactor );
		TrySetFollowTargetState( _sceneCanvas.FollowTarget ? CheckState.On : CheckState.Off );
		_syncingCameraControls = false;
	}

	private CheckState TryGetFollowTargetState()
	{
		if ( _followTarget is null )
			return CheckState.Off;

		try
		{
			return _followTarget.State;
		}
		catch
		{
			return CheckState.Off;
		}
	}

	private float TryGetZoomSliderValue()
	{
		if ( _zoomSlider is null )
			return SceneCanvas.DefaultZoomFactor;

		try
		{
			return _zoomSlider.Value;
		}
		catch
		{
			return SceneCanvas.DefaultZoomFactor;
		}
	}

	private void TrySetFollowTargetState( CheckState state )
	{
		if ( _followTarget is null )
			return;

		try
		{
			_followTarget.State = state;
		}
		catch
		{
		}
	}

	private void TrySetZoomSliderValue( float value )
	{
		if ( _zoomSlider is null )
			return;

		try
		{
			_zoomSlider.Value = value;
		}
		catch
		{
		}
	}

	private void TrySetTimelineValue( float value )
	{
		if ( _timeline is null )
			return;

		try
		{
			_timeline.Value = value;
		}
		catch
		{
		}
	}

	private sealed class SceneCanvas : SceneRenderingWidget
	{
		public const float DefaultZoomFactor = 1.0f;
		public const float MinimumZoomFactor = 0.65f;
		public const float MaximumZoomFactor = 2.75f;
		private const float DefaultYaw = 155f;
		private const float DefaultPitch = 10f;

		private SceneWorld? _world;
		private SceneModel? _sceneModel;
		private string _currentSequence = string.Empty;
		private float _appliedPlaybackRate;
		private float _orbitYaw = DefaultYaw;
		private float _orbitPitch = DefaultPitch;
		private float _zoomFactor = DefaultZoomFactor;
		private bool _followTarget = true;
		private Vector3 _focusPoint;
		private Vector3 _panOffset;
		private float _contentRadius = 48f;
		private bool _hasValidFraming;
		private bool _orbitDragging;
		private bool _panDragging;
		private Vector2 _lastMousePosition;

		public Action? CameraStateChanged { get; set; }

		public float TimeNormalized
		{
			get
			{
				var sequence = _sceneModel?.CurrentSequence;
				return sequence?.TimeNormalized ?? 0f;
			}
		}

		public float ZoomFactor => _zoomFactor;
		public bool FollowTarget => _followTarget;

		public SceneCanvas( Widget parent ) : base( parent )
		{
			MouseTracking = true;
			FocusMode = FocusMode.Click;
			var scene = Scene.CreateEditorScene();
			Scene = scene;
			using ( scene.Push() )
			{
				var camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
				Camera = camera;
				camera.BackgroundColor = Theme.SurfaceBackground;
				camera.Enabled = true;
				camera.ZNear = 0.1f;
				camera.ZFar = 5000f;
				camera.FieldOfView = 35f;
			}

			var world = scene.SceneWorld;
			_world = world;
			if ( world is not null )
			{
				new ScenePointLight( world, new Vector3( 120, 120, 160 ), 600, Color.White * 6 ) { ShadowsEnabled = false };
				new ScenePointLight( world, new Vector3( -150, -50, 120 ), 600, Color.White * 3 ) { ShadowsEnabled = false };
			}

			ResetCamera();
		}

		public void LoadAnimation( string vmdlResourcePath, string sequenceName )
		{
			var world = _world;
			if ( world is null )
				return;

			ClearAnimation();

			var model = Model.Load( vmdlResourcePath );
			if ( model is null || model.IsError )
				return;

			_sceneModel = new SceneModel( world, model, Transform.Zero );
			_sceneModel.UseAnimGraph = false;
			_sceneModel.ColorTint = Color.White;
			_sceneModel.Update( 0f );

			var sequenceToLoad = string.IsNullOrWhiteSpace( sequenceName ) && model.AnimationCount > 0
				? model.GetAnimationName( 0 )
				: sequenceName;
			_currentSequence = sequenceToLoad ?? string.Empty;
			var currentSequence = _sceneModel.CurrentSequence;
			if ( currentSequence is not null && !string.IsNullOrWhiteSpace( _currentSequence ) )
			{
				currentSequence.Name = _currentSequence;
			}

			if ( currentSequence is not null )
				currentSequence.TimeNormalized = 0f;

			_sceneModel.Update( 0f );
			ResetCamera();
		}

		public void ClearAnimation()
		{
			_currentSequence = string.Empty;
			_sceneModel?.Delete();
			_sceneModel = null;
			_appliedPlaybackRate = 0f;
			_focusPoint = Vector3.Zero;
			_panOffset = Vector3.Zero;
			_contentRadius = 48f;
			_hasValidFraming = false;
			_orbitDragging = false;
			_panDragging = false;
			NotifyCameraStateChanged();
		}

		public void SetPlaybackRate( float rate )
		{
			if ( _sceneModel is null )
				return;

			_appliedPlaybackRate = rate;
			_sceneModel.PlaybackRate = rate;
		}

		public void SetPaused( bool paused )
		{
			if ( _sceneModel is null )
				return;

			_appliedPlaybackRate = paused ? 0f : MathF.Max( _appliedPlaybackRate, 0.25f );
			_sceneModel.PlaybackRate = _appliedPlaybackRate;
		}

		public void SeekNormalized( float normalizedTime )
		{
			if ( _sceneModel is null )
				return;

			var currentSequence = _sceneModel.CurrentSequence;
			if ( currentSequence is null )
				return;

			currentSequence.TimeNormalized = Math.Clamp( normalizedTime, 0f, 1f );
			_sceneModel.Update( 0f );
		}

		public void ResetCamera()
		{
			_orbitYaw = DefaultYaw;
			_orbitPitch = DefaultPitch;
			_zoomFactor = DefaultZoomFactor;
			_panOffset = Vector3.Zero;
			_followTarget = true;
			FrameCharacter();
			NotifyCameraStateChanged();
		}

		public void FrameCharacter()
		{
			var camera = Camera;

			if ( _sceneModel is null )
			{
				if ( camera is not null && camera.IsValid() )
				{
					camera.WorldPosition = new Vector3( 140, -180, 80 );
					camera.WorldRotation = Rotation.LookAt( Vector3.Zero - camera.WorldPosition, Vector3.Up );
				}

				return;
			}

			var idealFocus = CalculateIdealFocusPoint();
			_focusPoint = idealFocus;
			_panOffset = Vector3.Zero;
			_hasValidFraming = true;
			UpdateCameraTransform();
			NotifyCameraStateChanged();
		}

		public void OrbitBy( float yawDelta, float pitchDelta )
		{
			_orbitYaw += yawDelta;
			_orbitPitch = Math.Clamp( _orbitPitch + pitchDelta, -75f, 75f );
			UpdateCameraTransform();
			NotifyCameraStateChanged();
		}

		public void AdjustZoom( float scaleMultiplier )
		{
			if ( scaleMultiplier <= 0f )
				return;

			_zoomFactor = Math.Clamp( _zoomFactor * scaleMultiplier, MinimumZoomFactor, MaximumZoomFactor );
			UpdateCameraTransform();
			NotifyCameraStateChanged();
		}

		public void SetZoomFactor( float zoomFactor )
		{
			_zoomFactor = Math.Clamp( zoomFactor, MinimumZoomFactor, MaximumZoomFactor );
			UpdateCameraTransform();
			NotifyCameraStateChanged();
		}

		public void SetFollowTarget( bool followTarget )
		{
			_followTarget = followTarget;
			if ( _followTarget && _sceneModel is not null )
			{
				_focusPoint = CalculateIdealFocusPoint();
				_panOffset = Vector3.Zero;
				_hasValidFraming = true;
			}

			UpdateCameraTransform();
			NotifyCameraStateChanged();
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );
			_lastMousePosition = e.LocalPosition;

			if ( e.LeftMouseButton )
			{
				_orbitDragging = true;
				Cursor = CursorShape.ClosedHand;
			}
			else if ( e.RightMouseButton || e.MiddleMouseButton )
			{
				_panDragging = true;
				Cursor = CursorShape.SizeAll;
			}
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			_orbitDragging = false;
			_panDragging = false;
			Cursor = CursorShape.Arrow;
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );

			var delta = e.LocalPosition - _lastMousePosition;
			_lastMousePosition = e.LocalPosition;

			if ( _orbitDragging )
			{
				_orbitYaw -= delta.x * 0.35f;
				_orbitPitch = Math.Clamp( _orbitPitch + delta.y * 0.25f, -75f, 75f );
				UpdateCameraTransform();
				return;
			}

			var camera = Camera;
			if ( _panDragging && camera is not null && camera.IsValid() )
			{
				var distance = ComputeCameraDistance();
				var unitsPerPixel = Math.Max( distance * 0.0018f, 0.03f );
				_panOffset += (-camera.WorldRotation.Right * delta.x + camera.WorldRotation.Up * delta.y) * unitsPerPixel;
				UpdateCameraTransform();
				return;
			}
		}

		protected override void OnMouseWheel( WheelEvent e )
		{
			base.OnMouseWheel( e );
			AdjustZoom( e.Delta > 0 ? 0.9f : 1.1f );
			e.Accept();
		}

		protected override void PreFrame()
		{
			var scene = Scene;
			scene?.EditorTick( RealTime.Now, RealTime.Delta );

			if ( _sceneModel is null )
				return;

			var currentSequence = _sceneModel.CurrentSequence;
			if ( _appliedPlaybackRate > 0f && currentSequence is not null && currentSequence.TimeNormalized >= 0.999f )
			{
				currentSequence.TimeNormalized = 0f;
			}

			_sceneModel.Update( RealTime.Delta );
			UpdateFramingState();
			UpdateCameraTransform();
		}

		public override void OnDestroyed()
		{
			base.OnDestroyed();
			ClearAnimation();
			var scene = Scene;
			scene?.Destroy();
			Scene = null;
			_world = null;
		}

		private void UpdateFramingState()
		{
			if ( _sceneModel is null )
				return;

			var idealFocus = CalculateIdealFocusPoint();
			var bounds = _sceneModel.Bounds;
			var radius = Math.Max( bounds.Size.Length * 0.5f, 22f );
			_contentRadius = radius;

			if ( !_hasValidFraming )
			{
				_focusPoint = idealFocus;
				_hasValidFraming = true;
				return;
			}

			if ( _followTarget )
			{
				var lerp = Math.Clamp( RealTime.Delta * 8f, 0f, 1f );
				_focusPoint = Vector3.Lerp( _focusPoint, idealFocus, lerp );
			}
		}

		private Vector3 CalculateIdealFocusPoint()
		{
			if ( _sceneModel is null )
				return Vector3.Zero;

			var bounds = _sceneModel.Bounds;
			var focusZ = bounds.Mins.z.LerpTo( bounds.Maxs.z, 0.58f );
			return bounds.Center.WithZ( focusZ );
		}

		private float ComputeCameraDistance()
		{
			var camera = Camera;
			if ( camera is null || !camera.IsValid() )
				return 180f;

			var baseDistance = MathX.SphereCameraDistance( _contentRadius * 1.1f, camera.FieldOfView );
			var aspect = Math.Max( Size.x / Math.Max( Size.y, 1f ), 0.1f );
			if ( aspect < 1f )
			{
				baseDistance *= MathF.Sqrt( 1f / aspect );
			}

			return Math.Clamp( baseDistance * _zoomFactor, 32f, 4096f );
		}

		private void UpdateCameraTransform()
		{
			var camera = Camera;
			if ( camera is null || !camera.IsValid() )
				return;

			var lookAngle = new Angles( _orbitPitch, _orbitYaw, 0 );
			var lookRotation = lookAngle.ToRotation();
			var focus = _focusPoint + _panOffset;
			var distance = ComputeCameraDistance();
			var cameraPosition = focus + lookRotation.Backward * distance;
			camera.WorldPosition = cameraPosition;
			camera.WorldRotation = Rotation.LookAt( focus - cameraPosition, Vector3.Up );
		}

		private void NotifyCameraStateChanged()
		{
			CameraStateChanged?.Invoke();
		}
	}
}

internal sealed class RetargetSourceFacingPreviewWidget : Widget
{
	private readonly FacingCanvas _canvas;
	private readonly Label _statusLabel;

	public RetargetSourceFacingPreviewWidget( Widget parent ) : base( parent )
	{
		MinimumHeight = 280f;
		var root = Layout.Column();
		Layout = root;
		root.Margin = 0;
		root.Spacing = 6;

		var controls = new Widget( this );
		var controlsLayout = Layout.Row();
		controls.Layout = controlsLayout;
			controlsLayout.Spacing = 6;
			controlsLayout.Add( new Label( "Source Preview" ) );
			var resetButton = controlsLayout.Add( new Button( "Reset View" ) );
			if ( resetButton is not null )
			{
				resetButton.Clicked += () => _canvas?.ResetCamera();
			}
			controlsLayout.AddStretchCell();
			root.Add( controls );

		_canvas = new FacingCanvas( this );
		root.Add( _canvas, 1 );

		_statusLabel = root.Add( new Label.Body( "Scan a source FBX to inspect its facing." ) { Color = Theme.Text.WithAlpha( 0.72f ) } );
	}

	public void UpdatePreview( IReadOnlyList<NativeAuditBoneInfo>? bones, Vector3 effectiveEulerDegrees, string? message = null )
	{
		var resolvedMessage = string.IsNullOrWhiteSpace( message )
			? "Green arrow = Citizen front. Yellow arrow = current source facing. Rotate until both arrows point the same way."
			: message.Trim();
		_statusLabel.Text = resolvedMessage;
		_canvas.UpdateSkeleton( bones ?? Array.Empty<NativeAuditBoneInfo>(), effectiveEulerDegrees );
	}

	private sealed class FacingCanvas : SceneRenderingWidget
	{
		private const float DefaultYaw = 135f;
		private const float DefaultPitch = 18f;
		private const float MinimumZoomFactor = 0.6f;
		private const float MaximumZoomFactor = 2.8f;
		private static readonly Color SourceFacingColor = new Color( 0.96f, 0.79f, 0.24f );
		private static readonly Color PelvisMarkerColor = new Color( 0.98f, 0.86f, 0.36f );
		private static readonly Color HeadMarkerColor = new Color( 0.45f, 0.78f, 1.0f );

		private readonly List<(Vector3 Start, Vector3 End)> _segments = new();
		private readonly List<Vector3> _points = new();
		private Vector3 _sourceFacingArrowStart;
		private Vector3 _sourceFacingArrowEnd;
		private bool _hasSourceFacingArrow;
		private Vector3 _pelvisMarker;
		private bool _hasPelvisMarker;
		private Vector3 _headMarker;
		private bool _hasHeadMarker;
		private float _orbitYaw = DefaultYaw;
		private float _orbitPitch = DefaultPitch;
		private float _zoomFactor = 1.0f;
		private bool _orbitDragging;
		private Vector2 _lastMousePosition;
		private Vector3 _focusPoint;
		private float _contentRadius = 42f;

		public FacingCanvas( Widget parent ) : base( parent )
		{
			MinimumHeight = 230f;
			MouseTracking = true;
			FocusMode = FocusMode.Click;
			Scene = Scene.CreateEditorScene();

			using ( Scene.Push() )
			{
				var camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
				Camera = camera;
				camera.BackgroundColor = Theme.SurfaceBackground;
				camera.Enabled = true;
				camera.ZNear = 0.1f;
				camera.ZFar = 5000f;
				camera.FieldOfView = 40f;

				var ambient = new GameObject( true, "light" ).GetOrAddComponent<AmbientLight>( false );
				ambient.Color = Theme.Text.WithAlpha( 0.08f );
				ambient.Enabled = true;

				var light = new GameObject( true, "light" ).GetOrAddComponent<DirectionalLight>( false );
				light.WorldRotation = Rotation.From( 35f, 35f, 0f );
				light.LightColor = Color.White;
				light.Enabled = true;
			}

			ResetCamera();
		}

		public void UpdateSkeleton( IReadOnlyList<NativeAuditBoneInfo> bones, Vector3 effectiveEulerDegrees )
		{
			_segments.Clear();
			_points.Clear();
			_hasSourceFacingArrow = false;
			_hasPelvisMarker = false;
			_hasHeadMarker = false;

			if ( bones.Count == 0 )
			{
				_focusPoint = Vector3.Zero;
				_contentRadius = 42f;
				UpdateCameraTransform();
				Update();
				return;
			}

			var upAxis = DetermineUpAxis( bones );
			var previewPoints = new Dictionary<string, Vector3>( StringComparer.OrdinalIgnoreCase );
			foreach ( var bone in bones )
			{
				var rotated = RotateSourceOrientation( bone.WorldTransform.Translation, effectiveEulerDegrees );
				var previewPoint = ConvertToPreviewSpace( rotated, upAxis );
				previewPoints[bone.Name] = previewPoint;
				_points.Add( previewPoint );
			}

			foreach ( var bone in bones )
			{
				if ( string.IsNullOrWhiteSpace( bone.ParentName ) )
					continue;

				if ( !previewPoints.TryGetValue( bone.ParentName, out var parentPoint ) || !previewPoints.TryGetValue( bone.Name, out var childPoint ) )
					continue;

				_segments.Add( (parentPoint, childPoint) );
			}

			if ( TryBuildFacingArrow( bones, upAxis, effectiveEulerDegrees, out var arrowStart, out var arrowEnd ) )
			{
				_sourceFacingArrowStart = arrowStart;
				_sourceFacingArrowEnd = arrowEnd;
				_hasSourceFacingArrow = true;
			}

			if ( TryFindPreviewMarker( bones, upAxis, effectiveEulerDegrees, IsPelvisBoneName, out var pelvisMarker ) )
			{
				_pelvisMarker = pelvisMarker;
				_hasPelvisMarker = true;
			}

			if ( TryFindPreviewMarker( bones, upAxis, effectiveEulerDegrees, IsHeadBoneName, out var headMarker ) )
			{
				_headMarker = headMarker;
				_hasHeadMarker = true;
			}

			var mins = new Vector3(
				_points.Min( point => point.x ),
				_points.Min( point => point.y ),
				_points.Min( point => point.z ) );
			var maxs = new Vector3(
				_points.Max( point => point.x ),
				_points.Max( point => point.y ),
				_points.Max( point => point.z ) );
			_focusPoint = (mins + maxs) * 0.5f;
			_contentRadius = MathF.Max( (maxs - mins).Length * 0.55f, 24f );
			UpdateCameraTransform();
			Update();
		}

		public void ResetCamera()
		{
			_orbitYaw = DefaultYaw;
			_orbitPitch = DefaultPitch;
			_zoomFactor = 1.0f;
			UpdateCameraTransform();
			Update();
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );
			_lastMousePosition = e.LocalPosition;
			if ( e.LeftMouseButton )
			{
				_orbitDragging = true;
				Cursor = CursorShape.ClosedHand;
			}
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			_orbitDragging = false;
			Cursor = CursorShape.Arrow;
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );
			var delta = e.LocalPosition - _lastMousePosition;
			_lastMousePosition = e.LocalPosition;
			if ( !_orbitDragging )
				return;

			_orbitYaw -= delta.x * 0.32f;
			_orbitPitch = Math.Clamp( _orbitPitch + delta.y * 0.22f, -75f, 75f );
			UpdateCameraTransform();
			Update();
		}

		protected override void OnMouseWheel( WheelEvent e )
		{
			base.OnMouseWheel( e );
			var multiplier = e.Delta > 0 ? 0.9f : 1.1f;
			_zoomFactor = Math.Clamp( _zoomFactor * multiplier, MinimumZoomFactor, MaximumZoomFactor );
			UpdateCameraTransform();
			Update();
			e.Accept();
		}

		protected override void PreFrame()
		{
			Scene?.EditorTick( RealTime.Now, RealTime.Delta );
			DrawSkeletonPreview();
		}

		public override void OnDestroyed()
		{
			base.OnDestroyed();
			Scene?.Destroy();
			Scene = null;
		}

		private void DrawSkeletonPreview()
		{
			Gizmo.Draw.Grid( 0f, Gizmo.GridAxis.XY );
			DrawCitizenFrontGuide();

			Gizmo.Draw.LineThickness = 4f;
			Gizmo.Draw.Color = Theme.Primary.WithAlpha( 0.95f );
			foreach ( var segment in _segments )
				Gizmo.Draw.Line( segment.Start, segment.End );

			if ( _hasSourceFacingArrow )
			{
				Gizmo.Draw.LineThickness = 6f;
				Gizmo.Draw.Color = SourceFacingColor;
				Gizmo.Draw.Arrow( _sourceFacingArrowStart, _sourceFacingArrowEnd, 11f, 7f );
			}

			if ( _hasPelvisMarker )
			{
				Gizmo.Draw.Color = PelvisMarkerColor;
				Gizmo.Draw.LineSphere( new Sphere( _pelvisMarker, 1.15f ), 10 );
			}

			if ( _hasHeadMarker )
			{
				Gizmo.Draw.Color = HeadMarkerColor;
				Gizmo.Draw.LineSphere( new Sphere( _headMarker, 1.2f ), 10 );
			}

			Gizmo.Draw.Color = Theme.Text.WithAlpha( 0.86f );
			foreach ( var point in _points )
				Gizmo.Draw.LineSphere( new Sphere( point, 0.65f ), 6 );
		}

		private void DrawCitizenFrontGuide()
		{
			var groundOrigin = _hasPelvisMarker
				? new Vector3( _pelvisMarker.x, _pelvisMarker.y, _pelvisMarker.z - MathF.Max( _contentRadius * 0.46f, 18f ) )
				: Vector3.Zero;
			var arrowLength = MathF.Max( 42f, _contentRadius * 0.85f );
			Gizmo.Draw.LineThickness = 6f;
			Gizmo.Draw.Color = Theme.Green;
			Gizmo.Draw.Arrow( groundOrigin + Vector3.Up * 3f, groundOrigin + Vector3.Up * 3f + Vector3.Forward * arrowLength, 13f, 8f );
		}

		private void UpdateCameraTransform()
		{
			var camera = Camera;
			if ( camera is null || !camera.IsValid() )
				return;

			var distance = Math.Clamp( MathX.SphereCameraDistance( _contentRadius * 1.1f, camera.FieldOfView ) * _zoomFactor, 28f, 2048f );
			var lookRotation = new Angles( _orbitPitch, _orbitYaw, 0f ).ToRotation();
			var focus = _focusPoint.WithZ( _focusPoint.z + MathF.Max( _contentRadius * 0.08f, 6f ) );
			var cameraPosition = focus + lookRotation.Backward * distance;
			camera.WorldPosition = cameraPosition;
			camera.WorldRotation = Rotation.LookAt( focus - cameraPosition, Vector3.Up );
		}

		private static int DetermineUpAxis( IReadOnlyList<NativeAuditBoneInfo> bones )
		{
			if ( bones.Count == 0 )
				return 2;

			if ( TryInferUpAxisFromBodyLandmarks( bones, out var inferredUpAxis ) )
				return inferredUpAxis;

			static float Range( IEnumerable<float> values ) => values.Max() - values.Min();
			var xRange = Range( bones.Select( bone => bone.WorldTransform.Translation.X ) );
			var yRange = Range( bones.Select( bone => bone.WorldTransform.Translation.Y ) );
			var zRange = Range( bones.Select( bone => bone.WorldTransform.Translation.Z ) );
			if ( xRange >= yRange && xRange >= zRange )
				return 0;
			if ( yRange >= zRange )
				return 1;
			return 2;
		}

		private static int DetermineUpAxis( IReadOnlyList<NativeBoneInfo> bones )
		{
			if ( bones.Count == 0 )
				return 2;

			var bonesByName = bones.ToDictionary( bone => bone.Name, StringComparer.OrdinalIgnoreCase );
			var worldTransforms = new Dictionary<string, WorldTransform>( StringComparer.OrdinalIgnoreCase );
			foreach ( var bone in bones )
				ComputeRestWorldTransform( bone, bonesByName, worldTransforms );

			if ( TryFindWorldPoint( worldTransforms, IsPelvisBoneName, out var pelvis )
				&& (TryFindWorldPoint( worldTransforms, IsHeadBoneName, out var head )
					|| TryFindWorldPoint( worldTransforms, IsNeckBoneName, out head )
					|| TryFindWorldPoint( worldTransforms, IsChestBoneName, out head )
					|| TryFindWorldPoint( worldTransforms, IsSpineBoneName, out head )) )
			{
				var vertical = head - pelvis;
				var absX = MathF.Abs( vertical.X );
				var absY = MathF.Abs( vertical.Y );
				var absZ = MathF.Abs( vertical.Z );
				if ( absX >= absY && absX >= absZ )
					return 0;
				if ( absY >= absZ )
					return 1;
				return 2;
			}

			static float Range( IEnumerable<float> values ) => values.Max() - values.Min();
			var translations = worldTransforms.Values.Select( transform => transform.Translation ).ToList();
			var xRange = Range( translations.Select( point => point.X ) );
			var yRange = Range( translations.Select( point => point.Y ) );
			var zRange = Range( translations.Select( point => point.Z ) );
			if ( xRange >= yRange && xRange >= zRange )
				return 0;
			if ( yRange >= zRange )
				return 1;
			return 2;
		}

		private static WorldTransform ComputeRestWorldTransform(
			NativeBoneInfo bone,
			IReadOnlyDictionary<string, NativeBoneInfo> bonesByName,
			Dictionary<string, WorldTransform> worldTransforms )
		{
			if ( worldTransforms.TryGetValue( bone.Name, out var cached ) )
				return cached;

			var local = bone.LocalTransform;
			WorldTransform result;
			if ( string.IsNullOrWhiteSpace( bone.ParentName ) || !bonesByName.TryGetValue( bone.ParentName, out var parentBone ) )
			{
				result = new WorldTransform( local.Translation, local.Rotation );
			}
			else
			{
				var parent = ComputeRestWorldTransform( parentBone, bonesByName, worldTransforms );
				result = new WorldTransform(
					parent.Translation + NVector3.Transform( local.Translation, parent.Rotation ),
					RetargetMath.Normalize( parent.Rotation * local.Rotation ) );
			}

			worldTransforms[bone.Name] = result;
			return result;
		}

		private static bool TryFindWorldPoint(
			IReadOnlyDictionary<string, WorldTransform> worldTransforms,
			Func<string, bool> predicate,
			out NVector3 point )
		{
			foreach ( var pair in worldTransforms )
			{
				if ( predicate( pair.Key ) )
				{
					point = pair.Value.Translation;
					return true;
				}
			}

			point = default;
			return false;
		}

		private static bool TryInferUpAxisFromBodyLandmarks( IReadOnlyList<NativeAuditBoneInfo> bones, out int upAxis )
		{
			upAxis = 2;

			if ( !TryFindWorldPoint( bones, IsPelvisBoneName, out var pelvis ) )
				return false;

			if ( !TryFindWorldPoint( bones, IsHeadBoneName, out var head )
				&& !TryFindWorldPoint( bones, IsNeckBoneName, out head )
				&& !TryFindWorldPoint( bones, IsChestBoneName, out head )
				&& !TryFindWorldPoint( bones, IsSpineBoneName, out head ) )
			{
				return false;
			}

			var vertical = head - pelvis;
			var absX = MathF.Abs( vertical.X );
			var absY = MathF.Abs( vertical.Y );
			var absZ = MathF.Abs( vertical.Z );
			if ( absX >= absY && absX >= absZ )
			{
				upAxis = 0;
				return true;
			}

			if ( absY >= absZ )
			{
				upAxis = 1;
				return true;
			}

			upAxis = 2;
			return true;
		}

		private static bool TryBuildFacingArrow( IReadOnlyList<NativeAuditBoneInfo> bones, int upAxis, Vector3 effectiveEulerDegrees, out Vector3 start, out Vector3 end )
		{
			start = default;
			end = default;

			if ( !TryFindWorldPoint( bones, IsPelvisBoneName, out var pelvis )
				|| !TryFindWorldPoint( bones, name => IsUpperLegBoneName( name, leftSide: true ), out var leftUpperLeg )
				|| !TryFindWorldPoint( bones, name => IsUpperLegBoneName( name, leftSide: false ), out var rightUpperLeg )
				|| !TryFindWorldPoint( bones, name => IsShoulderLikeBoneName( name, leftSide: true ), out var leftShoulder )
				|| !TryFindWorldPoint( bones, name => IsShoulderLikeBoneName( name, leftSide: false ), out var rightShoulder ) )
			{
				return false;
			}

			var hipsMid = (leftUpperLeg + rightUpperLeg) * 0.5f;
			var shouldersMid = (leftShoulder + rightShoulder) * 0.5f;
			var up = NormalizeOrFallback( shouldersMid - hipsMid, GetUpAxisVector( upAxis ) );
			var rightHint = NormalizeOrFallback( ((rightShoulder - leftShoulder) + (rightUpperLeg - leftUpperLeg)) * 0.5f, NVector3.UnitX );
			var forward = NormalizeOrFallback( NVector3.Cross( up, rightHint ), GetForwardFallbackVector( upAxis ) );

			var rotatedPelvis = RotateSourceOrientation( pelvis, effectiveEulerDegrees );
			var rotatedForward = RotateSourceOrientation( forward, effectiveEulerDegrees );
			var previewPelvis = ConvertToPreviewSpace( rotatedPelvis, upAxis );
			var previewForward = ConvertDirectionToPreviewSpace( rotatedForward, upAxis );
			var previewUp = GetPreviewUpAxisVector();
			var horizontalForward = previewForward.WithZ( 0f );
			previewForward = horizontalForward.Length > 0.001f ? horizontalForward.Normal : Vector3.Forward;

			var previewRight = previewUp.Cross( previewForward );
			previewRight = previewRight.Length > 0.001f ? previewRight.Normal : Vector3.Right;
			var previewFloorZ = bones
				.Select( bone => ConvertToPreviewSpace( RotateSourceOrientation( bone.WorldTransform.Translation, effectiveEulerDegrees ), upAxis ).z )
				.Min();
			var previewLeftShoulder = ConvertToPreviewSpace( RotateSourceOrientation( leftShoulder, effectiveEulerDegrees ), upAxis );
			var previewRightShoulder = ConvertToPreviewSpace( RotateSourceOrientation( rightShoulder, effectiveEulerDegrees ), upAxis );
			var shoulderWidth = MathF.Max( (previewRightShoulder - previewLeftShoulder).Length, 8f );
			var sideOffset = MathF.Max( shoulderWidth * 1.15f, 12f );
			var arrowLength = MathF.Max( shoulderWidth * 2.6f, 28f );

			// Keep the facing arrow as a floor marker beside the rig, not attached to the pelvis.
			start = new Vector3( previewPelvis.x, previewPelvis.y, previewFloorZ + 2.5f ) + previewRight * sideOffset - previewForward * 4f;
			end = start + previewForward * arrowLength;
			return true;
		}

		private static bool TryFindPreviewMarker(
			IReadOnlyList<NativeAuditBoneInfo> bones,
			int upAxis,
			Vector3 effectiveEulerDegrees,
			Func<string, bool> predicate,
			out Vector3 point )
		{
			point = default;
			if ( !TryFindWorldPoint( bones, predicate, out var worldPoint ) )
				return false;

			var rotated = RotateSourceOrientation( worldPoint, effectiveEulerDegrees );
			point = ConvertToPreviewSpace( rotated, upAxis );
			return true;
		}

		private static bool TryFindWorldPoint( IReadOnlyList<NativeAuditBoneInfo> bones, Func<string, bool> predicate, out NVector3 point )
		{
			point = default;
			foreach ( var bone in bones )
			{
				if ( predicate( bone.Name ) )
				{
					point = bone.WorldTransform.Translation;
					return true;
				}
			}

			return false;
		}

		private static NVector3 RotateSourceOrientation( NVector3 point, Vector3 eulerDegrees )
		{
			// The Blender backend applies sourceFacingEulerDegrees as native XYZ Euler rotations.
			// Keep this preview in the same convention so "looks aligned" matches the actual solve.
			var rotated = RotateAroundX( point, MathF.PI / 180f * eulerDegrees.x );
			rotated = RotateAroundY( rotated, MathF.PI / 180f * eulerDegrees.y );
			rotated = RotateAroundZ( rotated, MathF.PI / 180f * eulerDegrees.z );
			return rotated;
		}

		private static NVector3 RotateAroundX( NVector3 point, float radians )
		{
			var sin = MathF.Sin( radians );
			var cos = MathF.Cos( radians );
			return new NVector3(
				point.X,
				point.Y * cos - point.Z * sin,
				point.Y * sin + point.Z * cos );
		}

		private static NVector3 RotateAroundY( NVector3 point, float radians )
		{
			var sin = MathF.Sin( radians );
			var cos = MathF.Cos( radians );
			return new NVector3(
				point.X * cos + point.Z * sin,
				point.Y,
				-point.X * sin + point.Z * cos );
		}

		private static NVector3 RotateAroundZ( NVector3 point, float radians )
		{
			var sin = MathF.Sin( radians );
			var cos = MathF.Cos( radians );
			return new NVector3(
				point.X * cos - point.Y * sin,
				point.X * sin + point.Y * cos,
				point.Z );
		}

		private static Vector3 ConvertDirectionToPreviewSpace( NVector3 direction, int upAxis )
		{
			var converted = ConvertToPreviewSpace( direction, upAxis );
			return converted.Length.AlmostEqual( 0f ) ? Vector3.Forward : converted.Normal;
		}

		private static Vector3 GetPreviewUpAxisVector()
		{
			return Vector3.Up;
		}

		private static Vector3 ConvertToPreviewSpace( NVector3 point, int upAxis )
		{
			// s&box/Citizen preview uses X as front and Z as up. Source FBX files vary:
			// Unity-style rigs are usually Y-up/Z-forward, Blender-style rigs are Z-up/Y-forward.
			// Map the inferred source up axis to preview Z and the likely source forward axis to preview X.
			return upAxis switch
			{
				0 => new Vector3( point.Y, point.Z, point.X ),
				1 => new Vector3( point.Z, point.X, point.Y ),
				_ => new Vector3( point.Y, point.X, point.Z )
			};
		}

		private static NVector3 NormalizeOrFallback( NVector3 value, NVector3 fallback )
		{
			return value.LengthSquared() > 0.000001f ? NVector3.Normalize( value ) : fallback;
		}

		private static NVector3 GetUpAxisVector( int upAxis )
		{
			return upAxis switch
			{
				0 => NVector3.UnitX,
				1 => NVector3.UnitY,
				_ => NVector3.UnitZ
			};
		}

		private static NVector3 GetForwardFallbackVector( int upAxis )
		{
			return upAxis switch
			{
				1 => NVector3.UnitZ,
				_ => NVector3.UnitY
			};
		}

		private static bool IsPelvisBoneName( string boneName )
		{
			var normalized = NormalizeBoneName( boneName );
			return normalized.Contains( "pelvis" ) || normalized.Equals( "hips" ) || normalized.Equals( "hip" ) || normalized.Contains( "roothips" );
		}

		private static bool IsHeadBoneName( string boneName )
		{
			var normalized = NormalizeBoneName( boneName );
			return normalized.Contains( "head" );
		}

		private static bool IsNeckBoneName( string boneName )
		{
			var normalized = NormalizeBoneName( boneName );
			return normalized.Contains( "neck" );
		}

		private static bool IsChestBoneName( string boneName )
		{
			var normalized = NormalizeBoneName( boneName );
			return normalized.Contains( "chest" )
				|| normalized.Contains( "upperchest" );
		}

		private static bool IsSpineBoneName( string boneName )
		{
			var normalized = NormalizeBoneName( boneName );
			return normalized.Contains( "spine" );
		}

		private static bool IsUpperLegBoneName( string boneName, bool leftSide )
		{
			var normalized = NormalizeBoneName( boneName );
			if ( !MatchesSide( normalized, leftSide ) )
				return false;

			return normalized.Contains( "thigh" )
				|| normalized.Contains( "upleg" )
				|| normalized.Contains( "upperleg" )
				|| normalized.Contains( "legupper" )
				|| normalized.Contains( "upperleg" );
		}

		private static bool IsShoulderLikeBoneName( string boneName, bool leftSide )
		{
			var normalized = NormalizeBoneName( boneName );
			if ( !MatchesSide( normalized, leftSide ) )
				return false;

			return normalized.Contains( "clavicle" )
				|| normalized.Contains( "shoulder" )
				|| normalized.Contains( "upperarm" )
				|| normalized.Contains( "armupper" );
		}

		private static bool MatchesSide( string normalizedBoneName, bool leftSide )
		{
			return leftSide ? LooksLikeLeft( normalizedBoneName ) : LooksLikeRight( normalizedBoneName );
		}

		private static bool LooksLikeLeft( string normalizedBoneName )
		{
			return normalizedBoneName.Contains( "left" )
				|| normalizedBoneName.EndsWith( "l" )
				|| normalizedBoneName.Contains( "lft" );
		}

		private static bool LooksLikeRight( string normalizedBoneName )
		{
			return normalizedBoneName.Contains( "right" )
				|| normalizedBoneName.EndsWith( "r" )
				|| normalizedBoneName.Contains( "rgt" );
		}

		private static string NormalizeBoneName( string boneName )
		{
			if ( string.IsNullOrWhiteSpace( boneName ) )
				return string.Empty;

			var builder = new System.Text.StringBuilder( boneName.Length );
			foreach ( var ch in boneName )
			{
				if ( char.IsLetterOrDigit( ch ) )
					builder.Append( char.ToLowerInvariant( ch ) );
			}

			return builder.ToString();
		}
	}
}

internal sealed class RetargetLiveCompareWidget : Widget
{
	private readonly Label _summaryLabel;
	private readonly RetargetLivePreviewWidget _sourcePreview;
	private readonly RetargetLivePreviewWidget _resultPreview;

	public RetargetLiveCompareWidget( Widget parent ) : base( parent )
	{
		var rootLayout = Layout.Column();
		Layout = rootLayout;
		rootLayout.Margin = 0;
		rootLayout.Spacing = 6;

		_summaryLabel = rootLayout.Add( new Label( "Compare preview is waiting for a source clip and a retarget result." ) { WordWrap = true } );

		var split = rootLayout.Add( new Splitter( this ), 1 );
		split.IsHorizontal = true;

		var sourceHost = CreatePreviewPane( split, "Source" );
		_sourcePreview = new RetargetLivePreviewWidget( sourceHost, compactMode: true );
		sourceHost.Layout.Add( _sourcePreview, 1 );
		split.AddWidget( sourceHost );
		split.SetStretch( 0, 1 );

		var resultHost = CreatePreviewPane( split, "Result" );
		_resultPreview = new RetargetLivePreviewWidget( resultHost, compactMode: true );
		resultHost.Layout.Add( _resultPreview, 1 );
		split.AddWidget( resultHost );
		split.SetStretch( 1, 1 );
	}

	public void LoadAnimations(
		string sourceVmdlResourcePath,
		string sourceSequenceName,
		string resultVmdlResourcePath,
		string resultSequenceName )
	{
		var hasSource = !string.IsNullOrWhiteSpace( sourceVmdlResourcePath );
		var hasResult = !string.IsNullOrWhiteSpace( resultVmdlResourcePath );

		if ( hasSource )
			_sourcePreview.LoadAnimation( sourceVmdlResourcePath, sourceSequenceName );
		else
			_sourcePreview.ClearAnimation( "Source preview is waiting for a generated source clip preview." );

		if ( hasResult )
			_resultPreview.LoadAnimation( resultVmdlResourcePath, resultSequenceName );
		else
			_resultPreview.ClearAnimation( "Result preview is waiting for a generated Citizen animation." );

		_summaryLabel.Text = (hasSource, hasResult) switch
		{
			(true, true) => "Live compare is showing the source clip on the left and the retargeted Citizen result on the right.",
			(true, false) => "Source clip is ready, but the retargeted Citizen result is not available yet.",
			(false, true) => "Retargeted Citizen result is ready, but the source clip preview is not available yet.",
			_ => "Compare preview is waiting for a source clip and a retarget result."
		};
	}

	public void ClearAnimations( string message )
	{
		_sourcePreview.ClearAnimation( message );
		_resultPreview.ClearAnimation( message );
		_summaryLabel.Text = string.IsNullOrWhiteSpace( message )
			? "Compare preview is waiting for a source clip and a retarget result."
			: message;
	}

	private static Widget CreatePreviewPane( Widget parent, string title )
	{
		var pane = new Widget( parent );
		pane.Layout = Layout.Column();
		pane.Layout.Margin = 0;
		pane.Layout.Spacing = 6;
		pane.Layout.Add( new Label( title ) );
		return pane;
	}
}

internal sealed class RetargetVideoPreviewWidget : Widget
{
	private Widget? _content;
	private string _currentPath = string.Empty;

	public string CurrentPath => _currentPath;

	public RetargetVideoPreviewWidget( Widget parent ) : base( parent )
	{
		var rootLayout = Layout.Column();
		Layout = rootLayout;
		rootLayout.Margin = 0;
		rootLayout.Spacing = 6;
		ShowPlaceholder( "No video preview has been generated yet." );
	}

	public void LoadVideo( string absolutePath )
	{
		_currentPath = absolutePath ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( absolutePath ) || !File.Exists( absolutePath ) )
		{
			ShowPlaceholder( "No video preview is available for this run." );
			return;
		}

		_content?.Destroy();
		var url = new Uri( absolutePath ).AbsoluteUri;
		_content = new VideoWidget( this, url );
		Layout?.Add( _content, 1 );
	}

	private void ShowPlaceholder( string message )
	{
		_currentPath = string.Empty;
		_content?.Destroy();
		var label = new Label( message )
		{
			WordWrap = true,
			Alignment = TextFlag.Center
		};
		_content = label;
		Layout?.Add( _content, 1 );
	}
}
