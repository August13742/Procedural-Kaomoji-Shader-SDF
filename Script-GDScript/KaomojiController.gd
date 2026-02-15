@tool
extends MeshInstance3D

enum Shape { 
	DOT, CAPSULE_V, CAPSULE_H, 
	LINE_H, CARET_U, CARET_D, CARET_L, CARET_R, 
	CIRCLE, ARC_U, ARC_D, 
	CROSS, ASTERISK, 
	TRIANGLE, SQUARE_HOLLOW, 
	W_MOUTH,
	SCRIBBLE_ACRON
}

enum Mood { CUSTOM, IDLE, PAIN, DEAD, JOY, SHOCK, SUSPICIOUS, COMPLAINT, DEAD_INSIDE, SCREAM }

@export_category("Topology")
@export var face_scale: float = 1.0:
	set(v): face_scale = v; _sync_shader_topology()
@export var face_center_offset: Vector2 = Vector2(0.5, 0.5):
	set(v): face_center_offset = v; _sync_shader_topology()
@export var eye_spacing: float = 0.1:
	set(v): eye_spacing = v; _update_layout()
@export var eye_height: float = 0.05:
	set(v): eye_height = v; _update_layout()
@export var mouth_vertical_pos: float = -0.15:
	set(v): mouth_vertical_pos = v; _update_layout()

@export_category("Expression")
@export var current_mood: Mood = Mood.IDLE:
	set(v): current_mood = v; _apply_mood_preset()
	
@export_group("Manual Override")
@export var left_eye_shape: Shape = Shape.CAPSULE_V:
	set(v): left_eye_shape = v; _update_features()
@export var right_eye_shape: Shape = Shape.CAPSULE_V:
	set(v): right_eye_shape = v; _update_features()
@export var mouth_shape: Shape = Shape.LINE_H:
	set(v): mouth_shape = v; _update_features()
@export_tool_button("Apply") var apply:Callable = func():_update_features()
var _shape_library: Dictionary = {}

func _ready() -> void:
	_initialize_shape_library()
	_sync_shader_topology()
	_update_layout()
	_apply_mood_preset()

func _initialize_shape_library() -> void:
	# ========== SDF TYPE PARAMETER REFERENCE ==========
	# Format: shape { t=type, p=Vector4(param1, param2, param3, param4), o=offset, r=rotation }
	#
	# T0 Box:      xy=Size(width,height), z=Rounding(0.0-0.1), w=unused
	#              • Increase xy for larger shapes | Lower z for sharp corners | Higher z for rounded
	# T1 Arc:      x=Radius, y=Thickness, z=unused, w=Aperture(radians, ~1.2 for 90°)
	#              • Adjust radius to widen/narrow | Aperture controls arc opening angle
	# T2 Caret:    x=unused, y=Length, z=Thickness, w=Angle(radians, ±PI/2)
	#              • y=Length: increase for longer caret | w=Angle: rotate via angle param instead
	# T3 Star2:    x=unused, y=Length, z=Thickness, w=unused (2-point star)
	# T4 Star3:    x=unused, y=Length, z=Thickness, w=unused (3-point star/asterisk)
	# T5 Triangle: x=Radius, y=unused, z=Thickness(hollow), w=unused
	#              • Thickness controls line width | Negative z fills solid
	# T6 BoxHollow:xy=Size(width,height), z=Thickness, w=unused
	#              • Thickness controls outline width
	# T7 W-Mouth:  x=Radius, y=Thickness, z=unused, w=Aperture(~1.5)
	#              • Mirrored arc (W-shaped mouth) | Aperture controls width opening
	# T8 Scribble: xy=Bounds(width,height), z=Rounding, w=unused
	#              • Defines container for animated scribble fill | Uses shader TIME for animation
	#
	# Common Tuning Tips:
	# • Offsets (o): Adjust to reposition shape within the feature region
	# • Rotations (r): Use 0, PI/2, PI, etc. for standard rotations (carets use flip/invert tricks)
	# • Thickness: ~0.01 for thin lines | ~0.015 for bold lines | Adjust to match face scale
	
	# --- BASICS (Box shapes: solid and hollow) ---
	# DOT: tiny filled circle approximation
	_shape_library[Shape.DOT] = { t=0, p=Vector4(0.02, 0.02, 0.02, 0), o=Vector2(0, 0.03), r=0.0 }
	# CAPSULE_V: vertical pill shape (tall eye)
	_shape_library[Shape.CAPSULE_V] = { t=0, p=Vector4(0.02, 0.08, 0.02, 0), o=Vector2.ZERO, r=0.0 }
	# CAPSULE_H: horizontal pill shape (wide eye)
	_shape_library[Shape.CAPSULE_H] = { t=0, p=Vector4(0.08, 0.02, 0.02, 0), o=Vector2.ZERO, r=0.0 }
	# LINE_H: thin horizontal line
	_shape_library[Shape.LINE_H] = { t=0, p=Vector4(0.05, 0.01, 0.005, 0), o=Vector2(0, 0.03), r=0.0 }

	
	# --- CARETS (Angled lines: pointing up/down/left/right) ---
	# All carets use t=2, length=0.08, thickness=0.02, rotations differ:
	_shape_library[Shape.CARET_D] = { t=2, p=Vector4(0, 0.08, 0.01, 0.8), o=Vector2(0, 0), r=0.0 }
	_shape_library[Shape.CARET_U] = { t=2, p=Vector4(0, 0.08, 0.01, 0.8), o=Vector2(0, 0.05), r=PI }
	_shape_library[Shape.CARET_L] = { t=2, p=Vector4(0, 0.08, 0.02, 0.8), o=Vector2(0.03, 0), r=PI/2 }
	_shape_library[Shape.CARET_R] = { t=2, p=Vector4(0, 0.08, 0.02, 0.8), o=Vector2(0.03, 0), r=-PI/2 }
	
	# --- ARCS/CIRCLES (Curved shapes) ---
	# CIRCLE: full closed circle (aperture=PI)
	_shape_library[Shape.CIRCLE] = { t=1, p=Vector4(0.035, 0.01, 0.00, PI), o=Vector2.ZERO, r=0.0 }
	# ARC_U: upward arc (smile), radius=0.08, aperture=1.2rad (~69°)
	_shape_library[Shape.ARC_U] = { t=1, p=Vector4(0.08, 0.01, 0.00, 1.2), o=Vector2(0, 0.04), r=PI }
	# ARC_D: downward arc (frown), same params, flipped by rotation
	_shape_library[Shape.ARC_D] = { t=1, p=Vector4(0.08, 0.01, 0.00, 1.2), o=Vector2(0, -0.04), r=0.0 }
	# W_MOUTH: W-shaped mouth (cute grin), mirrored aperture arc
	_shape_library[Shape.W_MOUTH] = { t=7, p=Vector4(0.03, 0.01, 0.0, 1.5), o=Vector2(0, 0.02), r=PI }
	
	# --- SYMBOLS (Multi-pointed shapes) ---
	# CROSS: 2-point star rotated (+ shape), length=0.06, thickness=0.015
	_shape_library[Shape.CROSS] = { t=3, p=Vector4(0, 0.06, 0.015, 0), o=Vector2.ZERO, r=PI/4 }
	# ASTERISK: 3-point star (* shape), length=0.06, thickness=0.015
	_shape_library[Shape.ASTERISK] = { t=4, p=Vector4(0, 0.06, 0.015, 0), o=Vector2.ZERO, r=0.0 }
	# TRIANGLE: hollow triangle outline, radius=0.06, line_thickness=0.01
	_shape_library[Shape.TRIANGLE] = { t=5, p=Vector4(0.06, 0, 0.01, 0), o=Vector2(0, 0.01), r=0.0 }

	# SQUARE_HOLLOW: outlined square, size=(0.05,0.05), outline_thickness=0.01
	_shape_library[Shape.SQUARE_HOLLOW] = { t=6, p=Vector4(0.05, 0.05, 0.01, 0), o=Vector2.ZERO, r=0.0 }

	# --- SPECIAL (Animated/complex shapes) ---
	# SCRIBBLE_ACRON: boiling/moving scribble fill, uses shader TIME uniform for animation
	# size=(0.05w, 0.07h), rounding=0.05 | Customize via scr_* uniforms in shader
	_shape_library[Shape.SCRIBBLE_ACRON] = { t=8, p=Vector4(0.05, 0.06, 0.05, 0), o=Vector2(-0.01, 0.0), r=0.0 }

func _apply_mood_preset() -> void:
	if current_mood == Mood.CUSTOM: return
	
	match current_mood:
		Mood.IDLE: _set_face_state(Shape.CAPSULE_V, Shape.CAPSULE_V, Shape.LINE_H)
		Mood.PAIN: _set_face_state(Shape.CARET_R, Shape.CARET_L, Shape.TRIANGLE)
		Mood.DEAD: _set_face_state(Shape.CROSS, Shape.CROSS, Shape.TRIANGLE)
		Mood.JOY:  _set_face_state(Shape.CARET_R, Shape.CARET_L, Shape.W_MOUTH)
		Mood.SHOCK: _set_face_state(Shape.CIRCLE, Shape.CIRCLE, Shape.TRIANGLE)
		Mood.SUSPICIOUS: _set_face_state(Shape.CAPSULE_H, Shape.CAPSULE_H, Shape.CARET_U)
		Mood.COMPLAINT: _set_face_state(Shape.SCRIBBLE_ACRON, Shape.SCRIBBLE_ACRON, Shape.TRIANGLE)
		Mood.DEAD_INSIDE: _set_face_state(Shape.SCRIBBLE_ACRON, Shape.SCRIBBLE_ACRON, Shape.LINE_H)
		Mood.SCREAM: _set_face_state(Shape.CARET_R, Shape.CARET_L, Shape.TRIANGLE)

func _get_active_material() -> ShaderMaterial:
	return get_active_material(0) as ShaderMaterial

func _sync_shader_topology() -> void:
	var mat = _get_active_material()
	if mat:
		mat.set_shader_parameter("face_center", face_center_offset)
		mat.set_shader_parameter("face_scale", face_scale)

func _update_layout() -> void:
	var mat = _get_active_material()
	if not mat: return
	mat.set_shader_parameter("le_origin", Vector2(-eye_spacing, eye_height))
	mat.set_shader_parameter("re_origin", Vector2(eye_spacing, eye_height))
	mat.set_shader_parameter("m_origin", Vector2(0.0, mouth_vertical_pos))

func _update_features() -> void:
	if not _shape_library.is_empty():_initialize_shape_library()
	var mat = _get_active_material()
	if not mat: return
	_upload_feature(mat, "le", _shape_library[left_eye_shape], false)
	_upload_feature(mat, "re", _shape_library[right_eye_shape], true)
	_upload_feature(mat, "m", _shape_library[mouth_shape], false)

func _upload_feature(mat: ShaderMaterial, pfx: String, data: Dictionary, is_right: bool) -> void:
	var off = data.o
	# Mirror X offset for right eye to maintain symmetry
	if is_right: off.x = -off.x
	
	mat.set_shader_parameter(pfx + "_params", data.p)
	mat.set_shader_parameter(pfx + "_type", data.t)
	mat.set_shader_parameter(pfx + "_rot", data.r)
	mat.set_shader_parameter(pfx + "_offset", off)

func _set_face_state(l: Shape, r: Shape, m: Shape) -> void:
	left_eye_shape = l; right_eye_shape = r; mouth_shape = m; _update_features()
