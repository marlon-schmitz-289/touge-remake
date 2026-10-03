#version 450

// Linear light: texture × material colour × (sky ambient + sun); gloss adds a sky/ground reflection (Schlick Fresnel)
// and an HDR sun highlight that feeds the bloom.

layout(location = 0) in vec3 vPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec4 vColor;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

layout(push_constant) uniform Push {
    mat4 uMvp;
    vec4 uSun; // xyz towards the sun (model space), w = sun intensity
    vec4 uEye; // xyz camera (model space)
    vec4 uUp;  // xyz world up (model space)
    vec4 uSky; // rgb ambient / reflected sky colour (linear)
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec4 t = texture(uTexture, vUv);
    if (t.a < 0.5) discard;
    vec3 v = normalize(pc.uEye.xyz - vPos);
    vec3 n = normalize(vNormal);
    if (dot(n, v) < 0.0) n = -n; // no culling: shade the side we see
    vec3 l = pc.uSun.xyz;
    vec3 base = t.rgb * vColor.rgb;
    vec3 c = base * (pc.uSky.rgb + pc.uSun.w * max(dot(n, l), 0.0));

    vec3 r = reflect(-v, n);
    vec3 env = pc.uSky.rgb * mix(0.25, 1.6, smoothstep(-0.15, 0.25, dot(r, pc.uUp.xyz)));
    float fresnel = 0.05 + 0.6 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
    float spec = pow(max(dot(r, l), 0.0), 120.0) * 6.0 * pc.uSun.w;
    c += vColor.a * (env * fresnel + spec);
    FragColor = vec4(c, 1.0);
}
