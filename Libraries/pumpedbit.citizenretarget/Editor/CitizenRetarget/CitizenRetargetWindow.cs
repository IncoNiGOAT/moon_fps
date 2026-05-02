using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO.Compression;

#nullable enable

namespace Editor.CitizenRetarget;

[EditorApp( CitizenRetargetPluginInfo.AppTitle, "sports_mma", "Retarget humanoid FBX clips onto Citizen with queueing, mapping, and preview" )]
public sealed class CitizenRetargetWindow : BaseWindow
{
	private sealed class BackgroundJobProgressReporter
	{
		private readonly BackgroundJobState _job;

		public BackgroundJobProgressReporter( BackgroundJobState job )
		{
			_job = job;
		}

		public void ReportDetail( string detail )
		{
			_job.Detail = detail;
		}

		public void ReportProgress( float progress, string? detail = null )
		{
			_job.IsIndeterminate = false;
			_job.Progress = Math.Clamp( progress, 0f, 1f );
			if ( !string.IsNullOrWhiteSpace( detail ) )
				_job.Detail = detail;
		}
	}

	private sealed class BackgroundJobState
	{
		public string Id { get; init; } = Guid.NewGuid().ToString( "N" );
		public string Scope { get; init; } = string.Empty;
		public string Title { get; init; } = string.Empty;
		public string Detail { get; set; } = string.Empty;
		public bool IsIndeterminate { get; set; } = true;
		public float Progress { get; set; }
		public bool BlocksInteraction { get; init; } = true;
		public bool ShowInJobsStrip { get; init; } = true;
		public Task? Task { get; set; }
		public Action? ApplySuccess { get; set; }
		public Action<Exception>? ApplyError { get; set; }
		public Exception? Error { get; set; }
		public bool CompletedNotified { get; set; }
	}

	private sealed class EnvironmentDependencyRow
	{
		public Label Status { get; init; } = null!;
		public Label Title { get; init; } = null!;
		public Label Summary { get; init; } = null!;
		public Label Detail { get; init; } = null!;
		public Widget FieldHost { get; init; } = null!;
		public Widget Actions { get; init; } = null!;
	}

	private sealed class ScanJobResult
	{
		public List<RetargetClipDescriptor> Clips { get; init; } = new();
		public RetargetSourceInspection Inspection { get; init; } = new();
		public List<RetargetSlotAssignmentState> MappingState { get; init; } = new();
		public string DetectedSourceProfilePath { get; init; } = string.Empty;
		public string DetectedSourceProfileDisplayName { get; init; } = string.Empty;
		public string DetectedMappingProfilePath { get; init; } = string.Empty;
	}

	private sealed class TargetAnimationLoadResult
	{
		public List<RetargetTargetAnimationEntry> ImportedTargets { get; init; } = new();
		public List<RetargetTargetAnimationEntry> BuiltInTargets { get; init; } = new();
		public int DeletedCount { get; init; }
	}

	private sealed class ClipBrowserListView : ListView
	{
		public Func<bool>? CanAddToQueue { get; set; }
		public Action? AddToQueue { get; set; }

		public ClipBrowserListView( Widget parent ) : base( parent )
		{
		}

		protected override void OnMousePress( MouseEvent e )
		{
			var item = GetItemAt( e.LocalPosition );
			if ( e.LeftMouseButton && item is not null && CitizenRetargetWindow.RectContainsPoint( CitizenRetargetWindow.GetOverflowIndicatorRect( item.Rect ), e.LocalPosition ) )
			{
				if ( item.Object is not null )
					SelectItem( item.Object, true, true );

				if ( CanAddToQueue?.Invoke() == true && AddToQueue is not null )
				{
					var menu = new ContextMenu();
					menu.AddOption( "Add To Retarget Queue", action: AddToQueue );
					menu.OpenAtCursor();
					e.Accepted = true;
					return;
				}
			}

			base.OnMousePress( e );
		}

		protected override void OnMouseRightClick( MouseEvent e )
		{
			base.OnMouseRightClick( e );

			if ( CanAddToQueue?.Invoke() != true || AddToQueue is null )
				return;

			var menu = new ContextMenu();
			menu.AddOption( "Add To Retarget Queue", action: AddToQueue );
			menu.OpenAtCursor();
			e.Accepted = true;
		}
	}

	private sealed class TargetAnimationListView : ListView
	{
		public Func<RetargetTargetAnimationEntry?>? ResolveContextTarget { get; set; }
		public Func<RetargetTargetAnimationEntry, bool>? CanDeleteImportedAnimation { get; set; }
		public Action<RetargetTargetAnimationEntry>? DeleteImportedAnimation { get; set; }

		public TargetAnimationListView( Widget parent ) : base( parent )
		{
		}

		protected override void OnMousePress( MouseEvent e )
		{
			var item = GetItemAt( e.LocalPosition );
			if ( e.LeftMouseButton && item is not null && CitizenRetargetWindow.RectContainsPoint( CitizenRetargetWindow.GetOverflowIndicatorRect( item.Rect ), e.LocalPosition ) )
			{
				if ( item.Object is not null )
					SelectItem( item.Object, true, true );

				var target = ResolveContextTarget?.Invoke();
				if ( target is not null
					&& target.IsImported
					&& !target.IsReadOnly
					&& DeleteImportedAnimation is not null
					&& CanDeleteImportedAnimation?.Invoke( target ) != false )
				{
					var menu = new ContextMenu();
					menu.AddOption( "Delete Imported Animation", action: () => DeleteImportedAnimation( target ) );
					menu.OpenAtCursor();
					e.Accepted = true;
					return;
				}
			}

			base.OnMousePress( e );
		}

		protected override void OnMouseRightClick( MouseEvent e )
		{
			base.OnMouseRightClick( e );

			var target = ResolveContextTarget?.Invoke();
			if ( target is null )
				return;

			if ( !target.IsImported || target.IsReadOnly || DeleteImportedAnimation is null || CanDeleteImportedAnimation?.Invoke( target ) == false )
				return;

			var menu = new ContextMenu();
			menu.AddOption( "Delete Imported Animation", action: () => DeleteImportedAnimation( target ) );
			menu.OpenAtCursor();
			e.Accepted = true;
		}
	}

	private sealed class LinearProgressIndicator : Widget
	{
		public float Progress { get; set; }
		public bool IsIndeterminate { get; set; }
		public Color AccentColor { get; set; } = Theme.Primary;

		public LinearProgressIndicator( Widget parent ) : base( parent )
		{
			FixedHeight = 8f;
			OnPaintOverride = PaintIndicator;
		}

		private bool PaintIndicator()
		{
			var rect = LocalRect;
			if ( rect.Width <= 0 || rect.Height <= 0 )
				return false;

			var radius = MathF.Min( 4f, rect.Height * 0.5f );
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground.Lighten( 0.1f ) );
			Paint.DrawRect( rect, radius );

			Paint.SetBrush( AccentColor );
			if ( IsIndeterminate )
			{
				var segmentWidth = MathF.Max( rect.Width * 0.24f, 36f );
				var travel = rect.Width + segmentWidth;
				var offset = (RealTime.Now * 160f) % travel - segmentWidth;
				var left = MathF.Max( rect.Left, offset );
				var right = MathF.Min( rect.Right, offset + segmentWidth );
				if ( right > left )
				{
					var segmentRect = new Rect( left, rect.Top, right - left, rect.Height );
					Paint.DrawRect( segmentRect, radius );
				}
			}
			else
			{
				var clamped = Math.Clamp( Progress, 0f, 1f );
				if ( clamped > 0f )
				{
					var fillRect = rect;
					fillRect.Width *= clamped;
					Paint.DrawRect( fillRect, radius );
				}
			}

			return false;
		}
	}

	private readonly CitizenRetargetPipeline _pipeline = new();
	private readonly RetargetEnvironmentDiagnosticsService _environmentDiagnostics = new();
	private RetargetToolSettings _toolSettings = new();
	private CitizenRetargetJob _job = new();
	private RetargetSourceProfile _sourceProfile = new();
	private RetargetMappingProfile _mappingProfile = new();
	private RetargetSourceLibraryRef _activeSourceLibrary = new();
	private RetargetSessionState _session = new();
	private RetargetSourceInspection? _inspection;
	private List<RetargetClipDescriptor> _allClips = new();
	private List<RetargetSlotAssignmentState> _mappingState = new();
	private readonly List<RetargetQueueItem> _queueItems = new();
	private readonly List<BackgroundJobState> _backgroundJobs = new();
	private readonly Dictionary<string, Vector3> _sourceFacingEulerOverrides = new( StringComparer.OrdinalIgnoreCase );
	private readonly HashSet<string> _sourceFacingManualOverrideEnabled = new( StringComparer.OrdinalIgnoreCase );
	private RetargetSlotAssignmentState? _selectedSlot;
	private NativeAuditBoneInfo? _selectedSourceBone;
	private RetargetImportResult? _latestResult;
	private RetargetImportResult? _selectedCompareResult;
	private RetargetImportResult? _inspectedRunResult;
	private RetargetEnvironmentReport? _environmentReport;
	private RetargetTargetAnimationEntry? _selectedTargetAnimation;
	private readonly List<RetargetTargetPresetRef> _targetPresets = new();
	private bool _advancedSettingsVisible;
	private bool _showBuiltInTargetAnimations;
	private bool _builtInTargetAnimationsLoaded;
	private bool _builtInTargetAnimationsLoadRequested;
	private bool _showOpenExistingTargetFlow;
	private bool _showCreateTargetFlow;
	private bool _queueRunning;
	private bool _cancelQueueRequested;
	private bool _jobInFlight;
	private bool _suppressHistorySelection;
	private bool _suppressSourcePresetSync;
	private bool _suppressSourceFacingFieldSync;
	private bool _suppressSourceFacingManualOverrideSync;
	private bool _startQueueAfterEnvironmentCheck;
	private Vector3 _sourceFacingEulerDegrees;
	private string _latestQueueFailureMessage = string.Empty;
	private string _latestSetupFailureMessage = string.Empty;
	private string _lastScanStatus = "No source scan has run yet.";
	private string _lastScanDetails = string.Empty;
	private string _lastScanSourcePath = string.Empty;
	private DateTime? _lastScanCompletedAt;
	private int _jobsSpinnerFrame;

	private Label _statusLabel = null!;
	private Label _summaryLabel = null!;
	private Label _clipSummaryLabel = null!;
	private Label _queueSummaryLabel = null!;
	private Label _artifactSummaryLabel = null!;
	private Label _environmentSummaryLabel = null!;
	private Label _environmentDetailsLabel = null!;
	private EnvironmentDependencyRow _environmentSettingsRow = null!;
	private EnvironmentDependencyRow _environmentBackendRow = null!;
	private EnvironmentDependencyRow _environmentBlenderRow = null!;
	private EnvironmentDependencyRow _environmentRokokoRow = null!;
	private EnvironmentDependencyRow _environmentNativeFbxRow = null!;
	private EnvironmentDependencyRow _environmentTargetRow = null!;
	private Label _runHealthLabel = null!;
	private Label _runNextStepLabel = null!;
	private Label _issuesLabel = null!;
	private Label _targetSummaryLabel = null!;
	private Label _builtInTargetSummaryLabel = null!;
	private Label _homeQueueSummaryLabel = null!;
	private Label _resultMetaLabel = null!;
	private Label _resultDetailsLabel = null!;
	private Label _currentTargetWorkspaceLabel = null!;
	private Label _newTargetVmdlPreviewLabel = null!;
	private Label _newTargetFolderPreviewLabel = null!;
	private Label _newTargetPrefixPreviewLabel = null!;
	private Label _sourceBusyLabel = null!;
	private Label _targetBusyLabel = null!;
	private Label _queueBusyLabel = null!;
	private Label _sourceFacingSummaryLabel = null!;
	private Label _sourceFacingHintLabel = null!;
	private Label _jobsStripLabel = null!;
	private Label _jobsStripProgressLabel = null!;
	private LinearProgressIndicator _jobsStripProgressBar = null!;
	private Label _poseCompensationSummaryLabel = null!;
	private Label _mappingSelectionTitleLabel = null!;
	private Label _mappingSelectionSummaryLabel = null!;
	private Label _mappingSelectionHintLabel = null!;
	private LineEdit _sourcePath = null!;
	private LineEdit _mappingProfilePath = null!;
	private LineEdit _targetVmdlPath = null!;
	private LineEdit _targetAnimGraphPath = null!;
	private LineEdit _targetPosePresetId = null!;
	private LineEdit _outputFolder = null!;
	private LineEdit _sequencePrefix = null!;
	private LineEdit _clipFilter = null!;
	private LineEdit _boneFilter = null!;
	private LineEdit _mappingSearch = null!;
	private LineEdit _newTargetName = null!;
	private LineEdit _sourceFacingTiltField = null!;
	private LineEdit _sourceFacingRollField = null!;
	private LineEdit _sourceFacingFacingField = null!;
	private LineEdit _backendPath = null!;
	private LineEdit _blenderPath = null!;
	private LineEdit _rokokoAddonPath = null!;
	private ComboBox _rootMotionMode = null!;
	private ComboBox _sourceProfilePreset = null!;
	private ComboBox _mappingSourceProfilePreset = null!;
	private ComboBox _mappingFilter = null!;
	private Checkbox _importHands = null!;
	private Button _scanButton = null!;
	private Button _retryFailedQueueButton = null!;
	private Button _clearFinishedQueueButton = null!;
	private Button _clearQueueButton = null!;
	private Button _scanEnvironmentButton = null!;
	private Button _openEnvironmentBackendButton = null!;
	private Button _browseEnvironmentBackendButton = null!;
	private Button _browseBlenderButton = null!;
	private Button _autoScanRokokoAddonButton = null!;
	private Button _browseRokokoAddonButton = null!;
	private Button _openRokokoAddonButton = null!;
	private Button _clearRokokoAddonButton = null!;
	private Button _downloadNativeHelperButton = null!;
	private Button _openNativeHelperFolderButton = null!;
	private Button _copyEnvironmentDiagnosticsButton = null!;
	private Button _exportSupportBundleButton = null!;
	private Button _useHistoryAsCompareButton = null!;
	private Button _assignBoneButton = null!;
	private Button _clearSlotButton = null!;
	private Button _resetSlotButton = null!;
	private Button _saveMappingButton = null!;
	private Button _toggleAdvancedButton = null!;
	private Button _openRunDirButton = null!;
	private Button _openManifestButton = null!;
	private Button _openRawExportButton = null!;
	private Button _openImportedAnimationButton = null!;
	private Button _openGeneratedModelButton = null!;
	private Button _openRunLogButton = null!;
	private Button _clearHomeResultButton = null!;
	private Button _inspectHomeResultInRunsButton = null!;
	private Button _openHomeGeneratedModelButton = null!;
	private Button _addClipToQueueButton = null!;
	private Button _openExistingTargetButton = null!;
	private Button _createTargetButton = null!;
	private Button _toggleBuiltInTargetAnimationsButton = null!;
	private Button _beginOpenExistingTargetButton = null!;
	private Button _beginCreateTargetButton = null!;
	private Button _browseSourceButton = null!;
	private Button _openPoseCompensationButton = null!;
	private Checkbox _sourceFacingManualOverrideCheckbox = null!;
	private Button _sourceFacingResetButton = null!;
	private Button _sourceFacingRotate180Button = null!;
	private Button _sourceFacingRotateLeftButton = null!;
	private Button _sourceFacingRotateRightButton = null!;
	private Button _sourceFacingTiltForwardButton = null!;
	private Button _sourceFacingTiltBackButton = null!;
	private Button _sourceFacingRollLeftButton = null!;
	private Button _sourceFacingRollRightButton = null!;
	private Button _editCompensationDataButton = null!;
	private Button _runHomeQueueButton = null!;
	private Button _removeHomeQueueItemButton = null!;
	private Button _clearHomeQueueButton = null!;
	private Widget _advancedSettingsHost = null!;
	private Widget _targetWorkspaceIntroHost = null!;
	private Widget _targetOpenExistingHost = null!;
	private Widget _targetCreateHost = null!;
	private Widget _builtInTargetSectionHost = null!;
	private Widget _sourceFacingCard = null!;
	private Widget _jobsStripHost = null!;
	private ClipBrowserListView _clipList = null!;
	private ListView _sourceBoneList = null!;
	private ListView _mappingList = null!;
	private ListView _queueList = null!;
	private ListView _historyList = null!;
	private ListView _targetPresetList = null!;
	private ListView _targetAnimationList = null!;
	private ListView _builtInTargetAnimationList = null!;
	private ListView _homeQueueList = null!;
	private TextEdit _runLogOutput = null!;
	private RetargetLivePreviewWidget _livePreview = null!;
	private RetargetSourceFacingPreviewWidget _sourceFacingPreview = null!;
	public CitizenRetargetWindow()
	{
		DeleteOnClose = true;
		WindowTitle = CitizenRetargetPluginInfo.DisplayVersion;
		SetWindowIcon( "sports_mma" );
		Size = new Vector2( 1680, 980 );
		_toolSettings = RetargetToolSettings.Load();
		_job = _pipeline.LoadOrCreateJob();
		_activeSourceLibrary = RetargetSourceLibraryResolver.FromJob( _job );
		_session = CreateSessionStateFromJob( _job );
		LoadProfilesFromJob();
		BuildUi();
		TryHydrateFromJob();
		Show();
	}

	[Menu( "Editor", "Tools/CARL" )]
	public static void OpenFromMenu()
	{
		new CitizenRetargetWindow();
	}

	[Menu( "Editor", "CARL/Open Retargeter" )]
	public static void OpenFromLibraryMenu()
	{
		OpenFromMenu();
	}

	[EditorEvent.Hotload]
	public void OnHotload()
	{
		BuildUi();
		TryHydrateFromJob();
	}

	protected override void OnClosed()
	{
		base.OnClosed();
		_pipeline.Dispose();
	}

	[EditorEvent.Frame]
	private void ProcessQueue()
	{
		if ( !_queueRunning || _jobInFlight )
			return;

		if ( _cancelQueueRequested )
		{
			_queueRunning = false;
			_cancelQueueRequested = false;
			var removedCount = _queueItems.Count;
			_queueItems.Clear();
			RefreshQueueList();
			SetStatus( removedCount > 0 ? $"Queue cleared after the current clip finished. Removed {removedCount} item(s)." : "Queue cleared." );
			UpdateButtons();
			return;
		}

		var next = _queueItems.FirstOrDefault( item => item.Status == "pending" );
		if ( next is null )
		{
			FinalizeQueueIfIdle();
			return;
		}

		RunQueuedItem( next );
	}

	private void FinalizeQueueIfIdle()
	{
		if ( !_queueRunning || _jobInFlight )
			return;

		if ( _queueItems.Any( item => item.Status == "pending" || item.Status == "running" ) )
			return;

		_queueRunning = false;
		var completedCount = _queueItems.Count( item => item.Status == "completed" );
		var failedCount = _queueItems.Count( item => item.Status == "failed" );
		if ( failedCount > 0 )
		{
			var summary = $"Queue finished with {failedCount} failure(s) and {completedCount} completed run(s).";
			SetStatus( string.IsNullOrWhiteSpace( _latestQueueFailureMessage ) ? summary : $"{summary} Last error: {_latestQueueFailureMessage}" );
		}
		else if ( completedCount > 0 )
		{
			_queueItems.Clear();
			RefreshQueueList();
			SetStatus( $"Queue finished. Completed {completedCount} run(s) and cleared the queue." );
		}
		else
		{
			SetStatus( "Queue finished." );
		}

		UpdateButtons();
	}

	[EditorEvent.Frame]
	private void ProcessBackgroundJobs()
	{
		if ( _backgroundJobs.Count == 0 )
			return;

		_jobsSpinnerFrame++;
		var completedJobs = new List<BackgroundJobState>();
		foreach ( var job in _backgroundJobs.ToList() )
		{
			if ( job.Task is null || !job.Task.IsCompleted || job.CompletedNotified )
				continue;

			job.CompletedNotified = true;
			try
			{
				if ( job.Error is not null )
					job.ApplyError?.Invoke( job.Error );
				else
					job.ApplySuccess?.Invoke();
			}
			catch ( Exception exception )
			{
				SetStatus( $"Background job apply failed: {exception.Message}" );
			}
			completedJobs.Add( job );
		}

		foreach ( var job in completedJobs )
			_backgroundJobs.Remove( job );

		RefreshBackgroundJobUi();
		UpdateButtons();
	}

	private void BuildUi()
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 8;
		Layout.Clear( true );

		_statusLabel = new Label( "Ready." );
		_summaryLabel = new Label( string.Empty ) { WordWrap = true };

		var workspaceTabs = Layout.Add( new TabWidget( this ), 1 );
		workspaceTabs.StateCookie = "CitizenRetarget.WorkspaceTabs";
		workspaceTabs.AddPage( "Home", "home", BuildHomeTab( workspaceTabs ) );
		workspaceTabs.AddPage( "Mapping", "account_tree", BuildMappingTab( workspaceTabs ) );
		workspaceTabs.AddPage( "Diagnostics", "history", BuildRunsTab( workspaceTabs ) );

		_jobsStripHost = Layout.Add( new Widget( this ) );
		_jobsStripHost.Layout = Layout.Column();
		_jobsStripHost.Layout.Margin = 8;
		_jobsStripHost.Layout.Spacing = 6;
		var jobsStripRow = _jobsStripHost.Layout.AddRow();
		jobsStripRow.Spacing = 8;
		_jobsStripLabel = jobsStripRow.Add( CreateSupportingLabel( "No background jobs.", false ) );
		jobsStripRow.AddStretchCell();
		_jobsStripProgressLabel = jobsStripRow.Add( CreateMetricLabel( string.Empty ) );
		_jobsStripProgressBar = _jobsStripHost.Layout.Add( new LinearProgressIndicator( _jobsStripHost ) );
		_jobsStripProgressBar.AccentColor = Theme.Primary;
		_jobsStripHost.Hide();

		ApplyJobToControls();
		ApplySettingsToControls();
		RefreshClipList();
		RefreshSourceBoneList();
		RefreshMappingList();
		RefreshQueueList();
		RefreshHistoryList();
		RefreshResultList();
		RefreshResultSurfaces();
		RefreshEnvironmentDiagnostics();
		UpdateDiagnostics();
		RefreshClipSummary();
		RefreshBackgroundJobUi();
		UpdateButtons();
	}

	private Widget BuildHomeTab( Widget parent )
	{
		var pane = new Widget( parent );
		pane.Layout = Layout.Column();
		pane.Layout.Margin = 0;
		pane.Layout.Spacing = 8;

		var workspace = pane.Layout.Add( new Splitter( pane ), 1 );
		workspace.IsHorizontal = true;

		var sourcePane = new Widget( workspace );
		sourcePane.Layout = Layout.Column();
		sourcePane.Layout.Margin = 0;
		sourcePane.Layout.Spacing = 8;
		workspace.AddWidget( sourcePane );
		workspace.SetStretch( 0, 4 );

		var previewPane = new Widget( workspace );
		previewPane.Layout = Layout.Column();
		previewPane.Layout.Margin = 0;
		previewPane.Layout.Spacing = 8;
		workspace.AddWidget( previewPane );
		workspace.SetStretch( 1, 7 );

		var resultPane = new Widget( workspace );
		resultPane.Layout = Layout.Column();
		resultPane.Layout.Margin = 0;
		resultPane.Layout.Spacing = 8;
		workspace.AddWidget( resultPane );
		workspace.SetStretch( 2, 4 );

		var sourceCard = CreateCard( sourcePane, "Source" );
		var sourcePathRow = sourceCard.Layout.AddRow();
		sourcePathRow.Spacing = 6;
		sourcePathRow.Add( CreateFieldLabel( "FBX" ) );
		_sourcePath = sourcePathRow.Add( StyleInteractiveField( new LineEdit() ) );
		_sourcePath.PlaceholderText = @"C:\...\humanoid.fbx";
		_sourcePath.TextEdited += _ => SyncJobFromControls();
		_browseSourceButton = sourcePathRow.Add( StyleSecondaryActionButton( new Button( "Browse" ) { Clicked = BrowseSourceFbx } ) );
		var sourcePresetRow = sourceCard.Layout.AddRow();
		sourcePresetRow.Spacing = 6;
		sourcePresetRow.Add( CreateFieldLabel( "Preset" ) );
		_sourceProfilePreset = sourcePresetRow.Add( CreateSourcePresetCombo() );
		_sourceProfilePreset.ItemChanged += () => OnSourcePresetChanged( _sourceProfilePreset );
		var sourceActions = sourceCard.Layout.AddRow();
		sourceActions.Spacing = 6;
		_scanButton = sourceActions.Add( StylePrimaryActionButton( new Button.Primary( "Scan" ) { Clicked = BeginScanAndInspectAsync } ) );
		_toggleAdvancedButton = sourceActions.Add( StyleSecondaryActionButton( new Button( "Show Advanced" ) { Clicked = ToggleAdvancedSettings } ) );
		sourceActions.AddStretchCell();
		_sourceBusyLabel = sourceCard.Layout.Add( CreateCaptionLabel( string.Empty, true ) );

		_advancedSettingsHost = sourceCard.Layout.Add( new Widget( sourceCard ) );
		_advancedSettingsHost.Layout = Layout.Column();
		_advancedSettingsHost.Layout.Margin = 0;
		_advancedSettingsHost.Layout.Spacing = 6;
		AddLabeledLineEdit( _advancedSettingsHost, "Mapping Profile", out _mappingProfilePath, CitizenTargetProfile.DefaultMappingProfileAssetPath, SyncJobFromControls );
		AddLabeledLineEdit( _advancedSettingsHost, "Target VMDL", out _targetVmdlPath, CitizenTargetProfile.DefaultTargetVmdlPath, SyncJobFromControls );
		AddLabeledLineEdit( _advancedSettingsHost, "Target AnimGraph", out _targetAnimGraphPath, string.Empty, SyncJobFromControls );
		_targetAnimGraphPath.PlaceholderText = $"Optional — empty uses {CitizenTargetProfile.DefaultCitizenAnimGraphPath}";
		AddLabeledLineEdit( _advancedSettingsHost, "Target Pose Preset", out _targetPosePresetId, CitizenTargetProfile.DefaultTargetPosePresetId, SyncJobFromControls );
		AddLabeledLineEdit( _advancedSettingsHost, "Animation Folder", out _outputFolder, CitizenTargetProfile.DefaultOutputAnimationFolder, SyncJobFromControls );
		AddLabeledLineEdit( _advancedSettingsHost, "Sequence Prefix", out _sequencePrefix, CitizenTargetProfile.DefaultSequencePrefix, SyncJobFromControls );

		var rootRow = _advancedSettingsHost.Layout.AddRow();
		rootRow.Spacing = 6;
		rootRow.Add( CreateFieldLabel( "Root motion" ) );
		_rootMotionMode = rootRow.Add( StyleInteractiveField( new ComboBox() ) );
		_rootMotionMode.AddItem( "Keep" );
		_rootMotionMode.AddItem( "In Place (XY locked, Z from clip)" );
		_rootMotionMode.AddItem( "Fully In Place (XYZ locked)" );
		_rootMotionMode.ItemChanged += SyncJobFromControls;

		_importHands = _advancedSettingsHost.Layout.Add( new Checkbox( "Enable finger / hand slots" ) );
		_importHands.StateChanged += _ => { SyncJobFromControls(); RebuildAutoMap(); };
		_advancedSettingsHost.Layout.Add( new Label.Subtitle( "Target pose and compensation" ) );
		_poseCompensationSummaryLabel = _advancedSettingsHost.Layout.Add( CreateSupportingLabel( "Pose preset details will appear here." ) );
		var poseActions = _advancedSettingsHost.Layout.AddRow();
		poseActions.Spacing = 6;
		_openPoseCompensationButton = poseActions.Add( StyleSecondaryActionButton( new Button( "Open Compensation Data" ) { Clicked = OpenPoseCompensationData } ) );
		poseActions.AddStretchCell();
		SetAdvancedSettingsVisible( _advancedSettingsVisible );

		var clipCard = CreateCard( sourcePane, "Clips", 1 );
		AddLabeledLineEdit( clipCard, "Filter", out _clipFilter, "Filter clips", RefreshClipList );
		_clipSummaryLabel = clipCard.Layout.Add( CreatePlaceholderLabel( "Scan a source FBX to load clips and start a queue." ) );
		_clipList = clipCard.Layout.Add( StyleInteractiveList( new ClipBrowserListView( clipCard )
		{
			CanAddToQueue = () => _clipList.SelectedItems.OfType<RetargetClipDescriptor>().Any() && !_queueRunning && !_jobInFlight && !HasActiveBackgroundJob( "source" ) && !HasActiveBackgroundJob( "queue" ),
			AddToQueue = AddSelectedClipsToQueue
		} ), 1 );
		_clipList.MultiSelect = true;
		_clipList.ItemSize = new Vector2( 0, 34 );
		_clipList.ItemPaint = PaintClipItem;
		_clipList.ItemSelected = _ => OnClipSelectionChanged();
		var clipActions = clipCard.Layout.AddRow();
		clipActions.Spacing = 6;
		_addClipToQueueButton = clipActions.Add( StylePrimaryActionButton( new Button.Primary( "Add To Retarget Queue" ) { Clicked = AddSelectedClipsToQueue } ) );
		clipActions.AddStretchCell();

		var previewCard = CreateCard( previewPane, "Preview", 4 );
		_livePreview = previewCard.Layout.Add( new RetargetLivePreviewWidget( previewCard ), 1 );

		var homeQueueCard = CreateCard( previewPane, "Queue", 2 );
		_homeQueueSummaryLabel = homeQueueCard.Layout.Add( CreatePlaceholderLabel( "No queued clips yet." ) );
		_queueBusyLabel = homeQueueCard.Layout.Add( CreateCaptionLabel( string.Empty, true ) );
		var homeQueueActions = homeQueueCard.Layout.AddRow();
		homeQueueActions.Spacing = 6;
		_runHomeQueueButton = homeQueueActions.Add( StylePrimaryActionButton( new Button.Primary( "Run Queue" ) { Clicked = RunQueue } ) );
		_removeHomeQueueItemButton = homeQueueActions.Add( StyleSecondaryActionButton( new Button( "Remove Selected" ) { Clicked = RemoveSelectedHomeQueueItems } ) );
		_clearHomeQueueButton = homeQueueActions.Add( StyleSubtleActionButton( new Button( "Clear Queue" ) { Clicked = ClearQueue } ) );
		homeQueueCard.Layout.AddSeparator();
		_homeQueueList = homeQueueCard.Layout.Add( StyleInteractiveList( new ListView( homeQueueCard ) ), 1 );
		_homeQueueList.ItemSize = new Vector2( 0, 72 );
		_homeQueueList.ItemPaint = PaintQueueItem;
		_homeQueueList.ItemSelected = _ => UpdateButtons();

		var targetPresetCard = CreateCard( resultPane, "Target Workspace" );
		_currentTargetWorkspaceLabel = targetPresetCard.Layout.Add( CreateSupportingLabel( "Open an existing target or create a new Citizen fork for imported animations." ) );
		_targetWorkspaceIntroHost = targetPresetCard.Layout.Add( new Widget( targetPresetCard ) );
		_targetWorkspaceIntroHost.Layout = Layout.Column();
		_targetWorkspaceIntroHost.Layout.Margin = 0;
		_targetWorkspaceIntroHost.Layout.Spacing = 6;
		var targetEntryActions = _targetWorkspaceIntroHost.Layout.AddRow();
		targetEntryActions.Spacing = 6;
		_beginCreateTargetButton = targetEntryActions.Add( StylePrimaryActionButton( new Button.Primary( "Create New Target" ) { Clicked = BeginCreateTargetFlow } ) );
		_beginOpenExistingTargetButton = targetEntryActions.Add( StyleSecondaryActionButton( new Button( "Open Existing Target" ) { Clicked = BeginOpenExistingTargetFlow } ) );
		targetEntryActions.AddStretchCell();

		_targetOpenExistingHost = targetPresetCard.Layout.Add( new Widget( targetPresetCard ) );
		_targetOpenExistingHost.Layout = Layout.Column();
		_targetOpenExistingHost.Layout.Margin = 0;
		_targetOpenExistingHost.Layout.Spacing = 6;
		_targetOpenExistingHost.Layout.Add( new Label.Subtitle( "Existing targets" ) );
		_targetPresetList = _targetOpenExistingHost.Layout.Add( StyleInteractiveList( new ListView( targetPresetCard ) ) );
		_targetPresetList.ItemSize = new Vector2( 0, 28 );
		_targetPresetList.ItemPaint = PaintTargetPresetItem;
		_targetPresetList.ItemSelected = _ => UpdateButtons();
		var targetPresetActions = _targetOpenExistingHost.Layout.AddRow();
		targetPresetActions.Spacing = 6;
		_openExistingTargetButton = targetPresetActions.Add( StylePrimaryActionButton( new Button.Primary( "Open Existing Target" ) { Clicked = OpenSelectedTargetPreset } ) );
		targetPresetActions.Add( StyleSubtleActionButton( new Button( "Back" ) { Clicked = ResetTargetWorkspaceFlow } ) );
		targetPresetActions.AddStretchCell();

		_targetCreateHost = targetPresetCard.Layout.Add( new Widget( targetPresetCard ) );
		_targetCreateHost.Layout = Layout.Column();
		_targetCreateHost.Layout.Margin = 0;
		_targetCreateHost.Layout.Spacing = 6;
		_targetCreateHost.Layout.Add( new Label.Subtitle( "New target" ) );
		var newTargetRow = _targetCreateHost.Layout.AddRow();
		newTargetRow.Spacing = 6;
		_newTargetName = newTargetRow.Add( StyleInteractiveField( new LineEdit() ) );
		_newTargetName.PlaceholderText = "citizen_retarget";
		_newTargetName.TextEdited += _ => UpdateNewTargetPreview();
		_createTargetButton = newTargetRow.Add( StylePrimaryActionButton( new Button( "Create New" ) { Clicked = CreateNewTargetPreset } ) );
		newTargetRow.Add( StyleSubtleActionButton( new Button( "Back" ) { Clicked = ResetTargetWorkspaceFlow } ) );
		_newTargetVmdlPreviewLabel = _targetCreateHost.Layout.Add( CreateSupportingLabel( "VMDL: -" ) );
		_newTargetFolderPreviewLabel = _targetCreateHost.Layout.Add( CreateSupportingLabel( "Animation folder: -" ) );
		_newTargetPrefixPreviewLabel = _targetCreateHost.Layout.Add( CreateSupportingLabel( "Sequence prefix: -" ) );

		var resultHistoryCard = CreateCard( resultPane, "Target Results", 1 );
		_resultMetaLabel = resultHistoryCard.Layout.Add( CreateSupportingLabel( "No active target result yet. Pick a target animation or run a retarget." ) );
		_targetBusyLabel = resultHistoryCard.Layout.Add( CreateCaptionLabel( string.Empty, true ) );
		_targetBusyLabel.Hide();
		var resultActions = resultHistoryCard.Layout.AddRow();
		resultActions.Spacing = 6;
		_inspectHomeResultInRunsButton = resultActions.Add( StyleSecondaryActionButton( new Button( "Open Diagnostics" ) { Clicked = InspectHomeResultInRuns } ) );
		_openHomeGeneratedModelButton = resultActions.Add( StyleSecondaryActionButton( new Button( "Open Model" ) { Clicked = OpenHomeGeneratedModel } ) );
		_clearHomeResultButton = resultActions.Add( StyleSubtleActionButton( new Button( "Clear Active" ) { Clicked = ClearHomeResult } ) );
		resultActions.AddStretchCell();
		_targetSummaryLabel = resultHistoryCard.Layout.Add( CreatePlaceholderLabel( "No imported retarget animations yet." ) );
		var targetAnimationList = new TargetAnimationListView( resultHistoryCard );
		targetAnimationList.ResolveContextTarget = () => _targetAnimationList?.SelectedItems.OfType<RetargetTargetAnimationEntry>().FirstOrDefault();
		targetAnimationList.CanDeleteImportedAnimation = CanDeleteImportedTargetAnimation;
		targetAnimationList.DeleteImportedAnimation = DeleteImportedTargetAnimation;
		_targetAnimationList = resultHistoryCard.Layout.Add( StyleInteractiveList( targetAnimationList ), 1 );
		_targetAnimationList.ItemSize = new Vector2( 0, 52 );
		_targetAnimationList.ItemPaint = PaintTargetAnimationItem;
		_targetAnimationList.ItemSelected = item =>
		{
			if ( item is RetargetTargetAnimationEntry target )
				OnTargetAnimationSelectionChanged( target );
		};
		resultHistoryCard.Layout.AddSeparator();
		var builtInHeader = resultHistoryCard.Layout.AddRow();
		builtInHeader.Spacing = 6;
		builtInHeader.Add( new Label.Subtitle( "Built-in target animations" ) );
		builtInHeader.AddStretchCell();
		_toggleBuiltInTargetAnimationsButton = builtInHeader.Add( StyleSubtleActionButton( new Button( "Show Built-In" ) { Clicked = ToggleBuiltInTargetAnimations } ) );
		_builtInTargetSectionHost = resultHistoryCard.Layout.Add( new Widget( resultHistoryCard ) );
		_builtInTargetSectionHost.Layout = Layout.Column();
		_builtInTargetSectionHost.Layout.Margin = 0;
		_builtInTargetSectionHost.Layout.Spacing = 6;
		_builtInTargetSummaryLabel = _builtInTargetSectionHost.Layout.Add( CreatePlaceholderLabel( "Target inherits base Citizen animations." ) );
		_builtInTargetAnimationList = _builtInTargetSectionHost.Layout.Add( StyleInteractiveList( new ListView( resultHistoryCard ) ), 1 );
		_builtInTargetAnimationList.ItemSize = new Vector2( 0, 44 );
		_builtInTargetAnimationList.ItemPaint = PaintTargetAnimationItem;
		_builtInTargetAnimationList.ItemSelected = item =>
		{
			if ( item is RetargetTargetAnimationEntry target )
				OnTargetAnimationSelectionChanged( target );
		};
		_builtInTargetSectionHost.Hide();
		return pane;
	}

	private Widget BuildMappingTab( Widget parent )
	{
		var pane = new Widget( parent );
		pane.Layout = Layout.Column();
		pane.Layout.Margin = 0;
		pane.Layout.Spacing = 8;

		var split = pane.Layout.Add( new Splitter( pane ), 1 );
		split.IsHorizontal = true;

		var left = new Widget( split );
		left.Layout = Layout.Column();
		left.Layout.Margin = 0;
		left.Layout.Spacing = 8;
		split.AddWidget( left );
		split.SetStretch( 0, 3 );

		var right = new Widget( split );
		right.Layout = Layout.Column();
		right.Layout.Margin = 0;
		right.Layout.Spacing = 8;
		split.AddWidget( right );
		split.SetStretch( 1, 5 );

		var mappingSetupCard = CreateCard( left, "Source Setup" );
		var mappingPresetRow = mappingSetupCard.Layout.AddRow();
		mappingPresetRow.Spacing = 6;
		mappingPresetRow.Add( CreateFieldLabel( "Preset" ) );
		_mappingSourceProfilePreset = mappingPresetRow.Add( CreateSourcePresetCombo() );
		_mappingSourceProfilePreset.ItemChanged += () => OnSourcePresetChanged( _mappingSourceProfilePreset );

		_sourceFacingCard = mappingSetupCard.Layout.Add( new Widget( mappingSetupCard ) );
		_sourceFacingCard.Layout = Layout.Column();
		_sourceFacingCard.Layout.Margin = 0;
		_sourceFacingCard.Layout.Spacing = 6;
		AddInlineSectionHeader( _sourceFacingCard, "Orientation Calibration" );
		_sourceFacingSummaryLabel = _sourceFacingCard.Layout.Add( CreateSupportingLabel( "Scan a source FBX to align its skeleton before retargeting." ) );
		_sourceFacingHintLabel = _sourceFacingCard.Layout.Add( CreatePlaceholderLabel( "Align the yellow source arrow with the green Citizen front arrow. Enable manual override only when the preset is wrong." ) );
		_sourceFacingHintLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.ControlBackground.WithAlpha( 0.34f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 6px;" );
		_sourceFacingPreview = _sourceFacingCard.Layout.Add( new RetargetSourceFacingPreviewWidget( _sourceFacingCard ) );
		AddInlineSectionHeader( _sourceFacingCard, "Rotation Override" );
		_sourceFacingManualOverrideCheckbox = _sourceFacingCard.Layout.Add( new Checkbox( "Enable manual override" ) );
		_sourceFacingManualOverrideCheckbox.StateChanged += state =>
		{
			if ( _suppressSourceFacingManualOverrideSync )
				return;

			SetSourceFacingManualOverrideEnabled( state == CheckState.On );
		};
		var sourceFacingMatrix = _sourceFacingCard.Layout.AddRow();
		sourceFacingMatrix.Spacing = 6;
		sourceFacingMatrix.Add( CreateSourceFacingDegreeCell( _sourceFacingCard, "X", out _sourceFacingTiltField, value => SetSourceFacingEulerDegrees( new Vector3( value, _sourceFacingEulerDegrees.y, _sourceFacingEulerDegrees.z ) ) ) );
		sourceFacingMatrix.Add( CreateSourceFacingDegreeCell( _sourceFacingCard, "Y", out _sourceFacingRollField, value => SetSourceFacingEulerDegrees( new Vector3( _sourceFacingEulerDegrees.x, value, _sourceFacingEulerDegrees.z ) ) ) );
		sourceFacingMatrix.Add( CreateSourceFacingDegreeCell( _sourceFacingCard, "Z", out _sourceFacingFacingField, value => SetSourceFacingEulerDegrees( _sourceFacingEulerDegrees.WithZ( value ) ) ) );
		var sourceFacingModeActions = _sourceFacingCard.Layout.AddRow();
		sourceFacingModeActions.Spacing = 6;
		_sourceFacingResetButton = sourceFacingModeActions.Add( StyleSubtleActionButton( new Button( "Reset to Preset" ) { Clicked = ResetSourceFacingCorrection } ) );
		sourceFacingModeActions.AddStretchCell();
		AddInlineSectionHeader( _sourceFacingCard, "Quick Rotate" );
		var xAdjustRow = _sourceFacingCard.Layout.AddRow();
		xAdjustRow.Spacing = 6;
		xAdjustRow.Add( CreateFieldLabel( "X" ) );
		_sourceFacingTiltBackButton = xAdjustRow.Add( StyleSecondaryActionButton( new Button( "-90" ) { Clicked = () => RotateSourceFacingBy( new Vector3( -90f, 0f, 0f ), "X" ) } ) );
		_sourceFacingTiltForwardButton = xAdjustRow.Add( StyleSecondaryActionButton( new Button( "+90" ) { Clicked = () => RotateSourceFacingBy( new Vector3( 90f, 0f, 0f ), "X" ) } ) );
		xAdjustRow.AddStretchCell();
		var yAdjustRow = _sourceFacingCard.Layout.AddRow();
		yAdjustRow.Spacing = 6;
		yAdjustRow.Add( CreateFieldLabel( "Y" ) );
		_sourceFacingRollRightButton = yAdjustRow.Add( StyleSecondaryActionButton( new Button( "-90" ) { Clicked = () => RotateSourceFacingBy( new Vector3( 0f, -90f, 0f ), "Y" ) } ) );
		_sourceFacingRollLeftButton = yAdjustRow.Add( StyleSecondaryActionButton( new Button( "+90" ) { Clicked = () => RotateSourceFacingBy( new Vector3( 0f, 90f, 0f ), "Y" ) } ) );
		yAdjustRow.AddStretchCell();
		var zAdjustRow = _sourceFacingCard.Layout.AddRow();
		zAdjustRow.Spacing = 6;
		zAdjustRow.Add( CreateFieldLabel( "Z" ) );
		_sourceFacingRotateLeftButton = zAdjustRow.Add( StyleSecondaryActionButton( new Button( "-90" ) { Clicked = () => RotateSourceFacingBy( new Vector3( 0f, 0f, -90f ), "Z" ) } ) );
		_sourceFacingRotateRightButton = zAdjustRow.Add( StyleSecondaryActionButton( new Button( "+90" ) { Clicked = () => RotateSourceFacingBy( new Vector3( 0f, 0f, 90f ), "Z" ) } ) );
		_sourceFacingRotate180Button = zAdjustRow.Add( StylePrimaryActionButton( new Button.Primary( "180" ) { Clicked = () => RotateSourceFacingBy( new Vector3( 0f, 0f, 180f ), "Z" ) } ) );
		zAdjustRow.AddStretchCell();

		var sourceBonesCard = CreateCard( left, "Source Bones", 1 );
		AddLabeledLineEdit( sourceBonesCard, "Filter", out _boneFilter, "Filter source bones", RefreshSourceBoneList );
		_sourceBoneList = sourceBonesCard.Layout.Add( StyleInteractiveList( new ListView( sourceBonesCard ) ), 1 );
		_sourceBoneList.ItemSize = new Vector2( 0, 34 );
		_sourceBoneList.ItemPaint = PaintSourceBoneItem;
		_sourceBoneList.ItemSelected = item => OnSourceBoneSelectionChanged( item as NativeAuditBoneInfo );

		var mappingCard = CreateCard( right, "Bone Map Editor", 1 );
		var mappingFilterRow = mappingCard.Layout.AddRow();
		mappingFilterRow.Spacing = 6;
		_mappingSearch = mappingFilterRow.Add( StyleInteractiveField( new LineEdit() ) );
		_mappingSearch.PlaceholderText = "Filter slots, target bones, or mapped source bones";
		_mappingSearch.TextEdited += _ => RefreshMappingList();
		_mappingFilter = mappingFilterRow.Add( StyleInteractiveField( new ComboBox() ) );
		_mappingFilter.AddItem( "All" );
		_mappingFilter.AddItem( "Needs Attention" );
		_mappingFilter.AddItem( "Manual Overrides" );
		_mappingFilter.AddItem( "Auto Mapped" );
		_mappingFilter.AddItem( "Required Only" );
		_mappingFilter.ItemChanged += RefreshMappingList;
		_mappingFilter.FixedWidth = 220;

		AddInlineSectionHeader( mappingCard, "Selected Slot" );
		_mappingSelectionTitleLabel = mappingCard.Layout.Add( CreateSupportingLabel( "No slot selected yet." ) );
		_mappingSelectionSummaryLabel = mappingCard.Layout.Add( CreatePlaceholderLabel( "Pick a row in Bone Map Editor, then pick a source bone on the left." ) );
		_mappingSelectionHintLabel = mappingCard.Layout.Add( CreatePlaceholderLabel( "Manual flow: select slot -> select source bone -> Assign Selected Bone -> Save Mapping Profile." ) );
		_mappingSelectionTitleLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.28f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.2f ).Hex}; border-radius: 6px;" );
		_mappingSelectionSummaryLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.ControlBackground.WithAlpha( 0.32f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 6px;" );
		_mappingSelectionHintLabel.SetStyles( $"padding: 8px 12px; background-color: {Theme.ControlBackground.WithAlpha( 0.18f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.14f ).Hex}; border-radius: 6px;" );

		var mappingButtons = mappingCard.Layout.AddRow();
		mappingButtons.Spacing = 6;
		mappingButtons.Add( StyleSecondaryActionButton( new Button( "Rebuild Auto Map" ) { Clicked = RebuildAutoMap } ) );
		_assignBoneButton = mappingButtons.Add( StylePrimaryActionButton( new Button.Primary( "Assign Selected Bone" ) { Clicked = AssignSelectedBone } ) );
		_clearSlotButton = mappingButtons.Add( StyleSubtleActionButton( new Button( "Clear" ) { Clicked = ClearSelectedSlot } ) );
		_resetSlotButton = mappingButtons.Add( StyleSecondaryActionButton( new Button( "Reset to Auto" ) { Clicked = ResetSelectedSlot } ) );
		_saveMappingButton = mappingButtons.Add( StyleSecondaryActionButton( new Button( "Save Mapping Profile" ) { Clicked = SaveMappingProfile } ) );
		_mappingList = mappingCard.Layout.Add( StyleInteractiveList( new ListView( mappingCard ) ), 1 );
		_mappingList.ItemSize = new Vector2( 0, 56 );
		_mappingList.ItemPaint = PaintMappingItem;
		_mappingList.ItemSelected = item => OnMappingSlotSelected( item as RetargetSlotAssignmentState );

		return pane;
	}

	private Widget BuildRunsTab( Widget parent )
	{
		var scroll = new ScrollArea( parent );
		scroll.HorizontalSizeMode = SizeMode.Flexible;
		scroll.VerticalSizeMode = SizeMode.Flexible;
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;

		scroll.Canvas = new Widget( scroll );
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Margin = 0;
		scroll.Canvas.Layout.Spacing = 8;
		scroll.Canvas.HorizontalSizeMode = SizeMode.Flexible;
		scroll.Canvas.VerticalSizeMode = SizeMode.CanGrow;

		var pane = scroll.Canvas;

		var environmentCard = CreateCard( pane, "Environment", collapsible: true );
		var setupToolbar = environmentCard.Layout.AddRow();
		setupToolbar.Spacing = 10;
		var setupHeader = setupToolbar.Add( new Widget( environmentCard ), 1 );
		setupHeader.Layout = Layout.Column();
		setupHeader.Layout.Margin = 0;
		setupHeader.Layout.Spacing = 2;
		var setupTitle = setupHeader.Layout.Add( new Label.Body( "Setup health" )
		{
			WordWrap = false,
			Color = Theme.Text.WithAlpha( 0.96f )
		} );
		setupTitle.SetStyles( "font-weight: 700; padding: 0; background-color: transparent; border: 0px;" );
		var setupVersion = setupHeader.Layout.Add( new Label.Small( CitizenRetargetPluginInfo.DisplayVersion )
		{
			WordWrap = false,
			Color = Theme.Text.WithAlpha( 0.58f )
		} );
		setupVersion.SetStyles( "padding: 0; background-color: transparent; border: 0px;" );
		setupToolbar.AddStretchCell();
		_scanEnvironmentButton = setupToolbar.Add( StyleEnvironmentActionButton( new Button( "Re-scan" ) { Clicked = () => BeginEnvironmentCheck( false ) } ) );
		_copyEnvironmentDiagnosticsButton = setupToolbar.Add( StyleEnvironmentActionButton( new Button( "Copy Diagnostics" ) { Clicked = CopyEnvironmentDiagnostics } ) );
		_exportSupportBundleButton = setupToolbar.Add( StyleEnvironmentActionButton( new Button( "Export Support Bundle" ) { Clicked = ExportSupportBundle } ) );

		_environmentSummaryLabel = environmentCard.Layout.Add( CreateSupportingLabel( "Setup has not been checked yet." ) );
		_environmentSummaryLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.26f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.24f ).Hex}; border-radius: 6px;" );

		var dependencyList = environmentCard.Layout.Add( new Widget( environmentCard ) );
		dependencyList.Layout = Layout.Column();
		dependencyList.Layout.Margin = 0;
		dependencyList.Layout.Spacing = 6;
		dependencyList.SetStyles( $"padding: 6px 10px 6px 6px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.14f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 8px;" );

		_environmentSettingsRow = CreateEnvironmentDependencyRow( dependencyList, "Settings", "Project-local tool settings." );
		_environmentBackendRow = CreateEnvironmentDependencyRow( dependencyList, "Backend", "Bundled retarget scripts and recipes." );
		_backendPath = CreateHiddenEnvironmentPathField( environmentCard, SyncToolSettingsFromControls );
		_browseEnvironmentBackendButton = _environmentBackendRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Browse Override" ) { Clicked = BrowseEnvironmentBackendFolder } ) );
		_openEnvironmentBackendButton = _environmentBackendRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Open" ) { Clicked = OpenEnvironmentBackendFolder } ) );

		_environmentBlenderRow = CreateEnvironmentDependencyRow( dependencyList, "Blender", "External Blender executable used by the retarget backend." );
		_blenderPath = CreateHiddenEnvironmentPathField( environmentCard, SyncToolSettingsFromControls );
		_browseBlenderButton = _environmentBlenderRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Browse EXE" ) { Clicked = BrowseBlenderExecutablePath } ) );

		_environmentRokokoRow = CreateEnvironmentDependencyRow( dependencyList, "Rokoko Addon", "Blender addon required by the backend retarget solve." );
		_rokokoAddonPath = CreateHiddenEnvironmentPathField( environmentCard, SyncToolSettingsFromControls );
		_autoScanRokokoAddonButton = _environmentRokokoRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Auto Scan" ) { Clicked = AutoScanRokokoAddon }, primary: true ) );
		_browseRokokoAddonButton = _environmentRokokoRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Browse" ) { Clicked = BrowseRokokoAddonPath } ) );
		_openRokokoAddonButton = _environmentRokokoRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Open" ) { Clicked = OpenRokokoAddonFolder } ) );
		_clearRokokoAddonButton = _environmentRokokoRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Clear" ) { Clicked = ClearRokokoAddonPath }, subtle: true ) );

		_environmentNativeFbxRow = CreateEnvironmentDependencyRow( dependencyList, "Native FBX", "Native scanner DLL used to read FBX clips and skeletons." );
		_downloadNativeHelperButton = _environmentNativeFbxRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Download Helper" ) { Clicked = ConfirmDownloadNativeHelper }, primary: true ) );
		_openNativeHelperFolderButton = _environmentNativeFbxRow.Actions.Layout.Add( StyleEnvironmentActionButton( new Button( "Open" ) { Clicked = OpenNativeHelperFolder } ) );
		_environmentTargetRow = CreateEnvironmentDependencyRow( dependencyList, "Target", "Current Citizen target model used by Home retargeting." );

		var environmentDetailsCard = CreateCard( pane, "Environment Details", collapsible: true, initiallyExpanded: false );
		_environmentDetailsLabel = environmentDetailsCard.Layout.Add( CreatePlaceholderLabel( "Run setup diagnostics to see the raw dependency report." ) );
		_environmentDetailsLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.18f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 6px;" );

		var topSplit = pane.Layout.Add( new Splitter( pane ) );
		topSplit.IsHorizontal = true;
		topSplit.MinimumHeight = 240;
		topSplit.FixedHeight = 280;

		var queueHost = new Widget( topSplit );
		queueHost.Layout = Layout.Column();
		queueHost.Layout.Margin = 0;
		queueHost.Layout.Spacing = 0;
		var queueCard = CreateCard( queueHost, "Queue", 1 );
		_queueSummaryLabel = queueCard.Layout.Add( CreatePlaceholderLabel( "No queued clips yet." ) );
		var queueActions = queueCard.Layout.AddRow();
		queueActions.Spacing = 6;
		_retryFailedQueueButton = queueActions.Add( StyleSecondaryActionButton( new Button( "Retry Failed" ) { Clicked = RetryFailedQueueItems } ) );
		_clearFinishedQueueButton = queueActions.Add( StyleSecondaryActionButton( new Button( "Clear Finished" ) { Clicked = ClearFinishedQueueItems } ) );
		_clearQueueButton = queueActions.Add( StyleSubtleActionButton( new Button( "Clear Queue" ) { Clicked = ClearQueue } ) );
		queueCard.Layout.AddSeparator();
		_queueList = queueCard.Layout.Add( StyleInteractiveList( new ListView( queueCard ) ), 1 );
		_queueList.ItemSize = new Vector2( 0, 72 );
		_queueList.ItemPaint = PaintQueueItem;
		topSplit.AddWidget( queueHost );
		topSplit.SetStretch( 0, 1 );

		var historyHost = new Widget( topSplit );
		historyHost.Layout = Layout.Column();
		historyHost.Layout.Margin = 0;
		historyHost.Layout.Spacing = 0;
		var historyCard = CreateCard( historyHost, "History", 1 );
		var historyActions = historyCard.Layout.AddRow();
		historyActions.Spacing = 6;
		_useHistoryAsCompareButton = historyActions.Add( StylePrimaryActionButton( new Button( "Set As Active Result" ) { Clicked = UseSelectedHistoryRunAsCompareResult } ) );
		historyActions.AddStretchCell();
		_historyList = historyCard.Layout.Add( StyleInteractiveList( new ListView( historyCard ) ), 1 );
		_historyList.ItemSize = new Vector2( 0, 56 );
		_historyList.ItemPaint = PaintHistoryItem;
		_historyList.ItemSelected = item =>
		{
			if ( !_suppressHistorySelection )
				OpenHistoryEntry( item as RetargetRunHistoryEntry );
		};
		topSplit.AddWidget( historyHost );
		topSplit.SetStretch( 1, 1 );

		var runHealthCard = CreateCard( pane, "Run Health", collapsible: true );
		_runHealthLabel = runHealthCard.Layout.Add( CreateSupportingLabel( "No run selected yet. Run a retarget from Home or select a run from History." ) );
		_runNextStepLabel = runHealthCard.Layout.Add( CreatePlaceholderLabel( "Next step: select a run from History, or retarget a clip from Home." ) );
		_runHealthLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.26f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.24f ).Hex}; border-radius: 6px;" );
		_runNextStepLabel.SetStyles( $"padding: 10px 12px; background-color: {Theme.ControlBackground.WithAlpha( 0.34f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 6px;" );

		var issuesCard = CreateCard( pane, "Issues", collapsible: true );
		_issuesLabel = issuesCard.Layout.Add( CreatePlaceholderLabel( "No issues to show yet." ), 1 );
		_issuesLabel.SetStyles( $"padding: 12px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.18f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 6px;" );

		var artifactsCard = CreateCard( pane, "Artifacts", collapsible: true, initiallyExpanded: false );
		_artifactSummaryLabel = artifactsCard.Layout.Add( CreatePlaceholderLabel( "Select a run to inspect its artifacts. Start with Run Log when something fails." ) );
		var artifactButtonsPrimary = artifactsCard.Layout.AddRow();
		artifactButtonsPrimary.Spacing = 6;
		_openRunDirButton = artifactButtonsPrimary.Add( StyleSecondaryActionButton( new Button( "Run Dir" ) { Clicked = OpenCurrentRunDirectory } ) );
		_openManifestButton = artifactButtonsPrimary.Add( StyleSecondaryActionButton( new Button( "Manifest" ) { Clicked = OpenCurrentManifest } ) );
		_openRawExportButton = artifactButtonsPrimary.Add( StyleSecondaryActionButton( new Button( "Raw Export" ) { Clicked = OpenCurrentRawExport } ) );
		_openRunLogButton = artifactButtonsPrimary.Add( StylePrimaryActionButton( new Button( "Run Log" ) { Clicked = OpenCurrentRunLog } ) );
		var artifactButtonsSecondary = artifactsCard.Layout.AddRow();
		artifactButtonsSecondary.Spacing = 6;
		_openImportedAnimationButton = artifactButtonsSecondary.Add( StyleSecondaryActionButton( new Button( "Imported FBX" ) { Clicked = OpenCurrentImportedAnimation } ) );
		_openGeneratedModelButton = artifactButtonsSecondary.Add( StyleSecondaryActionButton( new Button( "Generated VMDL" ) { Clicked = OpenCurrentGeneratedModel } ) );

		var runLogCard = CreateCard( pane, "Run Log", collapsible: true, initiallyExpanded: false );
		_runLogOutput = runLogCard.Layout.Add( new TextEdit( runLogCard ) );
		_runLogOutput.MinimumHeight = 260;
		_runLogOutput.ReadOnly = true;

		return scroll;
	}

	private Widget BuildPreviewDock( Widget parent )
	{
		var pane = new Widget( parent );
		pane.Layout = Layout.Column();
		pane.Layout.Margin = 0;
		pane.Layout.Spacing = 8;
		pane.MinimumWidth = 560;

		var previewCard = CreateCard( pane, "Preview", 1 );
		_livePreview = previewCard.Layout.Add( new RetargetLivePreviewWidget( previewCard ), 1 );

		var summaryCard = CreateCard( pane, "Current Run" );
		_resultDetailsLabel = summaryCard.Layout.Add( CreatePlaceholderLabel( "Production path: template FBX to generated Citizen full-fork VMDL." ) );
		var artifactButtonsPrimary = summaryCard.Layout.AddRow();
		artifactButtonsPrimary.Spacing = 6;
		_openRunDirButton = artifactButtonsPrimary.Add( StyleSecondaryActionButton( new Button( "Run Dir" ) { Clicked = OpenCurrentRunDirectory } ) );
		_openManifestButton = artifactButtonsPrimary.Add( StyleSecondaryActionButton( new Button( "Manifest" ) { Clicked = OpenCurrentManifest } ) );
		_openRawExportButton = artifactButtonsPrimary.Add( StyleSecondaryActionButton( new Button( "Raw Export" ) { Clicked = OpenCurrentRawExport } ) );
		var artifactButtonsSecondary = summaryCard.Layout.AddRow();
		artifactButtonsSecondary.Spacing = 6;
		_openImportedAnimationButton = artifactButtonsSecondary.Add( StyleSecondaryActionButton( new Button( "Imported FBX" ) { Clicked = OpenCurrentImportedAnimation } ) );
		_openGeneratedModelButton = artifactButtonsSecondary.Add( StyleSecondaryActionButton( new Button( "Generated VMDL" ) { Clicked = OpenCurrentGeneratedModel } ) );

		return pane;
	}

	private Widget CreateCard( Widget parent, string title, int stretch = 0, bool collapsible = false, bool initiallyExpanded = true )
	{
		var card = new Widget( parent );
		card.Layout = Layout.Column();
		card.Layout.Margin = 10;
		card.Layout.Spacing = 8;
		card.HorizontalSizeMode = SizeMode.Flexible;
		card.VerticalSizeMode = stretch > 0 ? SizeMode.Flexible : SizeMode.CanShrink;
		card.SetStyles( $"background-color: {Theme.ControlBackground.Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.35f ).Hex}; border-radius: 8px;" );
		if ( stretch > 0 )
			parent.Layout.Add( card, stretch );
		else
			parent.Layout.Add( card );
		var header = card.Layout.AddRow();
		header.Spacing = 6;
		IconButton? toggle = null;
		Widget? body = null;
		bool expanded = initiallyExpanded;

		if ( collapsible )
		{
			toggle = new IconButton( expanded ? "expand_more" : "chevron_right", parent: card )
			{
				IconSize = 18,
				ToolTip = expanded ? "Collapse section" : "Expand section",
				Background = Theme.ButtonBackground.WithAlpha( 0.35f ),
				Foreground = Theme.Text
			};
			toggle.FixedSize = Theme.RowHeight;
			header.Add( toggle );
		}

		header.Add( new Label.Header( title ) );
		header.AddStretchCell();
		var separator = card.Layout.AddSeparator();

		if ( !collapsible )
			return card;

		body = new Widget( card );
		body.Layout = Layout.Column();
		body.Layout.Margin = 0;
		body.Layout.Spacing = 8;
		card.Layout.Add( body );

		void UpdateExpanded()
		{
			if ( body is null || toggle is null )
				return;

			toggle.Icon = expanded ? "expand_more" : "chevron_right";
			toggle.ToolTip = expanded ? "Collapse section" : "Expand section";
			if ( expanded )
			{
				separator.Show();
				body.Show();
				body.MaximumHeight = 1000000;
				card.MaximumHeight = 1000000;
				card.MinimumHeight = 0;
				card.VerticalSizeMode = stretch > 0 ? SizeMode.Flexible : SizeMode.CanGrow;
			}
			else
			{
				separator.Hide();
				body.Hide();
				body.MaximumHeight = 0;
				card.MinimumHeight = Theme.RowHeight + 20;
				card.MaximumHeight = Theme.RowHeight + 24;
				card.VerticalSizeMode = SizeMode.CanShrink;
			}

			card.Update();
			card.UpdateGeometry();
			card.Parent?.UpdateGeometry();
		}

		if ( toggle is not null )
			toggle.OnClick = () =>
			{
				expanded = !expanded;
				UpdateExpanded();
			};

		UpdateExpanded();
		return body;
	}

	private void AddInlineSectionHeader( Widget parent, string title )
	{
		var header = parent.Layout.AddRow();
		header.Spacing = 6;
		header.Add( new Label.Subtitle( title ) );
		header.AddStretchCell();
		parent.Layout.AddSeparator();
	}

	private static Label.Body CreateSupportingLabel( string text, bool wordWrap = true )
	{
		var label = new Label.Body( text )
		{
			WordWrap = wordWrap,
			Color = Theme.Text.WithAlpha( 0.78f )
		};
		label.SetStyles( "padding: 4px 6px;" );
		return label;
	}

	private static Label.Small CreatePlaceholderLabel( string text, bool wordWrap = true )
	{
		var label = new Label.Small( text )
		{
			WordWrap = wordWrap,
			Color = Theme.Text.WithAlpha( 0.58f )
		};
		label.SetStyles( "padding: 5px 6px;" );
		return label;
	}

	private static Label.Small CreateCaptionLabel( string text, bool wordWrap = false )
	{
		var label = new Label.Small( text )
		{
			WordWrap = wordWrap,
			Color = Theme.Text.WithAlpha( 0.64f )
		};
		label.SetStyles( "padding: 3px 6px;" );
		return label;
	}

	private static Label.Small CreateMetricLabel( string text, bool wordWrap = false )
	{
		var label = new Label.Small( text )
		{
			WordWrap = wordWrap,
			Color = Theme.Text.WithAlpha( 0.74f )
		};
		label.SetStyles( $"font-family: '{Theme.MonospaceFont}'; padding: 3px 6px;" );
		return label;
	}

	private static Label.Small CreateFieldLabel( string text )
	{
		var label = new Label.Small( text )
		{
			Color = Theme.Text.WithAlpha( 0.68f )
		};
		label.SetStyles( "padding: 2px 4px 4px 2px;" );
		return label;
	}

	private static Label.Body CreateEnvironmentHealthBadge( string title, RetargetEnvironmentCheckSeverity severity, string status, bool unknown = true )
	{
		var label = new Label.Body( string.Empty )
		{
			WordWrap = false
		};
		UpdateEnvironmentHealthBadge( label, title, severity, status, unknown );
		return label;
	}

	private static Label.Small CreateActionGroupLabel( string text )
	{
		var label = new Label.Small( text )
		{
			WordWrap = false,
			Color = Theme.Text.WithAlpha( 0.7f )
		};
		label.SetStyles( $"padding: 7px 9px; min-height: 30px; background-color: {Theme.ControlBackground.WithAlpha( 0.55f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.22f ).Hex}; border-radius: 6px;" );
		return label;
	}

	private static void UpdateEnvironmentHealthBadge( Label label, string title, RetargetEnvironmentCheckSeverity severity, string status, bool unknown = false )
	{
		var color = unknown
			? Theme.Border.WithAlpha( 0.8f )
			: severity switch
			{
				RetargetEnvironmentCheckSeverity.Ok => new Color( 0.33f, 0.78f, 0.42f ),
				RetargetEnvironmentCheckSeverity.Warning => new Color( 0.95f, 0.7f, 0.24f ),
				_ => new Color( 0.95f, 0.28f, 0.24f )
			};
		var token = unknown
			? "?"
			: severity switch
			{
				RetargetEnvironmentCheckSeverity.Ok => "OK",
				RetargetEnvironmentCheckSeverity.Warning => "WARN",
				_ => "ERR"
			};

		label.Text = $"{token} {title}: {status}";
		label.Color = unknown ? Theme.Text.WithAlpha( 0.68f ) : Theme.Text.WithAlpha( 0.95f );
		label.SetStyles( $"padding: 7px 10px; min-height: 30px; font-weight: 600; background-color: {color.WithAlpha( unknown ? 0.08f : 0.16f ).Hex}; border: 1px solid {color.WithAlpha( unknown ? 0.28f : 0.68f ).Hex}; border-radius: 999px;" );
	}

	private EnvironmentDependencyRow CreateEnvironmentDependencyRow( Widget parent, string title, string summary )
	{
		var row = parent.Layout.Add( new Widget( parent ) );
		row.Layout = Layout.Row();
		row.Layout.Margin = 0;
		row.Layout.Spacing = 8;
		row.MinimumHeight = 60;
		row.SetStyles( $"padding: 10px 11px; background-color: {Theme.ControlBackground.WithAlpha( 0.34f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.18f ).Hex}; border-radius: 8px;" );

		var status = row.Layout.Add( new Label.Body( "?" )
		{
			WordWrap = false
		} );
		status.MinimumWidth = 34;
		status.MaximumWidth = 34;
		status.MinimumHeight = 28;
		status.MaximumHeight = 28;
		status.FixedHeight = 28;
		status.Alignment = TextFlag.Center;

		var textHost = row.Layout.Add( new Widget( row ), 1 );
		textHost.Layout = Layout.Column();
		textHost.Layout.Margin = 0;
		textHost.Layout.Spacing = 3;

		var titleLabel = textHost.Layout.Add( new Label.Body( title )
		{
			WordWrap = false,
			Color = Theme.Text.WithAlpha( 0.96f )
		} );
		titleLabel.SetStyles( "font-weight: 700; padding: 0 0 1px 0; background-color: transparent; border: 0px;" );

		var summaryLabel = textHost.Layout.Add( new Label.Small( summary )
		{
			WordWrap = true,
			Color = Theme.Text.WithAlpha( 0.68f )
		} );
		summaryLabel.SetStyles( "padding: 0; background-color: transparent; border: 0px;" );

		var detailLabel = textHost.Layout.Add( new Label.Small( string.Empty )
		{
			WordWrap = true,
			Color = Theme.Text.WithAlpha( 0.56f )
		} );
		detailLabel.SetStyles( $"font-family: '{Theme.MonospaceFont}'; padding: 0; background-color: transparent; border: 0px;" );

		var fieldHost = textHost.Layout.Add( new Widget( textHost ) );
		fieldHost.Layout = Layout.Column();
		fieldHost.Layout.Margin = 0;
		fieldHost.Layout.Spacing = 0;

		var actions = row.Layout.Add( new Widget( row ) );
		actions.Layout = Layout.Row();
		actions.Layout.Margin = 0;
		actions.Layout.Spacing = 4;
		actions.FixedHeight = 32;

		UpdateEnvironmentStatusPill( status, RetargetEnvironmentCheckSeverity.Warning, true );
		return new EnvironmentDependencyRow
		{
			Status = status,
			Title = titleLabel,
			Summary = summaryLabel,
			Detail = detailLabel,
			FieldHost = fieldHost,
			Actions = actions
		};
	}

	private static LineEdit CreateHiddenEnvironmentPathField( Widget parent, Action onEdited )
	{
		var field = new LineEdit( parent );
		field.TextEdited += _ => onEdited();
		field.FixedHeight = 0;
		field.MaximumHeight = 0;
		field.MinimumHeight = 0;
		field.SetStyles( "opacity: 0; max-height: 0px; min-height: 0px; height: 0px; padding: 0px; margin: 0px; border: 0px;" );
		return field;
	}

	private static Button StyleEnvironmentActionButton( Button button, bool primary = false, bool subtle = false )
	{
		var background = primary
			? Theme.Primary.WithAlpha( 0.88f )
			: subtle
				? Theme.ControlBackground.WithAlpha( 0.46f )
				: Theme.ControlBackground.WithAlpha( 0.82f );
		var border = primary
			? Theme.Primary.Lighten( 0.12f ).WithAlpha( 0.65f )
			: Theme.Border.WithAlpha( subtle ? 0.18f : 0.34f );
		var text = subtle
			? Theme.Text.WithAlpha( 0.62f )
			: Theme.Text.WithAlpha( 0.94f );
		button.MinimumHeight = 30;
		button.SetStyles( $"padding: 5px 8px; min-height: 30px; min-width: 0px; border-radius: 6px; background-color: {background.Hex}; border: 1px solid {border.Hex}; color: {text.Hex}; font-weight: 600;" );
		return button;
	}

	private static void SetEnvironmentActionButtonPresent( Button button, bool present, bool primary = false, bool subtle = false )
	{
		button.Visible = present;
		if ( present )
		{
			button.MinimumWidth = 0;
			button.MinimumHeight = 30;
			button.MaximumWidth = 1000000;
			button.MaximumHeight = 1000000;
			StyleEnvironmentActionButton( button, primary, subtle );
			return;
		}

		button.Enabled = false;
		button.MinimumWidth = 0;
		button.MinimumHeight = 0;
		button.MaximumWidth = 0;
		button.MaximumHeight = 0;
		button.SetStyles( "padding: 0px; margin: 0px; min-width: 0px; max-width: 0px; width: 0px; min-height: 0px; max-height: 0px; height: 0px; border: 0px; opacity: 0;" );
	}

	private static void UpdateEnvironmentDependencyRow( EnvironmentDependencyRow row, RetargetEnvironmentCheckSeverity severity, string summary, string detail, bool unknown = false )
	{
		UpdateEnvironmentStatusPill( row.Status, severity, unknown );
		row.Summary.Text = summary;
		row.Detail.Text = CompactEnvironmentDetail( detail );
	}

	private static void UpdateEnvironmentStatusPill( Label label, RetargetEnvironmentCheckSeverity severity, bool unknown = false )
	{
		var color = unknown
			? Theme.Border.WithAlpha( 0.8f )
			: severity switch
			{
				RetargetEnvironmentCheckSeverity.Ok => new Color( 0.33f, 0.78f, 0.42f ),
				RetargetEnvironmentCheckSeverity.Warning => new Color( 0.95f, 0.7f, 0.24f ),
				_ => new Color( 0.95f, 0.28f, 0.24f )
			};
		label.Text = unknown
			? "?"
			: severity switch
			{
				RetargetEnvironmentCheckSeverity.Ok => "✓",
				RetargetEnvironmentCheckSeverity.Warning => "!",
				_ => "×"
			};
		label.Color = unknown ? Theme.Text.WithAlpha( 0.68f ) : Theme.Text.WithAlpha( 0.96f );
		label.SetStyles( $"padding: 0px; min-width: 28px; max-width: 28px; min-height: 28px; max-height: 28px; font-weight: 800; background-color: {color.WithAlpha( unknown ? 0.08f : 0.15f ).Hex}; border: 1px solid {color.WithAlpha( unknown ? 0.28f : 0.62f ).Hex}; border-radius: 7px;" );
	}

	private static string CompactEnvironmentDetail( string detail )
	{
		if ( string.IsNullOrWhiteSpace( detail ) )
			return string.Empty;

		var normalized = detail.Trim();
		const int maxLength = 128;
		if ( normalized.Length <= maxLength )
			return normalized;

		var keepStart = Math.Min( 42, normalized.Length / 2 );
		var keepEnd = maxLength - keepStart - 3;
		if ( keepEnd <= 0 )
			return normalized[..maxLength];
		return $"{normalized[..keepStart]}...{normalized[^keepEnd..]}";
	}

	private static T StyleInteractiveField<T>( T widget ) where T : Widget
	{
		widget.SetStyles( $"background-color: {Theme.SurfaceBackground.WithAlpha( 0.75f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.42f ).Hex}; border-radius: 6px; padding: 4px 6px;" );
		return widget;
	}

	private static T StyleInteractiveList<T>( T widget ) where T : Widget
	{
		widget.SetStyles( $"background-color: {Theme.SurfaceBackground.WithAlpha( 0.82f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.4f ).Hex}; border-radius: 8px;" );
		return widget;
	}

	private static Button StylePrimaryActionButton( Button button )
	{
		button.SetStyles( $"padding: 6px 12px; min-height: 32px; font-weight: 600; border-radius: 6px; background-color: {Theme.Primary.Hex}; color: white; border: 1px solid {Theme.Primary.Lighten( 0.15f ).Hex};" );
		return button;
	}

	private static Button StyleSecondaryActionButton( Button button )
	{
		button.SetStyles( $"padding: 6px 12px; min-height: 32px; font-weight: 500; border-radius: 6px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.92f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.5f ).Hex}; color: {Theme.Text.Hex};" );
		return button;
	}

	private static Button StyleSubtleActionButton( Button button )
	{
		button.SetStyles( $"padding: 5px 10px; min-height: 30px; border-radius: 6px; background-color: {Theme.ControlBackground.WithAlpha( 0.72f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.34f ).Hex}; color: {Theme.Text.WithAlpha( 0.88f ).Hex};" );
		return button;
	}

	private void AddLabeledLineEdit( Widget parent, string label, out LineEdit lineEdit, string placeholder, Action onEdited )
	{
		var row = parent.Layout.AddRow();
		row.Spacing = 6;
		row.Add( CreateFieldLabel( label ) );
		lineEdit = row.Add( StyleInteractiveField( new LineEdit() ) );
		lineEdit.PlaceholderText = placeholder;
		lineEdit.TextEdited += _ => onEdited();
	}

	private Widget CreateSourceFacingDegreeCell( Widget parent, string label, out LineEdit field, Action<float> onChanged )
	{
		var cell = new Widget( parent );
		cell.Layout = Layout.Column();
		cell.Layout.Margin = 0;
		cell.Layout.Spacing = 4;
		cell.SetStyles( $"padding: 8px; background-color: {Theme.SurfaceBackground.WithAlpha( 0.35f ).Hex}; border: 1px solid {Theme.Border.WithAlpha( 0.22f ).Hex}; border-radius: 7px;" );
		cell.Layout.Add( CreateCaptionLabel( label, false ) );
		var input = cell.Layout.Add( StyleInteractiveField( new LineEdit() ) );
		input.PlaceholderText = "0";
		input.TextEdited += _ =>
		{
			if ( _suppressSourceFacingFieldSync )
				return;

			var text = input.Text?.Trim() ?? string.Empty;
			if ( !float.TryParse( text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value )
				&& !float.TryParse( text, out value ) )
			{
				return;
			}

			onChanged( value );
		};
		field = input;
		return cell;
	}

	private ComboBox CreateSourcePresetCombo()
	{
		var combo = StyleInteractiveField( new ComboBox() );
		combo.AddItem( "Generic Humanoid" );
		combo.AddItem( "Mixamo Humanoid" );
		combo.AddItem( "Quaternius UAL2" );
		return combo;
	}

	private int GetSelectedSourcePresetIndex()
	{
		if ( _sourceProfilePreset is not null )
			return _sourceProfilePreset.CurrentIndex;
		if ( _mappingSourceProfilePreset is not null )
			return _mappingSourceProfilePreset.CurrentIndex;
		return 0;
	}

	private void SetSourcePresetControlsFromJob()
	{
		var index = GetSourcePresetIndexFromPath( _job.SourceProfilePath );
		_suppressSourcePresetSync = true;
		try
		{
			if ( _sourceProfilePreset is not null )
				_sourceProfilePreset.CurrentIndex = index;
			if ( _mappingSourceProfilePreset is not null )
				_mappingSourceProfilePreset.CurrentIndex = index;
		}
		finally
		{
			_suppressSourcePresetSync = false;
		}
	}

	private void OnSourcePresetChanged( ComboBox changedPreset )
	{
		if ( _suppressSourcePresetSync )
			return;

		_suppressSourcePresetSync = true;
		try
		{
			if ( _sourceProfilePreset is not null && !ReferenceEquals( changedPreset, _sourceProfilePreset ) )
				_sourceProfilePreset.CurrentIndex = changedPreset.CurrentIndex;
			if ( _mappingSourceProfilePreset is not null && !ReferenceEquals( changedPreset, _mappingSourceProfilePreset ) )
				_mappingSourceProfilePreset.CurrentIndex = changedPreset.CurrentIndex;
		}
		finally
		{
			_suppressSourcePresetSync = false;
		}

		SyncJobFromControls();
		LoadProfilesFromJob();
		if ( _inspection is not null )
			_mappingState = _pipeline.BuildMappingState( _job, _sourceProfile, _mappingProfile, _inspection );
		_sourceFacingEulerDegrees = GetStoredSourceFacingEulerDegrees();
		ClearLegacyManualFacingIfItMatchesPreset();
		RefreshSourceFacingUi();
		RefreshSourceBoneList();
		RefreshMappingList();
		UpdateDiagnostics();
		UpdateButtons();
	}

	private void LoadProfilesFromJob() { _sourceProfile = _pipeline.LoadSourceProfile( _job.SourceProfilePath ); _mappingProfile = _pipeline.LoadMappingProfile( _job.MappingProfilePath ); }

	private static int GetSourcePresetIndexFromPath( string? sourceProfilePath )
	{
		var normalized = NormalizeProfilePathForUi( sourceProfilePath );
		if ( normalized.Equals( NormalizeProfilePathForUi( CitizenTargetProfile.MixamoSourceProfileAssetPath ), StringComparison.OrdinalIgnoreCase ) )
			return 1;
		if ( normalized.Equals( NormalizeProfilePathForUi( CitizenTargetProfile.DefaultSourceProfileAssetPath ), StringComparison.OrdinalIgnoreCase ) )
			return 2;
		return 0;
	}

	private static string GetSourceProfilePathFromPresetIndex( int index )
	{
		return index switch
		{
			1 => CitizenTargetProfile.MixamoSourceProfileAssetPath,
			2 => CitizenTargetProfile.DefaultSourceProfileAssetPath,
			_ => string.Empty
		};
	}

	private static string NormalizeProfilePathForUi( string? path )
	{
		return (path ?? string.Empty).Trim().Replace( '\\', '/' ).TrimStart( '/' );
	}

	private void CacheSourceProfileForScan( IDictionary<string, RetargetSourceProfile> profilesByPath, string profilePath )
	{
		var key = NormalizeProfilePathForUi( profilePath );
		if ( string.IsNullOrWhiteSpace( key ) || profilesByPath.ContainsKey( key ) )
			return;

		profilesByPath[key] = _pipeline.LoadSourceProfile( profilePath );
	}

	private void TryHydrateFromJob()
	{
		SetStatus( File.Exists( _job.SourceFbxPath )
			? "Source path restored. Press Scan when you want to inspect the FBX."
			: "Point the workstation at a humanoid FBX and press Scan." );
	}

	private void ReportScanFailure( string phase, Exception exception )
	{
		var sourcePath = CitizenRetargetPaths.DecodeExternalPath( _job.SourceFbxPath ?? string.Empty );
		var message = $"Scan failed during {phase}: {exception.Message}";
		RememberScanDiagnostic(
			"failed",
			sourcePath,
			$"{message}{Environment.NewLine}{exception}" );
		SetStatus( message );
		if ( _clipSummaryLabel is not null )
			_clipSummaryLabel.Text = $"{message} Open Diagnostics -> Copy Diagnostics or Export Support Bundle if you need help.";

		try
		{
			var logPath = CitizenRetargetPaths.GetTempPath( "scan_failures.log" );
			Directory.CreateDirectory( Path.GetDirectoryName( logPath )! );
			File.AppendAllText(
				logPath,
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}" +
				$"Source: {sourcePath}{Environment.NewLine}" +
				$"{exception}{Environment.NewLine}{Environment.NewLine}" );
		}
		catch
		{
			// Status text is the important user-facing fallback; logging must never break Scan.
		}
	}

	private void RememberScanDiagnostic( string status, string sourcePath, string details )
	{
		_lastScanStatus = status;
		_lastScanSourcePath = sourcePath;
		_lastScanDetails = details;
		_lastScanCompletedAt = DateTime.Now;
	}

	private static string BuildScanSuccessDiagnostic( ScanJobResult result )
	{
		var lines = new List<string>
		{
			$"Clips: {result.Clips.Count}",
			$"Bones: {result.Inspection.Audit.Bones.Count}",
			$"Source profile: {result.DetectedSourceProfileDisplayName}",
			$"Source profile path: {result.DetectedSourceProfilePath}",
			$"Mapping profile path: {result.DetectedMappingProfilePath}"
		};

		if ( result.Clips.Count == 0 )
		{
			lines.Add( "No animation clips were discovered. The FBX may contain only mesh/skeleton data, unsupported animation stacks, or animation data that the native scanner could not expose." );
		}

		return string.Join( Environment.NewLine, lines );
	}

	private void BeginEnvironmentCheck( bool startQueueWhenReady )
	{
		if ( HasActiveBackgroundJob( "environment" ) )
		{
			SetStatus( "Setup diagnostics are already running." );
			return;
		}

		SyncJobFromControls();
		SyncToolSettingsFromControls();
		var jobSnapshot = _pipeline.CreateRuntimeJobSnapshot( _job );
		_startQueueAfterEnvironmentCheck = startQueueWhenReady;
		StartBackgroundJob(
			title: "Checking retarget setup",
			scope: "environment",
			blocksInteraction: false,
			showInJobsStrip: true,
			work: reporter =>
			{
				var report = _environmentDiagnostics.Check(
					jobSnapshot,
					probeBlenderAddon: true,
					progress: (progress, detail) => reporter.ReportProgress( progress, detail ) );

				return () =>
				{
					_environmentReport = report;
					RefreshEnvironmentDiagnostics();
					UpdateButtons();

					if ( report.CanRunRetarget )
					{
						_latestSetupFailureMessage = string.Empty;
						ClearPendingSetupMessages();
						RefreshQueueList();
						SetStatus( _startQueueAfterEnvironmentCheck
							? "Setup check passed. Starting queue."
							: "Setup check passed. Retarget environment is ready." );
						if ( _startQueueAfterEnvironmentCheck )
							StartQueueCore();
					}
					else
					{
						_latestSetupFailureMessage = BuildSetupFailureMessage( report );
						MarkPendingQueueItemsWithSetupFailure( _latestSetupFailureMessage );
						RefreshQueueList();
						SetStatus( _latestSetupFailureMessage );
					}

					_startQueueAfterEnvironmentCheck = false;
				};
			},
			onError: exception =>
			{
				_startQueueAfterEnvironmentCheck = false;
				_latestSetupFailureMessage = $"Setup diagnostics failed: {exception.Message}";
				_environmentReport = new RetargetEnvironmentReport
				{
					Checks =
					{
						new RetargetEnvironmentCheck
						{
							Id = "environment_exception",
							Title = "Environment diagnostics",
							Severity = RetargetEnvironmentCheckSeverity.Error,
							BlocksRetarget = true,
							Detail = exception.Message,
							FixHint = "Open the editor console. The setup checker failed before producing a report."
						}
					}
				};
				MarkPendingQueueItemsWithSetupFailure( _latestSetupFailureMessage );
				RefreshQueueList();
				RefreshEnvironmentDiagnostics();
				SetStatus( _latestSetupFailureMessage );
			} );
	}

	private void OpenEnvironmentBackendFolder()
	{
		SyncToolSettingsFromControls();
		var backendRoot = _environmentReport?.BackendRootPath ?? _toolSettings.BackendRootPath ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( backendRoot ) || !Directory.Exists( backendRoot ) )
		{
			SetStatus( "Backend folder is not available. Re-sync the plugin, or set Backend Override for development." );
			return;
		}

		_pipeline.OpenPath( backendRoot );
	}

	private static string GetNativeHelperTargetDirectory()
	{
		return Path.Combine( CitizenRetargetPaths.NativeRoot, "win-x64" );
	}

	private static string GetNativeHelperDllPath()
	{
		return Path.Combine( GetNativeHelperTargetDirectory(), NativeHelperInstaller.DllFileName );
	}

	private void ConfirmDownloadNativeHelper()
	{
		if ( HasActiveBackgroundJob( "environment" ) )
		{
			SetStatus( BuildActiveJobStatus( "environment", "Native helper install cannot start while setup diagnostics are running." ) );
			return;
		}

		var targetDirectory = GetNativeHelperTargetDirectory();
		var dllPath = Path.Combine( targetDirectory, NativeHelperInstaller.DllFileName );
		var action = File.Exists( dllPath ) ? "reinstall" : "download and install";
		Dialog.AskConfirm(
			() => BeginNativeHelperDownload( targetDirectory ),
			$"CARL will {action} the Windows native FBX helper from the official GitHub Release, verify SHA256, and place it in {targetDirectory}. Continue?",
			"Install Native Helper",
			"Download",
			"Cancel" );
	}

	private void BeginNativeHelperDownload( string targetDirectory )
	{
		if ( HasActiveBackgroundJob( "environment" ) )
		{
			SetStatus( "Setup diagnostics or native helper install is already running." );
			return;
		}

		StartBackgroundJob(
			title: "Installing native FBX helper",
			scope: "environment",
			blocksInteraction: false,
			showInJobsStrip: true,
			work: reporter =>
			{
				var result = NativeHelperInstaller.InstallDefaultWinX64Async(
					targetDirectory,
					(progress, detail) => reporter.ReportProgress( progress, detail ) ).GetAwaiter().GetResult();

				return () =>
				{
					SetStatus( $"Native FBX helper installed and verified: {result.DllPath}" );
					BeginEnvironmentCheck( false );
				};
			},
			onError: exception =>
			{
				SetStatus( $"Native helper install failed: {exception.Message}" );
				RefreshEnvironmentDiagnostics();
			} );
	}

	private void OpenNativeHelperFolder()
	{
		var targetDirectory = GetNativeHelperTargetDirectory();
		if ( !Directory.Exists( targetDirectory ) )
		{
			SetStatus( "Native helper folder is not available yet. Use Download Helper first." );
			return;
		}

		_pipeline.OpenPath( targetDirectory );
	}

	private void BrowseEnvironmentBackendFolder()
	{
		try
		{
			SyncToolSettingsFromControls();
			var fallbackDirectory = Directory.GetParent( CitizenRetargetPaths.ProjectRoot )?.FullName
				?? Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments );
			var selectedPath = RetargetFileDialogs.PickDirectory( "Choose Backend Override Folder", _toolSettings.BackendRootPath, fallbackDirectory );
			if ( string.IsNullOrWhiteSpace( selectedPath ) )
			{
				SetStatus( "Backend override selection cancelled." );
				return;
			}

			if ( _backendPath is not null )
				_backendPath.Text = selectedPath;
			SyncToolSettingsFromControls();
			SetStatus( $"Backend override set to '{selectedPath}'." );
			BeginEnvironmentCheck( false );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Browse backend failed: {exception.Message}" );
		}
	}

	private void BrowseBlenderExecutablePath()
	{
		try
		{
			SyncToolSettingsFromControls();
			var fallbackDirectory = Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles );
			var selectedPath = RetargetFileDialogs.PickExistingFile( "Choose blender.exe", "exe", _toolSettings.BlenderExecutablePath, fallbackDirectory );
			if ( string.IsNullOrWhiteSpace( selectedPath ) )
			{
				SetStatus( "Blender path selection cancelled." );
				return;
			}

			if ( !Path.GetFileName( selectedPath ).Equals( "blender.exe", StringComparison.OrdinalIgnoreCase ) )
			{
				SetStatus( "Selected file is not blender.exe. Pick Blender's executable file." );
				return;
			}

			if ( _blenderPath is not null )
				_blenderPath.Text = selectedPath;
			SyncToolSettingsFromControls();
			SetStatus( $"Blender path set to '{selectedPath}'." );
			BeginEnvironmentCheck( false );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Browse Blender failed: {exception.Message}" );
		}
	}

	private void AutoScanRokokoAddon()
	{
		if ( _rokokoAddonPath is not null )
			_rokokoAddonPath.Text = string.Empty;
		SyncToolSettingsFromControls();
		SetStatus( "Rokoko addon override cleared. Diagnostics will auto-scan Blender addons." );
		BeginEnvironmentCheck( false );
	}

	private void BrowseRokokoAddonPath()
	{
		try
		{
			SyncToolSettingsFromControls();
			var selectedPath = RetargetFileDialogs.PickDirectory(
				"Choose Rokoko Addon Folder",
				ResolveRokokoAddonFolder( _toolSettings.RokokoAddonPath ),
				Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ) );
			if ( string.IsNullOrWhiteSpace( selectedPath ) )
			{
				SetStatus( "Rokoko addon selection cancelled." );
				return;
			}

			if ( _rokokoAddonPath is not null )
				_rokokoAddonPath.Text = selectedPath;
			SyncToolSettingsFromControls();
			SetStatus( $"Rokoko addon path set to '{selectedPath}'." );
			BeginEnvironmentCheck( false );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Browse Rokoko addon failed: {exception.Message}" );
		}
	}

	private void OpenRokokoAddonFolder()
	{
		SyncToolSettingsFromControls();
		var folder = ResolveRokokoAddonFolder( ResolveCurrentRokokoAddonPath() );
		if ( string.IsNullOrWhiteSpace( folder ) || !Directory.Exists( folder ) )
		{
			SetStatus( "Rokoko addon folder is not available. Paste a valid addon folder/__init__.py path or use Auto Scan Rokoko." );
			return;
		}

		_pipeline.OpenPath( folder );
	}

	private void ClearRokokoAddonPath()
	{
		if ( _rokokoAddonPath is not null )
			_rokokoAddonPath.Text = string.Empty;
		SyncToolSettingsFromControls();
		SetStatus( "Rokoko addon path cleared. Auto Scan Rokoko will use Blender's installed addon list." );
	}

	private static string ResolveRokokoAddonFolder( string? configuredPath )
	{
		var path = (configuredPath ?? string.Empty).Trim().Trim( '"' );
		if ( string.IsNullOrWhiteSpace( path ) )
			return string.Empty;
		if ( Directory.Exists( path ) )
			return path;
		if ( File.Exists( path ) )
			return Path.GetDirectoryName( path ) ?? string.Empty;
		return path;
	}

	private string ResolveCurrentRokokoAddonPath()
	{
		if ( !string.IsNullOrWhiteSpace( _toolSettings.RokokoAddonPath ) )
			return _toolSettings.RokokoAddonPath;

		return _environmentReport?.Checks.FirstOrDefault( check => check.Id == "rokoko_addon" && !string.IsNullOrWhiteSpace( check.Path ) )?.Path ?? string.Empty;
	}

	private void CopyEnvironmentDiagnostics()
	{
		try
		{
			SyncToolSettingsFromControls();
			var text = BuildSupportDiagnosticsText();
			EditorUtility.Clipboard.Copy( text );
			SetStatus( "Copied CARL diagnostics to clipboard." );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Failed to copy diagnostics: {exception.Message}" );
		}
	}

	private void ExportSupportBundle()
	{
		try
		{
			SyncToolSettingsFromControls();
			var stamp = DateTime.Now.ToString( "yyyyMMdd_HHmmss" );
			var defaultBundleRoot = Path.Combine( CitizenRetargetPaths.LocalSettingsRoot, "support_bundles" );
			Directory.CreateDirectory( defaultBundleRoot );
			var zipPath = RetargetFileDialogs.SaveZipFile(
				"Save CARL Support Bundle",
				Path.Combine( defaultBundleRoot, $"citizen-retarget-support-{stamp}.zip" ) );
			if ( string.IsNullOrWhiteSpace( zipPath ) )
			{
				SetStatus( "Support bundle export cancelled." );
				return;
			}

			Directory.CreateDirectory( Path.GetDirectoryName( zipPath )! );
			var stagingRoot = Path.Combine( defaultBundleRoot, $"_staging-{stamp}" );
			if ( Directory.Exists( stagingRoot ) )
				Directory.Delete( stagingRoot, true );
			Directory.CreateDirectory( stagingRoot );

			File.WriteAllText( Path.Combine( stagingRoot, "diagnostics.txt" ), BuildSupportDiagnosticsText() );
			WriteSupportBundleSummary( Path.Combine( stagingRoot, "summary.json" ) );
			CopySupportFileIfExists( RetargetToolSettings.SettingsAbsolutePath, Path.Combine( stagingRoot, "settings.json" ) );
			CopySupportFileIfExists( CitizenRetargetPaths.GetTempPath( "scan_failures.log" ), Path.Combine( stagingRoot, "scan_failures.log" ) );

			var artifactResult = GetCurrentArtifactResult();
			if ( artifactResult is not null )
			{
				var runArtifactsRoot = Path.Combine( stagingRoot, "latest_run" );
				Directory.CreateDirectory( runArtifactsRoot );
				CopySupportFileIfExists( artifactResult.ArtifactLinks.ManifestPath, Path.Combine( runArtifactsRoot, "result_manifest.json" ) );
				CopySupportFileIfExists( artifactResult.ArtifactLinks.RunLogPath, Path.Combine( runArtifactsRoot, CitizenRetargetPaths.EditorRunLogFileName ) );
			}

			if ( File.Exists( zipPath ) )
				File.Delete( zipPath );
			ZipFile.CreateFromDirectory( stagingRoot, zipPath );
			Directory.Delete( stagingRoot, true );
			_pipeline.OpenPath( zipPath );
			SetStatus( $"Exported support bundle: {zipPath}" );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Failed to export support bundle: {exception.Message}" );
		}
	}

	private void WriteSupportBundleSummary( string path )
	{
		var latestEntry = (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>())
			.OrderByDescending( entry => entry.CreatedUtc ?? string.Empty )
			.FirstOrDefault();
		var summary = new
		{
			createdUtc = DateTime.UtcNow.ToString( "o" ),
			pluginVersion = CitizenRetargetPluginInfo.Version,
			projectRoot = CitizenRetargetPaths.ProjectRoot,
			pluginRoot = CitizenRetargetPaths.PluginRoot,
			setup = _environmentReport is null
				? "not scanned"
				: _environmentReport.BuildHeadline(),
			latestScan = new
			{
				status = _lastScanStatus,
				source = _lastScanSourcePath,
				completedAt = _lastScanCompletedAt?.ToString( "o" ) ?? string.Empty
			},
			latestRun = latestEntry is null
				? null
				: new
				{
					runId = latestEntry.RunId,
					clip = latestEntry.ClipName,
					sequence = latestEntry.SequenceName,
					status = latestEntry.Status,
					createdUtc = latestEntry.CreatedUtc,
					manifest = CitizenRetargetPaths.DecodeExternalPath( latestEntry.ManifestPath ),
					importedAsset = CitizenRetargetPaths.DecodeExternalPath( latestEntry.ImportedAssetPath ),
					targetVmdl = latestEntry.TargetVmdlPath
				}
		};
		File.WriteAllText( path, JsonSerializer.Serialize( summary, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	private static void CopySupportFileIfExists( string? sourcePath, string destinationPath )
	{
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !File.Exists( sourcePath ) )
			return;

		Directory.CreateDirectory( Path.GetDirectoryName( destinationPath )! );
		File.Copy( sourcePath, destinationPath, true );
	}

	private static string BuildSetupFailureMessage( RetargetEnvironmentReport report )
	{
		var firstIssue = report.BlockingIssues.FirstOrDefault();
		if ( firstIssue is null )
			return "Setup check failed. Open Diagnostics for details.";

		var hint = string.IsNullOrWhiteSpace( firstIssue.FixHint ) ? string.Empty : $" Fix: {firstIssue.FixHint}";
		return $"Setup blocked: {firstIssue.Title} - {firstIssue.Detail}.{hint}";
	}

	private void MarkPendingQueueItemsWithSetupFailure( string message )
	{
		foreach ( var item in _queueItems.Where( item => item.Status.Equals( "pending", StringComparison.OrdinalIgnoreCase ) ) )
			item.Message = message;
	}

	private void ClearPendingSetupMessages()
	{
		foreach ( var item in _queueItems.Where( item => item.Status.Equals( "pending", StringComparison.OrdinalIgnoreCase ) ) )
		{
			if ( item.Message.StartsWith( "Setup blocked:", StringComparison.OrdinalIgnoreCase )
				|| item.Message.StartsWith( "Setup diagnostics failed:", StringComparison.OrdinalIgnoreCase )
				|| item.Message.StartsWith( "Checking retarget setup", StringComparison.OrdinalIgnoreCase ) )
			{
				item.Message = "Queued";
			}
		}
	}

	private void RefreshEnvironmentDiagnostics()
	{
		if ( _environmentSummaryLabel is null || _environmentDetailsLabel is null )
			return;

		if ( _environmentReport is null )
		{
			_environmentSummaryLabel.Text = "Setup has not been checked yet.";
			_environmentDetailsLabel.Text = "Run setup diagnostics to verify Blender, the Rokoko addon, backend files, native FBX scanning, and the current target.";
			UpdateEnvironmentDependencyRow( _environmentSettingsRow, RetargetEnvironmentCheckSeverity.Warning, "Not checked yet.", "Settings will be stored under .sbox/citizen_retarget/settings.json.", unknown: true );
			UpdateEnvironmentDependencyRow( _environmentBackendRow, RetargetEnvironmentCheckSeverity.Warning, "Not checked yet.", "Bundled backend will be used unless an override is set.", unknown: true );
			UpdateEnvironmentDependencyRow( _environmentBlenderRow, RetargetEnvironmentCheckSeverity.Warning, "Not checked yet.", "Leave empty to auto-detect Blender, or browse to blender.exe.", unknown: true );
			UpdateEnvironmentDependencyRow( _environmentRokokoRow, RetargetEnvironmentCheckSeverity.Warning, "Not checked yet.", "Leave empty to auto-scan Blender addons, or browse to the addon folder.", unknown: true );
			UpdateEnvironmentDependencyRow( _environmentNativeFbxRow, RetargetEnvironmentCheckSeverity.Warning, "Not checked yet.", "Use Download Helper if the native scanner DLL is missing.", unknown: true );
			UpdateEnvironmentDependencyRow( _environmentTargetRow, RetargetEnvironmentCheckSeverity.Warning, "Not checked yet.", "Current target model must exist before retargeting.", unknown: true );
			return;
		}

		_environmentSummaryLabel.Text = $"{_environmentReport.BuildHeadline()} Checked at {_environmentReport.CheckedAt:HH:mm:ss}.";
		_environmentDetailsLabel.Text = BuildEnvironmentDetailsLabel( _environmentReport );
		UpdateEnvironmentDependencyRow( _environmentSettingsRow, GetEnvironmentGroupSeverity( _environmentReport, "tool_settings" ), BuildEnvironmentGroupStatus( _environmentReport, "tool_settings" ), BuildEnvironmentGroupDetail( _environmentReport, "Project-local settings file.", "tool_settings" ) );
		UpdateEnvironmentDependencyRow( _environmentBackendRow, GetEnvironmentGroupSeverity( _environmentReport, "backend_root", "backend_recipe", "backend_script", "target_reference", "recipe_target_reference" ), BuildEnvironmentGroupStatus( _environmentReport, "backend_root", "backend_recipe", "backend_script", "target_reference", "recipe_target_reference" ), BuildEnvironmentGroupDetail( _environmentReport, "Bundled backend is available.", "backend_root", "backend_recipe", "backend_script", "target_reference", "recipe_target_reference" ) );
		UpdateEnvironmentDependencyRow( _environmentBlenderRow, GetEnvironmentGroupSeverity( _environmentReport, "blender", "blender_version" ), BuildEnvironmentGroupStatus( _environmentReport, "blender", "blender_version" ), BuildBlenderEnvironmentDetail( _environmentReport ) );
		UpdateEnvironmentDependencyRow( _environmentRokokoRow, GetEnvironmentGroupSeverity( _environmentReport, "rokoko_addon", "rokoko_addon_path" ), BuildEnvironmentGroupStatus( _environmentReport, "rokoko_addon", "rokoko_addon_path" ), BuildEnvironmentGroupDetail( _environmentReport, "Rokoko addon was found in Blender.", "rokoko_addon", "rokoko_addon_path" ) );
		UpdateEnvironmentDependencyRow( _environmentNativeFbxRow, GetEnvironmentGroupSeverity( _environmentReport, "native_fbx" ), BuildEnvironmentGroupStatus( _environmentReport, "native_fbx" ), BuildEnvironmentGroupDetail( _environmentReport, "Native FBX scanner is available.", "native_fbx" ) );
		UpdateEnvironmentDependencyRow( _environmentTargetRow, GetEnvironmentGroupSeverity( _environmentReport, "target_model" ), BuildEnvironmentGroupStatus( _environmentReport, "target_model" ), BuildEnvironmentGroupDetail( _environmentReport, "Current target model exists.", "target_model" ) );
	}

	private static RetargetEnvironmentCheckSeverity GetEnvironmentGroupSeverity( RetargetEnvironmentReport report, params string[] ids )
	{
		var matches = report.Checks.Where( check => ids.Contains( check.Id ) ).ToList();
		if ( matches.Any( check => check.Severity == RetargetEnvironmentCheckSeverity.Error ) )
			return RetargetEnvironmentCheckSeverity.Error;
		if ( matches.Any( check => check.Severity == RetargetEnvironmentCheckSeverity.Warning ) )
			return RetargetEnvironmentCheckSeverity.Warning;
		return RetargetEnvironmentCheckSeverity.Ok;
	}

	private static string BuildEnvironmentGroupStatus( RetargetEnvironmentReport report, params string[] ids )
	{
		var matches = report.Checks.Where( check => ids.Contains( check.Id ) ).ToList();
		if ( matches.Count == 0 )
			return "Not checked";

		var blocking = matches.Count( check => check.BlocksRetarget && check.Severity == RetargetEnvironmentCheckSeverity.Error );
		if ( blocking > 0 )
			return $"{blocking} blocker{(blocking == 1 ? string.Empty : "s")}";

		var errors = matches.Count( check => check.Severity == RetargetEnvironmentCheckSeverity.Error );
		if ( errors > 0 )
			return $"{errors} error{(errors == 1 ? string.Empty : "s")}";

		var warnings = matches.Count( check => check.Severity == RetargetEnvironmentCheckSeverity.Warning );
		if ( warnings > 0 )
			return $"{warnings} warning{(warnings == 1 ? string.Empty : "s")}";

		return "Healthy";
	}

	private static string BuildEnvironmentGroupDetail( RetargetEnvironmentReport report, string fallback, params string[] ids )
	{
		var matches = report.Checks.Where( check => ids.Contains( check.Id ) ).ToList();
		var primary = matches.FirstOrDefault( check => check.BlocksRetarget && check.Severity == RetargetEnvironmentCheckSeverity.Error )
			?? matches.FirstOrDefault( check => check.Severity == RetargetEnvironmentCheckSeverity.Error )
			?? matches.FirstOrDefault( check => check.Severity == RetargetEnvironmentCheckSeverity.Warning )
			?? matches.FirstOrDefault( check => !string.IsNullOrWhiteSpace( check.Path ) )
			?? matches.FirstOrDefault();
		if ( primary is null )
			return fallback;
		if ( !string.IsNullOrWhiteSpace( primary.FixHint ) && primary.Severity != RetargetEnvironmentCheckSeverity.Ok )
			return primary.FixHint;
		if ( !string.IsNullOrWhiteSpace( primary.Path ) )
			return primary.Path;
		if ( !string.IsNullOrWhiteSpace( primary.Detail ) )
			return primary.Detail;
		return fallback;
	}

	private static string BuildBlenderEnvironmentDetail( RetargetEnvironmentReport report )
	{
		var issue = report.Checks.FirstOrDefault( check => check.Id is "blender" or "blender_version" && check.Severity != RetargetEnvironmentCheckSeverity.Ok );
		if ( issue is not null )
			return !string.IsNullOrWhiteSpace( issue.FixHint ) ? issue.FixHint : issue.Detail;

		var parts = new List<string>();
		if ( !string.IsNullOrWhiteSpace( report.BlenderVersion ) )
			parts.Add( $"Blender {report.BlenderVersion}" );
		if ( !string.IsNullOrWhiteSpace( report.BlenderExecutablePath ) )
			parts.Add( report.BlenderExecutablePath );
		return parts.Count > 0 ? string.Join( " | ", parts ) : "Blender executable was found.";
	}

	private static string BuildEnvironmentDetailsLabel( RetargetEnvironmentReport report )
	{
		var lines = new List<string>();
		foreach ( var check in report.Checks )
		{
			var status = check.Severity switch
			{
				RetargetEnvironmentCheckSeverity.Ok => "OK",
				RetargetEnvironmentCheckSeverity.Warning => "WARN",
				_ => "ERROR"
			};
			var blocking = check.BlocksRetarget && check.Severity == RetargetEnvironmentCheckSeverity.Error
				? " | blocks retarget"
				: string.Empty;
			lines.Add( $"{status} - {check.Title}{blocking}" );
			if ( !string.IsNullOrWhiteSpace( check.Detail ) )
				lines.Add( $"  {check.Detail}" );
			if ( !string.IsNullOrWhiteSpace( check.FixHint ) )
				lines.Add( $"  Fix: {check.FixHint}" );
			if ( !string.IsNullOrWhiteSpace( check.Path ) )
				lines.Add( $"  Path: {check.Path}" );
			lines.Add( string.Empty );
		}

		return string.Join( Environment.NewLine, lines ).TrimEnd();
	}

	private string BuildSupportDiagnosticsText()
	{
		var lines = new List<string>
		{
			"CARL Diagnostics",
			$"Plugin version: {CitizenRetargetPluginInfo.Version}",
			$"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
			$"Project: {CitizenRetargetPaths.ProjectRoot}",
			$"Plugin root: {CitizenRetargetPaths.PluginRoot}",
			$"Settings file: {RetargetToolSettings.SettingsAbsolutePath}",
			string.Empty,
			"Setup",
			$"Backend override setting: {_toolSettings.BackendRootPath}",
			$"Blender path setting: {_toolSettings.BlenderExecutablePath}",
			$"Rokoko addon path setting: {_toolSettings.RokokoAddonPath}",
			$"CITIZEN_RETARGET_BLENDER: {Environment.GetEnvironmentVariable( "CITIZEN_RETARGET_BLENDER" ) ?? string.Empty}",
			$"CITIZEN_RETARGET_ROKOKO_ADDON_PATH: {Environment.GetEnvironmentVariable( "CITIZEN_RETARGET_ROKOKO_ADDON_PATH" ) ?? string.Empty}",
		};

		if ( _environmentReport is null )
		{
			lines.Add( "Environment report: not scanned yet" );
		}
		else
		{
			lines.Add( $"Environment report: {_environmentReport.BuildHeadline()}" );
			lines.Add( $"Resolved backend: {_environmentReport.BackendRootPath}" );
			lines.Add( $"Backend source: {_environmentReport.BackendSource}" );
			lines.Add( $"Resolved Blender: {_environmentReport.BlenderExecutablePath}" );
			lines.Add( $"Blender version: {_environmentReport.BlenderVersion}" );
			foreach ( var check in _environmentReport.Checks )
			{
				lines.Add( $"- {check.Severity}: {check.Title}" );
				if ( !string.IsNullOrWhiteSpace( check.Detail ) )
					lines.Add( $"  detail: {check.Detail}" );
				if ( !string.IsNullOrWhiteSpace( check.FixHint ) )
					lines.Add( $"  fix: {check.FixHint}" );
				if ( !string.IsNullOrWhiteSpace( check.Path ) )
					lines.Add( $"  path: {check.Path}" );
			}
		}

		var latestEntry = (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>())
			.OrderByDescending( entry => entry.CreatedUtc ?? string.Empty )
			.FirstOrDefault();
		lines.Add( string.Empty );
		lines.Add( "Latest Run" );
		if ( latestEntry is null )
		{
			lines.Add( "No run history recorded." );
		}
		else
		{
			lines.Add( $"Run ID: {latestEntry.RunId}" );
			lines.Add( $"Clip: {latestEntry.ClipName}" );
			lines.Add( $"Sequence: {latestEntry.SequenceName}" );
			lines.Add( $"Status: {latestEntry.Status}" );
			lines.Add( $"Created: {latestEntry.CreatedUtc}" );
			lines.Add( $"Manifest: {CitizenRetargetPaths.DecodeExternalPath( latestEntry.ManifestPath )}" );
			lines.Add( $"Export: {CitizenRetargetPaths.DecodeExternalPath( latestEntry.ExportPath )}" );
			lines.Add( $"Imported: {CitizenRetargetPaths.DecodeExternalPath( latestEntry.ImportedAssetPath )}" );
			lines.Add( $"Target VMDL: {latestEntry.TargetVmdlPath}" );
		}

		lines.Add( string.Empty );
		lines.Add( "Latest Scan" );
		lines.Add( $"Status: {_lastScanStatus}" );
		lines.Add( $"Source: {_lastScanSourcePath}" );
		lines.Add( $"Completed at: {_lastScanCompletedAt?.ToString( "yyyy-MM-dd HH:mm:ss" ) ?? string.Empty}" );
		if ( !string.IsNullOrWhiteSpace( _lastScanDetails ) )
			lines.Add( _lastScanDetails );

		lines.Add( string.Empty );
		lines.Add( "Current Job" );
		lines.Add( $"Source FBX: {CitizenRetargetPaths.DecodeExternalPath( _job.SourceFbxPath ?? string.Empty )}" );
		lines.Add( $"Source profile: {_job.SourceProfilePath}" );
		lines.Add( $"Mapping profile: {_job.MappingProfilePath}" );
		lines.Add( $"Target VMDL: {_job.TargetVmdlPath}" );
		lines.Add( $"Animation folder: {_job.OutputAnimationFolder}" );
		lines.Add( $"Sequence prefix: {_job.SequencePrefix}" );

		return string.Join( Environment.NewLine, lines );
	}

	private BackgroundJobState StartBackgroundJob(
		string title,
		string scope,
		bool blocksInteraction,
		bool showInJobsStrip,
		Func<BackgroundJobProgressReporter, Action?> work,
		Action<Exception>? onError = null )
	{
		var job = new BackgroundJobState
		{
			Scope = scope,
			Title = title,
			Detail = title,
			BlocksInteraction = blocksInteraction,
			ShowInJobsStrip = showInJobsStrip
		};
		_backgroundJobs.Add( job );
		RefreshBackgroundJobUi();
		UpdateButtons();

		job.Task = Task.Run( () =>
		{
			try
			{
				job.ApplySuccess = work( new BackgroundJobProgressReporter( job ) );
			}
			catch ( Exception exception )
			{
				job.Error = exception;
			}
		} );

		job.ApplyError = onError;
		return job;
	}

	private bool HasActiveBackgroundJob( string scope )
	{
		return GetActiveBackgroundJob( scope ) is not null;
	}

	private bool HasBlockingBackgroundJob( string scope )
	{
		return GetBlockingBackgroundJob( scope ) is not null;
	}

	private BackgroundJobState? GetActiveBackgroundJob( string scope )
	{
		return _backgroundJobs.LastOrDefault( job =>
			job.Scope.Equals( scope, StringComparison.OrdinalIgnoreCase )
			&& !job.CompletedNotified );
	}

	private BackgroundJobState? GetBlockingBackgroundJob( string scope )
	{
		return _backgroundJobs.LastOrDefault( job =>
			job.Scope.Equals( scope, StringComparison.OrdinalIgnoreCase )
			&& job.BlocksInteraction
			&& !job.CompletedNotified );
	}

	private string BuildActiveJobStatus( string scope, string fallback )
	{
		var job = GetActiveBackgroundJob( scope );
		if ( job is null )
			return fallback;

		var detail = string.IsNullOrWhiteSpace( job.Detail ) ? job.Title : job.Detail;
		return string.IsNullOrWhiteSpace( detail ) ? fallback : $"{fallback} Current step: {detail}";
	}

	private string BuildSpinnerGlyph()
	{
		var frames = new[] { "◐", "◓", "◑", "◒" };
		return frames[_jobsSpinnerFrame % frames.Length];
	}

	private void RefreshBackgroundJobUi()
	{
		if ( _jobsStripHost is null || _jobsStripLabel is null || _jobsStripProgressLabel is null || _jobsStripProgressBar is null )
			return;

		var visibleJobs = _backgroundJobs.Where( job => job.ShowInJobsStrip ).ToList();
		if ( visibleJobs.Count == 0 )
		{
			_jobsStripHost.Hide();
			if ( _sourceBusyLabel is not null )
				_sourceBusyLabel.Text = string.Empty;
			if ( _targetBusyLabel is not null )
			{
				_targetBusyLabel.Text = string.Empty;
				_targetBusyLabel.Hide();
			}
			if ( _queueBusyLabel is not null )
				_queueBusyLabel.Text = string.Empty;
			_jobsStripProgressBar.IsIndeterminate = false;
			_jobsStripProgressBar.Progress = 0f;
			return;
		}

		var activeJob = visibleJobs[^1];
		var spinner = BuildSpinnerGlyph();
		_jobsStripLabel.Text = $"{spinner} {activeJob.Title}" + (string.IsNullOrWhiteSpace( activeJob.Detail ) || activeJob.Detail == activeJob.Title ? string.Empty : $" | {activeJob.Detail}");
		_jobsStripProgressBar.IsIndeterminate = activeJob.IsIndeterminate;
		_jobsStripProgressBar.Progress = activeJob.Progress;
		_jobsStripProgressLabel.Text = activeJob.IsIndeterminate
			? $"{visibleJobs.Count} background job(s) active"
			: $"{FormatProgressText( activeJob.Progress )} complete";
		_jobsStripHost.Show();

		if ( _sourceBusyLabel is not null )
			_sourceBusyLabel.Text = GetBusyLabelForScope( "source" );
		if ( _targetBusyLabel is not null )
		{
			_targetBusyLabel.Text = GetBusyLabelForScope( "target" );
			if ( string.IsNullOrWhiteSpace( _targetBusyLabel.Text ) )
				_targetBusyLabel.Hide();
			else
				_targetBusyLabel.Show();
		}
		if ( _queueBusyLabel is not null )
			_queueBusyLabel.Text = GetBusyLabelForScope( "queue" );
	}

	private string GetBusyLabelForScope( string scope )
	{
		var job = GetActiveBackgroundJob( scope );
		if ( job is null )
			return string.Empty;

		return $"{BuildSpinnerGlyph()} {job.Title}";
	}

	private static string FormatProgressText( float progress )
	{
		var percent = (int)MathF.Round( Math.Clamp( progress, 0f, 1f ) * 100f );
		return $"{percent}%";
	}

	private void ApplyJobToControls()
	{
		RefreshSourceLibraryFromJob();
		_sourcePath.Text = _job.SourceFbxPath ?? string.Empty;
		SetSourcePresetControlsFromJob();
		_mappingProfilePath.Text = _job.MappingProfilePath ?? CitizenTargetProfile.DefaultMappingProfileAssetPath;
		_targetVmdlPath.Text = _job.TargetVmdlPath ?? CitizenTargetProfile.DefaultTargetVmdlPath;
		_targetAnimGraphPath.Text = _job.TargetAnimGraphPath ?? string.Empty;
		_targetPosePresetId.Text = _job.TargetPosePresetId ?? CitizenTargetProfile.DefaultTargetPosePresetId;
		_outputFolder.Text = _job.OutputAnimationFolder ?? CitizenTargetProfile.DefaultOutputAnimationFolder;
		_sequencePrefix.Text = _job.SequencePrefix ?? CitizenTargetProfile.DefaultSequencePrefix;
		if ( _newTargetName is not null )
			_newTargetName.Text = DeriveTargetKeyFromJob();
		_rootMotionMode.CurrentIndex = _job.RootMotionMode switch
		{
			CitizenRetargetRootMotionMode.InPlace => 1,
			CitizenRetargetRootMotionMode.FullyInPlace => 2,
			_ => 0
		};
		_importHands.State = _job.ImportHands ? CheckState.On : CheckState.Off;
		UpdateNewTargetPreview();
		UpdatePoseCompensationSummary();
		SetTargetWorkspaceFlowState();
		_sourceFacingEulerDegrees = GetStoredSourceFacingEulerDegrees();
		RefreshSourceFacingUi();
	}

	private void SyncJobFromControls()
	{
		var previousLibraryId = _activeSourceLibrary.LibraryId;
		_job.SourceFbxPath = _sourcePath.Text?.Trim() ?? string.Empty;
		_job.SourceProfilePath = GetSourceProfilePathFromPresetIndex( GetSelectedSourcePresetIndex() );
		_job.MappingProfilePath = _mappingProfilePath.Text?.Trim() ?? CitizenTargetProfile.DefaultMappingProfileAssetPath;
		_job.TargetVmdlPath = _targetVmdlPath.Text?.Trim() ?? CitizenTargetProfile.DefaultTargetVmdlPath;
		_job.TargetAnimGraphPath = _targetAnimGraphPath.Text?.Trim() ?? string.Empty;
		_job.TargetPosePresetId = _targetPosePresetId.Text?.Trim() ?? CitizenTargetProfile.DefaultTargetPosePresetId;
		_job.OutputAnimationFolder = _outputFolder.Text?.Trim() ?? CitizenTargetProfile.DefaultOutputAnimationFolder;
		_job.SequencePrefix = _sequencePrefix.Text?.Trim() ?? CitizenTargetProfile.DefaultSequencePrefix;
		_job.RootMotionMode = _rootMotionMode.CurrentIndex switch
		{
			1 => CitizenRetargetRootMotionMode.InPlace,
			2 => CitizenRetargetRootMotionMode.FullyInPlace,
			_ => CitizenRetargetRootMotionMode.Keep
		};
		_job.ImportHands = _importHands.State == CheckState.On;
		RefreshSourceLibraryFromJob();
		if ( !string.Equals( previousLibraryId, _activeSourceLibrary.LibraryId, StringComparison.OrdinalIgnoreCase ) )
		{
			InvalidateCompareSelectionForSourceChange();
			_inspection = null;
			_allClips.Clear();
			_mappingState.Clear();
			_selectedSourceBone = null;
			_selectedSlot = null;
			_job.SelectedClipNames = new List<string>();
			if ( _clipFilter is not null )
				_clipFilter.Text = string.Empty;
			_clipList?.UnselectAll();
			_sourceFacingEulerDegrees = GetStoredSourceFacingEulerDegrees();
			RefreshClipList();
			RefreshSourceBoneList();
			RefreshMappingList();
		}
		_pipeline.SaveJob( _job );
		UpdatePoseCompensationSummary();
		RefreshSourceFacingUi();
	}

	private void ApplySettingsToControls()
	{
		if ( _backendPath is not null )
			_backendPath.Text = _toolSettings.BackendRootPath ?? string.Empty;
		if ( _blenderPath is not null )
			_blenderPath.Text = _toolSettings.BlenderExecutablePath ?? string.Empty;
		if ( _rokokoAddonPath is not null )
			_rokokoAddonPath.Text = _toolSettings.RokokoAddonPath ?? string.Empty;
	}

	private void SyncToolSettingsFromControls()
	{
		var backendRootPath = _backendPath?.Text?.Trim() ?? string.Empty;
		var blenderExecutablePath = _blenderPath?.Text?.Trim() ?? string.Empty;
		var rokokoAddonPath = _rokokoAddonPath?.Text?.Trim() ?? string.Empty;
		var changed = !string.Equals( _toolSettings.BackendRootPath ?? string.Empty, backendRootPath, StringComparison.Ordinal )
			|| !string.Equals( _toolSettings.BlenderExecutablePath ?? string.Empty, blenderExecutablePath, StringComparison.Ordinal )
			|| !string.Equals( _toolSettings.RokokoAddonPath ?? string.Empty, rokokoAddonPath, StringComparison.Ordinal );

		_toolSettings.BackendRootPath = backendRootPath;
		_toolSettings.BlenderExecutablePath = blenderExecutablePath;
		_toolSettings.RokokoAddonPath = rokokoAddonPath;
		_toolSettings.Save();
		if ( changed )
		{
			_environmentReport = null;
			_latestSetupFailureMessage = string.Empty;
			ClearPendingSetupMessages();
		}
		RefreshEnvironmentDiagnostics();
		RefreshQueueList();
		UpdateButtons();
	}

	private Vector3 GetStoredSourceFacingEulerDegrees()
	{
		return _sourceFacingEulerOverrides.TryGetValue( _activeSourceLibrary.LibraryId, out var storedEuler )
			? storedEuler
			: Vector3.Zero;
	}

	private void SetSourceFacingEulerDegrees( Vector3 eulerDegrees )
	{
		_sourceFacingEulerDegrees = NormalizeFacingEulerDegrees( eulerDegrees );
		if ( string.IsNullOrWhiteSpace( _activeSourceLibrary.LibraryId ) )
			return;

		if ( MathF.Abs( _sourceFacingEulerDegrees.x ) <= 0.001f
			&& MathF.Abs( _sourceFacingEulerDegrees.y ) <= 0.001f
			&& MathF.Abs( _sourceFacingEulerDegrees.z ) <= 0.001f )
		{
			_sourceFacingEulerOverrides.Remove( _activeSourceLibrary.LibraryId );
		}
		else
		{
			_sourceFacingEulerOverrides[_activeSourceLibrary.LibraryId] = _sourceFacingEulerDegrees;
		}

		RefreshSourceFacingUi();
		RefreshSourceFacingFields();
		UpdateButtons();
	}

	private void ResetSourceFacingCorrection()
	{
		if ( _inspection is null || _inspection.Audit.Bones.Count == 0 )
			return;

		SetSourceFacingEulerDegrees( Vector3.Zero );
		SetStatus( "Source orientation reset to the selected preset default." );
	}

	private void RotateSourceFacingBy( Vector3 deltaDegrees, string axisLabel )
	{
		SetSourceFacingEulerDegrees( _sourceFacingEulerDegrees + deltaDegrees );
		SetStatus( IsZeroSourceFacingEuler( _sourceFacingEulerDegrees )
			? "Source orientation uses the detected default for this source."
			: $"Source {axisLabel} adjustment saved: {FormatSourceFacingEuler( _sourceFacingEulerDegrees )}." );
	}

	private static float NormalizeFacingDegrees( float degrees )
	{
		var normalized = degrees % 360f;
		if ( normalized > 180f )
			normalized -= 360f;
		if ( normalized <= -180f )
			normalized += 360f;
		return normalized;
	}

	private static Vector3 NormalizeFacingEulerDegrees( Vector3 eulerDegrees )
	{
		return new Vector3(
			NormalizeFacingDegrees( eulerDegrees.x ),
			NormalizeFacingDegrees( eulerDegrees.y ),
			NormalizeFacingDegrees( eulerDegrees.z ) );
	}

	private static bool IsZeroSourceFacingEuler( Vector3 eulerDegrees )
	{
		return MathF.Abs( eulerDegrees.x ) <= 0.001f
			&& MathF.Abs( eulerDegrees.y ) <= 0.001f
			&& MathF.Abs( eulerDegrees.z ) <= 0.001f;
	}

	private static string FormatSourceFacingEuler( Vector3 eulerDegrees )
	{
		return $"X {eulerDegrees.x:+0;-0;0} | Y {eulerDegrees.y:+0;-0;0} | Z {eulerDegrees.z:+0;-0;0}";
	}

	private static string FormatSourceFacingFieldValue( float value )
	{
		return NormalizeFacingDegrees( value ).ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture );
	}

	private void RefreshSourceFacingFields()
	{
		if ( _sourceFacingTiltField is null || _sourceFacingRollField is null || _sourceFacingFacingField is null )
			return;

		_suppressSourceFacingFieldSync = true;
		try
		{
			_sourceFacingTiltField.Text = FormatSourceFacingFieldValue( _sourceFacingEulerDegrees.x );
			_sourceFacingRollField.Text = FormatSourceFacingFieldValue( _sourceFacingEulerDegrees.y );
			_sourceFacingFacingField.Text = FormatSourceFacingFieldValue( _sourceFacingEulerDegrees.z );
		}
		finally
		{
			_suppressSourceFacingFieldSync = false;
		}
	}

	private Vector3 GetSourceFacingBaseEulerDegrees()
	{
		if ( TryReadSourceProfileFacingPreset( _job.SourceProfilePath, out var presetEuler ) )
			return NormalizeFacingEulerDegrees( presetEuler );

		var backendSourceProfileId = _sourceProfile?.BackendSourceProfileId?.Trim() ?? string.Empty;
		return backendSourceProfileId.Equals( "mixamo_humanoid", StringComparison.OrdinalIgnoreCase )
			? new Vector3( 0f, 0f, 180f )
			: Vector3.Zero;
	}

	private bool IsSourceFacingHandledByProfile()
	{
		var backendSourceProfileId = _sourceProfile?.BackendSourceProfileId?.Trim() ?? string.Empty;
		return backendSourceProfileId.Equals( "quaternius_ual2", StringComparison.OrdinalIgnoreCase );
	}

	private bool IsSourceFacingManualOverrideEnabled()
	{
		return !string.IsNullOrWhiteSpace( _activeSourceLibrary.LibraryId )
			&& _sourceFacingManualOverrideEnabled.Contains( _activeSourceLibrary.LibraryId );
	}

	private Vector3 GetAppliedSourceFacingEulerDegrees()
	{
		var baseEuler = GetSourceFacingBaseEulerDegrees();
		if ( !IsSourceFacingManualOverrideEnabled() )
			return NormalizeFacingEulerDegrees( baseEuler );

		if ( IsSourceFacingHandledByProfile() )
			return _sourceFacingEulerDegrees;

		return NormalizeFacingEulerDegrees( baseEuler + _sourceFacingEulerDegrees );
	}

	private static bool TryReadSourceProfileFacingPreset( string? sourceProfilePath, out Vector3 presetEuler )
	{
		presetEuler = Vector3.Zero;
		if ( string.IsNullOrWhiteSpace( sourceProfilePath ) )
			return false;

		try
		{
			var normalized = sourceProfilePath.Replace( '\\', '/' ).Trim().TrimStart( '/' );
			if ( normalized.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
				normalized = normalized["Assets/".Length..];

			var absolutePath = CitizenRetargetPaths.GetAssetAbsolutePath( normalized );
			if ( !File.Exists( absolutePath ) )
				return false;

			using var document = JsonDocument.Parse( File.ReadAllText( absolutePath ) );
			if ( !document.RootElement.TryGetProperty( "DefaultSourceFacingEulerDegrees", out var element ) || element.ValueKind != JsonValueKind.Array )
				return false;

			var values = element.EnumerateArray()
				.Take( 3 )
				.Select( value => value.TryGetSingle( out var number ) ? number : 0f )
				.ToArray();
			if ( values.Length < 3 )
				return false;

			presetEuler = new Vector3( values[0], values[1], values[2] );
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsSameSourceFacingEuler( Vector3 left, Vector3 right )
	{
		var delta = NormalizeFacingEulerDegrees( left - right );
		return IsZeroSourceFacingEuler( delta );
	}

	private void ClearLegacyManualFacingIfItMatchesPreset()
	{
		var presetEuler = GetSourceFacingBaseEulerDegrees();
		if ( IsZeroSourceFacingEuler( presetEuler ) || string.IsNullOrWhiteSpace( _activeSourceLibrary.LibraryId ) )
			return;

		if ( !_sourceFacingEulerOverrides.TryGetValue( _activeSourceLibrary.LibraryId, out var storedEuler ) )
			return;

		if ( !IsSameSourceFacingEuler( storedEuler, presetEuler ) )
			return;

		_sourceFacingEulerOverrides.Remove( _activeSourceLibrary.LibraryId );
		_sourceFacingEulerDegrees = Vector3.Zero;
	}

	private void SetSourceFacingManualOverrideEnabled( bool enabled )
	{
		if ( string.IsNullOrWhiteSpace( _activeSourceLibrary.LibraryId ) )
			return;

		if ( enabled )
		{
			_sourceFacingManualOverrideEnabled.Add( _activeSourceLibrary.LibraryId );
			SetStatus( "Manual orientation override enabled for this source." );
		}
		else
		{
			_sourceFacingManualOverrideEnabled.Remove( _activeSourceLibrary.LibraryId );
			SetStatus( "Manual orientation override disabled. Preset/default orientation will be used." );
		}

		RefreshSourceFacingUi();
		UpdateButtons();
	}

	private Vector3 GetEffectiveSourceFacingPreviewEulerDegrees()
	{
		// Preview must mirror what the backend receives. Profile defaults are backend-side hints,
		// while manual override replaces them instead of stacking on top.
		return GetAppliedSourceFacingEulerDegrees();
	}

	private void RefreshSourceFacingUi()
	{
		if ( _sourceFacingCard is null || _sourceFacingPreview is null || _sourceFacingSummaryLabel is null || _sourceFacingHintLabel is null )
			return;

		var hasInspection = _inspection is not null && _inspection.Audit.Bones.Count > 0;
		if ( hasInspection )
		{
			var inspection = _inspection;
			if ( inspection is null )
			{
				_sourceFacingCard.Hide();
				_sourceFacingPreview.UpdatePreview( null, Vector3.Zero, "Scan a source FBX to inspect its facing." );
				RefreshSourceFacingManualOverrideCheckbox();
				return;
			}

			_sourceFacingCard.Show();
			var effectiveEuler = GetEffectiveSourceFacingPreviewEulerDegrees();
			var baseEuler = NormalizeFacingEulerDegrees( GetSourceFacingBaseEulerDegrees() );
			var profileHandled = IsSourceFacingHandledByProfile();
			var manualOverrideEnabled = IsSourceFacingManualOverrideEnabled();
			var manualCorrectionText = IsZeroSourceFacingEuler( _sourceFacingEulerDegrees )
				? "none"
				: FormatSourceFacingEuler( _sourceFacingEulerDegrees );
			var detectedText = IsZeroSourceFacingEuler( baseEuler )
				? "Detected default facing: no built-in correction."
				: $"Detected default orientation: {FormatSourceFacingEuler( baseEuler )}.";
			_sourceFacingSummaryLabel.Text = profileHandled
				? $"{detectedText} This profile already handles facing automatically. Manual override: {(manualOverrideEnabled ? manualCorrectionText : "off")}."
				: $"{detectedText} Manual override: {(manualOverrideEnabled ? manualCorrectionText : "off")}.";
			_sourceFacingHintLabel.Text = manualOverrideEnabled
				? "Edit X/Y/Z until the yellow source arrow points the same way as the green Citizen front arrow."
				: "Preset/default orientation is active. Enable manual override only if the yellow source arrow points the wrong way.";
			_sourceFacingPreview.UpdatePreview(
				inspection.Audit.Bones,
				effectiveEuler,
				profileHandled && !manualOverrideEnabled
					? "Green arrow = Citizen front. This preset handles source facing automatically."
					: manualOverrideEnabled
						? $"Manual override: {manualCorrectionText}. Yellow arrow should match the green Citizen front arrow."
						: "Preset/default orientation is active. Enable manual override if the yellow arrow needs correction." );
			RefreshSourceFacingManualOverrideCheckbox();
			RefreshSourceFacingFields();
			return;
		}

		_sourceFacingCard.Hide();
		_sourceFacingPreview.UpdatePreview( null, Vector3.Zero, "Scan a source FBX to inspect its facing." );
		RefreshSourceFacingManualOverrideCheckbox();
		RefreshSourceFacingFields();
	}

	private void RefreshSourceFacingManualOverrideCheckbox()
	{
		if ( _sourceFacingManualOverrideCheckbox is null )
			return;

		_suppressSourceFacingManualOverrideSync = true;
		try
		{
			_sourceFacingManualOverrideCheckbox.State = IsSourceFacingManualOverrideEnabled()
				? CheckState.On
				: CheckState.Off;
		}
		finally
		{
			_suppressSourceFacingManualOverrideSync = false;
		}
	}

	private void ToggleAdvancedSettings()
	{
		SetAdvancedSettingsVisible( !_advancedSettingsVisible );
	}

	private void BrowseSourceFbx()
	{
		try
		{
			var currentPath = CitizenRetargetPaths.DecodeExternalPath( _job.SourceFbxPath );
			var selectedPath = RetargetFileDialogs.PickExistingFile(
				"Choose Source FBX",
				"fbx",
				currentPath,
				Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments ) );
			if ( string.IsNullOrWhiteSpace( selectedPath ) )
			{
				SetStatus( "Source FBX selection cancelled." );
				return;
			}

			if ( _sourcePath is not null )
				_sourcePath.Text = selectedPath;
			SyncJobFromControls();
			SetStatus( $"Source FBX selected: {selectedPath}. Press Scan to inspect it." );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Browse failed: {exception.Message}" );
		}
	}

	private void BeginOpenExistingTargetFlow()
	{
		LoadTargetPresetInventory();
		_showOpenExistingTargetFlow = true;
		_showCreateTargetFlow = false;
		SetTargetWorkspaceFlowState();
	}

	private void BeginCreateTargetFlow()
	{
		_showOpenExistingTargetFlow = false;
		_showCreateTargetFlow = true;
		if ( _newTargetName is not null && string.IsNullOrWhiteSpace( _newTargetName.Text ) )
			_newTargetName.Text = DeriveTargetKeyFromJob();
		UpdateNewTargetPreview();
		SetTargetWorkspaceFlowState();
	}

	private void ResetTargetWorkspaceFlow()
	{
		_showOpenExistingTargetFlow = false;
		_showCreateTargetFlow = false;
		ClearTargetPresetInventory();
		SetTargetWorkspaceFlowState();
	}

	private void SetTargetWorkspaceFlowState()
	{
		if ( _targetWorkspaceIntroHost is not null )
		{
			if ( !_showOpenExistingTargetFlow && !_showCreateTargetFlow )
				_targetWorkspaceIntroHost.Show();
			else
				_targetWorkspaceIntroHost.Hide();
		}

		if ( _targetOpenExistingHost is not null )
		{
			if ( _showOpenExistingTargetFlow )
				_targetOpenExistingHost.Show();
			else
				_targetOpenExistingHost.Hide();
		}

		if ( _targetCreateHost is not null )
		{
			if ( _showCreateTargetFlow )
				_targetCreateHost.Show();
			else
				_targetCreateHost.Hide();
		}

		if ( _currentTargetWorkspaceLabel is not null )
		{
			_currentTargetWorkspaceLabel.Text = _showCreateTargetFlow
				? "Create a new Citizen target. The plugin will generate a new VMDL and animation folder for it."
				: _showOpenExistingTargetFlow
					? "Pick an existing target to make it the only active target in this session."
					: $"Current target: {_job.TargetVmdlPath}. Home works with this target only.";
		}
	}

	private void UpdateNewTargetPreview()
	{
		var key = SanitizeTargetKey( _newTargetName?.Text );
		var preset = BuildTargetPresetFromKey( key, $"models/citizen_custom/citizen_{key}.vmdl", false );
		if ( _newTargetVmdlPreviewLabel is not null )
			_newTargetVmdlPreviewLabel.Text = $"VMDL: {preset.TargetVmdlPath}";
		if ( _newTargetFolderPreviewLabel is not null )
			_newTargetFolderPreviewLabel.Text = $"Animation Folder: {preset.OutputAnimationFolder}";
		if ( _newTargetPrefixPreviewLabel is not null )
			_newTargetPrefixPreviewLabel.Text = $"Sequence Prefix: {preset.SequencePrefix}";
		UpdateButtons();
	}

	private void OpenSelectedTargetPreset()
	{
		var preset = _targetPresetList?.SelectedItems.OfType<RetargetTargetPresetRef>().FirstOrDefault();
		if ( preset is null )
		{
			SetStatus( "Select a target preset before opening it." );
			return;
		}

		ApplyTargetPreset( preset, ensureTargetExists: false );
		ResetTargetWorkspaceFlow();
		SetStatus( $"Opened target '{preset.DisplayName}'." );
	}

	private void CreateNewTargetPreset()
	{
		var requestedKey = _newTargetName?.Text;
		var targetKey = SanitizeTargetKey( string.IsNullOrWhiteSpace( requestedKey ) ? _activeSourceLibrary.DisplayName : requestedKey );
		var existingPresets = DiscoverTargetPresets();
		var uniqueKey = targetKey;
		var suffix = 2;
		while ( existingPresets.Any( preset => preset.Key.Equals( uniqueKey, StringComparison.OrdinalIgnoreCase ) ) )
		{
			uniqueKey = $"{targetKey}_{suffix}";
			suffix++;
		}

		var preset = BuildTargetPresetFromKey( uniqueKey, $"models/citizen_custom/citizen_{uniqueKey}.vmdl", false );
		ApplyTargetPreset( preset, ensureTargetExists: true );
		if ( _newTargetName is not null )
			_newTargetName.Text = uniqueKey;
		UpdateNewTargetPreview();
		ResetTargetWorkspaceFlow();
		SetStatus( $"Created new target '{uniqueKey}'." );
	}

	private void ApplyTargetPreset( RetargetTargetPresetRef preset, bool ensureTargetExists )
	{
		_job.TargetVmdlPath = preset.TargetVmdlPath;
		_job.OutputAnimationFolder = preset.OutputAnimationFolder;
		_job.SequencePrefix = preset.SequencePrefix;
		_targetVmdlPath.Text = _job.TargetVmdlPath;
		_targetAnimGraphPath.Text = _job.TargetAnimGraphPath ?? string.Empty;
		_outputFolder.Text = _job.OutputAnimationFolder;
		_sequencePrefix.Text = _job.SequencePrefix;
		if ( _newTargetName is not null )
			_newTargetName.Text = preset.Key;
		_selectedTargetAnimation = null;
		_builtInTargetAnimationsLoaded = false;
		_builtInTargetAnimationsLoadRequested = false;
		SetBuiltInTargetAnimationVisibility( false );
		_session.CompareSelection.SelectedResultRunId = string.Empty;
		_selectedCompareResult = null;
		_pipeline.SaveJob( _job );
		if ( ensureTargetExists )
			_pipeline.EnsureTargetAssetExists( _job );
		ClearTargetPresetInventory();
		RefreshResultList();
		RefreshResultSurfaces();
		UpdateButtons();
		BeginRefreshTargetAnimationListAsync( ensureTargetExists
			? $"Created target '{preset.DisplayName}' and loaded its animation list."
			: $"Opened target '{preset.DisplayName}'." );
	}

	private void ToggleBuiltInTargetAnimations()
	{
		if ( _showBuiltInTargetAnimations )
		{
			SetBuiltInTargetAnimationVisibility( false );
			return;
		}

		if ( !_builtInTargetAnimationsLoaded )
		{
			_builtInTargetAnimationsLoadRequested = true;
			if ( _toggleBuiltInTargetAnimationsButton is not null )
			{
				_toggleBuiltInTargetAnimationsButton.Enabled = false;
				_toggleBuiltInTargetAnimationsButton.Text = "Loading Built-In...";
			}

			BeginRefreshTargetAnimationListAsync( "Loaded built-in target animations." );
			return;
		}

		SetBuiltInTargetAnimationVisibility( true );
	}

	private void SetBuiltInTargetAnimationVisibility( bool visible )
	{
		_showBuiltInTargetAnimations = visible;
		if ( _builtInTargetSectionHost is not null )
		{
			if ( visible )
				_builtInTargetSectionHost.Show();
			else
				_builtInTargetSectionHost.Hide();
		}

		if ( _toggleBuiltInTargetAnimationsButton is not null )
		{
			_toggleBuiltInTargetAnimationsButton.Enabled = true;
			_toggleBuiltInTargetAnimationsButton.Text = visible ? "Hide Built-In" : "Show Built-In";
		}
	}

	private void SetAdvancedSettingsVisible( bool visible )
	{
		_advancedSettingsVisible = visible;
		if ( _advancedSettingsHost is not null )
		{
			if ( visible )
				_advancedSettingsHost.Show();
			else
				_advancedSettingsHost.Hide();
		}

		if ( _toggleAdvancedButton is not null )
			_toggleAdvancedButton.Text = visible ? "Hide Advanced" : "Show Advanced";
	}

	private static RetargetSessionState CreateSessionStateFromJob( CitizenRetargetJob job )
	{
		var sourceLibrary = RetargetSourceLibraryResolver.FromJob( job );
		var selectedSourceClipName = job.SelectedClipNames?.FirstOrDefault() ?? string.Empty;
		var selectedResultRunId = string.IsNullOrWhiteSpace( job.LastSuccessfulRunId )
			? string.Empty
			: job.LastSuccessfulRunId;

		return new RetargetSessionState
		{
			CompareSelection = new RetargetCompareSelection
			{
				SelectedSourceLibraryId = selectedSourceClipName.Length > 0 ? sourceLibrary.LibraryId : string.Empty,
				SelectedSourceClipName = selectedSourceClipName,
				SelectedResultRunId = selectedResultRunId
			},
			SelectedHistoryRunId = string.Empty,
			HomeResultFilter = "All"
		};
	}

	private void RefreshSourceLibraryFromJob()
	{
		_activeSourceLibrary = RetargetSourceLibraryResolver.FromJob( _job );
	}

	private void InvalidateCompareSelectionForSourceChange()
	{
		_session.CompareSelection.SelectedSourceLibraryId = _activeSourceLibrary.LibraryId;
		_session.CompareSelection.SelectedSourceClipName = string.Empty;
		_session.CompareSelection.SelectedResultRunId = string.Empty;
		_selectedCompareResult = null;
		RefreshResultList();
		RefreshResultSurfaces();
	}

	private void SyncSelectedClipsToJob()
	{
		_job.SelectedClipNames = _clipList.SelectedItems.OfType<RetargetClipDescriptor>().Select( clip => clip.DisplayName ).ToList();
		_pipeline.SaveJob( _job );
	}

	private void OnClipSelectionChanged()
	{
		SyncSelectedClipsToJob();
		SyncCompareSelectionFromCurrentClip();
		ResolveCompareResultSelection();
		RefreshClipSummary();
		RefreshResultList();
		RefreshResultSurfaces();
		UpdateButtons();
	}

	private void BeginScanAndInspectAsync()
	{
		if ( HasActiveBackgroundJob( "source" ) )
		{
			SetStatus( BuildActiveJobStatus( "source", "Source scan is already running." ) );
			return;
		}

		if ( _jobInFlight )
		{
			SetStatus( "Wait for the current retarget job to finish before scanning a new source." );
			return;
		}

		CitizenRetargetJob jobSnapshot;
		RetargetSourceProfile sourceProfileSnapshot;
		RetargetMappingProfile mappingProfileSnapshot;
		Dictionary<string, RetargetSourceProfile> sourceProfilesByPath;
		string mappingProfilePath;
		try
		{
			SetStatus( "Preparing source scan..." );
			SyncJobFromControls();
			var resolvedSourcePath = _pipeline.ResolveSourceFbxPath( _job );
			_job.SourceFbxPath = resolvedSourcePath;
			if ( _sourcePath is not null )
				_sourcePath.Text = resolvedSourcePath;
			_pipeline.SaveJob( _job );
			RememberScanDiagnostic( "running", resolvedSourcePath, "Source scan has started." );
			SetStatus( $"Scanning '{Path.GetFileName( resolvedSourcePath )}'..." );
			LoadProfilesFromJob();
			RefreshSourceLibraryFromJob();
			jobSnapshot = _pipeline.CreateRuntimeJobSnapshot( _job );
			sourceProfileSnapshot = _pipeline.LoadSourceProfile( jobSnapshot.SourceProfilePath );
			sourceProfilesByPath = new Dictionary<string, RetargetSourceProfile>( StringComparer.OrdinalIgnoreCase )
			{
				[NormalizeProfilePathForUi( jobSnapshot.SourceProfilePath )] = sourceProfileSnapshot
			};
			CacheSourceProfileForScan( sourceProfilesByPath, CitizenTargetProfile.MixamoSourceProfileAssetPath );
			CacheSourceProfileForScan( sourceProfilesByPath, CitizenTargetProfile.DefaultSourceProfileAssetPath );
			mappingProfilePath = string.IsNullOrWhiteSpace( sourceProfileSnapshot.DefaultMappingProfilePath )
				? (jobSnapshot.MappingProfilePath ?? CitizenTargetProfile.DefaultMappingProfileAssetPath)
				: sourceProfileSnapshot.DefaultMappingProfilePath;
			mappingProfileSnapshot = _pipeline.LoadMappingProfile( mappingProfilePath );
		}
		catch ( Exception exception )
		{
			ReportScanFailure( "prepare", exception );
			UpdateButtons();
			RefreshBackgroundJobUi();
			return;
		}

		StartBackgroundJob(
			title: "Scanning source FBX",
			scope: "source",
			blocksInteraction: true,
			showInJobsStrip: true,
			work: reporter =>
			{
				reporter.ReportDetail( "Scanning clips..." );
				var clips = _pipeline.ScanClips( jobSnapshot ).ToList();
				reporter.ReportProgress( 0.35f, "Inspecting source skeleton..." );
				var inspection = _pipeline.InspectSourceSkeleton( jobSnapshot );
				var effectiveSourceProfilePath = jobSnapshot.SourceProfilePath;
				var effectiveSourceProfile = sourceProfileSnapshot;
				if ( string.IsNullOrWhiteSpace( effectiveSourceProfilePath ) )
				{
					var detectedSourceProfilePath = _pipeline.DetectSourceProfilePath( jobSnapshot, inspection, clips );
					if ( !string.IsNullOrWhiteSpace( detectedSourceProfilePath ) )
					{
						effectiveSourceProfilePath = detectedSourceProfilePath;
						var detectedProfileKey = NormalizeProfilePathForUi( effectiveSourceProfilePath );
						if ( !sourceProfilesByPath.TryGetValue( detectedProfileKey, out effectiveSourceProfile ) )
							throw new InvalidOperationException( $"Detected source profile '{effectiveSourceProfilePath}' was not preloaded before background scanning." );
					}
				}

				var effectiveMappingProfilePath = string.IsNullOrWhiteSpace( effectiveSourceProfile.DefaultMappingProfilePath )
					? mappingProfilePath
					: effectiveSourceProfile.DefaultMappingProfilePath;
				var effectiveMappingProfile = effectiveMappingProfilePath.Equals( mappingProfilePath, StringComparison.OrdinalIgnoreCase )
					? mappingProfileSnapshot
					: _pipeline.LoadMappingProfile( effectiveMappingProfilePath );
				reporter.ReportProgress( 0.75f, "Building mapping state..." );
				var mappingState = _pipeline.BuildMappingState( jobSnapshot, effectiveSourceProfile, effectiveMappingProfile, inspection );
				reporter.ReportProgress( 1f, "Applying scan results..." );
				var result = new ScanJobResult
				{
					Clips = clips,
					Inspection = inspection,
					MappingState = mappingState,
					DetectedSourceProfilePath = effectiveSourceProfilePath,
					DetectedSourceProfileDisplayName = effectiveSourceProfile.DisplayName,
					DetectedMappingProfilePath = effectiveMappingProfilePath
				};
				return () => ApplyScanJobResult( result );
			},
			onError: exception =>
			{
				ReportScanFailure( "background", exception );
				UpdateButtons();
				RefreshBackgroundJobUi();
			} );
	}

	private void ApplyScanJobResult( ScanJobResult result )
	{
		if ( !string.IsNullOrWhiteSpace( result.DetectedSourceProfilePath ) )
			_job.SourceProfilePath = result.DetectedSourceProfilePath;
		if ( !string.IsNullOrWhiteSpace( result.DetectedMappingProfilePath ) )
			_job.MappingProfilePath = result.DetectedMappingProfilePath;
		LoadProfilesFromJob();
		RefreshSourceLibraryFromJob();
		SetSourcePresetControlsFromJob();
		_mappingProfilePath.Text = _job.MappingProfilePath;
		_allClips = result.Clips;
		_inspection = result.Inspection;
		_mappingState = result.MappingState;
		_sourceFacingEulerDegrees = GetStoredSourceFacingEulerDegrees();
		ClearLegacyManualFacingIfItMatchesPreset();
		if ( _allClips.Count > 0 && !string.IsNullOrWhiteSpace( _clipFilter?.Text ) && CountVisibleClips( _clipFilter.Text ) == 0 )
			_clipFilter.Text = string.Empty;
		RefreshClipList();
		RefreshSourceBoneList();
		RefreshMappingList();
		RefreshSourceFacingUi();
		UpdateDiagnostics();
		RefreshClipSummary();
		SyncCompareSelectionFromCurrentClip();
		ResolveCompareResultSelection();
		RefreshResultList();
		RefreshResultSurfaces();
		UpdateButtons();
		RememberScanDiagnostic( _allClips.Count == 0 ? "completed_no_clips" : "completed", CitizenRetargetPaths.DecodeExternalPath( _job.SourceFbxPath ?? string.Empty ), BuildScanSuccessDiagnostic( result ) );
		SetStatus( _allClips.Count == 0
			? $"Scanned source and inspected {_inspection.Audit.Bones.Count} bones using '{result.DetectedSourceProfileDisplayName}', but no animation clips were found."
			: $"Scanned {_allClips.Count} clips and inspected {_inspection.Audit.Bones.Count} bones using '{result.DetectedSourceProfileDisplayName}'." );
		if ( _allClips.Count == 0 && _clipSummaryLabel is not null )
			_clipSummaryLabel.Text = "Scan finished, but no animation clips were found. Try a different FBX export, or export a support bundle from Diagnostics.";
	}

	private void BeginRefreshTargetAnimationListAsync( string statusMessage )
	{
		if ( _targetAnimationList is null || _builtInTargetAnimationList is null )
			return;

		if ( HasActiveBackgroundJob( "target" ) )
			return;

		var previewVmdlPath = _pipeline.ResolvePreviewableTargetVmdlPath( _job );
		var importedSources = _pipeline.GetTargetAnimationSources( _job );
		var shouldLoadBuiltIns = _showBuiltInTargetAnimations || _builtInTargetAnimationsLoadRequested || _builtInTargetAnimationsLoaded;
		StartBackgroundJob(
			title: "Loading target animations",
			scope: "target",
			blocksInteraction: false,
			showInJobsStrip: false,
			work: reporter =>
			{
				reporter.ReportDetail( "Reading imported target animations..." );
				var importedTargets = importedSources
					.Select( target => new RetargetTargetAnimationEntry
					{
						SequenceName = target.SequenceName,
						Looping = target.Looping,
						IsImported = true,
						IsReadOnly = false,
						VmdlResourcePath = previewVmdlPath,
						SourceResourcePath = target.ResourcePath
					} )
					.ToList();
				reporter.ReportProgress( 0.5f, shouldLoadBuiltIns ? "Reading model animation list..." : "Skipping built-in animation load..." );
				var builtInTargets = shouldLoadBuiltIns
					? _pipeline.GetModelAnimationNames( previewVmdlPath )
						.Where( sequenceName => !importedTargets.Select( target => target.SequenceName ).Contains( sequenceName, StringComparer.OrdinalIgnoreCase ) )
						.Select( sequenceName => new RetargetTargetAnimationEntry
						{
							SequenceName = sequenceName,
							Looping = sequenceName.Contains( "loop", StringComparison.OrdinalIgnoreCase ),
							IsImported = false,
							IsReadOnly = true,
							VmdlResourcePath = previewVmdlPath
						} )
						.OrderBy( target => target.SequenceName, StringComparer.OrdinalIgnoreCase )
						.ToList()
					: new List<RetargetTargetAnimationEntry>();
				reporter.ReportProgress( 1f, "Applying target animation list..." );
				return () => ApplyTargetAnimationEntries( importedTargets, builtInTargets, statusMessage, shouldLoadBuiltIns );
			},
			onError: exception =>
			{
				_builtInTargetAnimationsLoadRequested = false;
				if ( _toggleBuiltInTargetAnimationsButton is not null && !_showBuiltInTargetAnimations )
					_toggleBuiltInTargetAnimationsButton.Text = "Show Built-In";
				SetStatus( $"Target load failed: {exception.Message}" );
			} );
	}

	private void ApplyTargetAnimationEntries(
		List<RetargetTargetAnimationEntry> importedTargets,
		List<RetargetTargetAnimationEntry> builtInTargets,
		string statusMessage,
		bool builtInsLoaded )
	{
		_targetAnimationList.SetItems( importedTargets );
		_builtInTargetAnimationList.SetItems( builtInTargets );
		_targetAnimationList.UpdateIfDirty();
		_builtInTargetAnimationList.UpdateIfDirty();
		if ( builtInsLoaded )
			_builtInTargetAnimationsLoaded = true;
		if ( _builtInTargetAnimationsLoadRequested && builtInsLoaded )
		{
			_builtInTargetAnimationsLoadRequested = false;
			SetBuiltInTargetAnimationVisibility( true );
		}
		if ( _selectedTargetAnimation is not null )
		{
			var matchingImported = importedTargets.FirstOrDefault( target =>
				target.IsImported == _selectedTargetAnimation.IsImported
				&& target.SequenceName.Equals( _selectedTargetAnimation.SequenceName, StringComparison.OrdinalIgnoreCase ) );
			if ( matchingImported is not null )
				_targetAnimationList.SelectItem( matchingImported, true, true );

			var matchingBuiltIn = builtInTargets.FirstOrDefault( target =>
				target.IsImported == _selectedTargetAnimation.IsImported
				&& target.SequenceName.Equals( _selectedTargetAnimation.SequenceName, StringComparison.OrdinalIgnoreCase ) );
			if ( matchingBuiltIn is not null )
				_builtInTargetAnimationList.SelectItem( matchingBuiltIn, true, true );
		}
		if ( _targetSummaryLabel is not null )
			_targetSummaryLabel.Text = BuildTargetAnimationSummary( importedTargets, builtInTargets );
		if ( _builtInTargetSummaryLabel is not null )
			_builtInTargetSummaryLabel.Text = BuildBuiltInTargetAnimationSummary( builtInTargets );
		SetBuiltInTargetAnimationVisibility( _showBuiltInTargetAnimations );
		UpdateButtons();
		SetStatus( statusMessage );
	}

	private void RebuildAutoMap()
	{
		if ( _inspection is null )
			return;

		SyncJobFromControls();
		LoadProfilesFromJob();
		_mappingState = _pipeline.BuildMappingState( _job, _sourceProfile, _mappingProfile, _inspection );
		RefreshMappingList();
		RefreshMappingEditorState();
		UpdateDiagnostics();
		UpdateButtons();
		SetStatus( "Rebuilt the mapping state from the current profiles." );
	}

	private void SaveMappingProfile()
	{
		try
		{
			SyncJobFromControls();
			LoadProfilesFromJob();
			var path = _pipeline.SaveMappingProfile( _mappingProfile, _mappingState, _job.MappingProfilePath );
			SetStatus( $"Saved mapping profile to '{path}'." );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Saving mapping profile failed: {exception.Message}" );
		}
	}

	private void OnSourceBoneSelectionChanged( NativeAuditBoneInfo? bone )
	{
		_selectedSourceBone = bone;
		RefreshMappingEditorState();
		UpdateButtons();
	}

	private void OnMappingSlotSelected( RetargetSlotAssignmentState? slot )
	{
		_selectedSlot = slot;
		TrySelectSourceBoneForSelectedSlot();
		RefreshMappingEditorState();
		UpdateButtons();
	}

	private void TrySelectSourceBoneForSelectedSlot()
	{
		if ( _selectedSlot is null || _sourceBoneList is null || _inspection is null )
			return;

		var preferredBoneName = !string.IsNullOrWhiteSpace( _selectedSlot.AssignedSourceBone )
			? _selectedSlot.AssignedSourceBone
			: _selectedSlot.EffectiveSourceBone;
		if ( string.IsNullOrWhiteSpace( preferredBoneName ) )
			return;

		var preferredBone = _inspection.Audit.Bones.FirstOrDefault( bone =>
			bone.Name.Equals( preferredBoneName, StringComparison.OrdinalIgnoreCase ) );
		if ( preferredBone is null )
			return;

		_selectedSourceBone = preferredBone;
		_sourceBoneList.SelectItem( preferredBone, true, true );
	}

	private void RefreshMappingEditorState()
	{
		if ( _mappingSelectionTitleLabel is null || _mappingSelectionSummaryLabel is null || _mappingSelectionHintLabel is null )
			return;

		if ( _selectedSlot is null )
		{
			_mappingSelectionTitleLabel.Text = "No slot selected yet.";
			_mappingSelectionSummaryLabel.Text = "Pick a row in Bone Map Editor, then pick a source bone on the left.";
			_mappingSelectionHintLabel.Text = "Manual flow: select slot -> select source bone -> Assign Selected Bone -> Save Mapping Profile.";
			return;
		}

		var currentSource = string.IsNullOrWhiteSpace( _selectedSlot.EffectiveSourceBone ) ? "Unmapped" : _selectedSlot.EffectiveSourceBone;
		var autoSource = string.IsNullOrWhiteSpace( _selectedSlot.AutoSourceBone ) ? "No auto match" : _selectedSlot.AutoSourceBone;
		var sourceState = _selectedSlot.IsManualOverride
			? "Manual override"
			: string.IsNullOrWhiteSpace( _selectedSlot.EffectiveSourceBone )
				? "Needs manual assignment"
				: "Auto mapped";
		var importance = _selectedSlot.Required ? "Required slot" : "Optional slot";
		var selectedBoneText = _selectedSourceBone?.Name ?? "No source bone selected";
		var candidates = _selectedSlot.CandidateBones
			.Where( bone => !string.IsNullOrWhiteSpace( bone ) )
			.Take( 4 )
			.ToList();
		var candidateText = candidates.Count == 0
			? "No other auto candidates."
			: $"Auto candidates: {string.Join( ", ", candidates )}{(_selectedSlot.CandidateBones.Count > candidates.Count ? "..." : string.Empty)}";

		_mappingSelectionTitleLabel.Text = $"{_selectedSlot.SlotId} -> {_selectedSlot.TargetBone}";
		_mappingSelectionSummaryLabel.Text =
			$"Current source: {currentSource}{Environment.NewLine}" +
			$"State: {sourceState} | {importance}{Environment.NewLine}" +
			$"Auto suggestion: {autoSource}{Environment.NewLine}" +
			$"{candidateText}";
		_mappingSelectionHintLabel.Text = string.IsNullOrWhiteSpace( _selectedSourceBone?.Name )
			? "Select a source bone on the left, then press Assign Selected Bone. Use Clear to leave the slot unmapped or Reset to Auto to restore the auto suggestion."
			: $"Selected source bone: {selectedBoneText}. Press Assign Selected Bone to apply it, then Save Mapping Profile when you are happy with the overrides.";
	}

	private void AddSelectedClipsToQueue()
	{
		var selectedClips = _clipList.SelectedItems.OfType<RetargetClipDescriptor>().ToList();
		if ( selectedClips.Count == 0 )
		{
			SetStatus( "Select one or more clips before adding them to the queue." );
			return;
		}

		var addedCount = 0;
		var refreshedCount = 0;
		foreach ( var clip in selectedClips )
		{
			var existing = _queueItems.FirstOrDefault( item => item.Clip.DisplayName.Equals( clip.DisplayName, StringComparison.OrdinalIgnoreCase ) );
			if ( existing is not null )
			{
				existing.Status = "pending";
				existing.Message = "Queued";
				existing.Result = null;
				refreshedCount++;
				continue;
			}

			_queueItems.Add( new RetargetQueueItem
			{
				Clip = clip,
				Status = "pending",
				Message = "Queued"
			} );
			addedCount++;
		}

		RefreshQueueList();
		RefreshClipSummary();
		UpdateButtons();
		SetStatus( $"Queue updated. Added {addedCount} clip(s), refreshed {refreshedCount} existing clip(s)." );
	}

	private void RunQueue()
	{
		if ( _queueRunning || _jobInFlight )
			return;

		var pendingCount = _queueItems.Count( item => item.Status == "pending" );
		if ( pendingCount == 0 )
		{
			SetStatus( _queueItems.Count == 0
				? "Queue is empty. Add clips from Home before starting a run."
				: "Queue has no pending clips. Requeue failed clips or clear finished items first." );
			return;
		}

		if ( _environmentReport is null || !_environmentReport.CanRunRetarget )
		{
			_latestSetupFailureMessage = "Checking retarget setup before starting the queue...";
			MarkPendingQueueItemsWithSetupFailure( _latestSetupFailureMessage );
			RefreshQueueList();
			SetStatus( _latestSetupFailureMessage );
			BeginEnvironmentCheck( startQueueWhenReady: true );
			return;
		}

		StartQueueCore();
	}

	private void StartQueueCore()
	{
		if ( _queueRunning || _jobInFlight )
			return;

		var pendingCount = _queueItems.Count( item => item.Status == "pending" );
		if ( pendingCount == 0 )
			return;

		_latestQueueFailureMessage = string.Empty;
		_latestSetupFailureMessage = string.Empty;
		ClearPendingSetupMessages();
		_cancelQueueRequested = false;
		_queueRunning = true;
		RefreshQueueList();
		UpdateButtons();
		SetStatus( $"Starting queue with {pendingCount} pending clip(s)." );
	}

	private void RetryFailedQueueItems()
	{
		var failedItems = _queueItems.Where( item => item.Status == "failed" ).ToList();
		if ( failedItems.Count == 0 )
		{
			SetStatus( "Queue has no failed clips to retry." );
			return;
		}

		foreach ( var item in failedItems )
		{
			item.Status = "pending";
			item.Message = "Retry queued";
			item.Result = null;
		}

		RefreshQueueList();
		UpdateButtons();
		SetStatus( $"Re-queued {failedItems.Count} failed clip(s)." );
	}

	private void ClearFinishedQueueItems()
	{
		if ( _queueRunning || _jobInFlight )
		{
			SetStatus( "Wait for the current queue run to finish before clearing finished items." );
			return;
		}

		var removedCount = _queueItems.RemoveAll( item => item.Status == "completed" || item.Status == "failed" );
		RefreshQueueList();
		UpdateButtons();
		SetStatus( removedCount > 0 ? $"Cleared {removedCount} finished queue item(s)." : "Queue has no finished items to clear." );
	}

	private void ClearQueue()
	{
		if ( _jobInFlight || _queueItems.Any( item => item.Status == "running" ) )
		{
			_cancelQueueRequested = true;
			var removedWaitingCount = _queueItems.RemoveAll( item => item.Status != "running" );
			RefreshQueueList();
			UpdateButtons();
			SetStatus(
				removedWaitingCount > 0
					? $"Queue clear requested. Removed {removedWaitingCount} waiting item(s); the current clip will finish before the queue is fully cleared."
					: "Queue clear requested. The current clip will finish before the queue is fully cleared." );
			return;
		}

		_queueRunning = false;
		_cancelQueueRequested = false;
		var removedCount = _queueItems.Count;
		_queueItems.Clear();
		RefreshQueueList();
		UpdateButtons();
		SetStatus( removedCount > 0 ? $"Cleared {removedCount} queued clip(s)." : "Queue is already empty." );
	}

	private void RemoveSelectedHomeQueueItems()
	{
		if ( _queueRunning || _jobInFlight )
		{
			SetStatus( "Wait for the current queue run to finish before editing the queue." );
			return;
		}

		var selectedItems = _homeQueueList?.SelectedItems.OfType<RetargetQueueItem>().ToList() ?? new List<RetargetQueueItem>();
		if ( selectedItems.Count == 0 )
		{
			SetStatus( "Select one or more queue items on Home before removing them." );
			return;
		}

		var removedCount = 0;
		foreach ( var item in selectedItems )
		{
			if ( _queueItems.Remove( item ) )
				removedCount++;
		}

		RefreshQueueList();
		UpdateButtons();
		SetStatus( removedCount > 0 ? $"Removed {removedCount} item(s) from the queue." : "No queue items were removed." );
	}

	private void RunQueuedItem( RetargetQueueItem item )
	{
		_jobInFlight = true;
		item.Status = "running";
		item.Message = "Retargeting in Blender";
		RefreshQueueList();
		UpdateButtons();
		SyncJobFromControls();
		LoadProfilesFromJob();
		SetStatus( $"Retargeting '{item.Clip.DisplayName}' through the production path (template FBX -> generated Citizen VMDL)." );
		var jobSnapshot = _pipeline.CreateRuntimeJobSnapshot( _job );
		var sourceProfileSnapshot = _sourceProfile;
		var mappingProfileSnapshot = _mappingProfile;
		var mappingStateSnapshot = _mappingState.ToList();
		var sourceFacingEulerDegrees = GetAppliedSourceFacingEulerDegrees();
		RetargetDiagnosticSummary diagnostics;
		try
		{
			diagnostics = _pipeline.ValidateImportDiagnostics( mappingStateSnapshot );
		}
		catch ( Exception exception )
		{
			ApplyFailedQueueItem( item, exception );
			return;
		}

		StartBackgroundJob(
			title: $"Retargeting {item.Clip.DisplayName}",
			scope: "queue",
			blocksInteraction: true,
			showInJobsStrip: true,
			work: reporter =>
			{
				reporter.ReportProgress( 0.12f, "Running Blender retarget backend..." );
				var result = _pipeline.RunBackendRetargetOnly( jobSnapshot, item.Clip, sourceProfileSnapshot, mappingProfileSnapshot, mappingStateSnapshot, sourceFacingEulerDegrees );
				reporter.ReportProgress( 0.78f, "Importing animation and updating the target..." );
				reporter.ReportProgress( 1f, "Finalizing imported assets..." );
				return () =>
				{
					try
					{
						var finalizedResult = _pipeline.FinalizeImportedResult( _job, item.Clip, result, diagnostics );
						ApplyCompletedQueueItem( item, finalizedResult );
					}
					catch ( Exception exception )
					{
						ApplyFailedQueueItem( item, exception );
					}
				};
			},
			onError: exception => ApplyFailedQueueItem( item, exception ) );
	}

	private void ApplyCompletedQueueItem( RetargetQueueItem item, RetargetImportResult result )
	{
		item.Result = result;
		var completedWithoutImportError =
			result.Manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase )
			&& string.IsNullOrWhiteSpace( result.PostImportError );
		item.Status = completedWithoutImportError ? "completed" : "failed";
		item.Message = BuildQueueResultMessage( result, completedWithoutImportError );
		_latestResult = result;
		if ( completedWithoutImportError && IsSingleSelectedClip( item.Clip ) )
		{
			_session.CompareSelection.SelectedSourceLibraryId = _activeSourceLibrary.LibraryId;
			_session.CompareSelection.SelectedSourceClipName = item.Clip.DisplayName;
			_session.CompareSelection.SelectedResultRunId = result.Manifest.RunId;
			_selectedCompareResult = result;
		}
		else if ( _inspectedRunResult is null )
		{
			_inspectedRunResult = result;
		}

		if ( string.IsNullOrWhiteSpace( result.PostImportError ) )
		{
			SetStatus( $"Completed '{item.Clip.DisplayName}'. Imported FBX and generated Citizen model are ready." );
		}
		else
		{
			_latestQueueFailureMessage = result.PostImportError;
			SetStatus( $"Retarget completed in Blender for '{item.Clip.DisplayName}', but import into the target project failed: {result.PostImportError}" );
		}

		_jobInFlight = false;
		RefreshResultSurfaces();
		RefreshRunsInspectionState();
		RefreshQueueList();
		RefreshHistoryList();
		RefreshResultList();
		UpdateDiagnostics();
		FinalizeQueueIfIdle();
		UpdateButtons();
		if ( !_queueItems.Any( queueItem => queueItem.Status == "pending" || queueItem.Status == "running" ) )
			BeginRefreshTargetAnimationListAsync( $"Loaded target animations after '{item.Clip.DisplayName}'." );
	}

	private void ApplyFailedQueueItem( RetargetQueueItem item, Exception exception )
	{
		item.Status = "failed";
		item.Message = BuildQueueExceptionMessage( exception );
		_latestResult = null;
		_latestQueueFailureMessage = item.Message;
		_jobInFlight = false;
		RefreshResultSurfaces();
		RefreshRunsInspectionState();
		RefreshQueueList();
		RefreshHistoryList();
		RefreshResultList();
		UpdateDiagnostics();
		FinalizeQueueIfIdle();
		UpdateButtons();
		SetStatus( $"Retarget failed for '{item.Clip.DisplayName}': {item.Message}" );
	}

	private static string BuildQueueResultMessage( RetargetImportResult result, bool completedWithoutImportError )
	{
		if ( completedWithoutImportError )
			return "Imported FBX + Citizen model ready";

		if ( !string.IsNullOrWhiteSpace( result.PostImportError ) )
			return $"target import failed: {TruncateDiagnosticText( result.PostImportError, 120 )}";

		var failedStage = FindFirstFailedStage( result.Manifest );
		if ( failedStage is not null )
		{
			var stageName = FormatStageName( failedStage.StageId );
			var stageError = TruncateDiagnosticText( failedStage.Error, 120 );
			return string.IsNullOrWhiteSpace( stageError )
				? $"failed during {stageName}"
				: $"failed during {stageName}: {stageError}";
		}

		if ( result.Manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			return $"motion validation failed: {TruncateDiagnosticText( result.Manifest.MotionTrajectoryAnalysis.Failures[0], 120 )}";

		if ( result.Manifest.UnmappedRequiredSlots.Count > 0 )
			return $"missing required slots: {BuildIssueListPreview( result.Manifest.UnmappedRequiredSlots, 4 )}";

		return string.IsNullOrWhiteSpace( result.Manifest.Status )
			? "backend finished with unknown status"
			: $"backend status: {result.Manifest.Status.Replace( '_', ' ' )}";
	}

	private static string BuildQueueExceptionMessage( Exception exception )
	{
		var message = (exception.Message ?? string.Empty)
			.Split( new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries )
			.FirstOrDefault()
			?.Trim() ?? string.Empty;

		if ( string.IsNullOrWhiteSpace( message )
			|| message.Equals( "failed", StringComparison.OrdinalIgnoreCase )
			|| message.Equals( "error", StringComparison.OrdinalIgnoreCase ) )
		{
			return "retarget failed before a detailed manifest was written";
		}

		return TruncateDiagnosticText( message, 140 );
	}

	private bool CanDeleteImportedTargetAnimation( RetargetTargetAnimationEntry target )
	{
		if ( target is null || !target.IsImported || target.IsReadOnly )
			return false;

		var sourceBusy = HasActiveBackgroundJob( "source" );
		var targetBusy = HasActiveBackgroundJob( "target" );
		var queueBusy = HasActiveBackgroundJob( "queue" );
		return !_queueRunning && !sourceBusy && !targetBusy && !queueBusy && !_jobInFlight;
	}

	private void DeleteImportedTargetAnimation( RetargetTargetAnimationEntry target )
	{
		if ( target is null || !target.IsImported || target.IsReadOnly )
		{
			SetStatus( "Only imported target animations can be deleted." );
			return;
		}

		if ( !CanDeleteImportedTargetAnimation( target ) )
		{
			SetStatus( "Wait for the current background work to finish before deleting target animations." );
			return;
		}

		if ( string.IsNullOrWhiteSpace( target.SourceResourcePath ) )
		{
			SetStatus( $"Target animation '{target.SequenceName}' does not expose a deletable source asset path." );
			return;
		}

		var targetSnapshot = new RetargetTargetAnimationEntry
		{
			SequenceName = target.SequenceName,
			IsImported = target.IsImported,
			IsReadOnly = target.IsReadOnly,
			Looping = target.Looping,
			SourceResourcePath = target.SourceResourcePath,
			VmdlResourcePath = target.VmdlResourcePath
		};

		SetStatus( $"Removing imported target animation '{target.SequenceName}'..." );
		StartBackgroundJob(
			title: $"Removing {target.SequenceName}",
			scope: "target",
			blocksInteraction: false,
			showInJobsStrip: true,
			work: reporter =>
			{
				reporter.ReportDetail( "Deleting imported animation assets..." );
				var absoluteSourcePath = CitizenRetargetPaths.GetAssetAbsolutePath( targetSnapshot.SourceResourcePath );
				var absoluteFolder = Path.GetDirectoryName( absoluteSourcePath ) ?? string.Empty;
				var fileStem = Path.GetFileNameWithoutExtension( absoluteSourcePath );
				var deletedCount = 0;
				foreach ( var extension in new[] { ".fbx", ".smd", ".dmx" } )
				{
					var candidate = Path.Combine( absoluteFolder, $"{fileStem}{extension}" );
					if ( !File.Exists( candidate ) )
						continue;

					File.Delete( candidate );
					deletedCount++;
				}

				reporter.ReportProgress( 0.7f, "Rebuilding target asset..." );
				if ( deletedCount > 0 )
					_pipeline.RefreshTargetAssetDefinition( _job );
				reporter.ReportProgress( 0.88f, "Refreshing target animations..." );
				var importedTargets = BuildImportedTargetAnimationEntries();
				var builtInTargets = (_showBuiltInTargetAnimations || _builtInTargetAnimationsLoaded || _builtInTargetAnimationsLoadRequested)
					? BuildBuiltInTargetAnimationEntries( importedTargets )
					: new List<RetargetTargetAnimationEntry>();
				reporter.ReportProgress( 1f, "Applying UI updates..." );
				var loadResult = new TargetAnimationLoadResult
				{
					DeletedCount = deletedCount,
					ImportedTargets = importedTargets,
					BuiltInTargets = builtInTargets
				};
				return () =>
				{
					if ( loadResult.DeletedCount == 0 )
					{
						SetStatus( $"Imported target animation '{targetSnapshot.SequenceName}' is already missing on disk." );
						RefreshResultList();
						RefreshResultSurfaces();
						UpdateButtons();
						return;
					}

					if ( _selectedTargetAnimation is not null
						&& _selectedTargetAnimation.IsImported
						&& _selectedTargetAnimation.SequenceName.Equals( targetSnapshot.SequenceName, StringComparison.OrdinalIgnoreCase ) )
					{
						_selectedTargetAnimation = null;
					}

					var selectedResultEntry = FindHistoryEntryByRunId( _session.CompareSelection.SelectedResultRunId );
					if ( selectedResultEntry is not null
						&& selectedResultEntry.SequenceName.Equals( targetSnapshot.SequenceName, StringComparison.OrdinalIgnoreCase ) )
					{
						_session.CompareSelection.SelectedResultRunId = string.Empty;
						_selectedCompareResult = null;
					}

					_pipeline.SaveJob( _job );
					ApplyTargetAnimationEntries(
						loadResult.ImportedTargets,
						loadResult.BuiltInTargets,
						$"Removed imported target animation '{targetSnapshot.SequenceName}'.",
						_showBuiltInTargetAnimations || _builtInTargetAnimationsLoaded || _builtInTargetAnimationsLoadRequested );
					RefreshResultList();
					RefreshResultSurfaces();
					UpdateButtons();
				};
			},
			onError: exception =>
			{
				SetStatus( $"Failed to delete imported target animation '{targetSnapshot.SequenceName}': {exception.Message}" );
				UpdateButtons();
			} );
	}

	private void AssignSelectedBone()
	{
		if ( _selectedSlot is null || _selectedSourceBone is null )
			return;

		_selectedSlot.AssignedSourceBone = _selectedSourceBone.Name;
		_selectedSlot.IsManualOverride = true;
		RefreshMappingList();
		RefreshMappingEditorState();
		UpdateDiagnostics();
		UpdateButtons();
		SetStatus( $"Assigned '{_selectedSourceBone.Name}' to '{_selectedSlot.SlotId}'." );
	}

	private void ClearSelectedSlot()
	{
		if ( _selectedSlot is null )
			return;

		_selectedSlot.AssignedSourceBone = string.Empty;
		_selectedSlot.IsManualOverride = true;
		RefreshMappingList();
		RefreshMappingEditorState();
		UpdateDiagnostics();
		UpdateButtons();
		SetStatus( $"Cleared manual source assignment for '{_selectedSlot.SlotId}'." );
	}

	private void ResetSelectedSlot()
	{
		if ( _selectedSlot is null )
			return;

		_selectedSlot.AssignedSourceBone = string.Empty;
		_selectedSlot.IsManualOverride = false;
		RefreshMappingList();
		RefreshMappingEditorState();
		UpdateDiagnostics();
		UpdateButtons();
		SetStatus( $"Reset '{_selectedSlot.SlotId}' back to the auto mapping suggestion." );
	}

	private void OpenResultInModelDoc()
	{
		try
		{
			SyncJobFromControls();
			_pipeline.OpenResultInModelDoc( _job );
			SetStatus( $"Opened '{_job.TargetVmdlPath}' in the model viewer." );
		}
		catch ( Exception exception )
		{
			SetStatus( $"Open model failed: {exception.Message}" );
		}
	}

	private void OpenModelAssetInViewer( string? assetPath )
	{
		if ( string.IsNullOrWhiteSpace( assetPath ) )
			return;

		_pipeline.OpenModelAsset( assetPath );
	}

	private void OpenArtifact( string? absolutePath )
	{
		if ( !string.IsNullOrWhiteSpace( absolutePath ) )
			_pipeline.OpenPath( absolutePath );
	}

	private RetargetImportResult? GetCurrentArtifactResult()
		=> GetRunsInspectionResult();

	private void OpenCurrentRunDirectory() => OpenArtifact( GetCurrentArtifactResult()?.ArtifactLinks.RunDirectoryPath );
	private void OpenCurrentManifest() => OpenArtifact( GetCurrentArtifactResult()?.ArtifactLinks.ManifestPath );
	private void OpenCurrentRawExport() => OpenArtifact( GetCurrentArtifactResult()?.ArtifactLinks.ExportPath );
	private void OpenCurrentImportedAnimation() => OpenArtifact( GetCurrentArtifactResult()?.ArtifactLinks.ImportedAnimationPath );
	private void OpenCurrentGeneratedModel() => OpenModelAssetInViewer( GetCurrentArtifactResult()?.ArtifactLinks.VmdlPath );
	private void OpenCurrentRunLog() => OpenArtifact( GetCurrentArtifactResult()?.ArtifactLinks.RunLogPath );
	private void OpenHomeGeneratedModel()
	{
		var activePreviewModelPath = GetActivePreviewResult()?.ArtifactLinks.VmdlPath;
		if ( !string.IsNullOrWhiteSpace( activePreviewModelPath ) )
		{
			OpenModelAssetInViewer( activePreviewModelPath );
			return;
		}

		OpenModelAssetInViewer( _job.TargetVmdlPath );
	}

	private void OpenPoseCompensationData()
	{
		var compensationPath = Path.Combine( CitizenRetargetPaths.DataRoot, "citizen_arm_rest_compensation.json" );
		OpenArtifact( compensationPath );
	}

	private void OpenHistoryEntry( RetargetRunHistoryEntry? entry )
	{
		if ( entry is null )
			return;
		var hydrated = BuildImportResultFromHistoryEntry( entry );
		if ( hydrated is null )
			return;

		_session.SelectedHistoryRunId = entry.RunId;
		_inspectedRunResult = hydrated;
		RefreshHistoryList();
		RefreshRunsInspectionState();
		UpdateDiagnostics();
		RefreshClipSummary();
		SetStatus( $"Loaded run '{entry.RunId}' from history." );
	}

	private void UseSelectedHistoryRunAsCompareResult()
	{
		var entry = FindHistoryEntryByRunId( _session.SelectedHistoryRunId );
		if ( entry is null )
		{
			SetStatus( "Select a run in Diagnostics before setting it as the active result." );
			return;
		}

		if ( !MatchesActiveSourceLibrary( entry ) )
		{
			SetStatus( $"Run '{entry.RunId}' belongs to a different source library. Switch the source FBX first to compare it on Home." );
			return;
		}

		var clip = FindClipForHistoryEntry( entry );
		if ( clip is not null )
		{
			_job.SelectedClipNames = new List<string> { clip.DisplayName };
			_pipeline.SaveJob( _job );
			RefreshClipList();
		}

		_selectedTargetAnimation = null;
		_session.CompareSelection.SelectedSourceLibraryId = _activeSourceLibrary.LibraryId;
		_session.CompareSelection.SelectedSourceClipName = entry.ClipName ?? string.Empty;
		_session.CompareSelection.SelectedResultRunId = entry.RunId ?? string.Empty;
		ResolveCompareResultSelection();
		RefreshResultList();
		RefreshResultSurfaces();
		UpdateButtons();
		SetStatus( $"Run '{entry.RunId}' is now the active result on Home." );
	}

	private void ClearHomeResult()
	{
		var selectedClip = GetSingleSelectedClip();
		_session.CompareSelection.SelectedSourceLibraryId = selectedClip is null ? string.Empty : _activeSourceLibrary.LibraryId;
		_session.CompareSelection.SelectedSourceClipName = selectedClip?.DisplayName ?? string.Empty;
		_session.CompareSelection.SelectedResultRunId = string.Empty;
		_selectedCompareResult = null;
		_selectedTargetAnimation = null;
		RefreshResultList();
		RefreshResultSurfaces();
		UpdateButtons();
		SetStatus( selectedClip is null
			? "Cleared the active result."
			: $"Cleared the active result for '{selectedClip.DisplayName}'." );
	}

	private void InspectHomeResultInRuns()
	{
		var entry = FindHistoryEntryByRunId( _session.CompareSelection.SelectedResultRunId );
		if ( entry is null )
		{
			SetStatus( "Select a result on Home before inspecting it in Runs." );
			return;
		}

		var hydrated = BuildImportResultFromHistoryEntry( entry );
		if ( hydrated is null )
		{
			SetStatus( $"Run '{entry.RunId}' could not be hydrated for inspection." );
			return;
		}

		_session.SelectedHistoryRunId = entry.RunId;
		_inspectedRunResult = hydrated;
		RefreshHistoryList();
		RefreshRunsInspectionState();
		UpdateDiagnostics();
		UpdateButtons();
		SetStatus( $"Run '{entry.RunId}' is ready to inspect in Runs." );
	}

	private RetargetImportResult? GetActivePreviewResult()
	{
		return _selectedCompareResult;
	}

	private RetargetTargetAnimationEntry? GetActiveTargetPreviewAnimation()
	{
		return _selectedTargetAnimation;
	}

	private static bool HasMaterializedImportedResult( RetargetImportResult? result )
	{
		if ( result is null )
			return false;

		var importedAnimationPath = result.ArtifactLinks.ImportedAnimationPath;
		var generatedModelPath = result.ArtifactLinks.VmdlPath;
		return !string.IsNullOrWhiteSpace( importedAnimationPath )
			&& File.Exists( importedAnimationPath )
			&& !string.IsNullOrWhiteSpace( generatedModelPath )
			&& File.Exists( generatedModelPath );
	}

	private RetargetClipDescriptor? GetSingleSelectedClip()
	{
		var selectedClips = _clipList?.SelectedItems.OfType<RetargetClipDescriptor>().ToList() ?? new List<RetargetClipDescriptor>();
		return selectedClips.Count == 1 ? selectedClips[0] : null;
	}

	private RetargetImportResult? GetRunsInspectionResult()
		=> _inspectedRunResult ?? _latestResult;

	private RetargetRunHistoryEntry? GetSelectedHistoryEntry()
		=> FindHistoryEntryByRunId( _session.SelectedHistoryRunId );

	private void SyncCompareSelectionFromCurrentClip()
	{
		var selectedClip = GetSingleSelectedClip();
		if ( selectedClip is null )
		{
			_session.CompareSelection.SelectedSourceLibraryId = string.Empty;
			_session.CompareSelection.SelectedSourceClipName = string.Empty;
			_session.CompareSelection.SelectedResultRunId = string.Empty;
			_selectedCompareResult = null;
			return;
		}

		_session.CompareSelection.SelectedSourceLibraryId = _activeSourceLibrary.LibraryId;
		_session.CompareSelection.SelectedSourceClipName = selectedClip.DisplayName;

		if ( string.IsNullOrWhiteSpace( _session.CompareSelection.SelectedResultRunId ) )
			return;

		var currentResultEntry = FindHistoryEntryByRunId( _session.CompareSelection.SelectedResultRunId );
		if ( currentResultEntry is null || !MatchesClip( currentResultEntry, selectedClip ) || !MatchesActiveSourceLibrary( currentResultEntry ) || !MatchesActiveTargetWorkspace( currentResultEntry ) )
			_session.CompareSelection.SelectedResultRunId = string.Empty;
	}

	private void ResolveCompareResultSelection()
	{
		var selectedClip = GetSingleSelectedClip();
		if ( selectedClip is null )
		{
			_selectedCompareResult = null;
			return;
		}

		if ( !string.Equals( _session.CompareSelection.SelectedSourceLibraryId, _activeSourceLibrary.LibraryId, StringComparison.OrdinalIgnoreCase ) )
		{
			_selectedCompareResult = null;
			_session.CompareSelection.SelectedResultRunId = string.Empty;
			return;
		}

		var selectedResultEntry = FindHistoryEntryByRunId( _session.CompareSelection.SelectedResultRunId );
		if ( selectedResultEntry is null || !MatchesClip( selectedResultEntry, selectedClip ) || !MatchesActiveSourceLibrary( selectedResultEntry ) || !MatchesActiveTargetWorkspace( selectedResultEntry ) )
		{
			selectedResultEntry = FindPreferredHistoryEntryForClip( selectedClip );
			_session.CompareSelection.SelectedResultRunId = selectedResultEntry?.RunId ?? string.Empty;
		}

		_selectedCompareResult = selectedResultEntry is null
			? null
			: BuildImportResultFromHistoryEntry( selectedResultEntry );
	}

	private RetargetImportResult? BuildImportResultFromHistoryEntry( RetargetRunHistoryEntry entry )
	{
		var manifest = LoadManifestFromHistoryEntry( entry ) ?? BuildFallbackManifestFromHistoryEntry( entry );
		var manifestAbsolutePath = !string.IsNullOrWhiteSpace( entry.ManifestPath ) && File.Exists( entry.ManifestPath )
			? entry.ManifestPath
			: string.Empty;
		var runDirectoryPath = !string.IsNullOrWhiteSpace( manifestAbsolutePath )
			? Path.GetDirectoryName( manifestAbsolutePath ) ?? string.Empty
			: string.Empty;
		var importedAnimationPath = CitizenRetargetPaths.DecodeExternalPath( entry.ImportedAssetPath );
		var exportPath = CitizenRetargetPaths.DecodeExternalPath( entry.ExportPath );
		var previewVideoPath = CitizenRetargetPaths.DecodeExternalPath( entry.PreviewVideoPath );
		var comparisonVideoPath = CitizenRetargetPaths.DecodeExternalPath( entry.ComparisonVideoPath );
		var targetVmdlPath = string.IsNullOrWhiteSpace( entry.TargetVmdlPath ) ? _job.TargetVmdlPath : entry.TargetVmdlPath;
		var runLogPath = string.IsNullOrWhiteSpace( runDirectoryPath )
			? string.Empty
			: Path.Combine( runDirectoryPath, CitizenRetargetPaths.EditorRunLogFileName );

		return new RetargetImportResult
		{
			ManifestAbsolutePath = manifestAbsolutePath,
			Manifest = manifest,
			PreviewArtifacts = new RetargetPreviewArtifacts
			{
				PreviewVideoPath = previewVideoPath,
				ComparisonVideoPath = comparisonVideoPath
			},
			ArtifactLinks = new RetargetArtifactLinks
			{
				RunDirectoryPath = runDirectoryPath,
				ManifestPath = manifestAbsolutePath,
				ImportedAnimationPath = importedAnimationPath,
				ExportPath = exportPath,
				VmdlPath = CitizenRetargetPaths.GetAssetAbsolutePath( targetVmdlPath ),
				RunLogPath = File.Exists( runLogPath ) ? runLogPath : string.Empty
			},
			SequenceName = entry.SequenceName,
			VmdlResourcePath = targetVmdlPath
		};
	}

	private static RetargetRunManifest? LoadManifestFromHistoryEntry( RetargetRunHistoryEntry entry )
	{
		if ( string.IsNullOrWhiteSpace( entry.ManifestPath ) || !File.Exists( entry.ManifestPath ) )
			return null;

		try
		{
			return RetargetManifestJson.Deserialize<RetargetRunManifest>( File.ReadAllText( entry.ManifestPath ) );
		}
		catch
		{
			return null;
		}
	}

	private RetargetRunManifest BuildFallbackManifestFromHistoryEntry( RetargetRunHistoryEntry entry )
	{
		return new RetargetRunManifest
		{
			RunId = entry.RunId ?? string.Empty,
			Status = entry.Status ?? string.Empty,
			SourceFile = _job.SourceFbxPath ?? string.Empty,
			RetargetedActionName = entry.SequenceName ?? string.Empty,
			Warnings = new List<string>
			{
				"Run manifest is unavailable. Showing preview from persisted history entry."
			},
			BackendWarnings = new List<string>(),
			UnmappedRequiredSlots = new List<string>(),
			Stages = new List<RetargetStageManifestEntry>(),
			MotionTrajectoryAnalysis = new RetargetMotionTrajectoryAnalysis(),
			PoseNormalization = new RetargetPoseNormalizationSummary(),
			Outputs = new RetargetOutputsSummary
			{
				RunDir = string.Empty,
				ExportPath = CitizenRetargetPaths.DecodeExternalPath( entry.ExportPath ),
				ManifestPath = string.Empty,
				BoneMapOverridesPath = string.Empty
			}
		};
	}

	private RetargetRunHistoryEntry? FindPreferredHistoryEntryForClip( RetargetClipDescriptor clip )
	{
		var matchingEntries = (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>())
			.Where( entry => MatchesClip( entry, clip ) && MatchesActiveSourceLibrary( entry ) && MatchesActiveTargetWorkspace( entry ) )
			.ToList();
		return matchingEntries.FirstOrDefault( IsCompletedRun )
			?? matchingEntries.FirstOrDefault();
	}

	private RetargetRunHistoryEntry? FindHistoryEntryByRunId( string? runId )
	{
		if ( string.IsNullOrWhiteSpace( runId ) )
			return null;

		return (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>())
			.FirstOrDefault( entry => entry.RunId.Equals( runId, StringComparison.OrdinalIgnoreCase ) );
	}

	private RetargetClipDescriptor? FindClipForHistoryEntry( RetargetRunHistoryEntry entry )
	{
		return _allClips.FirstOrDefault( clip =>
			clip.DisplayName.Equals( entry.ClipName, StringComparison.OrdinalIgnoreCase )
			|| BuildSequenceNameForClip( clip ).Equals( entry.SequenceName, StringComparison.OrdinalIgnoreCase ) );
	}

	private bool MatchesCurrentSelectedClip( RetargetImportResult result )
	{
		var selectedClip = GetSingleSelectedClip();
		return selectedClip is not null && MatchesClip( result, selectedClip );
	}

	private bool IsSingleSelectedClip( RetargetClipDescriptor clip )
	{
		var selectedClip = GetSingleSelectedClip();
		return selectedClip is not null && selectedClip.DisplayName.Equals( clip.DisplayName, StringComparison.OrdinalIgnoreCase );
	}

	private bool MatchesClip( RetargetImportResult result, RetargetClipDescriptor clip )
	{
		var sequenceName = BuildSequenceNameForClip( clip );
		return !string.IsNullOrWhiteSpace( result.SequenceName )
			&& result.SequenceName.Equals( sequenceName, StringComparison.OrdinalIgnoreCase );
	}

	private bool MatchesClip( RetargetRunHistoryEntry entry, RetargetClipDescriptor clip )
	{
		var sequenceName = BuildSequenceNameForClip( clip );
		return (!string.IsNullOrWhiteSpace( entry.ClipName )
				&& entry.ClipName.Equals( clip.DisplayName, StringComparison.OrdinalIgnoreCase ))
			|| (!string.IsNullOrWhiteSpace( sequenceName )
				&& entry.SequenceName.Equals( sequenceName, StringComparison.OrdinalIgnoreCase ));
	}

	private bool MatchesActiveSourceLibrary( RetargetRunHistoryEntry entry )
	{
		if ( string.IsNullOrWhiteSpace( _activeSourceLibrary.LibraryId ) )
			return true;

		var historySourcePath = TryGetHistoryEntrySourceFile( entry );
		if ( string.IsNullOrWhiteSpace( historySourcePath ) )
			return true;

		return string.Equals(
			NormalizeSourcePathForComparison( historySourcePath ),
			NormalizeSourcePathForComparison( _activeSourceLibrary.SourceFbxPath ),
			StringComparison.OrdinalIgnoreCase );
	}

	private bool MatchesActiveTargetWorkspace( RetargetRunHistoryEntry entry )
	{
		var currentTargetPath = NormalizeAssetPathForComparison( _job.TargetVmdlPath );
		if ( string.IsNullOrWhiteSpace( currentTargetPath ) )
			return true;

		var historyTargetPath = NormalizeAssetPathForComparison( entry.TargetVmdlPath );
		if ( string.IsNullOrWhiteSpace( historyTargetPath ) )
			return false;

		return string.Equals( historyTargetPath, currentTargetPath, StringComparison.OrdinalIgnoreCase );
	}

	private static string TryGetHistoryEntrySourceFile( RetargetRunHistoryEntry entry )
	{
		if ( string.IsNullOrWhiteSpace( entry.ManifestPath ) || !File.Exists( entry.ManifestPath ) )
			return string.Empty;

		try
		{
			var manifest = RetargetManifestJson.Deserialize<RetargetRunManifest>( File.ReadAllText( entry.ManifestPath ) );
			return CitizenRetargetPaths.DecodeExternalPath( manifest.SourceFile ).Trim().Trim( '"' );
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string NormalizeSourcePathForComparison( string path )
	{
		var decoded = CitizenRetargetPaths.DecodeExternalPath( path ).Trim().Trim( '"' );
		if ( string.IsNullOrWhiteSpace( decoded ) )
			return string.Empty;

		try
		{
			return Path.GetFullPath( decoded );
		}
		catch
		{
			return decoded;
		}
	}

	private static string NormalizeAssetPathForComparison( string? path )
	{
		var decoded = CitizenRetargetPaths.DecodeExternalPath( path ?? string.Empty )
			.Replace( '\\', '/' )
			.Trim();
		if ( string.IsNullOrWhiteSpace( decoded ) )
			return string.Empty;

		if ( decoded.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
			decoded = decoded["Assets/".Length..];

		return decoded.TrimStart( '/' );
	}

	private static bool IsCompletedRun( RetargetRunHistoryEntry entry )
		=> entry.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase );

	private string BuildSequenceNameForClip( RetargetClipDescriptor clip )
		=> RetargetSequenceNames.Build( _job.SequencePrefix, clip.DisplayName );

	private void RefreshClipList()
	{
		var filter = _clipFilter?.Text?.Trim() ?? string.Empty;
		var visible = string.IsNullOrWhiteSpace( filter )
			? _allClips
			: _allClips.Where( clip => ClipMatchesFilter( clip, filter ) ).ToList();
		_clipList.SetItems( visible );

		if ( _job.SelectedClipNames is null || _job.SelectedClipNames.Count == 0 )
			return;

		foreach ( var clip in visible.Where( clip => _job.SelectedClipNames.Contains( clip.DisplayName, StringComparer.OrdinalIgnoreCase ) ) )
		{
			_clipList.SelectItem( clip, true, true );
		}

		RefreshClipSummary();
	}

	private int CountVisibleClips( string? filter )
	{
		var normalizedFilter = filter?.Trim() ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( normalizedFilter ) )
			return _allClips.Count;

		return _allClips.Count( clip => ClipMatchesFilter( clip, normalizedFilter ) );
	}

	private static bool ClipMatchesFilter( RetargetClipDescriptor clip, string filter )
	{
		return clip.DisplayName.Contains( filter, StringComparison.OrdinalIgnoreCase )
			|| clip.SourceName.Contains( filter, StringComparison.OrdinalIgnoreCase );
	}

	private void RefreshSourceBoneList()
	{
		var bones = _inspection?.Audit.Bones ?? new List<NativeAuditBoneInfo>();
		var filter = _boneFilter?.Text?.Trim() ?? string.Empty;
		var visible = string.IsNullOrWhiteSpace( filter )
			? bones
			: bones.Where( bone => bone.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) || bone.ParentName.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToList();
		_sourceBoneList.SetItems( visible.OrderBy( bone => bone.Name, StringComparer.OrdinalIgnoreCase ).ToList() );
	}

	private void RefreshMappingList()
	{
		var currentFilter = _mappingFilter?.CurrentText ?? "All";
		var search = _mappingSearch?.Text?.Trim() ?? string.Empty;
		var visible = _mappingState.Where( slot => currentFilter switch
		{
			"Needs Attention" => slot.Enabled && string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ),
			"Manual Overrides" => slot.IsManualOverride,
			"Auto Mapped" => slot.Enabled && !slot.IsManualOverride && !string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ),
			"Required Only" => slot.Required,
			_ => true
		} ).Where( slot =>
			string.IsNullOrWhiteSpace( search )
			|| slot.SlotId.Contains( search, StringComparison.OrdinalIgnoreCase )
			|| slot.TargetBone.Contains( search, StringComparison.OrdinalIgnoreCase )
			|| slot.EffectiveSourceBone.Contains( search, StringComparison.OrdinalIgnoreCase )
			|| slot.AutoSourceBone.Contains( search, StringComparison.OrdinalIgnoreCase ) ).ToList();
		_mappingList.SetItems( visible );
		if ( _selectedSlot is not null )
		{
			var matchingSlot = visible.FirstOrDefault( slot => slot.SlotId.Equals( _selectedSlot.SlotId, StringComparison.OrdinalIgnoreCase ) );
			if ( matchingSlot is not null )
			{
				_selectedSlot = matchingSlot;
				_mappingList.SelectItem( matchingSlot, true, true );
			}
			else
			{
				_selectedSlot = null;
			}
		}

		RefreshMappingEditorState();
	}

	private void RefreshQueueList()
	{
		_queueList.SetItems( _queueItems.ToList() );
		if ( _homeQueueList is not null )
			_homeQueueList.SetItems( _queueItems.ToList() );
		if ( _queueSummaryLabel is not null )
			_queueSummaryLabel.Text = BuildQueueSummary();
		if ( _homeQueueSummaryLabel is not null )
			_homeQueueSummaryLabel.Text = BuildQueueSummary();
		RefreshHomeStatusStrip();
	}

	private void LoadTargetPresetInventory()
	{
		if ( _targetPresetList is null )
			return;

		_targetPresets.Clear();
		_targetPresets.AddRange( DiscoverTargetPresets() );
		_targetPresetList.SetItems( _targetPresets.ToList() );

		var currentPreset = _targetPresets.FirstOrDefault( preset =>
			preset.TargetVmdlPath.Equals( _job.TargetVmdlPath, StringComparison.OrdinalIgnoreCase ) );
		if ( currentPreset is not null )
			_targetPresetList.SelectItem( currentPreset, true, true );
	}

	private void ClearTargetPresetInventory()
	{
		_targetPresets.Clear();
		if ( _targetPresetList is not null )
			_targetPresetList.SetItems( new List<RetargetTargetPresetRef>() );
	}

	private void RefreshTargetAnimationList()
	{
		if ( _targetAnimationList is null || _builtInTargetAnimationList is null )
			return;

		var importedTargets = BuildImportedTargetAnimationEntries();
		var builtInTargets = (_showBuiltInTargetAnimations || _builtInTargetAnimationsLoaded)
			? BuildBuiltInTargetAnimationEntries( importedTargets )
			: new List<RetargetTargetAnimationEntry>();
		_targetAnimationList.SetItems( importedTargets );
		_builtInTargetAnimationList.SetItems( builtInTargets );
		_targetAnimationList.UpdateIfDirty();
		_builtInTargetAnimationList.UpdateIfDirty();
		SyncTargetAnimationSelection(importedTargets, builtInTargets);
		if ( _targetSummaryLabel is not null )
			_targetSummaryLabel.Text = BuildTargetAnimationSummary( importedTargets, builtInTargets );
		if ( _builtInTargetSummaryLabel is not null )
			_builtInTargetSummaryLabel.Text = BuildBuiltInTargetAnimationSummary( builtInTargets );
		SetBuiltInTargetAnimationVisibility( _showBuiltInTargetAnimations );
	}

	private void SyncTargetAnimationSelection(
		IReadOnlyList<RetargetTargetAnimationEntry> importedTargets,
		IReadOnlyList<RetargetTargetAnimationEntry> builtInTargets )
	{
		RetargetTargetAnimationEntry? preferredImported = null;
		RetargetTargetAnimationEntry? preferredBuiltIn = null;

		if ( _selectedTargetAnimation is not null )
		{
			preferredImported = importedTargets.FirstOrDefault( target =>
				target.IsImported == _selectedTargetAnimation.IsImported
				&& target.SequenceName.Equals( _selectedTargetAnimation.SequenceName, StringComparison.OrdinalIgnoreCase ) );
			preferredBuiltIn = builtInTargets.FirstOrDefault( target =>
				target.IsImported == _selectedTargetAnimation.IsImported
				&& target.SequenceName.Equals( _selectedTargetAnimation.SequenceName, StringComparison.OrdinalIgnoreCase ) );
		}

		if ( preferredImported is null )
		{
			var activeResultSequence = GetActivePreviewResult()?.SequenceName ?? string.Empty;
			if ( !string.IsNullOrWhiteSpace( activeResultSequence ) )
			{
				preferredImported = importedTargets.FirstOrDefault( target =>
					target.SequenceName.Equals( activeResultSequence, StringComparison.OrdinalIgnoreCase ) );
			}
		}

		if ( preferredImported is not null )
		{
			_selectedTargetAnimation = preferredImported;
			_targetAnimationList.SelectItem( preferredImported, true, true );
			return;
		}

		if ( preferredBuiltIn is not null )
		{
			_selectedTargetAnimation = preferredBuiltIn;
			_builtInTargetAnimationList.SelectItem( preferredBuiltIn, true, true );
			return;
		}

		_selectedTargetAnimation = null;
		_targetAnimationList.UnselectAll();
		_builtInTargetAnimationList.UnselectAll();
	}

	private List<RetargetTargetAnimationEntry> BuildImportedTargetAnimationEntries()
	{
		var previewVmdlPath = _pipeline.ResolvePreviewableTargetVmdlPath( _job );
		return _pipeline.GetTargetAnimationSources( _job )
			.Select( target => new RetargetTargetAnimationEntry
			{
				SequenceName = target.SequenceName,
				Looping = target.Looping,
				IsImported = true,
				IsReadOnly = false,
				VmdlResourcePath = previewVmdlPath,
				SourceResourcePath = target.ResourcePath
			} )
			.ToList();
	}

	private List<RetargetTargetAnimationEntry> BuildBuiltInTargetAnimationEntries( IReadOnlyList<RetargetTargetAnimationEntry> importedTargets )
	{
		var previewVmdlPath = _pipeline.ResolvePreviewableTargetVmdlPath( _job );
		var importedNames = importedTargets
			.Select( target => target.SequenceName )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		return _pipeline.GetModelAnimationNames( previewVmdlPath )
			.Where( sequenceName => !importedNames.Contains( sequenceName ) )
			.Select( sequenceName => new RetargetTargetAnimationEntry
			{
				SequenceName = sequenceName,
				Looping = sequenceName.Contains( "loop", StringComparison.OrdinalIgnoreCase ),
				IsImported = false,
				IsReadOnly = true,
				VmdlResourcePath = previewVmdlPath
			} )
			.OrderBy( target => target.SequenceName, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	private List<RetargetTargetPresetRef> DiscoverTargetPresets()
	{
		var discovered = new List<RetargetTargetPresetRef>();
		var citizenCustomRoot = CitizenRetargetPaths.GetAssetAbsolutePath( "models/citizen_custom" );
		if ( Directory.Exists( citizenCustomRoot ) )
		{
			foreach ( var vmdlPath in Directory.EnumerateFiles( citizenCustomRoot, "citizen_*.vmdl", SearchOption.TopDirectoryOnly ) )
			{
				var resourcePath = CitizenRetargetPaths.GetAssetResourcePath( vmdlPath );
				var fileName = Path.GetFileNameWithoutExtension( vmdlPath );
				var key = fileName.StartsWith( "citizen_", StringComparison.OrdinalIgnoreCase )
					? fileName["citizen_".Length..]
					: fileName;
				discovered.Add( BuildTargetPresetFromKey( key, resourcePath, true ) );
			}
		}

		var currentKey = DeriveTargetKeyFromJob();
		if ( !discovered.Any( preset => preset.Key.Equals( currentKey, StringComparison.OrdinalIgnoreCase ) ) )
			discovered.Add( BuildTargetPresetFromKey( currentKey, _job.TargetVmdlPath, File.Exists( CitizenRetargetPaths.GetAssetAbsolutePath( _job.TargetVmdlPath ) ) ) );

		return discovered
			.OrderByDescending( preset => preset.TargetVmdlPath.Equals( _job.TargetVmdlPath, StringComparison.OrdinalIgnoreCase ) )
			.ThenBy( preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	private RetargetTargetPresetRef BuildTargetPresetFromKey( string rawKey, string targetVmdlPath, bool existsOnDisk )
	{
		var key = SanitizeTargetKey( rawKey );
		return new RetargetTargetPresetRef
		{
			Key = key,
			DisplayName = key.Equals( DeriveTargetKeyFromJob(), StringComparison.OrdinalIgnoreCase ) ? $"{key} (current)" : key,
			TargetVmdlPath = string.IsNullOrWhiteSpace( targetVmdlPath ) ? $"models/citizen_custom/citizen_{key}.vmdl" : targetVmdlPath,
			OutputAnimationFolder = $"models/citizen_custom/animations/{key}",
			SequencePrefix = $"{key}_",
			ExistsOnDisk = existsOnDisk
		};
	}

	private string DeriveTargetKeyFromJob()
	{
		var fileName = Path.GetFileNameWithoutExtension( _job.TargetVmdlPath ?? string.Empty );
		if ( fileName.StartsWith( "citizen_", StringComparison.OrdinalIgnoreCase ) )
			fileName = fileName["citizen_".Length..];
		return SanitizeTargetKey( string.IsNullOrWhiteSpace( fileName ) ? _activeSourceLibrary.DisplayName : fileName );
	}

	private static string SanitizeTargetKey( string? value )
	{
		var safe = new string( (value ?? string.Empty)
			.Select( character => char.IsLetterOrDigit( character ) ? char.ToLowerInvariant( character ) : '_' )
			.ToArray() )
			.Trim( '_' );
		return string.IsNullOrWhiteSpace( safe ) ? "citizen_custom" : safe;
	}

	private void RefreshHistoryList()
	{
		var history = (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>()).ToList();
		_historyList.SetItems( history );
		if ( string.IsNullOrWhiteSpace( _session.SelectedHistoryRunId ) )
			return;

		var selectedEntry = history.FirstOrDefault( entry => entry.RunId.Equals( _session.SelectedHistoryRunId, StringComparison.OrdinalIgnoreCase ) );
		if ( selectedEntry is not null )
		{
			_suppressHistorySelection = true;
			try
			{
				_historyList.SelectItem( selectedEntry, true, true );
			}
			finally
			{
				_suppressHistorySelection = false;
			}
		}
	}

	private void RefreshResultList()
	{
		RefreshTargetAnimationList();
	}

	private void RefreshRunsInspectionState()
	{
		var selectedEntry = GetSelectedHistoryEntry();
		var inspectedResult = GetRunsInspectionResult();
		if ( _artifactSummaryLabel is not null )
			_artifactSummaryLabel.Text = inspectedResult is null && selectedEntry is null
				? "Select a run to inspect its artifacts, or use the latest run as a fallback."
				: BuildRunInspectionSummary( selectedEntry, inspectedResult );
		if ( _runLogOutput is not null )
			_runLogOutput.PlainText = BuildRunLogText( selectedEntry, inspectedResult );
	}

	private void RefreshResultSurfaces()
	{
		var activeResult = GetActivePreviewResult();
		var targetPreviewAnimation = GetActiveTargetPreviewAnimation();
		var selectedClip = GetSingleSelectedClip();

		if ( activeResult is null )
		{
			if ( targetPreviewAnimation is not null && !string.IsNullOrWhiteSpace( targetPreviewAnimation.VmdlResourcePath ) )
			{
				_livePreview.LoadAnimation( targetPreviewAnimation.VmdlResourcePath, targetPreviewAnimation.SequenceName );
			}
			else
			{
				_livePreview.ClearAnimation( BuildEmptyPreviewMessage( selectedClip ) );
			}
			RefreshRunsInspectionState();
			RefreshHomeStatusStrip();
			UpdateButtons();
			return;
		}

		var hasImportedAnimation =
			activeResult.Manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase )
			&& !string.IsNullOrWhiteSpace( activeResult.ArtifactLinks.ImportedAnimationPath )
			&& File.Exists( activeResult.ArtifactLinks.ImportedAnimationPath )
			&& !string.IsNullOrWhiteSpace( activeResult.VmdlResourcePath );

		if ( hasImportedAnimation )
			_livePreview.LoadAnimation( activeResult.VmdlResourcePath, activeResult.SequenceName );
		else
			_livePreview.ClearAnimation( BuildUnavailableResultPreviewMessage( activeResult ) );
		RefreshRunsInspectionState();
		RefreshHomeStatusStrip();
		UpdateButtons();
	}

	private RetargetRunManifest GetDiagnosticsManifest( RetargetRunHistoryEntry? entry, RetargetImportResult? result )
	{
		return result?.Manifest
			?? (entry is null ? null : LoadManifestFromHistoryEntry( entry ))
			?? (entry is null ? new RetargetRunManifest() : BuildFallbackManifestFromHistoryEntry( entry ));
	}

	private static RetargetStageManifestEntry? FindFirstFailedStage( RetargetRunManifest manifest )
	{
		return manifest.Stages.FirstOrDefault( stage => stage.Status.Equals( "failed", StringComparison.OrdinalIgnoreCase ) );
	}

	private static string FormatStageName( string stageId )
	{
		if ( string.IsNullOrWhiteSpace( stageId ) )
			return "unknown stage";

		var readable = stageId.Replace( '_', ' ' ).Replace( '-', ' ' ).Trim();
		return string.IsNullOrWhiteSpace( readable ) ? stageId : readable;
	}

	private static string TruncateDiagnosticText( string? text, int maxLength = 180 )
	{
		var value = (text ?? string.Empty).Trim();
		if ( value.Length <= maxLength )
			return value;

		return value[..Math.Max( 0, maxLength - 1 )].TrimEnd() + "...";
	}

	private static string BuildIssueListPreview( IReadOnlyList<string> values, int take = 4 )
	{
		if ( values.Count == 0 )
			return string.Empty;

		var preview = values.Take( take ).ToList();
		var suffix = values.Count > take ? $" (+{values.Count - take} more)" : string.Empty;
		return string.Join( ", ", preview ) + suffix;
	}

	private static Color GetRunStatusColor( string? status )
	{
		var value = status ?? string.Empty;
		if ( value.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
			return Theme.Green;
		if ( value.Contains( "failed", StringComparison.OrdinalIgnoreCase ) )
			return Theme.Yellow;
		return Theme.Blue;
	}

	private static string FormatHistoryStatus( string? status )
	{
		var value = status ?? string.Empty;
		if ( value.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
			return "success";
		if ( value.Equals( "import_failed", StringComparison.OrdinalIgnoreCase ) )
			return "import failed";
		if ( value.Contains( "failed", StringComparison.OrdinalIgnoreCase ) )
			return "failed";
		return string.IsNullOrWhiteSpace( value ) ? "history" : value.Replace( '_', ' ' );
	}

	private static void AppendLogSection( List<string> lines, string title, bool addLeadingSpacing = true )
	{
		if ( addLeadingSpacing && lines.Count > 0 )
			lines.Add( string.Empty );

		lines.Add( title );
		lines.Add( new string( '-', title.Length ) );
	}

	private bool HasMaterializedImportedResult( RetargetRunHistoryEntry? entry )
	{
		if ( entry is null )
			return false;

		var importedAnimationPath = CitizenRetargetPaths.DecodeExternalPath( entry.ImportedAssetPath ?? string.Empty );
		var generatedModelPath = string.IsNullOrWhiteSpace( entry.TargetVmdlPath )
			? string.Empty
			: CitizenRetargetPaths.GetAssetAbsolutePath( entry.TargetVmdlPath );
		return !string.IsNullOrWhiteSpace( importedAnimationPath )
			&& File.Exists( importedAnimationPath )
			&& !string.IsNullOrWhiteSpace( generatedModelPath )
			&& File.Exists( generatedModelPath );
	}

	private string BuildRunHealthLabel( RetargetDiagnosticSummary diagnostics, RetargetRunHistoryEntry? entry, RetargetImportResult? result )
	{
		if ( result is null && entry is null )
		{
			return diagnostics.MissingRequiredSlots.Count > 0
				? "Retarget is not ready yet because required mapping slots are still missing."
				: "No run selected yet.";
		}

		var manifest = GetDiagnosticsManifest( entry, result );
		var sequenceName = entry?.SequenceName ?? result?.SequenceName ?? manifest.RetargetedActionName ?? "selected run";
		var failedStage = FindFirstFailedStage( manifest );
		if ( result is not null && !string.IsNullOrWhiteSpace( result.PostImportError ) )
			return $"Failed during target import for '{sequenceName}'.";
		if ( failedStage is not null )
			return $"Failed during stage '{FormatStageName( failedStage.StageId )}' for '{sequenceName}'.";
		if ( manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			return $"Run '{sequenceName}' completed with motion validation failures.";
		if ( manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
		{
			var materialized = result is not null ? HasMaterializedImportedResult( result ) : HasMaterializedImportedResult( entry );
			return materialized
				? $"Run '{sequenceName}' completed successfully."
				: $"Run '{sequenceName}' completed, but the target animation is not materialized on disk.";
		}

		return string.IsNullOrWhiteSpace( manifest.Status )
			? $"Run '{sequenceName}' is selected for inspection."
			: $"Run '{sequenceName}' is currently '{manifest.Status}'.";
	}

	private string BuildRunNextStepLabel( RetargetDiagnosticSummary diagnostics, RetargetRunHistoryEntry? entry, RetargetImportResult? result )
	{
		if ( result is null && entry is null )
		{
			return diagnostics.MissingRequiredSlots.Count > 0
				? "Next step: fix the missing required slots in Mapping, then run the queue again."
				: "Next step: select a run from History, or retarget a clip from Home.";
		}

		var manifest = GetDiagnosticsManifest( entry, result );
		var failedStage = FindFirstFailedStage( manifest );
		if ( result is not null && !string.IsNullOrWhiteSpace( result.PostImportError ) )
			return "Next step: open Run Log first. If Blender export succeeded, inspect Raw Export and Manifest after that.";
		if ( failedStage is not null )
			return $"Next step: open Run Log and inspect the failed '{FormatStageName( failedStage.StageId )}' stage.";
		if ( manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			return "Next step: review the motion failures in Run Log, then verify pose preset, root motion, and mapping.";
		if ( diagnostics.MissingRequiredSlots.Count > 0 )
			return "Next step: complete the missing required mapping slots before trusting this retarget setup.";
		if ( manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
			return "Next step: preview the animation or open the generated model. Use Run Log only if the result still looks wrong.";

		return "Next step: open Run Log for the detailed backend and stage trace.";
	}

	private string BuildIssuesLabel( RetargetDiagnosticSummary diagnostics, RetargetRunHistoryEntry? entry, RetargetImportResult? result )
	{
		var manifest = GetDiagnosticsManifest( entry, result );
		var blockingIssues = new List<string>();
		var warningSummaries = new List<string>();
		if ( manifest.BackendWarnings.Count > 0 )
			warningSummaries.Add( $"Backend warnings: {manifest.BackendWarnings.Count}" );
		if ( manifest.Warnings.Count > 0 )
			warningSummaries.Add( $"Run warnings: {manifest.Warnings.Count}" );
		if ( manifest.MotionTrajectoryAnalysis.Warnings.Count > 0 )
			warningSummaries.Add( $"Motion warnings: {manifest.MotionTrajectoryAnalysis.Warnings.Count}" );
		if ( diagnostics.DisabledFingerSlots.Count > 0 )
			warningSummaries.Add( $"Disabled finger slots: {diagnostics.DisabledFingerSlots.Count}" );
		var warningCount = warningSummaries.Count;

		if ( diagnostics.MissingRequiredSlots.Count > 0 )
			blockingIssues.Add( $"Missing required slots: {BuildIssueListPreview( diagnostics.MissingRequiredSlots, 6 )}" );
		if ( diagnostics.DuplicateSourceBones.Count > 0 )
			blockingIssues.Add( $"Duplicate source assignments: {BuildIssueListPreview( diagnostics.DuplicateSourceBones, 3 )}" );
		if ( diagnostics.DuplicateTargetBones.Count > 0 )
			blockingIssues.Add( $"Duplicate target assignments: {BuildIssueListPreview( diagnostics.DuplicateTargetBones, 3 )}" );
		if ( result is not null && !string.IsNullOrWhiteSpace( result.PostImportError ) )
			blockingIssues.Add( $"Target import failed: {TruncateDiagnosticText( result.PostImportError, 220 )}" );
		if ( manifest.UnmappedRequiredSlots.Count > 0 )
			blockingIssues.Add( $"Manifest unmapped required slots: {BuildIssueListPreview( manifest.UnmappedRequiredSlots, 6 )}" );
		if ( manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			blockingIssues.Add( $"Motion failures: {BuildIssueListPreview( manifest.MotionTrajectoryAnalysis.Failures, 3 )}" );

		var failedStages = manifest.Stages
			.Where( stage => stage.Status.Equals( "failed", StringComparison.OrdinalIgnoreCase ) )
			.ToList();
		foreach ( var stage in failedStages )
		{
			var stageError = string.IsNullOrWhiteSpace( stage.Error ) ? "No error text was recorded." : stage.Error;
			blockingIssues.Add( $"Stage '{FormatStageName( stage.StageId )}' failed: {TruncateDiagnosticText( stageError, 220 )}" );
		}

		if ( blockingIssues.Count == 0 )
		{
			if ( warningCount == 0 )
				return "No blocking issues were detected for this run.";

			var warningLines = new List<string>
			{
				"No blocking issues were detected.",
				string.Empty,
				"Warnings recorded:"
			};
			warningLines.AddRange( warningSummaries.Select( summary => $"- {summary}" ) );
			warningLines.Add( string.Empty );
			warningLines.Add( "Open Run Log if you need the full warning list." );
			return string.Join( Environment.NewLine, warningLines );
		}

		var lines = new List<string>
		{
			"Blocking issues:"
		};
		lines.AddRange( blockingIssues.Select( issue => $"- {issue}" ) );
		if ( warningCount > 0 )
		{
			lines.Add( string.Empty );
			lines.Add( "Warnings recorded:" );
			lines.AddRange( warningSummaries.Select( summary => $"- {summary}" ) );
			lines.Add( string.Empty );
			lines.Add( "Open Run Log for the full warning list." );
		}

		return string.Join( Environment.NewLine, lines );
	}

	private void UpdateDiagnostics()
	{
		var diagnostics = _pipeline.BuildDiagnostics( _mappingState );
		var selectedEntry = GetSelectedHistoryEntry();
		var diagnosticsResult = GetRunsInspectionResult();
		RefreshEnvironmentDiagnostics();
		if ( _runHealthLabel is not null )
			_runHealthLabel.Text = BuildRunHealthLabel( diagnostics, selectedEntry, diagnosticsResult );
		if ( _runNextStepLabel is not null )
			_runNextStepLabel.Text = BuildRunNextStepLabel( diagnostics, selectedEntry, diagnosticsResult );
		if ( _issuesLabel is not null )
			_issuesLabel.Text = BuildIssuesLabel( diagnostics, selectedEntry, diagnosticsResult );
		_summaryLabel.Text = diagnostics.MissingRequiredSlots.Count == 0
			? BuildRunSummary()
			: $"Missing required slots: {string.Join( ", ", diagnostics.MissingRequiredSlots )}";
		RefreshHomeStatusStrip();
	}

	private string BuildRunSummary()
	{
		if ( _latestResult is null )
			return "Bone-map coverage is ready. Queue a clip when you want the first retarget run.";

		if ( !string.IsNullOrWhiteSpace( _latestResult.PostImportError ) )
			return $"Latest run exported from Blender, but import failed: {_latestResult.PostImportError}";

		if ( _latestResult.Manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			return $"Latest run failed {_latestResult.Manifest.MotionTrajectoryAnalysis.Failures.Count} motion gate(s). See Runs for details.";

		if ( _latestResult.Manifest.BackendWarnings.Count > 0 )
			return $"Latest run completed with {_latestResult.Manifest.BackendWarnings.Count} backend warning(s). See Runs for details.";

		if ( _latestResult.Manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
			return $"Latest run '{_latestResult.Manifest.RunId}' completed. Sequence '{_latestResult.SequenceName}' is now in the generated Citizen model.";

		return $"Latest run '{_latestResult.Manifest.RunId}' is {_latestResult.Manifest.Status}.";
	}

	private void RefreshHomeStatusStrip()
	{
		if ( _resultMetaLabel is not null )
			_resultMetaLabel.Text = BuildResultMetadataLabel();
	}

	private string BuildTargetAnimationSummary( IReadOnlyList<RetargetTargetAnimationEntry> importedTargets, IReadOnlyList<RetargetTargetAnimationEntry> builtInTargets )
	{
		var selectedClip = GetSingleSelectedClip();
		var activeResult = GetActivePreviewResult();
		if ( importedTargets.Count == 0 )
			return builtInTargets.Count == 0
				? "No imported retarget animations yet."
				: $"No imported retarget animations yet. Base target exposes {builtInTargets.Count} built-in animation(s).";

		var loopingCount = importedTargets.Count( target => target.Looping );
		if ( selectedClip is not null && activeResult is not null && !string.IsNullOrWhiteSpace( activeResult.SequenceName ) )
			return $"Imported target animations: {importedTargets.Count} total | {loopingCount} looping | '{selectedClip.DisplayName}' -> '{activeResult.SequenceName}'";

		return $"Imported target animations: {importedTargets.Count} total | {loopingCount} looping";
	}

	private string BuildBuiltInTargetAnimationSummary( IReadOnlyList<RetargetTargetAnimationEntry> builtInTargets )
	{
		if ( builtInTargets.Count == 0 )
			return "No built-in target animations were discovered.";

		var loopingCount = builtInTargets.Count( target => target.Looping );
		return $"Built-in target animations: {builtInTargets.Count} read-only | {loopingCount} looping";
	}

	private string BuildResultMetadataLabel()
	{
		var selectedResultEntry = FindHistoryEntryByRunId( _session.CompareSelection.SelectedResultRunId );
		var activeResult = GetActivePreviewResult();
		var selectedTargetAnimation = GetActiveTargetPreviewAnimation();
		if ( selectedResultEntry is null && activeResult is null && selectedTargetAnimation is null )
			return "No active target result yet. Pick a target animation or run a retarget.";

		var sequenceName = selectedResultEntry?.SequenceName ?? activeResult?.SequenceName ?? selectedTargetAnimation?.SequenceName ?? "unknown";
		var hasImportedResult = HasMaterializedImportedResult( activeResult );
		if ( activeResult is not null && !string.IsNullOrWhiteSpace( activeResult.PostImportError ) )
			return $"Result '{sequenceName}' exists in run history, but import into the current target failed. Check Diagnostics -> Run Log.";

		if ( selectedTargetAnimation?.IsReadOnly == true )
			return $"Previewing built-in target animation '{sequenceName}'.";

		if ( hasImportedResult || selectedTargetAnimation?.IsImported == true )
			return $"Previewing: {sequenceName}";

		if ( selectedResultEntry is not null )
			return $"Result '{sequenceName}' exists in history, but it is not currently materialized on this target.";

		return $"Previewing: {sequenceName}";
	}

	private void OnTargetAnimationSelectionChanged( RetargetTargetAnimationEntry target )
	{
		_selectedTargetAnimation = target;
		if ( target.IsReadOnly )
		{
			_session.CompareSelection.SelectedResultRunId = string.Empty;
			_selectedCompareResult = null;
			RefreshResultList();
			RefreshResultSurfaces();
			UpdateButtons();
			SetStatus( $"Built-in target animation '{target.SequenceName}' is selected in read-only mode." );
			return;
		}

		var entry = (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>())
			.Where( historyEntry => MatchesActiveTargetWorkspace( historyEntry ) )
			.Where( historyEntry => MatchesActiveSourceLibrary( historyEntry ) )
			.Where( historyEntry => historyEntry.SequenceName.Equals( target.SequenceName, StringComparison.OrdinalIgnoreCase ) )
			.OrderByDescending( historyEntry => historyEntry.CreatedUtc, StringComparer.OrdinalIgnoreCase )
			.FirstOrDefault();

		if ( entry is null )
		{
			_session.CompareSelection.SelectedResultRunId = string.Empty;
			_selectedCompareResult = null;
			RefreshResultList();
			RefreshResultSurfaces();
			UpdateButtons();
			SetStatus( target.IsImported
				? $"Target animation '{target.SequenceName}' is on the model, but no matching run history entry was found."
				: $"Built-in target animation '{target.SequenceName}' is selected in read-only mode." );
			return;
		}

		var clip = FindClipForHistoryEntry( entry );
		if ( clip is not null )
		{
			_job.SelectedClipNames = new List<string> { clip.DisplayName };
			_pipeline.SaveJob( _job );
			RefreshClipList();
		}

		_session.CompareSelection.SelectedSourceLibraryId = MatchesActiveSourceLibrary( entry ) ? _activeSourceLibrary.LibraryId : _session.CompareSelection.SelectedSourceLibraryId;
		_session.CompareSelection.SelectedSourceClipName = entry.ClipName ?? _session.CompareSelection.SelectedSourceClipName;
		_session.CompareSelection.SelectedResultRunId = entry.RunId ?? string.Empty;
		ResolveCompareResultSelection();
		RefreshResultList();
		RefreshResultSurfaces();
		UpdateButtons();
		SetStatus( $"Target animation '{target.SequenceName}' is now the active result selection." );
	}

	private void UpdateButtons()
	{
		var sourceBusy = HasActiveBackgroundJob( "source" );
		var targetBusy = HasActiveBackgroundJob( "target" );
		var queueBusy = HasActiveBackgroundJob( "queue" );
		var environmentBusy = HasActiveBackgroundJob( "environment" );
		var sourceBlocking = HasBlockingBackgroundJob( "source" );
		var targetBlocking = HasBlockingBackgroundJob( "target" );
		var queueBlocking = HasBlockingBackgroundJob( "queue" );
		var environmentBlocking = HasBlockingBackgroundJob( "environment" );
		var anyBusy = sourceBlocking || targetBlocking || queueBlocking || environmentBlocking || _jobInFlight;
		var hasClipSelection = _clipList?.SelectedItems.OfType<RetargetClipDescriptor>().Any() ?? false;
		var hasFacingPreview = _inspection is not null && _inspection.Audit.Bones.Count > 0;
		var facingManualControlsEnabled = hasFacingPreview && !anyBusy && IsSourceFacingManualOverrideEnabled();
		var hasSelectedSlot = _selectedSlot is not null;
		var hasSelectedBone = _selectedSourceBone is not null;
		var currentArtifactResult = GetCurrentArtifactResult();
		var currentRunDirectory = currentArtifactResult?.ArtifactLinks.RunDirectoryPath ?? string.Empty;
		var currentManifestPath = currentArtifactResult?.ArtifactLinks.ManifestPath ?? string.Empty;
		var currentRawExportPath = currentArtifactResult?.ArtifactLinks.ExportPath ?? string.Empty;
		var currentImportedAnimationPath = currentArtifactResult?.ArtifactLinks.ImportedAnimationPath ?? string.Empty;
		var currentGeneratedModelPath = currentArtifactResult?.ArtifactLinks.VmdlPath ?? string.Empty;
		var currentRunLogPath = currentArtifactResult?.ArtifactLinks.RunLogPath ?? string.Empty;
		var activePreviewResult = GetActivePreviewResult();
		var activePreviewGeneratedModelPath = activePreviewResult?.ArtifactLinks.VmdlPath ?? string.Empty;
		var currentTargetModelPath = CitizenRetargetPaths.GetAssetAbsolutePath( _job.TargetVmdlPath );
		_scanButton.Enabled = !anyBusy;
		if ( _browseSourceButton is not null )
			_browseSourceButton.Enabled = !_queueRunning && !anyBusy;
		if ( _beginOpenExistingTargetButton is not null )
			_beginOpenExistingTargetButton.Enabled = !_queueRunning && !anyBusy;
		if ( _beginCreateTargetButton is not null )
			_beginCreateTargetButton.Enabled = !_queueRunning && !anyBusy;
		if ( _openExistingTargetButton is not null )
			_openExistingTargetButton.Enabled = (_targetPresetList?.SelectedItems.OfType<RetargetTargetPresetRef>().Any() ?? false) && !_queueRunning && !anyBusy;
		if ( _createTargetButton is not null )
			_createTargetButton.Enabled = !string.IsNullOrWhiteSpace( SanitizeTargetKey( _newTargetName?.Text ) ) && !_queueRunning && !anyBusy;
		if ( _toggleBuiltInTargetAnimationsButton is not null )
			_toggleBuiltInTargetAnimationsButton.Enabled = !targetBusy && !queueBusy && !_jobInFlight;
		if ( _addClipToQueueButton is not null )
			_addClipToQueueButton.Enabled = hasClipSelection && !_queueRunning && !sourceBusy && !queueBusy && !_jobInFlight;
		if ( _sourceFacingManualOverrideCheckbox is not null )
			_sourceFacingManualOverrideCheckbox.Enabled = hasFacingPreview && !anyBusy;
		if ( _sourceFacingResetButton is not null )
			_sourceFacingResetButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingRotate180Button is not null )
			_sourceFacingRotate180Button.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingRotateLeftButton is not null )
			_sourceFacingRotateLeftButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingRotateRightButton is not null )
			_sourceFacingRotateRightButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingTiltForwardButton is not null )
			_sourceFacingTiltForwardButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingTiltBackButton is not null )
			_sourceFacingTiltBackButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingRollLeftButton is not null )
			_sourceFacingRollLeftButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingRollRightButton is not null )
			_sourceFacingRollRightButton.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingTiltField is not null )
			_sourceFacingTiltField.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingRollField is not null )
			_sourceFacingRollField.Enabled = facingManualControlsEnabled;
		if ( _sourceFacingFacingField is not null )
			_sourceFacingFacingField.Enabled = facingManualControlsEnabled;
		if ( _editCompensationDataButton is not null )
			_editCompensationDataButton.Enabled = !_jobInFlight;
		_retryFailedQueueButton.Enabled = _queueItems.Any( item => item.Status == "failed" ) && !_queueRunning && !anyBusy;
		_clearFinishedQueueButton.Enabled = _queueItems.Any( item => item.Status == "completed" || item.Status == "failed" ) && !_queueRunning && !anyBusy;
		_clearQueueButton.Enabled = _queueItems.Count > 0;
		if ( _scanEnvironmentButton is not null )
			_scanEnvironmentButton.Enabled = !environmentBusy;
		if ( _browseEnvironmentBackendButton is not null )
			_browseEnvironmentBackendButton.Enabled = !environmentBusy;
		if ( _browseBlenderButton is not null )
			_browseBlenderButton.Enabled = !environmentBusy;
		if ( _openEnvironmentBackendButton is not null )
		{
			var configuredBackendPath = _environmentReport?.BackendRootPath
				?? RetargetSetupResolver.Resolve( _toolSettings ).BackendRootPath;
			_openEnvironmentBackendButton.Enabled = !string.IsNullOrWhiteSpace( configuredBackendPath )
				&& Directory.Exists( configuredBackendPath );
		}
		if ( _copyEnvironmentDiagnosticsButton is not null )
			_copyEnvironmentDiagnosticsButton.Enabled = !environmentBusy;
		if ( _exportSupportBundleButton is not null )
			_exportSupportBundleButton.Enabled = !environmentBusy;
		if ( _autoScanRokokoAddonButton is not null )
			_autoScanRokokoAddonButton.Enabled = !environmentBusy;
		if ( _browseRokokoAddonButton is not null )
			_browseRokokoAddonButton.Enabled = !environmentBusy;
		if ( _openRokokoAddonButton is not null )
		{
			var hasResolvedRokokoPath = Directory.Exists( ResolveRokokoAddonFolder( ResolveCurrentRokokoAddonPath() ) );
			SetEnvironmentActionButtonPresent( _openRokokoAddonButton, hasResolvedRokokoPath );
			_openRokokoAddonButton.Enabled = !environmentBusy && hasResolvedRokokoPath;
		}
		if ( _clearRokokoAddonButton is not null )
		{
			var hasManualRokokoPath = !string.IsNullOrWhiteSpace( _toolSettings.RokokoAddonPath );
			SetEnvironmentActionButtonPresent( _clearRokokoAddonButton, hasManualRokokoPath, subtle: true );
			_clearRokokoAddonButton.Enabled = !environmentBusy && hasManualRokokoPath;
		}
		if ( _downloadNativeHelperButton is not null )
		{
			var nativeDllExists = File.Exists( GetNativeHelperDllPath() );
			_downloadNativeHelperButton.Text = nativeDllExists ? "Reinstall Helper" : "Download Helper";
			SetEnvironmentActionButtonPresent( _downloadNativeHelperButton, true, primary: !nativeDllExists );
			_downloadNativeHelperButton.Enabled = !environmentBusy;
		}
		if ( _openNativeHelperFolderButton is not null )
		{
			var nativeHelperTargetDirectory = GetNativeHelperTargetDirectory();
			var hasNativeHelperTargetDirectory = Directory.Exists( nativeHelperTargetDirectory );
			SetEnvironmentActionButtonPresent( _openNativeHelperFolderButton, hasNativeHelperTargetDirectory );
			_openNativeHelperFolderButton.Enabled = !environmentBusy && hasNativeHelperTargetDirectory;
		}
		_useHistoryAsCompareButton.Enabled = !string.IsNullOrWhiteSpace( _session.SelectedHistoryRunId ) && !anyBusy;
		_assignBoneButton.Enabled = hasSelectedSlot && hasSelectedBone && !anyBusy;
		_clearSlotButton.Enabled = hasSelectedSlot && !anyBusy;
		_resetSlotButton.Enabled = hasSelectedSlot && !anyBusy;
		_saveMappingButton.Enabled = _mappingState.Count > 0 && !anyBusy;
		_openRunDirButton.Enabled = !string.IsNullOrWhiteSpace( currentRunDirectory )
			&& Directory.Exists( currentRunDirectory );
		_openManifestButton.Enabled = !string.IsNullOrWhiteSpace( currentManifestPath )
			&& File.Exists( currentManifestPath );
		_openRawExportButton.Enabled = !string.IsNullOrWhiteSpace( currentRawExportPath )
			&& File.Exists( currentRawExportPath );
		if ( _openRunLogButton is not null )
		{
			_openRunLogButton.Enabled = !string.IsNullOrWhiteSpace( currentRunLogPath )
				&& File.Exists( currentRunLogPath );
		}
		_openImportedAnimationButton.Enabled = !string.IsNullOrWhiteSpace( currentImportedAnimationPath )
			&& File.Exists( currentImportedAnimationPath );
		_openGeneratedModelButton.Enabled = !string.IsNullOrWhiteSpace( currentGeneratedModelPath )
			&& File.Exists( currentGeneratedModelPath );
		if ( _clearHomeResultButton is not null )
			_clearHomeResultButton.Enabled = activePreviewResult is not null && !anyBusy;
		if ( _inspectHomeResultInRunsButton is not null )
			_inspectHomeResultInRunsButton.Enabled = activePreviewResult is not null && !anyBusy;
		if ( _openHomeGeneratedModelButton is not null )
		{
			_openHomeGeneratedModelButton.Enabled =
				(!string.IsNullOrWhiteSpace( activePreviewGeneratedModelPath ) && File.Exists( activePreviewGeneratedModelPath ))
				|| File.Exists( currentTargetModelPath );
		}
		_runHomeQueueButton.Enabled = _queueItems.Any( item => item.Status == "pending" ) && !_queueRunning && !sourceBusy && !queueBusy && !environmentBusy && !_jobInFlight;
		_removeHomeQueueItemButton.Enabled = (_homeQueueList?.SelectedItems.OfType<RetargetQueueItem>().Any() ?? false) && !_queueRunning && !sourceBusy && !queueBusy && !environmentBusy && !_jobInFlight;
		_clearHomeQueueButton.Enabled = _queueItems.Count > 0;
	}

	private void RefreshClipSummary()
	{
		if ( _clipSummaryLabel is null )
			return;

		if ( _inspection is null )
		{
			_clipSummaryLabel.Text = "Scan a source FBX to load clips, inspect the skeleton, and start a queue.";
			return;
		}

		var clipFilter = _clipFilter?.Text?.Trim() ?? string.Empty;
		var visibleCount = CountVisibleClips( clipFilter );
		var selectedCount = _clipList?.SelectedItems.OfType<RetargetClipDescriptor>().Count() ?? 0;
		_clipSummaryLabel.Text =
			$"Scanned {_allClips.Count} clip(s) and {_inspection.Audit.Bones.Count} bone(s). " +
			$"Visible: {visibleCount}. Selected: {selectedCount}.";
		RefreshHomeStatusStrip();
	}

	private string BuildQueueSummary()
	{
		if ( !string.IsNullOrWhiteSpace( _latestSetupFailureMessage ) )
			return _latestSetupFailureMessage;

		if ( _queueItems.Count == 0 )
			return "No queued clips yet.";

		var pendingCount = _queueItems.Count( item => item.Status == "pending" );
		var runningCount = _queueItems.Count( item => item.Status == "running" );
		var completedCount = _queueItems.Count( item => item.Status == "completed" );
		var failedCount = _queueItems.Count( item => item.Status == "failed" );
		return $"Pending: {pendingCount} | Running: {runningCount} | Completed: {completedCount} | Failed: {failedCount}";
	}

	private string BuildRunInspectionSummary( RetargetRunHistoryEntry? entry, RetargetImportResult? result )
	{
		if ( result is null && entry is null )
			return "Select a run to inspect its artifacts. Start with Run Log when something fails.";

		var runId = entry?.RunId ?? result?.Manifest.RunId ?? "unknown";
		var status = entry?.Status ?? result?.Manifest.Status ?? "unknown";
		var sequenceName = entry?.SequenceName ?? result?.SequenceName ?? "unknown";
		var targetVmdl = entry?.TargetVmdlPath ?? result?.VmdlResourcePath ?? string.Empty;
		var manifestPath = result?.ArtifactLinks.ManifestPath ?? string.Empty;
		var runDirectoryPath = result?.ArtifactLinks.RunDirectoryPath ?? string.Empty;
		var importedAnimationPath = result?.ArtifactLinks.ImportedAnimationPath ?? CitizenRetargetPaths.DecodeExternalPath( entry?.ImportedAssetPath ?? string.Empty );
		var exportPath = result?.ArtifactLinks.ExportPath ?? CitizenRetargetPaths.DecodeExternalPath( entry?.ExportPath ?? string.Empty );
		var generatedModelPath = result?.ArtifactLinks.VmdlPath ?? (!string.IsNullOrWhiteSpace( targetVmdl ) ? CitizenRetargetPaths.GetAssetAbsolutePath( targetVmdl ) : string.Empty);
		var sourceFile = result?.Manifest.SourceFile ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( sourceFile ) && entry is not null )
			sourceFile = TryGetHistoryEntrySourceFile( entry );

		var details = new List<string> { $"Run {runId} | {status} | {sequenceName}" };

		if ( !string.IsNullOrWhiteSpace( entry?.CreatedUtc ) )
			details.Add( $"Created: {entry.CreatedUtc}" );
		if ( !string.IsNullOrWhiteSpace( sourceFile ) )
			details.Add( $"Source: {sourceFile}" );
		if ( !string.IsNullOrWhiteSpace( targetVmdl ) )
			details.Add( $"Target: {targetVmdl}" );
		if ( result is not null && !string.IsNullOrWhiteSpace( result.PostImportError ) )
			details.Add( $"Primary failure: {TruncateDiagnosticText( result.PostImportError, 180 )}" );

		details.Add( $"Manifest: {BuildArtifactStateLabel( manifestPath, File.Exists )}" );
		details.Add( $"Run dir: {BuildArtifactStateLabel( runDirectoryPath, Directory.Exists )}" );
		details.Add( $"Export: {BuildArtifactStateLabel( exportPath, File.Exists )}" );
		details.Add( $"Imported FBX: {BuildArtifactStateLabel( importedAnimationPath, File.Exists )}" );
		details.Add( $"Generated VMDL: {BuildArtifactStateLabel( generatedModelPath, File.Exists )}" );

		if ( result is null )
			details.Add( "This artifact view is based on persisted history only." );

		return string.Join( Environment.NewLine, details );
	}

	private static bool IsStructuredRunLog( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return false;

		return text.Contains( "RUN STATUS", StringComparison.OrdinalIgnoreCase )
			&& text.Contains( "OUTPUTS", StringComparison.OrdinalIgnoreCase );
	}

	private string BuildRunLogText( RetargetRunHistoryEntry? entry, RetargetImportResult? result )
	{
		if ( result is not null )
		{
			var persistedLog = TryReadRunLog( result.ArtifactLinks.RunLogPath );
			if ( IsStructuredRunLog( persistedLog ) )
				return persistedLog;

			if ( IsStructuredRunLog( result.Log ) )
				return result.Log;
		}

		if ( entry is null && result is null )
			return "Select a run to inspect its detailed run log.";

		var diagnostics = _pipeline.BuildDiagnostics( _mappingState );
		var manifest = result?.Manifest ?? (entry is null ? null : LoadManifestFromHistoryEntry( entry )) ?? (entry is null ? new RetargetRunManifest() : BuildFallbackManifestFromHistoryEntry( entry ));
		var runId = entry?.RunId ?? manifest.RunId ?? "unknown";
		var status = entry?.Status ?? manifest.Status ?? "unknown";
		var sourceFile = manifest.SourceFile;
		var targetVmdl = entry?.TargetVmdlPath ?? result?.VmdlResourcePath ?? string.Empty;
		var exportPath = result?.ArtifactLinks.ExportPath ?? CitizenRetargetPaths.DecodeExternalPath( entry?.ExportPath ?? string.Empty );
		var importedAnimationPath = result?.ArtifactLinks.ImportedAnimationPath ?? CitizenRetargetPaths.DecodeExternalPath( entry?.ImportedAssetPath ?? string.Empty );
		var generatedModelPath = result?.ArtifactLinks.VmdlPath ?? (!string.IsNullOrWhiteSpace( targetVmdl ) ? CitizenRetargetPaths.GetAssetAbsolutePath( targetVmdl ) : string.Empty);
		var manifestPath = result?.ArtifactLinks.ManifestPath ?? entry?.ManifestPath ?? string.Empty;
		var runDirectoryPath = result?.ArtifactLinks.RunDirectoryPath ?? manifest.Outputs.RunDir ?? string.Empty;
		var lines = new List<string>();
		AppendLogSection( lines, "Run Status", addLeadingSpacing: false );
		lines.AddRange(
		[
			$"Summary: {BuildRunHealthLabel( diagnostics, entry, result )}",
			$"Run ID: {runId}",
			$"Status: {status}"
		] );

		if ( !string.IsNullOrWhiteSpace( entry?.CreatedUtc ) )
			lines.Add( $"Created: {entry.CreatedUtc}" );
		if ( !string.IsNullOrWhiteSpace( sourceFile ) )
			lines.Add( $"Source file: {sourceFile}" );
		if ( !string.IsNullOrWhiteSpace( targetVmdl ) )
			lines.Add( $"Target VMDL: {targetVmdl}" );

		AppendLogSection( lines, "Prerequisites" );
		if ( !string.IsNullOrWhiteSpace( manifest.PoseNormalization.TargetPosePresetId ) )
			lines.Add( $"Pose preset: {manifest.PoseNormalization.TargetPosePresetId}" );
		lines.Add( $"Required mapping: {manifest.MappingCoverage.MappedRequiredSlotCount}/{manifest.MappingCoverage.RequiredSlotCount}" );
		lines.Add( $"Optional mapping: {manifest.MappingCoverage.MappedOptionalSlotCount}/{manifest.MappingCoverage.OptionalSlotCount}" );
		lines.Add( $"User overrides: {manifest.MappingCoverage.UserOverrideSlotCount}" );

		AppendLogSection( lines, "Process" );
		if ( manifest.Stages.Count == 0 )
		{
			lines.Add( "- No stage data was recorded for this run." );
		}
		else
		{
			foreach ( var stage in manifest.Stages )
			{
				lines.Add( $"- {FormatStageName( stage.StageId )}: {stage.Status}" );
				if ( stage.Warnings.Count > 0 )
					lines.AddRange( stage.Warnings.Select( warning => $"  warning: {warning}" ) );
				if ( !string.IsNullOrWhiteSpace( stage.Error ) )
					lines.Add( $"  error: {stage.Error}" );
			}
		}

		AppendLogSection( lines, "Outputs" );
		lines.Add( $"Run dir: {BuildArtifactStateLabel( runDirectoryPath, Directory.Exists )} | {runDirectoryPath}" );
		lines.Add( $"Manifest: {BuildArtifactStateLabel( manifestPath, File.Exists )} | {manifestPath}" );
		lines.Add( $"Raw export: {BuildArtifactStateLabel( exportPath, File.Exists )} | {exportPath}" );
		lines.Add( $"Imported FBX: {BuildArtifactStateLabel( importedAnimationPath, File.Exists )} | {importedAnimationPath}" );
		lines.Add( $"Generated VMDL: {BuildArtifactStateLabel( generatedModelPath, File.Exists )} | {generatedModelPath}" );

		var warningLines = new List<string>();
		if ( manifest.BackendWarnings.Count > 0 )
			warningLines.AddRange( manifest.BackendWarnings.Select( warning => $"- Backend: {warning}" ) );
		if ( manifest.Warnings.Count > 0 )
			warningLines.AddRange( manifest.Warnings.Select( warning => $"- Run: {warning}" ) );
		if ( manifest.MotionTrajectoryAnalysis.Warnings.Count > 0 )
			warningLines.AddRange( manifest.MotionTrajectoryAnalysis.Warnings.Select( warning => $"- Motion warning: {warning}" ) );
		if ( manifest.MotionTrajectoryAnalysis.Failures.Count > 0 )
			warningLines.AddRange( manifest.MotionTrajectoryAnalysis.Failures.Select( failure => $"- Motion failure: {failure}" ) );

		if ( warningLines.Count > 0 )
		{
			AppendLogSection( lines, "Warnings" );
			lines.AddRange( warningLines );
		}

		var issuesText = BuildIssuesLabel( diagnostics, entry, result );
		if ( !string.IsNullOrWhiteSpace( issuesText ) )
		{
			AppendLogSection( lines, "Issues Summary" );
			lines.Add( issuesText );
		}

		AppendLogSection( lines, "What To Check Next" );
		lines.Add( BuildRunNextStepLabel( diagnostics, entry, result ) );

		return string.Join( Environment.NewLine, lines );
	}

	private static string BuildArtifactStateLabel( string path, Func<string, bool> exists )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return "missing";

		return exists( path )
			? "available"
			: "missing";
	}

	private static string TryReadRunLog( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
			return string.Empty;

		try
		{
			return File.ReadAllText( path );
		}
		catch
		{
			return string.Empty;
		}
	}

	private string BuildEmptyPreviewMessage( RetargetClipDescriptor? selectedClip )
	{
		if ( selectedClip is null )
			return "Select a clip and run retarget to see the target-side preview.";

		return $"No target-side preview is available yet for '{selectedClip.DisplayName}'. Run the queue or pick a target animation.";
	}

	private static string BuildUnavailableResultPreviewMessage( RetargetImportResult result )
	{
		if ( !string.IsNullOrWhiteSpace( result.PostImportError ) )
			return $"Run '{result.Manifest.RunId}' finished the Blender export, but import into the current target failed. Check Diagnostics -> Run Log.";

		if ( result.Manifest.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase ) )
			return $"Run '{result.Manifest.RunId}' completed, but no compiled target animation is available to preview yet.";

		return $"Run '{result.Manifest.RunId}' is '{result.Manifest.Status}', so target-side preview is unavailable.";
	}

	private void UpdatePoseCompensationSummary()
	{
		if ( _poseCompensationSummaryLabel is null )
			return;

		var presetId = string.IsNullOrWhiteSpace( _job.TargetPosePresetId )
			? CitizenTargetProfile.DefaultTargetPosePresetId
			: _job.TargetPosePresetId.Trim();
		var compensationPath = Path.Combine( CitizenRetargetPaths.DataRoot, "citizen_arm_rest_compensation.json" );
		if ( !File.Exists( compensationPath ) )
		{
			_poseCompensationSummaryLabel.Text = $"Pose preset: {presetId}. Local compensation data file is missing.";
			return;
		}

		try
		{
			using var document = JsonDocument.Parse( File.ReadAllText( compensationPath ) );
			var method = document.RootElement.TryGetProperty( "method", out var methodElement )
				? methodElement.GetString() ?? "unknown"
				: "unknown";
			var notes = document.RootElement.TryGetProperty( "notes", out var notesElement ) && notesElement.ValueKind == JsonValueKind.Array
				? notesElement.EnumerateArray().Select( note => note.GetString() ?? string.Empty ).Where( note => !string.IsNullOrWhiteSpace( note ) ).ToList()
				: new List<string>();
			var affectedBones = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			if ( document.RootElement.TryGetProperty( "offsets_deg", out var offsetsElement ) && offsetsElement.ValueKind == JsonValueKind.Object )
			{
				foreach ( var side in offsetsElement.EnumerateObject() )
				{
					foreach ( var property in side.Value.EnumerateObject() )
					{
						var split = property.Name.Split( '.', 2 );
						if ( split.Length == 2 && !string.IsNullOrWhiteSpace( split[0] ) )
							affectedBones.Add( split[0] );
					}
				}
			}

			var affectedBoneSummary = affectedBones.Count == 0
				? "No affected bones listed."
				: $"Affects {affectedBones.Count} bone(s): {string.Join( ", ", affectedBones.OrderBy( bone => bone, StringComparer.OrdinalIgnoreCase ).Take( 8 ) )}{(affectedBones.Count > 8 ? "..." : string.Empty)}";
			var noteSummary = notes.Count == 0 ? "No additional notes." : string.Join( " ", notes );
			_poseCompensationSummaryLabel.Text =
				$"Pose preset: {presetId}{Environment.NewLine}" +
				$"Compensation method: {method}{Environment.NewLine}" +
				$"{affectedBoneSummary}{Environment.NewLine}" +
				$"{noteSummary}";
		}
		catch ( Exception exception )
		{
			_poseCompensationSummaryLabel.Text = $"Pose preset: {presetId}. Failed to read local compensation data: {exception.Message}";
		}
	}

	private void SetStatus( string message ) { if ( _statusLabel is not null ) _statusLabel.Text = $"[{DateTime.Now:HH:mm:ss}] {message}"; }
	private void PaintClipItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetClipDescriptor clip )
			return;

		var badges = BuildClipBadges( clip );
		var accent = badges.Count > 0 ? badges[0].Color : Theme.Primary;
		PaintListRow( item.Rect, clip.DisplayName, $"{clip.FrameCount} frames @ {clip.FrameRate:0.##} fps", accent, badges, showMenuIndicator: true );
	}

	private void PaintSourceBoneItem( VirtualWidget item )
	{
		if ( item.Object is not NativeAuditBoneInfo bone )
			return;

		PaintListRow( item.Rect, bone.Name, string.IsNullOrWhiteSpace( bone.ParentName ) ? "root" : $"parent: {bone.ParentName}", Theme.Blue );
	}

	private void PaintMappingItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetSlotAssignmentState slot )
			return;

		var effectiveSource = string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ) ? "Unmapped" : slot.EffectiveSourceBone;
		var state = slot.IsManualOverride ? "Manual override" : string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ) ? "Needs assignment" : "Auto mapped";
		var color = string.IsNullOrWhiteSpace( slot.EffectiveSourceBone ) ? Theme.Yellow : slot.Required ? Theme.Green : Theme.Primary;
		var badges = new List<RetargetBadgeVisual>();
		if ( slot.Required )
			badges.Add( new RetargetBadgeVisual( "REQ", Theme.Green ) );
		if ( slot.IsManualOverride )
			badges.Add( new RetargetBadgeVisual( "MANUAL", Theme.Primary ) );
		PaintComfortableListRow( item.Rect, slot.SlotId, $"Source: {effectiveSource} | {state}", color, badges );
	}

	private void PaintQueueItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetQueueItem queueItem )
			return;

		var color = queueItem.Status switch
		{
			"completed" => Theme.Green,
			"failed" => Theme.Yellow,
			"running" => Theme.Primary,
			_ => Theme.Text
		};
		var subtitle = BuildQueueSubtitle( queueItem.Status, queueItem.Message );
		if ( queueItem.Status != "running" )
		{
			PaintComfortableListRow( item.Rect, queueItem.Clip.DisplayName, subtitle, color );
			return;
		}

		var queueJob = GetActiveBackgroundJob( "queue" );
		var progress = queueJob?.Progress ?? 0f;
		var isIndeterminate = queueJob?.IsIndeterminate ?? true;
		var progressLabel = queueJob is null
			? "Retargeting..."
			: queueJob.IsIndeterminate
				? (string.IsNullOrWhiteSpace( queueJob.Detail ) ? "Retargeting..." : queueJob.Detail)
				: $"{FormatProgressText( progress )} | {(string.IsNullOrWhiteSpace( queueJob.Detail ) ? "Retargeting..." : queueJob.Detail)}";
		PaintListRowWithProgress( item.Rect, queueItem.Clip.DisplayName, progressLabel, color, progress, isIndeterminate );
	}

	private static string BuildQueueSubtitle( string status, string message )
	{
		var statusLabel = FormatHistoryStatus( status );
		var detail = (message ?? string.Empty).Trim();
		if ( string.IsNullOrWhiteSpace( detail ) || detail.Equals( status, StringComparison.OrdinalIgnoreCase ) )
			return statusLabel;

		return $"{statusLabel} | {detail}";
	}

	private void PaintHistoryItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetRunHistoryEntry entry )
			return;

		var caption = string.IsNullOrWhiteSpace( entry.SequenceName ) ? entry.ClipName : $"{entry.ClipName} -> {entry.SequenceName}";
		var color = GetRunStatusColor( entry.Status );
		var created = FormatHistoryTimestamp( entry.CreatedUtc );
		PaintComfortableListRow( item.Rect, caption, $"{FormatHistoryStatus( entry.Status )} | {created}", color );
	}

	private void PaintHomeResultItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetRunHistoryEntry entry )
			return;

		var color = entry.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase )
			? Theme.Green
			: entry.Status.Contains( "failed", StringComparison.OrdinalIgnoreCase )
				? Theme.Yellow
				: Theme.Blue;
		var caption = string.IsNullOrWhiteSpace( entry.SequenceName ) ? entry.RunId : entry.SequenceName;
		var created = string.IsNullOrWhiteSpace( entry.CreatedUtc ) ? "history" : entry.CreatedUtc;
		PaintListRow( item.Rect, caption, $"{entry.Status} | {created} | {entry.RunId}", color );
	}

	private void PaintTargetPresetItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetTargetPresetRef preset )
			return;

		var subtitle = preset.ExistsOnDisk
			? $"{preset.TargetVmdlPath} | existing"
			: $"{preset.TargetVmdlPath} | virtual";
		PaintListRow( item.Rect, preset.DisplayName, subtitle, Theme.Blue );
	}

	private void PaintTargetAnimationItem( VirtualWidget item )
	{
		if ( item.Object is not RetargetTargetAnimationEntry target )
			return;

		var latestEntry = (_job.RecentRuns ?? new List<RetargetRunHistoryEntry>())
			.Where( entry => entry.SequenceName.Equals( target.SequenceName, StringComparison.OrdinalIgnoreCase ) )
			.OrderByDescending( entry => entry.CreatedUtc, StringComparer.OrdinalIgnoreCase )
			.FirstOrDefault();
		var color = target.IsReadOnly
			? Theme.Text
			: latestEntry is null
				? Theme.Blue
				: latestEntry.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase )
					? Theme.Green
					: latestEntry.Status.Contains( "failed", StringComparison.OrdinalIgnoreCase )
						? Theme.Yellow
						: Theme.Blue;
		var sourceKind = target.IsReadOnly ? "built-in" : "imported";
		var subtitle = latestEntry is null
			? (target.Looping ? $"{sourceKind} | looping | no history" : $"{sourceKind} | no history")
			: latestEntry.Status.Equals( "completed", StringComparison.OrdinalIgnoreCase )
				? $"{sourceKind} | {(target.Looping ? "looping | " : string.Empty)}{latestEntry.RunId}"
				: $"{sourceKind} | {latestEntry.Status} | {(target.Looping ? "looping | " : string.Empty)}{latestEntry.RunId}";
		PaintComfortableListRow( item.Rect, target.SequenceName, subtitle, color, showMenuIndicator: target.IsImported && !target.IsReadOnly );
	}

	private List<RetargetBadgeVisual> BuildClipBadges( RetargetClipDescriptor clip )
	{
		var badges = new List<RetargetBadgeVisual>();
		var queueItem = _queueItems.FirstOrDefault( item => item.Clip.DisplayName.Equals( clip.DisplayName, StringComparison.OrdinalIgnoreCase ) );
		if ( queueItem is not null )
		{
			switch ( queueItem.Status )
			{
				case "running":
					badges.Add( new RetargetBadgeVisual( "RUN", Theme.Primary ) );
					break;
				case "pending":
					badges.Add( new RetargetBadgeVisual( "QUEUED", Theme.Primary ) );
					break;
			}
		}

		if ( clip.DisplayName.Contains( "loop", StringComparison.OrdinalIgnoreCase ) )
			badges.Add( new RetargetBadgeVisual( "LOOP", Theme.Blue ) );

		return badges.Take( 2 ).ToList();
	}

	private static void PaintListRow( Rect rowRect, string title, string subtitle, Color accent, IReadOnlyList<RetargetBadgeVisual>? badges = null, bool showMenuIndicator = false )
	{
		var rect = rowRect.Shrink( 6, 3 );
		var isSelected = Paint.HasSelected;
		var isHovered = Paint.HasMouseOver;
		var fillColor = isSelected
			? accent.WithAlpha( 0.2f )
			: isHovered
				? Theme.SurfaceBackground.Lighten( 0.08f ).WithAlpha( 0.92f )
				: Theme.ControlBackground.WithAlpha( 0.78f );
		var borderColor = isSelected
			? accent.WithAlpha( 0.85f )
			: isHovered
				? Theme.Border.WithAlpha( 0.6f )
				: Theme.Border.WithAlpha( 0.24f );

		Paint.ClearPen();
		Paint.SetBrush( fillColor );
		Paint.DrawRect( rect, 6 );

		var accentStrip = new Rect( rect.Left, rect.Top, 4f, rect.Height );
		Paint.SetBrush( (isSelected ? accent : isHovered ? accent.WithAlpha( 0.55f ) : accent.WithAlpha( 0.18f )) );
		Paint.DrawRect( accentStrip, 4f );

		Paint.SetBrush( Color.Transparent );
		Paint.SetPen( borderColor );
		Paint.DrawRect( rect, 6 );

		var textRect = rect.Shrink( 14, 6 );
		textRect.Left += 2f;
		var menuInset = showMenuIndicator ? PaintOverflowIndicator( rect ) : 0f;
		if ( badges is { Count: > 0 } )
			textRect.Right -= PaintBadgesWithin( rect, badges, menuInset );
		textRect.Right -= menuInset;
		Paint.SetPen( isSelected ? Theme.Text : Theme.Text.WithAlpha( 0.95f ) );
		Paint.DrawText( textRect, title, TextFlag.LeftTop );

		textRect.Top += 14;
		Paint.SetPen( Theme.Text.WithAlpha( isSelected ? 0.78f : 0.6f ) );
		Paint.DrawText( textRect, subtitle, TextFlag.LeftTop );
	}

	private static void PaintComfortableListRow( Rect rowRect, string title, string subtitle, Color accent, IReadOnlyList<RetargetBadgeVisual>? badges = null, bool showMenuIndicator = false )
	{
		var rect = rowRect.Shrink( 6, 4 );
		var isSelected = Paint.HasSelected;
		var isHovered = Paint.HasMouseOver;
		var fillColor = isSelected
			? accent.WithAlpha( 0.2f )
			: isHovered
				? Theme.SurfaceBackground.Lighten( 0.08f ).WithAlpha( 0.92f )
				: Theme.ControlBackground.WithAlpha( 0.78f );
		var borderColor = isSelected
			? accent.WithAlpha( 0.85f )
			: isHovered
				? Theme.Border.WithAlpha( 0.6f )
				: Theme.Border.WithAlpha( 0.24f );

		Paint.ClearPen();
		Paint.SetBrush( fillColor );
		Paint.DrawRect( rect, 6 );

		var accentStrip = new Rect( rect.Left, rect.Top, 4f, rect.Height );
		Paint.SetBrush( isSelected ? accent : isHovered ? accent.WithAlpha( 0.55f ) : accent.WithAlpha( 0.18f ) );
		Paint.DrawRect( accentStrip, 4f );

		Paint.SetBrush( Color.Transparent );
		Paint.SetPen( borderColor );
		Paint.DrawRect( rect, 6 );

		var textRect = rect.Shrink( 14, 8 );
		textRect.Left += 2f;
		var menuInset = showMenuIndicator ? PaintOverflowIndicator( rect ) : 0f;
		if ( badges is { Count: > 0 } )
			textRect.Right -= PaintBadgesWithin( rect, badges, menuInset );
		textRect.Right -= menuInset;

		var titleRect = new Rect( textRect.Left, rect.Top + 8f, textRect.Width, 18f );
		var subtitleRect = new Rect( textRect.Left, rect.Top + 27f, textRect.Width, 16f );

		Paint.SetPen( isSelected ? Theme.Text : Theme.Text.WithAlpha( 0.96f ) );
		Paint.DrawText( titleRect, title, TextFlag.LeftTop );

		Paint.SetPen( Theme.Text.WithAlpha( isSelected ? 0.8f : 0.64f ) );
		Paint.DrawText( subtitleRect, subtitle, TextFlag.LeftTop );
	}

	private static string FormatHistoryTimestamp( string? createdUtc )
	{
		if ( string.IsNullOrWhiteSpace( createdUtc ) )
			return "history";

		if ( DateTime.TryParse( createdUtc, out var parsed ) )
			return parsed.ToLocalTime().ToString( "yyyy-MM-dd HH:mm:ss" );

		return createdUtc;
	}

	private static void PaintListRowWithProgress( Rect rowRect, string title, string subtitle, Color accent, float progress, bool isIndeterminate )
	{
		var rect = rowRect.Shrink( 6, 6 );
		var isSelected = Paint.HasSelected;
		var isHovered = Paint.HasMouseOver;
		var fillColor = isSelected
			? accent.WithAlpha( 0.2f )
			: isHovered
				? Theme.SurfaceBackground.Lighten( 0.08f ).WithAlpha( 0.92f )
				: Theme.ControlBackground.WithAlpha( 0.78f );
		var borderColor = isSelected
			? accent.WithAlpha( 0.85f )
			: isHovered
				? Theme.Border.WithAlpha( 0.6f )
				: Theme.Border.WithAlpha( 0.24f );

		Paint.ClearPen();
		Paint.SetBrush( fillColor );
		Paint.DrawRect( rect, 6 );

		var accentStrip = new Rect( rect.Left, rect.Top, 4f, rect.Height );
		Paint.SetBrush( (isSelected ? accent : isHovered ? accent.WithAlpha( 0.55f ) : accent.WithAlpha( 0.18f )) );
		Paint.DrawRect( accentStrip, 4f );

		Paint.SetBrush( Color.Transparent );
		Paint.SetPen( borderColor );
		Paint.DrawRect( rect, 6 );

		var contentLeft = rect.Left + 16f;
		var contentWidth = rect.Width - 32f;
		var titleRect = new Rect( contentLeft, rect.Top + 8f, contentWidth, 18f );
		var subtitleRect = new Rect( contentLeft, rect.Top + 29f, contentWidth, 16f );
		var progressRect = new Rect( contentLeft, rect.Bottom - 14f, contentWidth, 6f );

		Paint.SetPen( isSelected ? Theme.Text : Theme.Text.WithAlpha( 0.95f ) );
		Paint.DrawText( titleRect, title, TextFlag.LeftTop );

		Paint.SetPen( Theme.Text.WithAlpha( isSelected ? 0.78f : 0.6f ) );
		Paint.DrawText( subtitleRect, subtitle, TextFlag.LeftTop );
		PaintInlineProgressBar( progressRect, accent, progress, isIndeterminate );
	}

	private static void PaintInlineProgressBar( Rect rect, Color accent, float progress, bool isIndeterminate )
	{
		var radius = MathF.Min( 3f, rect.Height * 0.5f );
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.Lighten( 0.08f ) );
		Paint.DrawRect( rect, radius );
		Paint.SetBrush( accent );

		if ( isIndeterminate )
		{
			var segmentWidth = MathF.Max( rect.Width * 0.24f, 24f );
			var travel = rect.Width + segmentWidth;
			var offset = (RealTime.Now * 140f) % travel - segmentWidth;
			var left = MathF.Max( rect.Left, offset );
			var right = MathF.Min( rect.Right, offset + segmentWidth );
			if ( right > left )
			{
				var segmentRect = new Rect( left, rect.Top, right - left, rect.Height );
				Paint.DrawRect( segmentRect, radius );
			}
			return;
		}

		var clamped = Math.Clamp( progress, 0f, 1f );
		if ( clamped <= 0f )
			return;

		var fillRect = rect;
		fillRect.Width *= clamped;
		Paint.DrawRect( fillRect, radius );
	}

	private static float PaintMenuIndicator( Rect rowRect )
	{
		var width = 18f;
		var indicatorRect = new Rect( rowRect.Right - width - 8f, rowRect.Top + 6f, width, 14f );
		Paint.SetPen( Theme.Text.WithAlpha( Paint.HasMouseOver ? 0.85f : 0.45f ) );
		Paint.DrawText( indicatorRect, "⋮", TextFlag.Center );
		return width + 8f;
	}

	private static float PaintBadges( Rect rowRect, IReadOnlyList<RetargetBadgeVisual> badges )
	{
		var right = rowRect.Right - 10f;
		var top = rowRect.Top + 8f;
		var consumedWidth = 0f;
		for ( var index = badges.Count - 1; index >= 0; index-- )
		{
			var badge = badges[index];
			var width = MathF.Max( 42f, badge.Text.Length * 7f + 14f );
			var badgeRect = new Rect( right - width, top, width, 16f );
			Paint.SetBrush( badge.Color.WithAlpha( 0.2f ) );
			Paint.DrawRect( badgeRect, 8f );
			Paint.SetPen( badge.Color );
			Paint.DrawText( badgeRect, badge.Text, TextFlag.Center );
			right -= width + 6f;
			consumedWidth += width + 6f;
		}

		return consumedWidth;
	}

	private static Rect GetOverflowIndicatorRect( Rect rowRect )
	{
		var width = 26f;
		return new Rect( rowRect.Right - width - 8f, rowRect.Top + 5f, width, 18f );
	}

	private static float PaintOverflowIndicator( Rect rowRect )
	{
		var width = 26f;
		var indicatorRect = GetOverflowIndicatorRect( rowRect );
		Paint.ClearPen();
		Paint.SetBrush( Paint.HasMouseOver ? Theme.SurfaceBackground.Lighten( 0.12f ).WithAlpha( 0.96f ) : Theme.ControlBackground.WithAlpha( 0.92f ) );
		Paint.DrawRect( indicatorRect, 9f );
		Paint.SetPen( Theme.Text.WithAlpha( Paint.HasMouseOver ? 0.92f : 0.72f ) );
		Paint.DrawIcon( indicatorRect, "more_vert", 14, TextFlag.Center );
		return width + 10f;
	}

	private static bool RectContainsPoint( Rect rect, Vector2 point )
	{
		return point.x >= rect.Left
			&& point.x <= rect.Right
			&& point.y >= rect.Top
			&& point.y <= rect.Bottom;
	}

	private static float PaintBadgesWithin( Rect rowRect, IReadOnlyList<RetargetBadgeVisual> badges, float rightInset )
	{
		var right = rowRect.Right - 10f - rightInset;
		var top = rowRect.Top + 8f;
		var consumedWidth = 0f;
		for ( var index = badges.Count - 1; index >= 0; index-- )
		{
			var badge = badges[index];
			var width = MathF.Max( 42f, badge.Text.Length * 7f + 14f );
			var badgeRect = new Rect( right - width, top, width, 16f );
			Paint.SetBrush( badge.Color.WithAlpha( 0.2f ) );
			Paint.DrawRect( badgeRect, 8f );
			Paint.SetPen( badge.Color );
			Paint.DrawText( badgeRect, badge.Text, TextFlag.Center );
			right -= width + 6f;
			consumedWidth += width + 6f;
		}

		return consumedWidth;
	}

	private readonly struct RetargetBadgeVisual
	{
		public RetargetBadgeVisual( string text, Color color )
		{
			Text = text;
			Color = color;
		}

		public string Text { get; }
		public Color Color { get; }
	}
}
