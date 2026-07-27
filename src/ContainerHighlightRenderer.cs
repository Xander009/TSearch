#nullable disable
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TSearch
{
    public class ContainerHighlightRenderer : IRenderer
    {
        private static readonly Cuboidf FullCube = new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f);

        private readonly ICoreClientAPI capi;
        private readonly TSearchConfig config;

        private IShaderProgram prog;
        private MeshRef fillMesh;
        private MeshRef edgeMesh;
        private readonly Matrixf modelMat = new();

        // Per block id: the tessellated shape mesh, or null => render its selection boxes instead
        // (used for containers that tessellate to a placeholder full cube, e.g. the reed basket).
        private readonly Dictionary<int, MeshRef> shapeCache = new();

        private volatile List<BlockPos> positions = new();

        public double RenderOrder => 0.5;
        public int RenderRange => 128;

        private bool GlowStyle => config.HighlightStyle != "box";

        public bool ShaderReady => prog != null && !prog.LoadError && !prog.Disposed;

        public bool CanRender => ShaderReady;

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
            IBlockAccessor ba = capi.World.BlockAccessor;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            float pulse = 0.7f + 0.3f * (float)Math.Sin(capi.ElapsedMilliseconds / 350.0);

            rpi.GlToggleBlend(true);
            rpi.GLDisableDepthTest();
            rpi.GlDisableCullFace();

            prog.Use();
            prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
            prog.UniformMatrix("modelViewMatrix", rpi.CameraMatrixOriginf);
            prog.Uniform("glow", config.Glow * pulse);
            rpi.LineWidth = 2f;

            if (GlowStyle)
            {
                var fill = new Vec4f(config.GlowR(), config.GlowG(), config.GlowB(), config.GlowA());
                foreach (BlockPos bp in pos) RenderShapeGlow(rpi, ba, bp, camPos, fill);
            }
            else
            {
                var fill = new Vec4f(config.FillR(), config.FillG(), config.FillB(), config.FillA());
                var edge = new Vec4f(config.EdgeR(), config.EdgeG(), config.EdgeB(), config.EdgeA());
                foreach (BlockPos bp in pos) DrawBox(rpi, bp, FullCube, camPos, fill, edge, true);
            }

            prog.Stop();

            rpi.GLEnableDepthTest();
            rpi.GlEnableCullFace();
            rpi.GlToggleBlend(false);
        }

        private void RenderShapeGlow(IRenderAPI rpi, IBlockAccessor ba, BlockPos bp, Vec3d camPos, Vec4f fill)
        {
            Block block = ba.GetBlock(bp);
            MeshRef shape = GetShapeMesh(block);

            if (shape != null)
            {
                modelMat.Identity();
                modelMat.Translate((float)(bp.X - camPos.X), (float)(bp.Y - camPos.Y), (float)(bp.Z - camPos.Z));
                prog.UniformMatrix("modelMatrix", modelMat.Values);
                prog.Uniform("rgbaIn", fill);
                rpi.RenderMesh(shape);
                return;
            }

            // Placeholder-cube container (e.g. reed basket): fall back to its fitted selection boxes.
            Cuboidf[] boxes = block?.GetSelectionBoxes(ba, bp);
            if (boxes == null || boxes.Length == 0)
            {
                DrawBox(rpi, bp, FullCube, camPos, fill, default, false);
                return;
            }
            foreach (Cuboidf box in boxes)
                DrawBox(rpi, bp, box, camPos, fill, default, false);
        }

        private void DrawBox(IRenderAPI rpi, BlockPos bp, Cuboidf box, Vec3d camPos, Vec4f fill, Vec4f edge, bool withEdge)
        {
            const float m = 0.02f;

            modelMat.Identity();
            modelMat.Translate(
                (float)(bp.X - camPos.X) + box.X1 - m,
                (float)(bp.Y - camPos.Y) + box.Y1 - m,
                (float)(bp.Z - camPos.Z) + box.Z1 - m);
            modelMat.Scale(box.X2 - box.X1 + 2f * m, box.Y2 - box.Y1 + 2f * m, box.Z2 - box.Z1 + 2f * m);
            prog.UniformMatrix("modelMatrix", modelMat.Values);

            prog.Uniform("rgbaIn", fill);
            rpi.RenderMesh(fillMesh);

            if (withEdge)
            {
                prog.Uniform("rgbaIn", edge);
                rpi.RenderMesh(edgeMesh);
            }
        }

        private MeshRef GetShapeMesh(Block block)
        {
            if (block == null || block.Id == 0) return null;
            if (shapeCache.TryGetValue(block.Id, out MeshRef cached)) return cached;

            MeshRef result = null;
            try
            {
                capi.Tesselator.TesselateBlock(block, out MeshData md);
                if (md != null && md.xyz != null && !IsFullBlock(md))
                    result = capi.Render.UploadMesh(md);
            }
            catch { result = null; }

            shapeCache[block.Id] = result;
            return result;
        }

        // A mesh that fills the whole 0..1 block is almost always a placeholder for a container
        // whose real shape is drawn by a block-entity renderer; use selection boxes for those.
        private static bool IsFullBlock(MeshData md)
        {
            float[] xyz = md.xyz;
            int count = Math.Min(xyz.Length, md.VerticesCount * 3);
            if (count < 3) return true;

            float minX = 9, minY = 9, minZ = 9, maxX = -9, maxY = -9, maxZ = -9;
            for (int i = 0; i + 2 < count; i += 3)
            {
                float x = xyz[i], y = xyz[i + 1], z = xyz[i + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            const float e = 0.02f;
            return minX > -e && minY > -e && minZ > -e && maxX < 1 + e && maxY < 1 + e && maxZ < 1 + e
                && maxX - minX > 1 - 2 * e && maxY - minY > 1 - 2 * e && maxZ - minZ > 1 - 2 * e;
        }

        private void BuildMeshes()
        {
            float[][] c =
            {
                new[] { 0f, 0f, 0f },
                new[] { 1f, 0f, 0f },
                new[] { 1f, 0f, 1f },
                new[] { 0f, 0f, 1f },
                new[] { 0f, 1f, 0f },
                new[] { 1f, 1f, 0f },
                new[] { 1f, 1f, 1f },
                new[] { 0f, 1f, 1f },
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
            foreach (MeshRef m in shapeCache.Values)
                if (m != null) capi.Render.DeleteMesh(m);
            shapeCache.Clear();
            prog?.Dispose();
        }

        private const string VertexCode = @"#version 330 core
layout(location = 0) in vec3 vertexPositionIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform mat4 modelMatrix;

void main(void)
{
    gl_Position = projectionMatrix * modelViewMatrix * modelMatrix * vec4(vertexPositionIn, 1.0);
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
