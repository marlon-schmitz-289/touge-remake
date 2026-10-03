// Push constants shared by world.* and car.* (576 bytes: fine on Metal's 4 KB, Vulkan only guarantees 128).
// Written by WorldRenderer.WritePush. Positions/directions in world space, colours linear.
layout(push_constant) uniform Push {
    mat4 uMvp;
    mat4 uModel;        // model → world (world meshes: identity)
    vec4 uFog;          // rgb fog colour, a = 1 / fog distance (0 = no fog)
    vec4 uEye;          // xyz camera, w = 1: sky mesh (unlit, vertex alpha ignored)
    vec4 uSun;          // xyz towards the sun, w = direct sun strength
    vec4 uAmbient;      // rgb ambient for lit objects (cars), w = share of the baked light kept in shadow (world)
    vec4 uParams;       // x = shadows on, y = wetness 0..1, z = env-map strength, w = brake light 0..1
    vec4 uShadowTexel;  // xyz = world size of a shadow texel per cascade, w = 1 / atlas height (one tile)
    mat4 uShadow[3];    // world → cascade tile uv (xy) + depth (z)
    vec4 uSpotPos[2];   // headlights: xyz, w = tan of the horizontal half spread
    vec4 uSpotDir[2];   // beam axis xyz, w = tan of the vertical half spread
    vec4 uSpotColor;    // rgb intensity (0 = off), w = range
    vec4 uPointPos[4];  // street lights: xyz, w = radius (0 = unused)
    vec4 uPointColor;   // rgb intensity
} pc;
