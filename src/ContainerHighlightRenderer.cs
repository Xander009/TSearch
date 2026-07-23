#nullable disable
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace TSearch
{
    public class ContainerHighlightRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly TSearchConfig config;

        private IShaderProgram prog;
        private MeshRef fillMesh;
        private MeshRef edgeMesh;

        private volatile List<BlockPos> positions = new();

        public double RenderOrder => 0.5;
        public int RenderRange => 128;

        public bool ShaderReady => prog != null && !prog.LoadError && !prog.Disposed;

        public ContainerHighlightRenderer(ICoreClientAPI capi, TSearchConfig config)
        {
            this.capi = capi;
            this.config = config;

            BuildMeshes();

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
            rpi.GLDisableDepthTest();
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
            const float lo = -0.005f;
            const float hi = 1.005f;

            float[][] c =
            {
                new[] { lo, lo, lo },
                new[] { hi, lo, lo },
                new[] { hi, lo, hi },
                new[] { lo, lo, hi },
                new[] { lo, hi, lo },
                new[] { hi, hi, lo },
                new[] { hi, hi, hi },
                new[] { lo, hi, hi },
            };

            var fill = new MeshData(8, 36, false, false, true, false);
            foreach (float[] v in c) fill.AddVertexSkipTex(v[0], v[1], v[2], ColorUtil.WhiteArgb);
            int[] tri =
            {
                0,1,2, 0,2,3,
                4,6,5, 4,7,6,
                0,5,1, 0,4,5,
                3,2,6, 3,6,7,
                0,3,7, 0,7,4,
                1,5,6, 1,6,2,
            };
            foreach (int i in tri) fill.AddIndex(i);
            fill.SetMode(EnumDrawMode.Triangles);
            fillMesh = capi.Render.UploadMesh(fill);

            var edge = new MeshData(8, 24, false, false, true, false);
            foreach (float[] v in c) edge.AddVertexSkipTex(v[0], v[1], v[2], ColorUtil.WhiteArgb);
            int[] lines =
            {
                0,1, 1,2, 2,3, 3,0,
                4,5, 5,6, 6,7, 7,4,
                0,4, 1,5, 2,6, 3,7,
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

        private const string VertexCode = @"#version 330 core
layout(location = 0) in vec3 vertexPositionIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform vec3 origin;

void main(void)
{
    gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn + origin, 1.0); 
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
