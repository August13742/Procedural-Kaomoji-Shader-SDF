using Godot;
using System.Collections.Generic;

/// <summary>
/// KaomojiController manages Kaomoji facial expressions by controlling SDF (Signed Distance Field) shader parameters.
/// 
/// This script drives the visual appearance of a procedural Kaomoji face by updating shader parameters that define
/// the shapes, positions, and colors of features (left eye, right eye, mouth). It supports preset moods and manual
/// feature customization.
/// 
/// Shader Parameters Overview:
/// - face_center: Offset of face center from UV (0.5, 0.5)
/// - face_scale: Overall scale of the face geometry
/// - le_*, re_*, m_*: Parameters for Left Eye, Right Eye, and Mouth respectively
///   - {prefix}_type: SDF shape type (0-8, see get_sdf in shader)
///   - {prefix}_params: Shape-specific parameters (Vec4: usually x,y dimensions, z=thickness, w=angle)
///   - {prefix}_origin: Center position of the feature
///   - {prefix}_offset: Local offset from origin
///   - {prefix}_rot: Rotation in radians
///   - {prefix}_alpha: Opacity (0.0-1.0)
/// </summary>
[Tool]
public partial class KaomojiController : MeshInstance3D
{
    public enum Shape { Dot, CapsuleV, CapsuleH, LineH, CaretU, CaretD, CaretL, CaretR, Circle, ArcU, ArcD, Cross, Asterisk, Triangle, SquareHollow, WMouth, AcornSolid }
    public enum Mood { Custom, Idle, Pain, Dead, Joy, Shock, Suspicious, Complaint, DeadInside }

    private struct ShapeDef
    {
        /// <summary>SDF shape type (0-8). Determines which SDF function to use in shader.</summary>
        public int T;
        /// <summary>Shape parameters (Vec4). Typically: x,y = dimensions, z = thickness, w = angle/arc aperture.</summary>
        public Vector4 P;
        /// <summary>Local offset from feature origin before rotation.</summary>
        public Vector2 O;
        /// <summary>Rotation parameter (primarily used for shape orientation adjustments).</summary>
        public float R;
        
        public ShapeDef(int t, Vector4 p, Vector2 o, float r) { T = t; P = p; O = o; R = r; }
    }

    private Dictionary<Shape, ShapeDef> _shapeLibrary = new();
    private Tween _activeTween;

    #region Topology Exports
    /// <summary>
    /// Topology properties control the overall positioning and scale of the face in UV space.
    /// These affect the "face_center", "face_scale" shader parameters.
    /// Changes trigger SyncShaderTopology to update shader uniforms.
    /// </summary>
    [ExportCategory("Topology")]
    /// <summary>Uniform scale applied to all face features. Affects shader parameter "face_scale".</summary>
    private float _faceScale = 1.0f;
    [Export] public float FaceScale { get => _faceScale; set { _faceScale = value; SyncShaderTopology(); } }

    /// <summary>Center position of the face in UV space (typically 0.5, 0.5). Updates shader "face_center".</summary>
    private Vector2 _faceCenterOffset = new(0.5f, 0.5f);
    [Export] public Vector2 FaceCenterOffset { get => _faceCenterOffset; set { _faceCenterOffset = value; SyncShaderTopology(); } }

    /// <summary>Horizontal distance between left and right eyes. Updates shader "le_origin" and "re_origin".</summary>
    private float _eyeSpacing = 0.1f;
    [Export] public float EyeSpacing { get => _eyeSpacing; set { _eyeSpacing = value; UpdateLayout(); } }

    /// <summary>Vertical position of both eyes relative to face center. Updates shader "le_origin" and "re_origin".</summary>
    private float _eyeHeight = 0.05f;
    [Export] public float EyeHeight { get => _eyeHeight; set { _eyeHeight = value; UpdateLayout(); } }

    /// <summary>Vertical position of mouth relative to face center. Updates shader "m_origin".</summary>
    private float _mouthVerticalPos = -0.15f;
    [Export] public float MouthVerticalPos { get => _mouthVerticalPos; set { _mouthVerticalPos = value; UpdateLayout(); } }
    #endregion

    #region Expression Exports
    /// <summary>
    /// Expression properties control the emotional state and appearance of features.
    /// Can use preset moods or manual shape overrides.
    /// Changes automatically update the shader feature parameters (le_type, re_type, m_type, etc).
    /// </summary>
    [ExportCategory("Expression")]
    /// <summary>Current mood preset (Idle, Pain, Joy, etc). Setting to a non-Custom mood applies a preset. Custom allows manual control.</summary>
    private Mood _currentMood = Mood.Idle;
    [Export] public Mood CurrentMood { get => _currentMood; set { _currentMood = value; ApplyMoodPreset(); } }

    /// <summary>Duration in seconds for smooth transitions between mood changes.</summary>
    [Export] public float TransitionDuration { get; set; } = 0.2f;

    /// <summary>
    /// Manual feature overrides allow direct control of shapes when CurrentMood is set to Custom.
    /// These update shader parameters: le_type, le_params, re_type, re_params, m_type, m_params.
    /// </summary>
    [ExportGroup("Manual Overrides")]
    /// <summary>Shape of the left eye. Updates shader le_type and le_params via ShapeLibrary.</summary>
    private Shape _lShape = Shape.CapsuleV;
    [Export] public Shape LeftEyeShape { get => _lShape; set { _lShape = value; _currentMood = Mood.Custom; UpdateFeatures(); } }

    /// <summary>Shape of the right eye. Updates shader re_type and re_params via ShapeLibrary.</summary>
    private Shape _rShape = Shape.CapsuleV;
    [Export] public Shape RightEyeShape { get => _rShape; set { _rShape = value; _currentMood = Mood.Custom; UpdateFeatures(); } }

    /// <summary>Shape of the mouth. Updates shader m_type and m_params via ShapeLibrary.</summary>
    private Shape _mShape = Shape.LineH;
    [Export] public Shape MouthShape { get => _mShape; set { _mShape = value; _currentMood = Mood.Custom; UpdateFeatures(); } }
    #endregion

    public override void _Ready()
    {
        InitialiseShapeLibrary();
        SyncShaderTopology();
        UpdateLayout();
        // Force immediate apply on ready to avoid "invisible" face
        if (TryGetMap(_currentMood, out var s)) ApplyShapesImmediate(s.L, s.R, s.M);
    }

    /// <summary>
    /// Populates the shape library with predefined SDF shape configurations.
    /// Each shape is a combination of: SDF type, parameters (dimension/thickness/angle), offset, and rotation.
    /// The parameters correspond to shader uniforms: {prefix}_type, {prefix}_params, {prefix}_offset, {prefix}_rot.
    /// 
    /// Shape Types (matching get_sdf in shader) and their xyzw parameter meanings:
    /// 
    /// Type 0 - Box (rounded rectangle):
    ///   x,y = half-width/height (dimensions), z = corner radius, w = unused
    ///   Used for: Dot, CapsuleV, CapsuleH, LineH
    /// 
    /// Type 1 - Arc (circular arc segment):
    ///   x = arc radius, y = line thickness, z = unused, w = aperture (angle range in radians)
    ///   Used for: Circle, ArcU, ArcD
    /// 
    /// Type 2 - Caret (pointed wedge shape):
    ///   x = unused, y = length, z = thickness, w = angle (rotation/direction)
    ///   Used for: CaretU, CaretD, CaretL, CaretR
    /// 
    /// Type 3 - 2-Point Star (cross with two lines):
    ///   x = unused, y = ray length, z = line thickness, w = unused
    ///   Used for: Cross
    /// 
    /// Type 4 - 3-Point Star (three rays, 120° apart):
    ///   x = unused, y = ray length, z = line thickness, w = unused
    ///   Used for: Asterisk
    /// 
    /// Type 5 - Triangle:
    ///   x = side radius, y = unused, z = line thickness, w = unused
    ///   Used for: Triangle
    /// 
    /// Type 6 - Hollow Box (outline rectangle):
    ///   x,y = half-width/height, z = wall thickness, w = unused
    ///   Used for: SquareHollow
    /// 
    /// Type 7 - W-Mouth (W-shaped mouth formed by two arcs):
    ///   x = horizontal offset, y = thickness, z = unused, w = aperture (controls smile width)
    ///   Used for: WMouth
    /// 
    /// Type 8 - Acorn (cup + bar composite shape):
    ///   x,y = bounds (width/height), z = rounding/smoothness, w = unused
    ///   Used for: AcornSolid
    /// </summary>
    private void InitialiseShapeLibrary()
    {
        _shapeLibrary.Clear();
        _shapeLibrary[Shape.Dot]           = new(0, new(0.02f, 0.02f, 0.02f, 0), new(0, 0.03f), 0);
        _shapeLibrary[Shape.CapsuleV]      = new(0, new(0.02f, 0.08f, 0.02f, 0), Vector2.Zero, 0);
        _shapeLibrary[Shape.CapsuleH]      = new(0, new(0.08f, 0.02f, 0.02f, 0), Vector2.Zero, 0);
        _shapeLibrary[Shape.LineH]         = new(0, new(0.05f, 0.01f, 0.005f, 0), new(0, 0.03f), 0);
        _shapeLibrary[Shape.CaretD]        = new(2, new(0, 0.08f, 0.01f, 0.8f), Vector2.Zero, 0);
        _shapeLibrary[Shape.CaretU]        = new(2, new(0, 0.08f, 0.01f, 0.8f), new(0, 0.05f), Mathf.Pi);
        _shapeLibrary[Shape.CaretL]        = new(2, new(0, 0.08f, 0.02f, 0.8f), new(0.03f, 0), Mathf.Pi / 2);
        _shapeLibrary[Shape.CaretR]        = new(2, new(0, 0.08f, 0.02f, 0.8f), new(0.03f, 0), -Mathf.Pi / 2);
        _shapeLibrary[Shape.Circle]        = new(1, new(0.035f, 0.01f, 0, Mathf.Pi), Vector2.Zero, 0);
        _shapeLibrary[Shape.ArcU]          = new(1, new(0.08f, 0.01f, 0, 1.2f), new(0, 0.04f), Mathf.Pi);
        _shapeLibrary[Shape.ArcD]          = new(1, new(0.08f, 0.01f, 0, 1.2f), new(0, -0.04f), 0);
        _shapeLibrary[Shape.WMouth]        = new(7, new(0.03f, 0.01f, 0, 1.5f), new(0, 0.02f), Mathf.Pi);
        _shapeLibrary[Shape.Cross]         = new(3, new(0, 0.06f, 0.015f, 0), Vector2.Zero, Mathf.Pi / 4);
        _shapeLibrary[Shape.Asterisk]      = new(4, new(0, 0.06f, 0.015f, 0), Vector2.Zero, 0);
        _shapeLibrary[Shape.Triangle]      = new(5, new(0.06f, 0, 0.01f, 0), new(0, 0.01f), 0);
        _shapeLibrary[Shape.SquareHollow]  = new(6, new(0.05f, 0.05f, 0.01f, 0), Vector2.Zero, 0);
        _shapeLibrary[Shape.AcornSolid]    = new(8, new(0.05f, 0.06f, 0.05f, 0), new(-0.01f, 0), 0);
    }

    /// <summary>
    /// Helper method to look up mood configurations from the preset map.
    /// Centralizes all mood-to-shape mappings to avoid duplication.
    /// </summary>
    /// <param name="mood">The mood to look up</param>
    /// <param name="shapes">Output tuple of (LeftEye, RightEye, Mouth) shapes</param>
    /// <returns>True if mood exists in map, false otherwise</returns>
    private bool TryGetMap(Mood mood, out (Shape L, Shape R, Shape M) shapes)
    {
        var map = new Dictionary<Mood, (Shape L, Shape R, Shape M)> {
            { Mood.Idle, (Shape.CapsuleV, Shape.CapsuleV, Shape.LineH) },
            { Mood.Pain, (Shape.CaretR, Shape.CaretL, Shape.Triangle) },
            { Mood.Dead, (Shape.Cross, Shape.Cross, Shape.Triangle) },
            { Mood.Joy, (Shape.CaretR, Shape.CaretL, Shape.WMouth) },
            { Mood.Shock, (Shape.Circle, Shape.Circle, Shape.Triangle) },
            { Mood.Suspicious, (Shape.CapsuleH, Shape.CapsuleH, Shape.CaretU) },
            { Mood.Complaint, (Shape.AcornSolid, Shape.AcornSolid, Shape.Triangle) },
            { Mood.DeadInside, (Shape.AcornSolid, Shape.AcornSolid, Shape.LineH) },
        };
        return map.TryGetValue(mood, out shapes);
    }

    /// <summary>
    /// Applies preset mood configurations that set the left eye, right eye, and mouth shapes.
    /// If CurrentMood is Custom, this does nothing (allows manual override).
    /// Otherwise, uses smooth transitions with tweens if TransitionDuration is set and node is in scene tree.
    /// Falls back to immediate application if not in tree or duration is zero.
    /// </summary>
    private void ApplyMoodPreset()
    {
        if (_currentMood == Mood.Custom) return;
        if (TryGetMap(_currentMood, out var s))
        {
            if (TransitionDuration > 0 && IsInsideTree())
                RunTransition(s.L, s.R, s.M);
            else
                ApplyShapesImmediate(s.L, s.R, s.M);
        }
    }

    /// <summary>
    /// Applies shapes immediately without any transition or animation.
    /// Updates internal shape state and triggers feature upload to shader.
    /// Sets all feature alphas to fully opaque (1.0).
    /// </summary>
    private void ApplyShapesImmediate(Shape l, Shape r, Shape m)
    {
        _lShape = l; _rShape = r; _mShape = m;
        UpdateFeatures();
        SetAlphas(1.0f);
    }

    /// <summary>
    /// Runs a smooth transition animation between shape states using tweens.
    /// Animation sequence:
    /// 1. Fade out current features to invisible (half duration)
    /// 2. Swap shapes while invisible
    /// 3. Fade in new features (half duration)
    /// 
    /// Kills any active tween before starting to prevent overlapping animations.
    /// Uses cubic easing for professional feel.
    /// </summary>
    private void RunTransition(Shape targetL, Shape targetR, Shape targetM)
    {
        if (_activeTween != null && _activeTween.IsValid()) _activeTween.Kill();
        
        var mat = GetActiveMaterial(0) as ShaderMaterial;
        if (mat == null) return;

        _activeTween = CreateTween();
        _activeTween.SetParallel(true).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        
        float half = TransitionDuration * 0.5f;

        // Step 1: Fade out
        _activeTween.TweenMethod(Callable.From<float>(SetAlphas), 1.0f, 0.0f, half);
        
        // Step 2: Swap shapes while invisible
        _activeTween.Chain().TweenCallback(Callable.From(() => {
            _lShape = targetL; _rShape = targetR; _mShape = targetM;
            UpdateFeatures();
        }));

        // Step 3: Fade in
        _activeTween.Chain().SetParallel(true);
        _activeTween.TweenMethod(Callable.From<float>(SetAlphas), 0.0f, 1.0f, half);
    }

    /// <summary>
    /// Sets the alpha (opacity) of all facial features by updating shader parameters.
    /// Updates: le_alpha, re_alpha, m_alpha (left eye, right eye, mouth).
    /// Called during transitions to create fade-in/fade-out effects.
    /// </summary>
    /// <param name="alpha">Opacity value from 0.0 (invisible) to 1.0 (fully opaque)</param>
    private void SetAlphas(float alpha)
    {
        var mat = GetActiveMaterial(0) as ShaderMaterial;
        if (mat == null) return;
        mat.SetShaderParameter("le_alpha", alpha);
        mat.SetShaderParameter("re_alpha", alpha);
        mat.SetShaderParameter("m_alpha", alpha);
    }

    /// <summary>
    /// Updates all feature shader parameters by uploading the current eye and mouth shapes to the material.
    /// Calls UploadFeature for each feature (left eye, right eye, mouth) to set their shader uniforms.
    /// This is the main orchestrator that syncs the C# shape state with the shader.
    /// 
    /// Updates shader parameters:
    /// - le_type, le_params, le_offset, le_rot (left eye)
    /// - re_type, re_params, re_offset, re_rot (right eye)
    /// - m_type, m_params, m_offset, m_rot (mouth)
    /// </summary>
    private void UpdateFeatures()
    {
        if (_shapeLibrary.Count == 0) InitialiseShapeLibrary();
        var mat = GetActiveMaterial(0) as ShaderMaterial;
        if (mat == null) return;

        UploadFeature(mat, "le", _shapeLibrary[_lShape], false);
        UploadFeature(mat, "re", _shapeLibrary[_rShape], true);
        UploadFeature(mat, "m",  _shapeLibrary[_mShape], false);
    }

    /// <summary>
    /// Uploads a feature's SDF shape data to the shader material.
    /// This is the critical bridge between C# shape definitions and shader parameters.
    /// 
    /// For a feature with prefix "le" (left eye), this sets:
    /// - le_params: The shape dimensions and thickness (Vector4: x,y size, z thickness, w angle)
    /// - le_type: The SDF type enum (0-8)
    /// - le_rot: Rotation in radians
    /// - le_offset: Local position offset (mirrored on X axis for right features)
    /// 
    /// The shader then uses these uniform values in get_sdf() to render the actual shape via raymarching.
    /// </summary>
    /// <param name="mat">The ShaderMaterial to update</param>
    /// <param name="pfx">Shader parameter prefix ("le", "re", or "m")</param>
    /// <param name="data">ShapeDef containing type, params, offset, and rotation</param>
    /// <param name="isRight">If true, mirrors the X offset for right-side features</param>
    private void UploadFeature(ShaderMaterial mat, string pfx, ShapeDef data, bool isRight)
    {
        Vector2 off = data.O; if (isRight) off.X = -off.X;
        mat.SetShaderParameter(pfx + "_params", data.P);
        mat.SetShaderParameter(pfx + "_type", data.T);
        mat.SetShaderParameter(pfx + "_rot", data.R);
        mat.SetShaderParameter(pfx + "_offset", off);
    }

    /// <summary>
    /// Synchronizes face topology shader parameters with the current C# values.
    /// Updates shader uniforms that control the overall transform of the face:
    /// - face_center: Centers the face in UV space (default 0.5, 0.5)
    /// - face_scale: Uniform scale applied to all coordinates before SDF evaluation
    /// 
    /// Called when FaceScale or FaceCenterOffset properties change.
    /// </summary>
    private void SyncShaderTopology() {
        var mat = GetActiveMaterial(0) as ShaderMaterial;
        if (mat == null) return;
        mat.SetShaderParameter("face_center", _faceCenterOffset);
        mat.SetShaderParameter("face_scale", _faceScale);
    }

    /// <summary>
    /// Updates the layout positions of facial features in the shader.
    /// Sets the origin positions for left eye, right eye, and mouth based on topology properties:
    /// - le_origin: Left eye position (-eyeSpacing, eyeHeight)
    /// - re_origin: Right eye position (eyeSpacing, eyeHeight)
    /// - m_origin: Mouth position (0, mouthVerticalPos)
    /// 
    /// These origins are combined with offsets and rotations in the shader to position the final SDFs.
    /// Called when EyeSpacing, EyeHeight, or MouthVerticalPos properties change.
    /// </summary>
    private void UpdateLayout() {
        var mat = GetActiveMaterial(0) as ShaderMaterial;
        if (mat == null) return;
        mat.SetShaderParameter("le_origin", new Vector2(-_eyeSpacing, _eyeHeight));
        mat.SetShaderParameter("re_origin", new Vector2(_eyeSpacing, _eyeHeight));
        mat.SetShaderParameter("m_origin", new Vector2(0.0f, _mouthVerticalPos));
    }
}