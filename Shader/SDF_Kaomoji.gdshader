shader_type spatial;

// --- TOPOLOGY ---
uniform vec2 face_center = vec2(0.5, 0.5);
uniform float face_scale = 1.0;
uniform vec3 face_color : source_color = vec3(0.1, 0.1, 0.1);
uniform vec3 bg_color : source_color = vec3(1.0, 1.0, 1.0);

// --- ANCHORS ---
uniform vec2 le_origin; uniform vec2 re_origin; uniform vec2 m_origin;

// --- SCRIBBLE PARAMS ---
uniform int   scr_loops = 24;           // Number of pen strokes
uniform float scr_inner_mess = 0.1;    // Center drift (Relative to container size)
uniform float scr_outer_mess = 0.05;    // Edge drift (Relative to container size)

// [MODIFIED] Now relative scales (0.0 to 1.0 of the container size)
uniform float scr_rad_scale_min = 0.2;   // Smallest loop relative to shape
uniform float scr_rad_scale_max = 2.0;   // Largest loop relative to shape

uniform float scr_wobble_amp = 0.005;    // "Lumpiness" of each stroke
uniform float scr_pen_thick_min = 0.003; // Minimum ink weight (Absolute)
uniform float scr_pen_thick_max = 0.005; // Maximum ink weight (Absolute)

uniform float scr_fps = 1.0;            // Animation speed
uniform float scr_outline_width = 0.01; 

// --- FEATURES ---
uniform vec4 le_params; uniform float le_rot; uniform vec2 le_offset; uniform int le_type;
uniform vec4 re_params; uniform float re_rot; uniform vec2 re_offset; uniform int re_type;
uniform vec4 m_params;  uniform float m_rot;  uniform vec2 m_offset;  uniform int m_type;

// --- UTILS ---
vec2 hash2(vec2 p) {
    p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
    return -1.0 + 2.0 * fract(sin(p) * 43758.5453123);
}

// ------------------------------------------------------------------
//  SCRIBBLER
// ------------------------------------------------------------------
float scribble_stop_motion(vec2 p, float messiness, float ref_scale) {
    float t_step = floor(TIME * scr_fps);
    float total_ink = 0.0;
    float aa = fwidth(length(p));

    for(int i = 0; i < scr_loops; i++) {
        float f_i = float(i);
        vec2 seed = vec2(t_step, f_i * 7.13);
        vec4 h = vec4(hash2(seed), hash2(seed + 3.14));

        // 1. Stochastic Center Shift (Scaled by feature size)
        float loop_ratio = f_i / float(scr_loops);
        // We multiply by ref_scale so small objects don't have scribbles drifting way outside
        float current_jitter = mix(scr_inner_mess, scr_outer_mess, loop_ratio) * messiness * ref_scale;
        vec2 center_off = (h.xy) * current_jitter;
        vec2 q = p + center_off;

        float angle = atan(q.y, q.x);
        
        // 2. Low-Frequency Organic Wobble
        float noise_wobble = sin(angle * 2.0 + h.z * 6.28) * 0.5 
                           + sin(angle * 3.0 + h.w * 6.28) * 0.25;
        float wobble = noise_wobble * scr_wobble_amp;

        // 3. Controlled Random Radius (Relative to feature size)
        float min_r = scr_rad_scale_min * ref_scale;
        float max_r = scr_rad_scale_max * ref_scale;
        
        // Constraint: Clamp max radius to prevent it exceeding the container significantly
        // This effectively keeps the radius "inside the socket" logic 
        float target_r = mix(min_r, max_r, h.z * 0.5 + 0.5) + wobble;
        
        // 4. Variable Pen Thickness
        float current_thickness = mix(scr_pen_thick_min, scr_pen_thick_max, h.w * 0.5 + 0.5);
        
        // 5. Render Stroke
        float dist = length(q);
        float stroke = 1.0 - smoothstep(current_thickness - aa, current_thickness + aa, abs(dist - target_r));
        
        total_ink = max(total_ink, stroke);
    }
    return total_ink;
}

// --- SDF GEOMETRY ---
float sd_box(vec2 p, vec2 b, float r) {
    vec2 d = abs(p) - b + vec2(r);
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}
float sd_arc(vec2 p, float ra, float rb, float aperture) {
    vec2 sc = vec2(sin(aperture), cos(aperture));
    p.x = abs(p.x);
    return (sc.y*p.x > sc.x*p.y) ? length(p - sc * ra) - rb : abs(length(p) - ra) - rb;
}
float sd_caret(vec2 p, float thick, float len, float angle) {
    p.x = abs(p.x);
    vec2 dir = vec2(cos(angle), sin(angle));
    return length(p - dir * clamp(dot(p, dir), 0.0, len)) - thick;
}
float sd_star(vec2 p, float thick, float len, int points) {
    float d = 1e10;
    for(int i=0; i < points; i++) {
        float ang = 3.14159 * float(i) / float(points);
        vec2 dir = vec2(cos(ang), sin(ang));
        d = min(d, length(p - dir * clamp(dot(p, dir), -len, len)) - thick);
    }
    return d;
}
float sd_triangle(vec2 p, float r, float thick) {
    const float k = sqrt(3.0);
    p.x = abs(p.x) - r;
    p.y = p.y + r / k;
    if (p.x + k*p.y > 0.0) p = vec2(p.x - k*p.y, -k*p.x - p.y) / 2.0;
    p.x -= clamp(p.x, -2.0*r, 0.0);
    return abs(-length(p)*sign(p.y)) - thick;
}

float sd_box_hollow(vec2 p, vec2 b, float thick) {
    return abs(sd_box(p, b, 0.0)) - thick;
}

// --- THE ACORN SOCKET ---
float sd_acorn_socket(vec2 p, vec2 bounds, float rounding) {
    float cup = sd_box(p + vec2(0.0, 0.01), bounds, rounding);
    vec2 bar_size = vec2(bounds.x * 1.4, 0.005);
    float bar = sd_box(p - vec2(0.0, bounds.y * 0.8), bar_size, 0.005);
    return min(cup, bar);
}

float get_sdf(vec2 p, int type, vec4 par) {
    if (type == 0) return sd_box(p, par.xy, par.z);            
    if (type == 1) return sd_arc(p, par.x, par.y, par.w);      
    if (type == 2) return sd_caret(p, par.z, par.y, par.w);    
    if (type == 3) return sd_star(p, par.z, par.y, 2);         
    if (type == 4) return sd_star(p, par.z, par.y, 3);         
    if (type == 5) return sd_triangle(p, par.x, par.z);        
    if (type == 6) return sd_box_hollow(p, par.xy, par.z);     
    if (type == 7) { p.x = abs(p.x) - par.x; return sd_arc(p, par.x, par.y, par.w); }
    if (type == 8) return sd_acorn_socket(p, par.xy, par.z);
    return sd_box(p, par.xy, par.z);
}

// Helper to determine the "scale" of the object for relative scribbling
float get_feature_approx_scale(int type, vec4 par) {
    if (type == 0) return min(par.x, par.y); // Box: use smallest dimension
    if (type == 1) return par.x;             // Arc: use outer radius
    if (type == 2) return par.y * 0.5;       // Caret: use half length
    if (type == 3 || type == 4) return par.y;// Star: use length
    if (type == 5) return par.x;             // Triangle: use radius
    if (type == 8) return min(par.x, par.y); // Acorn: use smallest dimension
    return 0.1; // Default fallback scale
}

vec2 to_local(vec2 uv, vec2 anchor, vec2 offset, float rot) {
    vec2 p = uv - (anchor + offset);
    float c = cos(rot);
    float s = sin(rot);
    return vec2(p.x * c - p.y * s, p.x * s + p.y * c);
}

float resolve_feature(vec2 local_p, int type, vec4 params) {
    float d = get_sdf(local_p, type, params);
    float aa = fwidth(local_p.x) * 1.5;
    
    // Check if this feature supports scribbling (currently just Acorn/Type 8, but logic applies to all)
    if (type == 8) {
        float outline = 1.0 - smoothstep(scr_outline_width - aa, scr_outline_width, abs(d));
        
        // Calculate relative scale and pass to scribbler
        float feat_scale = get_feature_approx_scale(type, params);
        float fill = scribble_stop_motion(local_p, 1.0, feat_scale);
        
        float inside_mask = 1.0 - smoothstep(0.0, aa, d);
        return max(outline, fill * inside_mask);
    }
    return 1.0 - smoothstep(-aa, aa, d);
}

void fragment() {
    vec2 uv = (vec2(UV.x, 1.0 - UV.y) - face_center) / face_scale;
    
    float mask_le = resolve_feature(to_local(uv, le_origin, le_offset, le_rot), le_type, le_params);
    float mask_re = resolve_feature(to_local(uv, re_origin, re_offset, re_rot), re_type, re_params);
    float mask_m  = resolve_feature(to_local(uv, m_origin, m_offset, m_rot), m_type, m_params);
    
    float total_mask = max(max(mask_le, mask_re), mask_m);
    ALBEDO = mix(bg_color, face_color, clamp(total_mask, 0.0, 1.0));
}