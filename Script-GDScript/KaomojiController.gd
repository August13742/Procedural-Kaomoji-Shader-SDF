@tool
extends MeshInstance3D

enum Shape { 
	DOT, CAPSULE_V, CAPSULE_H, 
	LINE_H, CARET_U, CARET_D, CARET_L, CARET_R, 
	CIRCLE, ARC_U, ARC_D, 
	CROSS, ASTERISK, 
	TRIANGLE, SQUARE_HOLLOW, 
	W_MOUTH, ACORN_SOLID
}

enum Mood { CUSTOM, IDLE, PAIN, DEAD, JOY, SHOCK, SUSPICIOUS, COMPLAINT, DEAD_INSIDE }

# --- Topology ---
@export_category("Topology")

@export var face_scale: float = 1.0:
	set(v): 
		_face_scale = v
		if is_node_ready(): _sync_shader_topology()

@export var face_center_offset: Vector2 = Vector2(0.5, 0.5):
	set(v): 
		_face_center_offset = v
		if is_node_ready(): _sync_shader_topology()

@export var eye_spacing: float = 0.1:
	set(v): 
		_eye_spacing = v
		if is_node_ready(): _update_layout()

@export var eye_height: float = 0.05:
	set(v): 
		_eye_height = v
		if is_node_ready(): _update_layout()

@export var mouth_vertical_pos: float = -0.15:
	set(v): 
		_mouth_vertical_pos = v
		if is_node_ready(): _update_layout()

# --- Expression ---
@export_category("Expression")

@export var current_mood: Mood = Mood.IDLE:
	set(v):
		_current_mood = v
		if is_node_ready(): _apply_mood_preset()

@export var transition_duration: float = 0.2

@export_group("Manual Overrides")

@export var left_eye_shape: Shape = Shape.CAPSULE_V:
	set(v):
		_left_eye_shape = v
		_current_mood = Mood.CUSTOM
		if is_node_ready(): _update_features()

@export var right_eye_shape: Shape = Shape.CAPSULE_V:
	set(v):
		_right_eye_shape = v
		_current_mood = Mood.CUSTOM
		if is_node_ready(): _update_features()

@export var mouth_shape: Shape = Shape.LINE_H:
	set(v):
		_mouth_shape = v
		_current_mood = Mood.CUSTOM
		if is_node_ready(): _update_features()

# Backing fields
var _face_scale: float = 1.0
var _face_center_offset: Vector2 = Vector2(0.5, 0.5)
var _eye_spacing: float = 0.1
var _eye_height: float = 0.05
var _mouth_vertical_pos: float = -0.15
var _left_eye_shape: Shape = Shape.CAPSULE_V
var _right_eye_shape: Shape = Shape.CAPSULE_V
var _mouth_shape: Shape = Shape.LINE_H
var _current_mood: Mood = Mood.IDLE

var _shape_library: Dictionary = {}
var _active_tween: Tween = null

var _MOOD_MAP = {
	Mood.IDLE: [Shape.CAPSULE_V, Shape.CAPSULE_V, Shape.LINE_H],
	Mood.PAIN: [Shape.CARET_R, Shape.CARET_L, Shape.TRIANGLE],
	Mood.DEAD: [Shape.CROSS, Shape.CROSS, Shape.TRIANGLE],
	Mood.JOY: [Shape.CARET_R, Shape.CARET_L, Shape.W_MOUTH],
	Mood.SHOCK: [Shape.CIRCLE, Shape.CIRCLE, Shape.TRIANGLE],
	Mood.SUSPICIOUS: [Shape.CAPSULE_H, Shape.CAPSULE_H, Shape.CARET_U],
	Mood.COMPLAINT: [Shape.ACORN_SOLID, Shape.ACORN_SOLID, Shape.TRIANGLE],
	Mood.DEAD_INSIDE: [Shape.ACORN_SOLID, Shape.ACORN_SOLID, Shape.LINE_H],
}

func _ready() -> void:
	_initialize_shape_library()
	_sync_shader_topology()
	_update_layout()
	if _current_mood in _MOOD_MAP:
		var s = _MOOD_MAP[_current_mood]
		_apply_shapes_immediate(s[0], s[1], s[2])

func _initialize_shape_library() -> void:
	_shape_library.clear()
	# Type 0 - Box
	_shape_library[Shape.DOT]           = { t=0, p=Vector4(0.02, 0.02, 0.02, 0), o=Vector2(0, 0.03), r=0.0 }
	_shape_library[Shape.CAPSULE_V]     = { t=0, p=Vector4(0.02, 0.08, 0.02, 0), o=Vector2.ZERO, r=0.0 }
	_shape_library[Shape.CAPSULE_H]     = { t=0, p=Vector4(0.08, 0.02, 0.02, 0), o=Vector2.ZERO, r=0.0 }
	_shape_library[Shape.LINE_H]        = { t=0, p=Vector4(0.05, 0.01, 0.005, 0), o=Vector2(0, 0.03), r=0.0 }
	# Type 2 - Carets
	_shape_library[Shape.CARET_D]       = { t=2, p=Vector4(0, 0.08, 0.01, 0.8), o=Vector2(0, 0), r=0.0 }
	_shape_library[Shape.CARET_U]       = { t=2, p=Vector4(0, 0.08, 0.01, 0.8), o=Vector2(0, 0.05), r=PI }
	_shape_library[Shape.CARET_L]       = { t=2, p=Vector4(0, 0.08, 0.02, 0.8), o=Vector2(0.03, 0), r=PI / 2 }
	_shape_library[Shape.CARET_R]       = { t=2, p=Vector4(0, 0.08, 0.02, 0.8), o=Vector2(0.03, 0), r=-PI / 2 }
	# Type 1 - Arcs
	_shape_library[Shape.CIRCLE]        = { t=1, p=Vector4(0.035, 0.01, 0, PI), o=Vector2.ZERO, r=0.0 }
	_shape_library[Shape.ARC_U]         = { t=1, p=Vector4(0.08, 0.01, 0, 1.2), o=Vector2(0, 0.04), r=PI }
	_shape_library[Shape.ARC_D]         = { t=1, p=Vector4(0.08, 0.01, 0, 1.2), o=Vector2(0, -0.04), r=0.0 }
	# Others
	_shape_library[Shape.W_MOUTH]       = { t=7, p=Vector4(0.03, 0.01, 0, 1.5), o=Vector2(0, 0.02), r=PI }
	_shape_library[Shape.CROSS]         = { t=3, p=Vector4(0, 0.06, 0.015, 0), o=Vector2.ZERO, r=PI / 4 }
	_shape_library[Shape.ASTERISK]      = { t=4, p=Vector4(0, 0.06, 0.015, 0), o=Vector2.ZERO, r=0.0 }
	_shape_library[Shape.TRIANGLE]      = { t=5, p=Vector4(0.06, 0, 0.01, 0), o=Vector2(0, 0.01), r=0.0 }
	_shape_library[Shape.SQUARE_HOLLOW] = { t=6, p=Vector4(0.05, 0.05, 0.01, 0), o=Vector2.ZERO, r=0.0 }
	_shape_library[Shape.ACORN_SOLID]   = { t=8, p=Vector4(0.05, 0.06, 0.05, 0), o=Vector2(-0.01, 0), r=0.0 }

func _apply_mood_preset() -> void:
	if _current_mood == Mood.CUSTOM: return
	if _current_mood in _MOOD_MAP:
		var s = _MOOD_MAP[_current_mood]
		if transition_duration > 0 and is_inside_tree():
			_run_transition(s[0], s[1], s[2])
		else:
			_apply_shapes_immediate(s[0], s[1], s[2])

func _apply_shapes_immediate(l: Shape, r: Shape, m: Shape) -> void:
	_left_eye_shape = l
	_right_eye_shape = r
	_mouth_shape = m
	_update_features()
	_set_alphas(1.0)

func _run_transition(target_l: Shape, target_r: Shape, target_m: Shape) -> void:
	if _active_tween: _active_tween.kill()
	_active_tween = create_tween()
	_active_tween.set_parallel(true).set_ease(Tween.EASE_OUT).set_trans(Tween.TRANS_CUBIC)
	
	var half = transition_duration * 0.5
	_active_tween.tween_method(_set_alphas, 1.0, 0.0, half)
	_active_tween.chain().tween_callback(func():
		_left_eye_shape = target_l
		_right_eye_shape = target_r
		_mouth_shape = target_m
		_update_features()
	)
	_active_tween.chain().set_parallel(true)
	_active_tween.tween_method(_set_alphas, 0.0, 1.0, half)

func _set_alphas(alpha: float) -> void:
	var mat = _get_active_material()
	if mat:
		mat.set_shader_parameter("le_alpha", alpha)
		mat.set_shader_parameter("re_alpha", alpha)
		mat.set_shader_parameter("m_alpha", alpha)

func _get_active_material() -> ShaderMaterial:
	return get_active_material(0) as ShaderMaterial

func _update_features() -> void:
	if _shape_library.is_empty(): _initialize_shape_library()
	var mat = _get_active_material()
	if not mat: return
	
	_upload_feature(mat, "le", _shape_library[_left_eye_shape], false)
	_upload_feature(mat, "re", _shape_library[_right_eye_shape], true)
	_upload_feature(mat, "m",  _shape_library[_mouth_shape], false)

func _upload_feature(mat: ShaderMaterial, pfx: String, data: Dictionary, is_right: bool) -> void:
	var off = data.o # FIX: Removed .duplicate()
	if is_right:
		off = Vector2(-off.x, off.y) # FIX: Create new Vector2 to avoid modifying library
	
	mat.set_shader_parameter(pfx + "_params", data.p)
	mat.set_shader_parameter(pfx + "_type", data.t)
	mat.set_shader_parameter(pfx + "_rot", data.r)
	mat.set_shader_parameter(pfx + "_offset", off)

func _sync_shader_topology() -> void:
	var mat = _get_active_material()
	if mat:
		mat.set_shader_parameter("face_center", _face_center_offset)
		mat.set_shader_parameter("face_scale", _face_scale)

func _update_layout() -> void:
	var mat = _get_active_material()
	if mat:
		mat.set_shader_parameter("le_origin", Vector2(-_eye_spacing, _eye_height))
		mat.set_shader_parameter("re_origin", Vector2(_eye_spacing, _eye_height))
		mat.set_shader_parameter("m_origin", Vector2(0.0, _mouth_vertical_pos))