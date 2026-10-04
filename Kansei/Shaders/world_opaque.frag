#version 450

// world.glsl for batches whose texture and vertex alpha are fully opaque (CourseLoader): no discard, no alpha-to-coverage.
#define OPAQUE
#include "world.glsl"
