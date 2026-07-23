#nullable disable
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace TSearch
{
    /// <summary>
    /// Draws a translucent box plus a bright wireframe around every matched
    /// container. Rendering happens with depth testing disabled and a tiny
    /// custom shader, so the boxes are visible straight through walls — the
    /// engine's own HighlightBlocks is depth-tested and can't do this.
    /// </summary>
    public class ContainerHighlightRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly TSearchConfig config;

        private IShaderProgram prog;
        private MeshRef fillMesh;   // 12 triangles, translucent body
        private MeshRef edgeMesh;   // 12 line segments, bright outline

        // Positions of matched container blocks (min corner). Swapped atomically.
        private volatile List<BlockPos> positions = new();

        public double RenderOrder => 0.5;   // after opaque terrain, before we restore state
        public int RenderRange => 128;

        public bool ShaderReady => prog != null && !prog.LoadError && !prog.Disposed;

        public ContainerHighlightRenderer(ICoreClientAPI capi, TSearchConfig config)
        {
            this.capi = capi;
            this.config = config;

            BuildMeshes();

            // Compile now, and recompile whenever the game reloads shaders (F6 / resolution change).
            capi.Event.ReloadShader += LoadShader;
            LoadShader();

            capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "tsearch-highlights");
        }

        public void SetPositions(List<BlockPos> newPositions)
        {
            positions = newPositions ?? new List<BlockPos>();
        }

        public void Clear() => positions = new List<BlockPos>();

        public bool LoadShader()
        {
            IShaderProgram p = capi.Shader.NewShaderProgram();
            p.AssetDomain = "tsearch";
            p.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
            p.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
            p.VertexShader.Code = VertexCode;
            p.FragmentShader.Code = FragmentCode;

            capi.Shader.RegisterMemoryShaderProgram("tsearch-highlight", p);
            bool ok = p.Compile();
            if (ok) prog = p;
            return ok;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            List<BlockPos> pos = positions;
            if (pos.Count == 0 || !ShaderReady) return;

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlToggleBlend(true);
            rpi.GLDisableDepthTest();     // <-- the "see through walls" bit
            rpi.GlDisableCullFace();

            prog.Use();
            prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
            prog.UniformMatrix("modelViewMatrix", rpi.CameraMatrixOriginf);
            prog.Uniform("glow", config.Glow);

            var fill = new Vec4f(config.FillR(), config.FillG(), config.FillB(), config.FillA());
            var edge = new Vec4f(config.EdgeR(), config.EdgeG(), config.EdgeB(), config.EdgeA());

            rpi.LineWidth = 2f;

            foreach (BlockPos bp in pos)
            {
                prog.Uniform("origin",
                    (float)(bp.X - camPos.X),
                    (float)(bp.Y - camPos.Y),
                    (float)(bp.Z - camPos.Z));

                prog.Uniform("rgbaIn", fill);
                rpi.RenderMesh(fillMesh);

                prog.Uniform("rgbaIn", edge);
                rpi.RenderMesh(edgeMesh);
            }

            prog.Stop();

            rpi.GLEnableDepthTest();
            rpi.GlEnableCullFace();
            rpi.GlToggleBlend(false);
        }

        private void BuildMeshes()
        {
            // Slight outward margin so the box wraps the block instead of z-fighting its faces.
            const float lo = -0.005f;
            const float hi = 1.005f;

            // 8 corners
            float[][] c =
            {
                new[] { lo, lo, lo }, // 0
                new[] { hi, lo, lo }, // 1
                new[] { hi, lo, hi }, // 2
                new[] { lo, lo, hi }, // 3
                new[] { lo, hi, lo }, // 4
                new[] { hi, hi, lo }, // 5
                new[] { hi, hi, hi }, // 6
                new[] { lo, hi, hi }, // 7
            };

            // ---- filled cube (triangles) ----
            var fill = new MeshData(8, 36, false, false, true, false);
            foreach (float[] v in c) fill.AddVertexSkipTex(v[0], v[1], v[2], ColorUtil.WhiteArgb);
            int[] tri =
            {
                0,1,2, 0,2,3,   // bottom
                4,6,5, 4,7,6,   // top
                0,5,1, 0,4,5,   // -z
                3,2,6, 3,6,7,   // +z
                0,3,7, 0,7,4,   // -x
                1,5,6, 1,6,2,   // +x
            };
            foreach (int i in tri) fill.AddIndex(i);
            fill.SetMode(EnumDrawMode.Triangles);
            fillMesh = capi.Render.UploadMesh(fill);

            // ---- wireframe edges (line list) ----
            var edge = new MeshData(8, 24, false, false, true, false);
            foreach (float[] v in c) edge.AddVertexSkipTex(v[0], v[1], v[2], ColorUtil.WhiteArgb);
            int[] lines =
            {
                0,1, 1,2, 2,3, 3,0,   // bottom ring
                4,5, 5,6, 6,7, 7,4,   // top ring
                0,4, 1,5, 2,6, 3,7,   // verticals
            };
            foreach (int i in lines) edge.AddIndex(i);
            edge.SetMode(EnumDrawMode.Lines);
            edgeMesh = capi.Render.UploadMesh(edge);
        }

        public void Dispose()
        {
            capi.Event.ReloadShader -= LoadShader;
            capi.Render.DeleteMesh(fillMesh);
            capi.Render.DeleteMesh(edgeMesh);
            prog?.Dispose();
        }

        // Minimal shader: position + per-box world origin, flat color. No fog/lighting
        // so the color is exactly what the config asks for.
        private const string VertexCode = @"#version 330 core
layout(location = 0) in vec3 vertexPositionIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform vec3 origin;

void main(void)
{
    gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn + origin, 1.0);
    // Nudge toward the camera a hair so coplanar edges/fill don't fight.
    gl_Position.w += 0.0006;
}
";

        private const string FragmentCode = @"#version 330 core
uniform vec4 rgbaIn;
uniform float glow;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

void main(void)
{
    outColor = rgbaIn;
    outGlow = vec4(glow, 0.0, 0.0, rgbaIn.a);
}
";
    }
}
