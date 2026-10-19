namespace TSearch
{
    public class TSearchConfig
    {
        public int ScanRange = 32;

        public int ScanRangeVertical = 3;

        public int HighlightDurationMs = 10000;

        public double ClearDistanceBlocks = 6;

        public bool SearchFromHand = false;

        public bool SnapCameraToNearest = true;

        public bool CloseGuisOnSnap = true;

        public bool PlaySound = true;

        public bool ChatFeedback = true;

        public int[] GlowColor = { 255, 165, 0, 130 };

        public float Glow = 0.9f;

        public bool Debug = false;

        private static float Chan(int[] rgba, int i, int fallback)
            => (rgba != null && rgba.Length > i ? rgba[i] : fallback) / 255f;

        public float GlowR() => Chan(GlowColor, 0, 255);
        public float GlowG() => Chan(GlowColor, 1, 165);
        public float GlowB() => Chan(GlowColor, 2, 0);
        public float GlowA() => Chan(GlowColor, 3, 130);

        public int GlowColorRgba(int alpha)
        {
            int r = Clamp255(GlowColor, 0, 255);
            int g = Clamp255(GlowColor, 1, 165);
            int b = Clamp255(GlowColor, 2, 0);
            return Vintagestory.API.MathTools.ColorUtil.ToRgba(alpha, r, g, b);
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
