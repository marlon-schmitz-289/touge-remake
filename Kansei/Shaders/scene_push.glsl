// Push constants shared by world.*, car.*, effect.* and rain.* (720 bytes: fine on Metal's 4 KB, Vulkan only guarantees 128).
// Written by WorldRenderer.WritePush. Positions/directions in world space, colours linear.
layout(push_constant) uniform Push {
    mat4 uMvp;
    mat4 uModel;        // model → world (world meshes: the player car body for its contact shadow; rain.vert: rain parameters)
    vec4 uFog;          // rgb fog colour, a = glow of the dynamic lights in the fog (in-scattering, 0 = none)
    vec4 uEye;          // xyz camera, w = 1: sky mesh (unlit, vertex alpha ignored); effect.frag: mode
    vec4 uSun;          // xyz towards the sun, w = direct sun strength
    vec4 uAmbient;      // rgb ambient for lit objects (cars), w = share of the baked light kept in shadow (world)
    vec4 uParams;       // x = shadows on, y = wetness 0..1, z = env-map strength, w = brake light 0..1
    vec4 uShadowTexel;  // xyz = world size of a shadow texel per cascade, w = 1 / atlas height (one tile)
    mat4 uShadow[3];    // world → cascade tile uv (xy) + depth (z)
    vec4 uSpotPos[2];   // headlights: xyz, w = tan of the horizontal half spread
    vec4 uSpotDir[2];   // beam axis xyz, w = tan of the vertical half spread
    vec4 uSpotColor;    // rgb intensity (0 = off), w = range
    vec4 uPointPos[4];  // street lights: xyz, w = radius (0 = unused)
    vec4 uPointColor;   // rgb intensity, w = overlay layer pull in metres (world.vert, car.vert)
    vec4 uFogParams;    // x = linear fog start (m), y = 1 / (end − start), z = height-fog density at uFogSun.w (1/m), w = 1 / its scale height
    vec4 uFogSun;       // rgb sun light scattered into the fog towards the sun, w = height-fog base altitude
    vec4 uSky;          // rgb zenith colour (wet reflections), w = time (s)
    vec4 uTailPos[2];   // rear lamps as small point lights (w unused): they light the ground and streak on wet roads
    vec4 uTailColor;    // rgb intensity (0 = off)
    vec4 uSunColor;     // rgb tint of the direct sun, w = sun glints on the world (0 = none)
    vec4 uShadeSky;     // rgb tint of the shade (baked keep / car ambient) on upward normals, w = contact shadow under the car (world)
    vec4 uShadeGround;  // rgb tint of the shade on downward normals (bounce from the ground)
} pc;
