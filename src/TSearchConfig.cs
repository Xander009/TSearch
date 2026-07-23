#nullable disable
namespace TSearch
{
    /// <summary>
    /// User-editable settings, serialized to
    /// VintagestoryData/ModConfig/tsearch.json.
    /// Colors are RGBA arrays with each channel in the 0-255 range.
    /// </summary>
    public class TSearchConfig
    {
        /// <summary>Horizontal (X/Z) scan radius in blocks around the player.</summary>
        public int ScanRange = 32;

        /// <summary>Vertical (Y) scan radius in blocks. Kept small since containers are usually near your level.</summary>
        public int ScanRangeVertical = 3;

        /// <summary>How long highlights stay before auto-clearing, in milliseconds.</summary>
        public int HighlightDurationMs = 10000;

        /// <summary>Auto-clear once the player walks this many blocks from the search origin.
        /// The effective threshold is at least <see cref="ScanRange"/> so highlights survive while
        /// you walk to a container you just found.</summary>
        public double ClearDistanceBlocks = 6;

        /// <summary>Count an item held in the active hand as a valid search target (when nothing is hovered).</summary>
        public bool SearchFromHand = false;

        /// <summary>Draw highlights through walls (custom renderer). If false, uses the plain engine highlight.</summary>
        public bool SeeThrough = true;

        /// <summary>Snap the camera toward the nearest matched container when a search succeeds.</summary>
        public bool SnapCameraToNearest = true;

        /// <summary>Close all open GUIs before snapping the camera, for an unobstructed view.</summary>
        public bool CloseGuisOnSnap = true;

        /// <summary>Play a sound when a search finds one or more containers.</summary>
        public bool PlaySound = true;

        /// <summary>Print chat feedback ("Found N container(s)…") on each search.</summary>
        public bool ChatFeedback = true;

        /// <summary>Bright outline color of the highlight box, RGBA 0-255.</summary>
        public int[] EdgeColor = { 255, 165, 0, 255 };

        /// <summary>Translucent fill color of the highlight box, RGBA 0-255.</summary>
        public int[] FillColor = { 255, 165, 0, 60 };

        /// <summary>Extra glow (0-1) applied to the see-through highlight so it stays visible in the dark.</summary>
        public float Glow = 0.9f;

        // ---- derived helpers (methods, so they aren't serialized into the config file) ----

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

        /// <summary>Packed RGBA int used by the plain-engine highlight fallback.</summary>
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

        /// <summary>Clamp values loaded from disk so a hand-edited config can't crash the mod.</summary>
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
