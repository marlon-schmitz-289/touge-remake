#version 450

// Full-screen triangle from gl_VertexIndex, no vertex buffer. Fragment shaders derive UVs from gl_FragCoord,
// which has the same top-left (Vulkan/Metal) or bottom-left (GL, like its textures) origin as the render targets.

void main()
{
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
