#nullable disable
namespace TSearch 
{
    public class TSearchConfig
    {
        public int ScanRange = 32;

        public int ScanRangeVertical = 3;

        public int HighlightDurationMs = 10000;

        public double ClearDistanceBlocks = 6;

        public bool SearchFromHand = false;

        public bool SeeThrough = true;

        public bool SnapCameraToNearest = true;

        public bool CloseGuisOnSnap = true;

        public bool PlaySound = true;

        public bool ChatFeedback = true;

        public int[] EdgeColor = { 255, 165, 0, 255 };

        public int[] FillColor = { 255, 165, 0, 60 };

        public float Glow = 0.9f;

        private static float Chan(int[] rgba, int i, int fallback)
            => (rgba != null && rgba.Length > i ? rgba[i] : fallback) / 255f;

        public float EdgeR() => Chan(EdgeColor, 0, 255);
        public float EdgeG() => Chan(EdgeColor, 1, 165);
        public float EdgeB() => Chan(EdgeColor, 2, 0);
        public float EdgeA() => Chan(EdgeColor, 3, 255);

        public float FillR() => Chan(FillColor, 0, 255);
        public float FillG() => Chan(FillColor, 1, 165);
        public float FillB() => Chan(FillColor, 2, 0);
        public float FillA() => Chan(FillColor, 3, 60);

        public int EdgeColorRgba()
        {
            int r = Clamp255(EdgeColor, 0, 255);
            int g = Clamp255(EdgeColor, 1, 165);
            int b = Clamp255(EdgeColor, 2, 0);
            int a = Clamp255(EdgeColor, 3, 255);
            return Vintagestory.API.MathTools.ColorUtil.ToRgba(a, r, g, b);
        }

        private static int Clamp255(int[] rgba, int i, int fallback)
        {
            int v = (rgba != null && rgba.Length > i) ? rgba[i] : fallback;
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        public void Sanitize()
        {
            ScanRange = Clamp(ScanRange, 1, 128);
            ScanRangeVertical = Clamp(ScanRangeVertical, 1, 128);
            HighlightDurationMs = Clamp(HighlightDurationMs, 500, 600000);
            if (ClearDistanceBlocks < 0) ClearDistanceBlocks = 0;
            if (Glow < 0f) Glow = 0f;
            if (Glow > 1f) Glow = 1f;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
