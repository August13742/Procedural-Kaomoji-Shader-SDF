using Godot;
using System.Collections.Generic;

[Tool]
public partial class KaomojiController : MeshInstance3D
{
    #region Enums

    public enum Shape
    {
        Dot, CapsuleV, CapsuleH,
        LineH, CaretU, CaretD, CaretL, CaretR,
        Circle, ArcU, ArcD,
        Cross, Asterisk,
        Triangle, SquareHollow,
        WMouth,
        ScribbleAcron
    }

    public enum Mood
    {
        Custom, Idle, Pain, Dead, Joy, Shock, Suspicious, Complaint, DeadInside, Scream
    }

    #endregion

    #region Data Structures

    private struct ShapeDef
    {
        public int T;         // Type
        public Vector4 P;     // Params
        public Vector2 O;     // Offset
        public float R;       // Rotation

        public ShapeDef(int t, Vector4 p, Vector2 o, float r)
        {
            T = t; P = p; O = o; R = r;
        }
    }

    private Dictionary<Shape, ShapeDef> _shapeLibrary = new();

    #endregion

    #region Tween & Animation

    private Tween _activeTween;
    private float _leftEyeAlpha = 1.0f;
    private float _rightEyeAlpha = 1.0f;
    private float _mouthAlpha = 1.0f;

    #endregion

    #region Topology Exports

    [ExportCategory("Topology")]

    private float _faceScale = 1.0f;
    [Export]
    public float FaceScale
    {
        get => _faceScale;
        set { _faceScale = value; SyncShaderTopology(); }
    }

    private Vector2 _faceCenterOffset = new Vector2(0.5f, 0.5f);
    [Export]
    public Vector2 FaceCenterOffset
    {
        get => _faceCenterOffset;
        set { _faceCenterOffset = value; SyncShaderTopology(); }
    }

    private float _eyeSpacing = 0.1f;
    [Export]
    public float EyeSpacing
    {
        get => _eyeSpacing;
        set { _eyeSpacing = value; UpdateLayout(); }
    }

    private float _eyeHeight = 0.05f;
    [Export]
    public float EyeHeight
    {
        get => _eyeHeight;
        set { _eyeHeight = value; UpdateLayout(); }
    }

    private float _mouthVerticalPos = -0.15f;
    [Export]
    public float MouthVerticalPos
    {
        get => _mouthVerticalPos;
        set { _mouthVerticalPos = value; UpdateLayout(); }
    }

    #endregion

    #region Expression Exports

    [ExportCategory("Expression")]

    private Mood _currentMood = Mood.Idle;
    [Export]
    public Mood CurrentMood
    {
        get => _currentMood;
        set { _currentMood = value; ApplyMoodPreset(); }
    }

    private float _transitionDuration = 0.3f;
    [Export]
    public float TransitionDuration
    {
        get => _transitionDuration;
        set => _transitionDuration = Mathf.Max(0.0f, value);
    }

    private Tween.EaseType _transitionEasing = Tween.EaseType.Out;
    [Export]
    public Tween.EaseType TransitionEasing
    {
        get => _transitionEasing;
        set => _transitionEasing = value;
    }

    [ExportGroup("Manual Override")]

    private Shape _leftEyeShape = Shape.CapsuleV;
    [Export]
    public Shape LeftEyeShape
    {
        get => _leftEyeShape;
        set { _leftEyeShape = value; UpdateFeatures(); }
    }

    private Shape _rightEyeShape = Shape.CapsuleV;
    [Export]
    public Shape RightEyeShape
    {
        get => _rightEyeShape;
        set { _rightEyeShape = value; UpdateFeatures(); }
    }

    private Shape _mouthShape = Shape.LineH;
    [Export]
    public Shape MouthShape
    {
        get => _mouthShape;
        set { _mouthShape = value; UpdateFeatures(); }
    }

    [Export]
    public bool ApplyChanges
    {
        get => false;
        set { if (value) UpdateFeatures(); }
    }

    #endregion

    #region Lifecycle

    public override void _Ready()
    {
        InitialiseShapeLibrary();
        SyncShaderTopology();
        UpdateLayout();
        ApplyMoodPreset();
    }

    #endregion

    #region Shape Library Logic

    private void InitialiseShapeLibrary()
    {
        // ========== SDF TYPE PARAMETER REFERENCE ==========
        // Format: shape { t=type, p=Vector4(param1, param2, param3, param4), o=offset, r=rotation }
        //
        // T0 Box:       xy=Size(width,height), z=Rounding(0.0-0.1), w=unused
        //               • Increase xy for larger shapes | Lower z for sharp corners | Higher z for rounded
        // T1 Arc:       x=Radius, y=Thickness, z=unused, w=Aperture(radians, ~1.2 for 90°)
        //               • Adjust radius to widen/narrow | Aperture controls arc opening angle
        // T2 Caret:     x=unused, y=Length, z=Thickness, w=Angle(radians, ±PI/2)
        //               • y=Length: increase for longer caret | w=Angle: rotate via angle param instead
        // T3 Star2:     x=unused, y=Length, z=Thickness, w=unused (2-point star)
        // T4 Star3:     x=unused, y=Length, z=Thickness, w=unused (3-point star/asterisk)
        // T5 Triangle:  x=Radius, y=unused, z=Thickness(hollow), w=unused
        //               • Thickness controls line width | Negative z fills solid
        // T6 BoxHollow: xy=Size(width,height), z=Thickness, w=unused
        //               • Thickness controls outline width
        // T7 W-Mouth:   x=Radius, y=Thickness, z=unused, w=Aperture(~1.5)
        //               • Mirrored arc (W-shaped mouth) | Aperture controls width opening
        // T8 Scribble:  xy=Bounds(width,height), z=Rounding, w=unused
        //               • Defines container for animated scribble fill | Uses shader TIME for animation
        //
        // Common Tuning Tips:
        // • Offsets (o): Adjust to reposition shape within the feature region
        // • Rotations (r): Use 0, PI/2, PI, etc. for standard rotations (carets use flip/invert tricks)
        // • Thickness: ~0.01 for thin lines | ~0.015 for bold lines | Adjust to match face scale

        _shapeLibrary.Clear();

        // --- BASICS (Box shapes: solid and hollow) ---
        // DOT: tiny filled circle approximation
        _shapeLibrary[Shape.Dot] = new ShapeDef(0, new Vector4(0.02f, 0.02f, 0.02f, 0), new Vector2(0, 0.03f), 0.0f);
        // CAPSULE_V: vertical pill shape (tall eye)
        _shapeLibrary[Shape.CapsuleV] = new ShapeDef(0, new Vector4(0.02f, 0.08f, 0.02f, 0), Vector2.Zero, 0.0f);
        // CAPSULE_H: horizontal pill shape (wide eye)
        _shapeLibrary[Shape.CapsuleH] = new ShapeDef(0, new Vector4(0.08f, 0.02f, 0.02f, 0), Vector2.Zero, 0.0f);
        // LINE_H: thin horizontal line
        _shapeLibrary[Shape.LineH] = new ShapeDef(0, new Vector4(0.05f, 0.01f, 0.005f, 0), new Vector2(0, 0.03f), 0.0f);

        // --- CARETS (Angled lines: pointing up/down/left/right) ---
        // All carets use t=2, length=0.08, thickness=0.02, rotations differ:
        _shapeLibrary[Shape.CaretD] = new ShapeDef(2, new Vector4(0, 0.08f, 0.01f, 0.8f), new Vector2(0, 0), 0.0f);
        _shapeLibrary[Shape.CaretU] = new ShapeDef(2, new Vector4(0, 0.08f, 0.01f, 0.8f), new Vector2(0, 0.05f), Mathf.Pi);
        _shapeLibrary[Shape.CaretL] = new ShapeDef(2, new Vector4(0, 0.08f, 0.02f, 0.8f), new Vector2(0.03f, 0), Mathf.Pi / 2);
        _shapeLibrary[Shape.CaretR] = new ShapeDef(2, new Vector4(0, 0.08f, 0.02f, 0.8f), new Vector2(0.03f, 0), -Mathf.Pi / 2);

        // --- ARCS/CIRCLES (Curved shapes) ---
        // CIRCLE: full closed circle (aperture=PI)
        _shapeLibrary[Shape.Circle] = new ShapeDef(1, new Vector4(0.035f, 0.01f, 0.00f, Mathf.Pi), Vector2.Zero, 0.0f);
        // ARC_U: upward arc (smile), radius=0.08, aperture=1.2rad (~69°)
        _shapeLibrary[Shape.ArcU] = new ShapeDef(1, new Vector4(0.08f, 0.01f, 0.00f, 1.2f), new Vector2(0, 0.04f), Mathf.Pi);
        // ARC_D: downward arc (frown), same params, flipped by rotation
        _shapeLibrary[Shape.ArcD] = new ShapeDef(1, new Vector4(0.08f, 0.01f, 0.00f, 1.2f), new Vector2(0, -0.04f), 0.0f);
        // W_MOUTH: W-shaped mouth (cute grin), mirrored aperture arc
        _shapeLibrary[Shape.WMouth] = new ShapeDef(7, new Vector4(0.03f, 0.01f, 0.0f, 1.5f), new Vector2(0, 0.02f), Mathf.Pi);

        // --- SYMBOLS (Multi-pointed shapes) ---
        // CROSS: 2-point star rotated (+ shape), length=0.06, thickness=0.015
        _shapeLibrary[Shape.Cross] = new ShapeDef(3, new Vector4(0, 0.06f, 0.015f, 0), Vector2.Zero, Mathf.Pi / 4);
        // ASTERISK: 3-point star (* shape), length=0.06, thickness=0.015
        _shapeLibrary[Shape.Asterisk] = new ShapeDef(4, new Vector4(0, 0.06f, 0.015f, 0), Vector2.Zero, 0.0f);
        // TRIANGLE: hollow triangle outline, radius=0.06, line_thickness=0.01
        _shapeLibrary[Shape.Triangle] = new ShapeDef(5, new Vector4(0.06f, 0, 0.01f, 0), new Vector2(0, 0.01f), 0.0f);

        // SQUARE_HOLLOW: outlined square, size=(0.05,0.05), outline_thickness=0.01
        _shapeLibrary[Shape.SquareHollow] = new ShapeDef(6, new Vector4(0.05f, 0.05f, 0.01f, 0), Vector2.Zero, 0.0f);

        // --- SPECIAL (Animated/complex shapes) ---
        // SCRIBBLE_ACRON: boiling/moving scribble fill, uses shader TIME uniform for animation
        // size=(0.05w, 0.07h), rounding=0.05 | Customize via scr_* uniforms in shader
        _shapeLibrary[Shape.ScribbleAcron] = new ShapeDef(8, new Vector4(0.05f, 0.06f, 0.05f, 0), new Vector2(-0.01f, 0.0f), 0.0f);
    }

    #endregion

    #region Internal Logic

    private void ApplyMoodPreset()
    {
        if (_currentMood == Mood.Custom) return;

        switch (_currentMood)
        {
            case Mood.Idle:
                SetFaceState(Shape.CapsuleV, Shape.CapsuleV, Shape.LineH);
                break;
            case Mood.Pain:
                SetFaceState(Shape.CaretR, Shape.CaretL, Shape.Triangle);
                break;
            case Mood.Dead:
                SetFaceState(Shape.Cross, Shape.Cross, Shape.Triangle);
                break;
            case Mood.Joy:
                SetFaceState(Shape.CaretR, Shape.CaretL, Shape.WMouth);
                break;
            case Mood.Shock:
                SetFaceState(Shape.Circle, Shape.Circle, Shape.Triangle);
                break;
            case Mood.Suspicious:
                SetFaceState(Shape.CapsuleH, Shape.CapsuleH, Shape.CaretU);
                break;
            case Mood.Complaint:
                SetFaceState(Shape.ScribbleAcron, Shape.ScribbleAcron, Shape.Triangle);
                break;
            case Mood.DeadInside:
                SetFaceState(Shape.ScribbleAcron, Shape.ScribbleAcron, Shape.LineH);
                break;
            case Mood.Scream:
                SetFaceState(Shape.CaretR, Shape.CaretL, Shape.Triangle);
                break;
        }
    }

    private ShaderMaterial GetActiveMaterial()
    {
        return GetActiveMaterial(0) as ShaderMaterial;
    }

    private void SyncShaderTopology()
    {
        var mat = GetActiveMaterial();
        if (mat != null)
        {
            mat.SetShaderParameter("face_center", _faceCenterOffset);
            mat.SetShaderParameter("face_scale", _faceScale);
        }
    }

    private void UpdateLayout()
    {
        var mat = GetActiveMaterial();
        if (mat == null) return;

        mat.SetShaderParameter("le_origin", new Vector2(-_eyeSpacing, _eyeHeight));
        mat.SetShaderParameter("re_origin", new Vector2(_eyeSpacing, _eyeHeight));
        mat.SetShaderParameter("m_origin", new Vector2(0.0f, _mouthVerticalPos));
    }

    private void UpdateFeatures()
    {
        if (_shapeLibrary.Count == 0) InitialiseShapeLibrary();
        
        var mat = GetActiveMaterial();
        if (mat == null) return;

        if (_shapeLibrary.TryGetValue(_leftEyeShape, out var left))
            UploadFeature(mat, "le", left, false);
        
        if (_shapeLibrary.TryGetValue(_rightEyeShape, out var right))
            UploadFeature(mat, "re", right, true);
        
        if (_shapeLibrary.TryGetValue(_mouthShape, out var mouth))
            UploadFeature(mat, "m", mouth, false);

        UpdateAlphas();
    }

    private void UpdateAlphas()
    {
        var mat = GetActiveMaterial();
        if (mat == null) return;

        mat.SetShaderParameter("le_alpha", _leftEyeAlpha);
        mat.SetShaderParameter("re_alpha", _rightEyeAlpha);
        mat.SetShaderParameter("m_alpha", _mouthAlpha);
    }

    private void UploadFeature(ShaderMaterial mat, string pfx, ShapeDef data, bool isRight)
    {
        Vector2 off = data.O;
        // Mirror X offset for right eye to maintain symmetry
        if (isRight) off.X = -off.X;

        mat.SetShaderParameter(pfx + "_params", data.P);
        mat.SetShaderParameter(pfx + "_type", data.T);
        mat.SetShaderParameter(pfx + "_rot", data.R);
        mat.SetShaderParameter(pfx + "_offset", off);
    }

    private void SetFaceState(Shape l, Shape r, Shape m)
    {
        _leftEyeShape = l;
        _rightEyeShape = r;
        _mouthShape = m;
        // NotifyEditor(); // Optional: In C# tool scripts, sometimes needed to refresh Inspector
        UpdateFeatures();
    }

    #endregion

    #region Public Accessor API

    /// <summary>
    /// Set the mood of the face with optional animation transition.
    /// </summary>
    /// <param name="mood">The target mood to transition to</param>
    /// <param name="duration">Duration of transition in seconds (0 for instant)</param>
    public void SetMood(Mood mood, float duration = -1.0f)
    {
        if (duration < 0) duration = _transitionDuration;
        
        _currentMood = mood;
        
        if (duration > 0 && IsInsideTree())
        {
            ApplyMoodPresetAnimated(duration);
        }
        else
        {
            ApplyMoodPreset();
        }
    }

    /// <summary>
    /// Set a custom face expression with optional animation transition.
    /// </summary>
    /// <param name="leftEye">Shape for the left eye</param>
    /// <param name="rightEye">Shape for the right eye</param>
    /// <param name="mouth">Shape for the mouth</param>
    /// <param name="duration">Duration of transition in seconds (0 for instant)</param>
    public void SetCustomFace(Shape leftEye, Shape rightEye, Shape mouth, float duration = -1.0f)
    {
        if (duration < 0) duration = _transitionDuration;
        
        _currentMood = Mood.Custom;
        
        if (duration > 0 && IsInsideTree())
        {
            TransitionToFace(leftEye, rightEye, mouth, duration);
        }
        else
        {
            SetFaceState(leftEye, rightEye, mouth);
        }
    }

    /// <summary>
    /// Set the left eye shape with optional animation transition.
    /// </summary>
    public void SetLeftEye(Shape shape, float duration = -1.0f)
    {
        if (duration < 0) duration = _transitionDuration;
        SetCustomFace(shape, _rightEyeShape, _mouthShape, duration);
    }

    /// <summary>
    /// Set the right eye shape with optional animation transition.
    /// </summary>
    public void SetRightEye(Shape shape, float duration = -1.0f)
    {
        if (duration < 0) duration = _transitionDuration;
        SetCustomFace(_leftEyeShape, shape, _mouthShape, duration);
    }

    /// <summary>
    /// Set the mouth shape with optional animation transition.
    /// </summary>
    public void SetMouth(Shape shape, float duration = -1.0f)
    {
        if (duration < 0) duration = _transitionDuration;
        SetCustomFace(_leftEyeShape, _rightEyeShape, shape, duration);
    }

    /// <summary>
    /// Get the current left eye shape.
    /// </summary>
    public Shape GetLeftEye() => _leftEyeShape;

    /// <summary>
    /// Get the current right eye shape.
    /// </summary>
    public Shape GetRightEye() => _rightEyeShape;

    /// <summary>
    /// Get the current mouth shape.
    /// </summary>
    public Shape GetMouth() => _mouthShape;

    /// <summary>
    /// Get the current mood.
    /// </summary>
    public Mood GetMood() => _currentMood;

    #endregion

    #region Tween Animation Logic

    private void ApplyMoodPresetAnimated(float duration)
    {
        if (_currentMood == Mood.Custom) return;

        Shape targetLeft = Shape.CapsuleV;
        Shape targetRight = Shape.CapsuleV;
        Shape targetMouth = Shape.LineH;

        switch (_currentMood)
        {
            case Mood.Idle:
                targetLeft = Shape.CapsuleV;
                targetRight = Shape.CapsuleV;
                targetMouth = Shape.LineH;
                break;
            case Mood.Pain:
                targetLeft = Shape.CaretR;
                targetRight = Shape.CaretL;
                targetMouth = Shape.Triangle;
                break;
            case Mood.Dead:
                targetLeft = Shape.Cross;
                targetRight = Shape.Cross;
                targetMouth = Shape.Triangle;
                break;
            case Mood.Joy:
                targetLeft = Shape.CaretR;
                targetRight = Shape.CaretL;
                targetMouth = Shape.WMouth;
                break;
            case Mood.Shock:
                targetLeft = Shape.Circle;
                targetRight = Shape.Circle;
                targetMouth = Shape.Triangle;
                break;
            case Mood.Suspicious:
                targetLeft = Shape.CapsuleH;
                targetRight = Shape.CapsuleH;
                targetMouth = Shape.CaretU;
                break;
            case Mood.Complaint:
                targetLeft = Shape.ScribbleAcron;
                targetRight = Shape.ScribbleAcron;
                targetMouth = Shape.Triangle;
                break;
            case Mood.DeadInside:
                targetLeft = Shape.ScribbleAcron;
                targetRight = Shape.ScribbleAcron;
                targetMouth = Shape.LineH;
                break;
            case Mood.Scream:
                targetLeft = Shape.CaretR;
                targetRight = Shape.CaretL;
                targetMouth = Shape.Triangle;
                break;
        }

        TransitionToFace(targetLeft, targetRight, targetMouth, duration);
    }

    private void TransitionToFace(Shape leftEye, Shape rightEye, Shape mouth, float duration)
    {
        if (_activeTween != null && _activeTween.IsValid())
        {
            _activeTween.Kill();
        }

        _activeTween = CreateTween();
        _activeTween.SetParallel(true);
        _activeTween.SetTrans(Tween.TransitionType.Cubic);
        _activeTween.SetEase(_transitionEasing);

        float halfDuration = duration * 0.5f;

        _activeTween.TweenProperty(this, "_leftEyeAlpha", 0.0f, halfDuration);
        _activeTween.TweenProperty(this, "_rightEyeAlpha", 0.0f, halfDuration);
        _activeTween.TweenProperty(this, "_mouthAlpha", 0.0f, halfDuration);

        _activeTween.Chain();
        _activeTween.TweenCallback(Callable.From(() =>
        {
            SetFaceState(leftEye, rightEye, mouth);
        }));

        _activeTween.SetParallel(true);
        _activeTween.TweenProperty(this, "_leftEyeAlpha", 1.0f, halfDuration);
        _activeTween.TweenProperty(this, "_rightEyeAlpha", 1.0f, halfDuration);
        _activeTween.TweenProperty(this, "_mouthAlpha", 1.0f, halfDuration);

        _activeTween.Chain();
        _activeTween.TweenCallback(Callable.From(() => UpdateAlphas()));
    }

    #endregion
}