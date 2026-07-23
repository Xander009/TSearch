#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TSearch
{
    /// <summary>
    /// Client-only mod. Press T while hovering an item (inventory, hotbar, open
    /// container, hand, or Survival Handbook page) to highlight every nearby
    /// container holding that item. Highlights render through walls and the
    /// camera snaps toward the nearest match. Press T again to clear.
    /// Ported and expanded from the original InvSearch mod.
    /// </summary>
    public class TSearchSystem : ModSystem
    {
        private const int HighlightSlotId = 77; // engine-highlight channel for the fallback path

        private ICoreClientAPI capi;
        private TSearchConfig config;
        private ContainerHighlightRenderer renderer;

        private bool isHighlighting;
        private AssetLocation searchCode;
        private BlockPos lastPlayerPos;
        private long highlightStartedAt;

        private readonly List<GuiElementItemSlotGridBase> highlightedSlotGrids = new();

        // GuiComposer element dictionaries are still private in 1.22 — reflection needed.
        private static readonly FieldInfo fInteractiveElements = typeof(GuiComposer)
            .GetField("interactiveElements", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo fStaticElements = typeof(GuiComposer)
            .GetField("staticElements", BindingFlags.NonPublic | BindingFlags.Instance);

        // GuiDialogHandbook / BrowseHistoryElement reflection (VSSurvivalMod internals).
        private static readonly FieldInfo fBrowseHistory = typeof(GuiDialogHandbook)
            .GetField("browseHistory", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo fBrowseHistoryPage = typeof(GuiDialogHandbook)
            .Assembly.GetType("Vintagestory.GameContent.BrowseHistoryElement")
            ?.GetField("Page", BindingFlags.Public | BindingFlags.Instance);

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            config = LoadConfig(api);
            renderer = new ContainerHighlightRenderer(api, config);

            api.Input.RegisterHotKey(
                "tsearch",
                "Search Nearby Inventories for Hovered Item",
                GlKeys.T,
                HotkeyType.GUIOrOtherControls
            );
            api.Input.SetHotKeyHandler("tsearch", OnSearchKey);
            api.Event.RegisterGameTickListener(OnTick, 200);
        }

        private TSearchConfig LoadConfig(ICoreClientAPI api)
        {
            const string file = "tsearch.json";
            TSearchConfig cfg = null;
            try { cfg = api.LoadModConfig<TSearchConfig>(file); } catch { }
            if (cfg == null) cfg = new TSearchConfig();
            cfg.Sanitize();
            try { api.StoreModConfig(cfg, file); } catch { }
            return cfg;
        }

        private bool OnSearchKey(KeyCombination kc)
        {
            if (isHighlighting)
            {
                ClearAll();
                Msg("[TSearch] Cleared.");
                return true;
            }

            ItemStack targetStack = GetHoveredItemStack();
            if (targetStack?.Collectible == null)
            {
                Msg("[TSearch] Hover over an item in your inventory, hand, or handbook page, then press T.");
                return true;
            }

            searchCode = targetStack.Collectible.Code;
            BlockPos playerPos = capi.World.Player.Entity.Pos.AsBlockPos;
            lastPlayerPos = playerPos.Copy();

            List<BlockPos> matched = ScanForContainers(playerPos, searchCode);

            if (matched.Count == 0)
            {
                searchCode = null;
                Msg($"[TSearch] No nearby containers found with {targetStack.GetName()}.");
                return true;
            }

            isHighlighting = true;
            highlightStartedAt = capi.ElapsedMilliseconds;
            ApplyContainerHighlights(matched);
            HighlightSlotsInOpenGuis();

            if (config.PlaySound)
            {
                try { capi.Gui.PlaySound("camerasnap", false, 1f); } catch { }
            }

            BlockPos nearest = FindNearest(matched, playerPos);
            Msg($"[TSearch] Found {matched.Count} container(s) with {targetStack.GetName()} " +
                $"— nearest {(int)Math.Round(Math.Sqrt(nearest.DistanceSqTo(playerPos.X, playerPos.Y, playerPos.Z)))} blocks away.");

            if (config.SnapCameraToNearest) SnapCameraToNearest(nearest);

            return true;
        }

        private List<BlockPos> ScanForContainers(BlockPos playerPos, AssetLocation code)
        {
            var matched = new List<BlockPos>();
            int r = config.ScanRange;
            int rv = config.ScanRangeVertical;

            for (int x = -r; x <= r; x++)
            for (int y = -rv; y <= rv; y++)
            for (int z = -r; z <= r; z++)
            {
                BlockPos pos = playerPos.AddCopy(x, y, z);
                BlockEntity be = capi.World.BlockAccessor.GetBlockEntity(pos);
                IInventory inv = (be as IBlockEntityContainer)?.Inventory;
                if (inv == null) continue;

                foreach (ItemSlot slot in inv)
                {
                    if (slot?.Itemstack?.Collectible?.Code == code)
                    {
                        matched.Add(pos.Copy());
                        break;
                    }
                }
            }
            return matched;
        }

        // ---------------- container highlighting ----------------

        private void ApplyContainerHighlights(List<BlockPos> positions)
        {
            if (config.SeeThrough && renderer.ShaderReady)
            {
                renderer.SetPositions(positions);
                return;
            }

            // Fallback: plain engine highlight (depth-tested, not see-through).
            var pairs = new List<BlockPos>();
            var colors = new List<int>();
            int color = config.EdgeColorRgba();
            foreach (BlockPos pos in positions)
            {
                pairs.Add(pos.Copy());
                pairs.Add(pos.AddCopy(1, 1, 1));
                colors.Add(color);
            }
            capi.World.HighlightBlocks(
                capi.World.Player, HighlightSlotId,
                pairs, colors,
                EnumHighlightBlocksMode.Absolute,
                EnumHighlightShape.Cubes, 1f
            );
        }

        private void ClearContainerHighlights()
        {
            renderer.Clear();
            capi.World.HighlightBlocks(
                capi.World.Player, HighlightSlotId,
                new List<BlockPos>(),
                EnumHighlightBlocksMode.Absolute,
                EnumHighlightShape.Cubes
            );
        }

        // ---------------- slot highlighting in open GUIs ----------------

        private IEnumerable<GuiElementItemSlotGridBase> GetAllSlotGrids()
        {
            foreach (GuiDialog dialog in capi.Gui.OpenedGuis)
            {
                if (dialog?.Composers == null) continue;
                foreach (GuiComposer composer in dialog.Composers.Values)
                {
                    if (composer == null) continue;
                    foreach (FieldInfo fElems in new[] { fInteractiveElements, fStaticElements })
                    {
                        if (fElems?.GetValue(composer) is not Dictionary<string, GuiElement> elems) continue;
                        foreach (GuiElement elem in elems.Values)
                            if (elem is GuiElementItemSlotGridBase grid)
                                yield return grid;
                    }
                }
            }
        }

        private void HighlightSlotsInOpenGuis()
        {
            if (searchCode == null) return;
            ClearSlotHighlights();

            foreach (GuiElementItemSlotGridBase slotGrid in GetAllSlotGrids())
            {
                var slots = slotGrid.availableSlots;
                if (slots == null) continue;

                int matchKey = -1;
                foreach (var kvp in slots)
                {
                    if (kvp.Value?.Itemstack?.Collectible?.Code == searchCode)
                    {
                        matchKey = kvp.Key;
                        break;
                    }
                }

                if (matchKey < 0) continue;
                slotGrid.highlightSlotId = matchKey;
                highlightedSlotGrids.Add(slotGrid);
            }
        }

        private void ClearSlotHighlights()
        {
            foreach (GuiElementItemSlotGridBase grid in highlightedSlotGrids)
                try { grid.highlightSlotId = -1; } catch { }
            highlightedSlotGrids.Clear();
        }

        // ---------------- camera snap ----------------

        private static BlockPos FindNearest(List<BlockPos> positions, BlockPos from)
        {
            BlockPos nearest = null;
            double best = double.MaxValue;
            foreach (BlockPos p in positions)
            {
                double d = p.DistanceSqTo(from.X, from.Y, from.Z);
                if (d < best) { best = d; nearest = p; }
            }
            return nearest;
        }

        private void SnapCameraToNearest(BlockPos nearest)
        {
            if (nearest == null) return;
            if (config.CloseGuisOnSnap) CloseOpenDialogs();

            IClientPlayer player = capi.World.Player;
            EntityPlayer entity = player.Entity;

            // Ray origin = the actual camera position. Fall back to eye height if it
            // hasn't been populated yet (e.g. very first frame).
            Vec3d eyePos = entity.CameraPos;
            if (eyePos == null || (eyePos.X == 0 && eyePos.Y == 0 && eyePos.Z == 0))
                eyePos = entity.Pos.XYZ.Add(0, entity.LocalEyePos.Y, 0);

            Vec3d target = new Vec3d(nearest.X + 0.5, nearest.Y + 0.5, nearest.Z + 0.5);
            Vec3d dir = target.Sub(eyePos);
            double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (len < 1e-6) return;
            dir.X /= len; dir.Y /= len; dir.Z /= len;

            float yaw = (float)Math.Atan2(dir.X, dir.Z);

            // VS look-pitch convention (measured in-game): straight up = π/2, horizon = π, straight
            // down = 3π/2. So the pitch that looks along a direction of elevation e = asin(dir.Y) is
            // (π - e). Feeding a plain asin(dir.Y) (range ±π/2) lands outside the valid [π/2, 3π/2]
            // band and clamps to the up limit — which is why it used to snap straight up. Vertical
            // is driven by the entity's Pos.Pitch; yaw stays on CameraYaw (Pos.Yaw differs).
            float elevation = (float)Math.Asin(GameMath.Clamp((float)dir.Y, -1f, 1f));
            float lookPitch = (float)Math.PI - elevation;

            player.CameraYaw = yaw;
            player.CameraPitch = lookPitch;
            capi.Input.MousePitch = lookPitch;
            entity.Pos.Pitch = lookPitch;
        }

        private void CloseOpenDialogs()
        {
            // Copy first — TryClose mutates OpenedGuis. Only close real windows, never HUD elements.
            var open = new List<GuiDialog>(capi.Gui.OpenedGuis);
            foreach (GuiDialog dialog in open)
            {
                if (dialog == null || !dialog.IsOpened()) continue;
                if (dialog.DialogType != EnumDialogType.Dialog) continue;
                try { dialog.TryClose(); } catch { }
            }
            // Slots live inside those windows; once closed the highlights are gone anyway.
            ClearSlotHighlights();
        }

        // ---------------- hovered item resolution ----------------

        private ItemStack GetHoveredItemStack()
        {
            // 1. Mouse cursor
            ItemSlot mouseSlot = capi.World.Player.InventoryManager.MouseItemSlot;
            if (mouseSlot?.Itemstack != null) return mouseSlot.Itemstack;

            // 2. Hovered slot in an open GUI (hoverSlotId / hoverInv are public in 1.22)
            foreach (GuiElementItemSlotGridBase slotGrid in GetAllSlotGrids())
            {
                int hoverId = slotGrid.hoverSlotId;
                if (hoverId < 0) continue;
                IInventory inv = slotGrid.hoverInv;
                if (inv == null) continue;
                ItemSlot slot = inv[hoverId];
                if (slot?.Itemstack != null) return slot.Itemstack;
            }

            // 3. Currently viewed item page in the Survival Handbook
            ItemStack handbookStack = GetHandbookHoveredStack();
            if (handbookStack != null) return handbookStack;

            // 4. Active hand (opt-in)
            if (config.SearchFromHand)
                return capi.World.Player.Entity.ActiveHandItemSlot?.Itemstack;

            return null;
        }

        private ItemStack GetHandbookHoveredStack()
        {
            if (fBrowseHistory == null || fBrowseHistoryPage == null) return null;
            try
            {
                foreach (GuiDialog dialog in capi.Gui.OpenedGuis)
                {
                    if (dialog is not GuiDialogHandbook handbook) continue;
                    object historyObj = fBrowseHistory.GetValue(handbook);
                    if (historyObj == null) continue;

                    int count = (int)historyObj.GetType().GetProperty("Count").GetValue(historyObj);
                    if (count == 0) continue;

                    object topElement = historyObj.GetType().GetMethod("Peek").Invoke(historyObj, null);
                    if (topElement == null) continue;

                    object page = fBrowseHistoryPage.GetValue(topElement);
                    if (page is GuiHandbookItemStackPage itemPage)
                        return itemPage.Stack;
                }
            }
            catch { }
            return null;
        }

        // ---------------- lifecycle ----------------

        private void OnTick(float dt)
        {
            if (!isHighlighting) return;

            if (capi.ElapsedMilliseconds - highlightStartedAt > config.HighlightDurationMs)
            {
                ClearAll();
                return;
            }

            if (lastPlayerPos != null)
            {
                BlockPos cur = capi.World.Player.Entity.Pos.AsBlockPos;
                // Don't clear while the player is still inside the area they searched — otherwise
                // walking toward a highlighted container (up to ScanRange away) clears it almost
                // immediately. Effective threshold is at least the scan range.
                double clearDist = Math.Max(config.ClearDistanceBlocks, config.ScanRange);
                double clearSq = clearDist * clearDist;
                if (cur.DistanceSqTo(lastPlayerPos.X, lastPlayerPos.Y, lastPlayerPos.Z) > clearSq)
                {
                    ClearAll();
                    return;
                }
            }

            HighlightSlotsInOpenGuis();
        }

        private void ClearAll()
        {
            ClearContainerHighlights();
            ClearSlotHighlights();
            isHighlighting = false;
            searchCode = null;
        }

        private void Msg(string text)
        {
            if (config == null || config.ChatFeedback) capi.ShowChatMessage(text);
        }

        public override void Dispose()
        {
            if (capi != null && isHighlighting) ClearAll();
            renderer?.Dispose();
            renderer = null;
            base.Dispose();
        }
    }
}
