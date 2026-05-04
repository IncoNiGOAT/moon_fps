using Sandbox;
using System;
using System.Collections.Generic;

[Title( "Ultimate Light Manager" )]
[Category( "Light" )]
[Icon( "tungsten" )]
public class LightManager : Component, Component.ExecuteInEditor
{
    // Liste globale pour le Debug (Heatmap)
    public static List<LightManager> AllLights = new();

    // =========================================================
    // 0. SETUP & GENERAL
    // =========================================================
    public enum LightTypeEnum { Point, Spot }

    [Property, Order(0), Group("Setup")]
    [Title("Light Type")]
    public LightTypeEnum TargetLightType { get; set; } = LightTypeEnum.Point;

    private PointLight _pointLight;
    private SpotLight _spotLight;
    private Light ActiveLightComponent => (Light)_pointLight ?? _spotLight;

    [Property, Group("General")] 
    public bool IsEnabled { get; set; } = true;

    [Property, Group("General")]
    [Description("Couleur de base (utilisée si Kelvin et Disco sont désactivés)")]
    public Color LightColor { get; set; } = Color.White;

    [Property, Group("General"), Range(0, 100)]
    public float Brightness { get; set; } = 1.0f;

    [Property, Group("General")]
    public bool CastShadows { get; set; } = true;

    // =========================================================
    // 1. OPTIMIZATIONS (PERFORMANCE)
    // =========================================================
    // Je garde les optimisations dans un Groupe classique car ce sont des réglages techniques, pas des "Features" créatives.

    [Property, Group("Optimization"), Title("Hard Culling")]
    public float MaxDistance { get; set; } = 2000.0f;

    [Property, Group("Optimization"), Title("LOD - Smart Update")]
    public bool EnableLOD { get; set; } = true;

    [Property, Group("Optimization"), Title("Frustum Culling")]
    public bool EnableFrustumCulling { get; set; } = true;

    // Variables internes d'optimisation
    private float _distSquaredToCam;
    private float _nextUpdate;
    private int _lastKelvin = -1;
    private Color _cachedKelvinColor;

    // =========================================================
    // 2. FEATURES (NATIVE SYSTEM)
    // =========================================================
    // Chaque bloc ci-dessous crée un onglet/section activable dans l'éditeur

    // --- Soft Start ---
    [Property, FeatureEnabled("Soft Start")]
    public bool EnableSoftStart { get; set; } = false;

    [Property, Feature("Soft Start")]
    [Description("Durée de l'allumage en secondes (inertie thermique)")]
    public float SoftStartDuration { get; set; } = 1.0f;


    // --- Kelvin Temp ---
    [Property, FeatureEnabled("Kelvin")]
    public bool EnableKelvin { get; set; } = false;

    [Property, Feature("Kelvin"), Range(1000, 12000)]
    public int KelvinTemperature { get; set; } = 4500;


    // --- Broken Bulb ---
    [Property, FeatureEnabled("Broken Bulb")]
    public bool EnableBrokenBulb { get; set; } = false;

    [Property, Feature("Broken Bulb"), Range(0f, 1f)]
    public float BulbStability { get; set; } = 0.8f;


    // --- Gobo Animation ---
    [Property, FeatureEnabled("Gobo")]
    public bool EnableGobo { get; set; } = false;

    [Property, Feature("Gobo")]
    public float GoboSpeed { get; set; } = 1.0f;
    [Property, Feature("Gobo")]
    public float GoboContrast { get; set; } = 0.5f;


    // --- Sensor ---
    [Property, FeatureEnabled("Sensor")]
    public bool EnableSensor { get; set; } = false;

    [Property, Feature("Sensor")]
    public float SensorRange { get; set; } = 200.0f;
    [Property, Feature("Sensor")]
    public float SensorFadeSpeed { get; set; } = 4.0f;


    // --- Disco ---
    [Property, FeatureEnabled("Disco")]
    public bool EnableDisco { get; set; } = false;

    [Property, Feature("Disco")]
    public float DiscoSpeed { get; set; } = 20.0f;


    // --- Curve ---
    [Property, FeatureEnabled("Curve")]
    public bool EnableCurve { get; set; } = false;

    [Property, Feature("Curve")]
    public Curve FlickerCurve { get; set; }
    [Property, Feature("Curve")]
    public float CurveSpeed { get; set; } = 1.0f;


    // --- Fire ---
    [Property, FeatureEnabled("Fire")]
    public bool EnableFire { get; set; } = false;

    [Property, Feature("Fire")]
    public float FireSpeed { get; set; } = 10.0f;


    // --- Audio ---
    [Property, FeatureEnabled("Audio")]
    public bool EnableAudio { get; set; } = false;

    [Property, Feature("Audio")]
    public SoundEvent SoundOn { get; set; }
    [Property, Feature("Audio")]
    public SoundEvent SoundLoop { get; set; }


    // --- Horror (Legacy) ---
    [Property, FeatureEnabled("Horror")]
    public bool EnableHorror { get; set; } = false;
    // Pas de paramètres supplémentaires pour Horror, c'est juste On/Off


    // =========================================================
    // 3. DEBUG TOOLS
    // =========================================================
    
    [Property, Group("Debug")]
    public bool ShowHeatmap { get; set; } = false;
    [Property, Group("Debug")]
    public bool ShowLuxMeter { get; set; } = false;


    // =========================================================
    // CORE LOGIC VARIABLES
    // =========================================================
    
    private float _sensorFactor = 1.0f;
    private SoundHandle _loopHandle;
    private bool _wasActiveLastFrame;
    private float _nextFlickerTime;
    private float _brokenBulbMultiplier = 1.0f;
    private float _masterFade = 1.0f; 

    // =========================================================
    // LIFECYCLE
    // =========================================================

    protected override void OnStart()
    {
        if (!AllLights.Contains(this)) AllLights.Add(this);
        UpdateLightComponent();
        
        // Initialisation immédiate
        _masterFade = IsEnabled ? 1.0f : 0.0f;
    }

    protected override void OnDestroy()
    {
        if (AllLights.Contains(this)) AllLights.Remove(this);
        _loopHandle?.Stop();
    }

    protected override void OnUpdate()
    {
        // ---------------------------------------------------------
        // A. OPTIMIZATIONS (Culling & LOD)
        // ---------------------------------------------------------

        var light = ActiveLightComponent;
        if ( light == null ) return;
        if ( Scene.Camera == null ) return;

        // 1. Calcul Distance au Carré
        _distSquaredToCam = Transform.Position.DistanceSquared(Scene.Camera.Transform.Position);
        float maxDistSq = MaxDistance * MaxDistance;

        // 2. Hard Distance Culling
        if (_distSquaredToCam > maxDistSq)
        {
            if (light.Enabled) 
            {
                light.Enabled = false;
                _loopHandle?.Stop();
            }
            return; 
        }

        EnsureCorrectLightType();

        // ---------------------------------------------------------
        // B. GAMEPLAY LOGIC (toujours appliqué si la lumière est dans la distance max)
        // Frustum / LOD ne doivent pas court-circuiter avant Enabled/Couleur : sinon la lumière
        // peut rester à Enabled=false après le culling distance (vue depuis l’autre équipe / autre côté de la map).
        // ---------------------------------------------------------

        // 1. Sensor Logic
        if ( EnableSensor )
        {
            float rangeSq = SensorRange * SensorRange;
            float target = (_distSquaredToCam < rangeSq) ? 1.0f : 0.0f;
            _sensorFactor = MathX.Lerp( _sensorFactor, target, Time.Delta * SensorFadeSpeed );
        }
        else _sensorFactor = 1.0f;

        // 2. Soft Start Logic
        UpdateSoftStartLogic();

        // 3. Calcul Intensité (Merge FX)
        float fxIntensity = CalculateFxIntensity();
        
        // Combinaison finale
        float finalBrightness = fxIntensity * _sensorFactor * _masterFade;

        // Seuil d'activation
        bool shouldBeOn = finalBrightness > 0.001f;
        light.Enabled = shouldBeOn;
        
        // 4. Couleur
        light.LightColor = CalculateColor(finalBrightness);

        // 3. Frustum Culling — uniquement audio / debug (l’état de la lumière est déjà à jour)
        if (EnableFrustumCulling)
        {
            var camPos = Scene.Camera.Transform.Position;
            var camForward = Scene.Camera.Transform.Rotation.Forward;
            var dirToLight = Transform.Position - camPos;

            if (Vector3.Dot(camForward, dirToLight) <= 0) 
            {
                UpdateAudio( false );
                return;
            }

            var screenPos = Scene.Camera.PointToScreenNormal(Transform.Position);
            bool onScreen = screenPos.x > -0.2f && screenPos.x < 1.2f && 
                            screenPos.y > -0.2f && screenPos.y < 1.2f;

            if (!onScreen)
            {
                UpdateAudio( false );
                return;
            }
        }

        // 4. LOD Temporel — ne ralentit plus l’intensité / Enabled ; seulement audio + debug
        if (EnableLOD)
        {
            if (Time.Now < _nextUpdate)
            {
                UpdateAudio( shouldBeOn );
                return;
            }
            
            float delay = 0.0f;
            if (_distSquaredToCam > 1000 * 1000) delay = 0.1f;      
            else if (_distSquaredToCam > 500 * 500) delay = 0.05f;   

            _nextUpdate = Time.Now + delay + Game.Random.Float(0.0f, 0.01f);
        }

        // 5. Audio
        UpdateAudio(shouldBeOn);
        
        // 6. Debug
        if (_distSquaredToCam < 300 * 300) DrawDebugGizmos();
    }

    // =========================================================
    // LOGIC IMPLEMENTATION
    // =========================================================

    private void UpdateSoftStartLogic()
    {
        float targetFade = IsEnabled ? 1.0f : 0.0f;

        if (EnableSoftStart)
        {
            float speed = 5.0f / Math.Max(SoftStartDuration, 0.01f);
            _masterFade = MathX.Lerp(_masterFade, targetFade, Time.Delta * speed);
        }
        else
        {
            _masterFade = targetFade;
        }
    }

    private Color CalculateColor(float intensityMultiplier)
    {
        Color baseColor = LightColor;

        if (EnableDisco)
        {
            baseColor = new ColorHsv( (Time.Now * DiscoSpeed * 10f) % 360f, 1, 1 ).ToColor();
        }
        else if (EnableKelvin)
        {
            if (_lastKelvin != KelvinTemperature)
            {
                _cachedKelvinColor = KelvinToColor(KelvinTemperature);
                _lastKelvin = KelvinTemperature;
            }
            baseColor = _cachedKelvinColor;
        }

        return baseColor * intensityMultiplier;
    }

    private float CalculateFxIntensity()
    {
        float b = Brightness;

        if ( EnableCurve )
        {
            float time = (Time.Now * CurveSpeed) % 1.0f; 
            b *= FlickerCurve.Evaluate( time );
        }

        if ( EnableFire )
            b += MathF.Sin( Time.Now * FireSpeed ) * (Brightness * 0.2f);

        if ( EnableHorror && Game.Random.Float( 0, 1 ) > 0.9f )
            b = 0f;

        if ( EnableBrokenBulb )
        {
            UpdateBrokenBulbLogic();
            b *= _brokenBulbMultiplier;
        }

        if ( EnableGobo )
        {
            float noise = MathF.Sin( Time.Now * GoboSpeed );
            float goboFactor = 1.0f - ( (noise + 1.0f) / 2.0f * GoboContrast );
            b *= goboFactor;
        }

        return Math.Max( 0f, b );
    }

    private void UpdateBrokenBulbLogic()
    {
        if ( Time.Now >= _nextFlickerTime )
        {
            float chance = Game.Random.Float( 0f, 1f );
            if ( chance > BulbStability )
                _brokenBulbMultiplier = Game.Random.Float( 0.0f, 0.3f ); 
            else
                _brokenBulbMultiplier = Game.Random.Float( 0.8f, 1.0f ); 
            
            _nextFlickerTime = Time.Now + Game.Random.Float( 0.05f, 0.4f );
        }
    }

    private Color KelvinToColor( int k )
    {
        float temp = k / 100.0f;
        float r, g, b;

        if ( temp <= 66 ) r = 255;
        else
        {
            r = temp - 60;
            r = 329.698727446f * MathF.Pow( r, -0.1332047592f );
            r = Math.Clamp( r, 0, 255 );
        }

        if ( temp <= 66 )
        {
            g = temp;
            g = 99.4708025861f * MathF.Log( g ) - 161.1195681661f;
        }
        else
        {
            g = temp - 60;
            g = 288.1221695283f * MathF.Pow( g, -0.0755148492f );
        }
        g = Math.Clamp( g, 0, 255 );

        if ( temp >= 66 ) b = 255;
        else if ( temp <= 19 ) b = 0;
        else
        {
            b = temp - 10;
            b = 138.5177312231f * MathF.Log( b ) - 305.0447927307f;
        }
        b = Math.Clamp( b, 0, 255 );

        return new Color( r / 255.0f, g / 255.0f, b / 255.0f );
    }

    // =========================================================
    // UTILS
    // =========================================================

    private void UpdateAudio(bool active)
    {
        if ( !EnableAudio ) { _loopHandle?.Stop(); return; }

        if ( active != _wasActiveLastFrame )
        {
            if ( active && SoundOn != null ) Sound.Play( SoundOn, Transform.Position );
            _wasActiveLastFrame = active;
        }

        if ( active && SoundLoop != null )
        {
            if ( !_loopHandle.IsValid() ) _loopHandle = Sound.Play( SoundLoop, Transform.Position );
            _loopHandle.Position = Transform.Position;
        }
        else _loopHandle?.Stop();
    }

    private void EnsureCorrectLightType()
    {
        if ( TargetLightType == LightTypeEnum.Point )
        {
            if ( _spotLight != null ) { _spotLight.Destroy(); _spotLight = null; }
            if ( _pointLight == null ) _pointLight = Components.GetOrCreate<PointLight>();
            _pointLight.Shadows = CastShadows;
        }
        else if ( TargetLightType == LightTypeEnum.Spot )
        {
            if ( _pointLight != null ) { _pointLight.Destroy(); _pointLight = null; }
            if ( _spotLight == null ) _spotLight = Components.GetOrCreate<SpotLight>();
            _spotLight.Shadows = CastShadows;
        }
    }

    [Input] public void TurnOn() => IsEnabled = true;
    [Input] public void TurnOff() => IsEnabled = false;
    [Input] public void Toggle() => IsEnabled = !IsEnabled;
    [Input] public void SetColor( Color newColor ) => LightColor = newColor;

    private void UpdateLightComponent()
    {
        _pointLight = Components.Get<PointLight>();
        _spotLight = Components.Get<SpotLight>();
    }

    private void DrawDebugGizmos()
    {
        if ( !ShowHeatmap && !ShowLuxMeter ) return;

        using ( Gizmo.Scope( "LightDebug" ) )
        {
            if ( ShowLuxMeter )
            {
                Gizmo.Draw.Color = Color.Yellow;
                Gizmo.Draw.Text( $"INT: {Brightness * _masterFade:F1}", new Transform(Transform.Position + Vector3.Up * 10) );
            }

            if ( ShowHeatmap )
            {
                int overlapCount = 0;
                float warningRadius = 150.0f;

                foreach(var l in AllLights)
                {
                    if (l == this) continue;
                    if (l.Transform.Position.DistanceSquared(Transform.Position) < warningRadius * warningRadius)
                    {
                        overlapCount++;
                    }
                }

                if (overlapCount > 0)
                {
                    float heat = Math.Clamp(overlapCount / 3.0f, 0f, 1f);
                    Gizmo.Draw.Color = Color.Lerp(Color.Green, Color.Red, heat);
                    Gizmo.Draw.LineSphere(Transform.Position, warningRadius / 2);
                }
            }
        }
    }
}