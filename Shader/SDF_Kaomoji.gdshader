shader_type spatial;

uniform vec2 face_center = vec2(0.5, 0.5);
uniform float face_scale = 1.0;
uniform vec3 face_color : source_color = vec3(0.1, 0.1, 0.1);
uniform vec3 bg_color : source_color = vec3(1.0, 1.0, 1.0);

uniform float le_alpha = 1.0; uniform float re_alpha = 1.0; uniform float m_alpha = 1.0;

uniform vec2 le_origin; uniform vec2 re_origin; uniform vec2 m_origin;
uniform vec4 le_params; uniform float le_rot; uniform vec2 le_offset; uniform int le_type;
uniform vec4 re_params; uniform float re_rot; uniform vec2 re_offset; uniform int re_type;
uniform vec4 m_params;  uniform float m_rot;  uniform vec2 m_offset;  uniform int m_type;

float sd_box(vec2 p, vec2 b, float r) { vec2 d = abs(p) - b + vec2(r); return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r; }
float sd_arc(vec2 p, float ra, float rb, float aperture) { vec2 sc = vec2(sin(aperture), cos(aperture)); p.x = abs(p.x); return (sc.y*p.x > sc.x*p.y) ? length(p - sc * ra) - rb : abs(length(p) - ra) - rb; }
float sd_caret(vec2 p, float thick, float len, float angle) { p.x = abs(p.x); vec2 dir = vec2(cos(angle), sin(angle)); return length(p - dir * clamp(dot(p, dir), 0.0, len)) - thick; }
float sd_star(vec2 p, float thick, float len, int points) { float d = 1e10; for(int i=0; i < points; i++) { float ang = 3.14159 * float(i) / float(points); vec2 dir = vec2(cos(ang), sin(ang)); d = min(d, length(p - dir * clamp(dot(p, dir), -len, len)) - thick); } return d; }
float sd_triangle(vec2 p, float r, float thick) { const float k = sqrt(3.0); p.x = abs(p.x) - r; p.y = p.y + r / k; if (p.x + k*p.y > 0.0) p = vec2(p.x - k*p.y, -k*p.x - p.y) / 2.0; p.x -= clamp(p.x, -2.0*r, 0.0); return abs(-length(p)*sign(p.y)) - thick; }
float sd_box_hollow(vec2 p, vec2 b, float thick) { return abs(sd_box(p, b, 0.0)) - thick; }

float sd_acorn(vec2 p, vec2 bounds, float rounding) { 
    float cup = sd_box(p + vec2(0.0, 0.01), bounds, rounding); 
    float bar = sd_box(p - vec2(0.0, bounds.y * 0.8), vec2(bounds.x * 1.4, 0.015), 0.005); 
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
    if (type == 8) return sd_acorn(p, par.xy, par.z);
    return 1e5;
}

vec2 to_local(vec2 uv, vec2 anchor, vec2 offset, float rot) {
    vec2 p = uv - (anchor + offset);
    float c = cos(rot); float s = sin(rot);
    return vec2(p.x * c - p.y * s, p.x * s + p.y * c);
}

void fragment() {
    float lod = fwidth(UV.x) * 1.5;
    vec2 uv = (vec2(UV.x, 1.0 - UV.y) - face_center) / face_scale;
    
    float m_le = (1.0 - smoothstep(-lod, lod, get_sdf(to_local(uv, le_origin, le_offset, le_rot), le_type, le_params))) * le_alpha;
    float m_re = (1.0 - smoothstep(-lod, lod, get_sdf(to_local(uv, re_origin, re_offset, re_rot), re_type, re_params))) * re_alpha;
    float m_m  = (1.0 - smoothstep(-lod, lod, get_sdf(to_local(uv, m_origin, m_offset,  m_rot),  m_type,  m_params)))  * m_alpha;
    
    ALBEDO = mix(bg_color, face_color, clamp(max(max(m_le, m_re), m_m), 0.0, 1.0));
}