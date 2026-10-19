using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TSearch 
{
    public class TSearchSystem : ModSystem
    {
        private const int HighlightSlotId = 77;

        private ICoreClientAPI capi;
        private TSearchConfig config;
        private ContainerHighlightRenderer renderer;
        private Harmony harmony;
        private static TSearchSystem instance;
        private static bool overlayErrorLogged;
        private LoadedTexture whiteTex;
        private static int dbgPostfixCalls;
        private readonly Dictionary<GuiElementItemSlotGridBase, string> dbgGrids = new();
        private long dbgLastSummary;

        private bool isHighlighting;
        private AssetLocation searchCode;
        private BlockPos lastPlayerPos;
        private long highlightStartedAt;

        private static readonly FieldInfo fInteractiveElements = typeof(GuiComposer)
            .GetField("interactiveElements", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo fStaticElements = typeof(GuiComposer)
            .GetField("staticElements", BindingFlags.NonPublic | BindingFlags.Instance);

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
            instance = this;
            harmony = new Harmony("tsearch");
            MethodInfo target = AccessTools.Method(typeof(GuiElementItemSlotGridBase), nameof(GuiElementItemSlotGridBase.RenderInteractiveElements));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(TSearchSystem), nameof(SlotGridRenderPostfix)));
            Dbg($"startup: config ScanRange={config.ScanRange} ScanRangeVertical={config.ScanRangeVertical} " +
                $"HighlightDurationMs={config.HighlightDurationMs} ClearDistanceBlocks={config.ClearDistanceBlocks} " +
                $"GlowColor=[{string.Join(",", config.GlowColor ?? new int[0])}]");
            Dbg($"startup: Harmony target={(target == null ? "NOT FOUND" : target.DeclaringType?.Name + "." + target.Name)}, " +
                $"patched methods={string.Join(", ", harmony.GetPatchedMethods())}; container renderer CanRender={renderer.CanRender}");

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
                Dbg("clear: T pressed again");
                ClearAll();
                Msg("[TSearch] Cleared.");
                return true;
            }

            ItemStack targetStack = GetHoveredItemStack();
            Dbg($"search: T pressed, hovered item={targetStack?.Collectible?.Code?.ToString() ?? "none"}");
            if (targetStack?.Collectible == null)
            {
                Msg("[TSearch] Hover over an item in your inventory, hand, or handbook page, then press T.");
                return true;
            }

            searchCode = targetStack.Collectible.Code;
            BlockPos playerPos = capi.World.Player.Entity.Pos.AsBlockPos;
            lastPlayerPos = playerPos.Copy();

            List<BlockPos> matched = ScanForContainers(playerPos, searchCode);
            Dbg($"search: scanned around {playerPos} (r={config.ScanRange}, v={config.ScanRangeVertical}), " +
                $"{matched.Count} matching container(s): {string.Join(" ", matched.GetRange(0, Math.Min(10, matched.Count)))}");

            if (matched.Count == 0)
            {
                searchCode = null;
                Msg($"[TSearch] No nearby containers found with {targetStack.GetName()}.");
                return true;
            }

            isHighlighting = true;
            highlightStartedAt = capi.ElapsedMilliseconds;
            ApplyContainerHighlights(matched);

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

        private void ApplyContainerHighlights(List<BlockPos> positions)
        {
            Dbg($"highlight: path={(renderer.CanRender ? "custom glow renderer" : "engine fallback (shader not ready)")}");
            if (renderer.CanRender)
            {
                renderer.SetPositions(positions);
                return;
            }

            var pairs = new List<BlockPos>();
            var colors = new List<int>();
            int color = config.GlowColorRgba(255);
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

        private static void SlotGridRenderPostfix(GuiElementItemSlotGridBase __instance)
        {
            dbgPostfixCalls++;
            try { instance?.RenderSlotOverlay(__instance); }
            catch (Exception e)
            {
                if (overlayErrorLogged) return;
                overlayErrorLogged = true;
                instance?.capi.Logger.Error("[TSearch] Slot overlay failed: {0}", e);
            }
        }

        private void RenderSlotOverlay(GuiElementItemSlotGridBase grid)
        {
            if (searchCode == null) return;
            if (grid.SlotBounds == null || grid.renderedSlots == null)
            {
                DbgGrid(grid, $"skipped: SlotBounds={(grid.SlotBounds == null ? "null" : "ok")} renderedSlots={(grid.renderedSlots == null ? "null" : "ok")}");
                return;
            }

            int matches = 0;
            foreach (var kvp in grid.renderedSlots)
                if (kvp.Value?.Itemstack?.Collectible?.Code == searchCode) matches++;
            if (matches == 0)
            {
                DbgGrid(grid, $"no matches in {grid.renderedSlots.Count} slots");
                return;
            }

            float pulse = 0.5f + 0.5f * (float)Math.Sin(capi.ElapsedMilliseconds / 300.0);
            float r = config.GlowR(), g = config.GlowG(), bl = config.GlowB();
            float fillA = 0.15f + 0.15f * pulse;
            double t = Math.Max(3.0, GuiElement.scaled(3));
            int tex = WhiteTexture().TextureId;

            ElementBounds[] bounds = grid.SlotBounds;
            int i = 0, drawn = 0;
            string firstMatch = "-";
            foreach (var kvp in grid.renderedSlots)
            {
                if (i >= bounds.Length) break;
                ElementBounds b = bounds[i++];
                if (b == null) continue;

                double x = b.renderX, y = b.renderY, w = b.OuterWidth, h = b.OuterHeight;
                if (kvp.Value?.Itemstack?.Collectible?.Code != searchCode)
                {
                    Quad(tex, x, y, w, h, 0, 0, 0, 0.6f);
                    continue;
                }

                if (drawn == 0) firstMatch = $"x={x:F0} y={y:F0} {w:F0}x{h:F0}";
                drawn++;
                Quad(tex, x, y, w, h, r, g, bl, fillA);
                Quad(tex, x, y, w, t, r, g, bl, 1);
                Quad(tex, x, y + h - t, w, t, r, g, bl, 1);
                Quad(tex, x, y + t, t, h - 2 * t, r, g, bl, 1);
                Quad(tex, x + w - t, y + t, t, h - 2 * t, r, g, bl, 1);
            }
            DbgGrid(grid, $"slots={grid.renderedSlots.Count} bounds={bounds.Length} matches={matches} framed={drawn} " +
                $"firstMatch={firstMatch} tex={tex}");
        }

        private void Quad(int tex, double x, double y, double w, double h, float r, float g, float b, float a)
            => capi.Render.Render2DTexturePremultipliedAlpha(tex, x, y, w, h, 100, new Vec4f(r * a, g * a, b * a, a));

        private LoadedTexture WhiteTexture()
        {
            if (whiteTex != null && whiteTex.TextureId != 0) return whiteTex;
            using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, 4, 4);
            using (var ctx = new Cairo.Context(surface))
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Paint();
            }
            whiteTex ??= new LoadedTexture(capi);
            capi.Gui.LoadOrUpdateCairoTexture(surface, false, ref whiteTex);
            return whiteTex;
        }

        private void Dbg(string msg)
        {
            if (config?.Debug == true) capi.Logger.Notification("[TSearch debug] " + msg);
        }

        private void DbgGrid(GuiElementItemSlotGridBase grid, string info)
        {
            if (config?.Debug == true) dbgGrids[grid] = info;
        }

        private void DbgOverlaySummary()
        {
            dbgLastSummary = capi.ElapsedMilliseconds;
            var open = new List<string>();
            int gridCount = 0;
            foreach (GuiDialog d in capi.Gui.OpenedGuis)
                if (d?.DialogType == EnumDialogType.Dialog) open.Add(d.GetType().Name);
            foreach (GuiElementItemSlotGridBase g in GetAllSlotGrids()) gridCount++;

            Dbg($"overlay: search={searchCode?.ToString() ?? "none"} hook calls last second={dbgPostfixCalls}, " +
                $"open windows=[{string.Join(", ", open)}], slot grids found={gridCount}");
            foreach (var kv in dbgGrids)
                Dbg($"overlay:   {kv.Key.GetType().Name}#{kv.Key.GetHashCode()}: {kv.Value}");
            dbgPostfixCalls = 0;
            dbgGrids.Clear();
        }

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

            Vec3d eyePos = entity.CameraPos;
            if (eyePos == null || (eyePos.X == 0 && eyePos.Y == 0 && eyePos.Z == 0))
                eyePos = entity.Pos.XYZ.Add(0, entity.LocalEyePos.Y, 0);

            Vec3d target = new Vec3d(nearest.X + 0.5, nearest.Y + 0.5, nearest.Z + 0.5);
            Vec3d dir = target.Sub(eyePos);
            double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (len < 1e-6) return;
            dir.X /= len; dir.Y /= len; dir.Z /= len;

            float yaw = (float)Math.Atan2(dir.X, dir.Z);
            float lookPitch = (float)Math.PI;

            player.CameraYaw = yaw;
            player.CameraPitch = lookPitch;
            capi.Input.MousePitch = lookPitch;
            entity.Pos.Pitch = lookPitch;
        }

        private void CloseOpenDialogs()
        {
            var open = new List<GuiDialog>(capi.Gui.OpenedGuis);
            foreach (GuiDialog dialog in open)
            {
                if (dialog == null || !dialog.IsOpened()) continue;
                if (dialog.DialogType != EnumDialogType.Dialog) continue;
                try { dialog.TryClose(); } catch { }
            }
        }

        private ItemStack GetHoveredItemStack()
        {
            ItemSlot mouseSlot = capi.World.Player.InventoryManager.MouseItemSlot;
            if (mouseSlot?.Itemstack != null) return mouseSlot.Itemstack;

            foreach (GuiElementItemSlotGridBase slotGrid in GetAllSlotGrids())
            {
                int hoverId = slotGrid.hoverSlotId;
                if (hoverId < 0) continue;
                IInventory inv = slotGrid.hoverInv;
                if (inv == null) continue;
                ItemSlot slot = inv[hoverId];
                if (slot?.Itemstack != null) return slot.Itemstack;
            }

            ItemStack handbookStack = GetHandbookHoveredStack();
            if (handbookStack != null) return handbookStack;

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

        private void OnTick(float dt)
        {
            if (!isHighlighting) return;

            if (config.Debug && capi.ElapsedMilliseconds - dbgLastSummary >= 1000) DbgOverlaySummary();

            if (capi.ElapsedMilliseconds - highlightStartedAt > config.HighlightDurationMs)
            {
                Dbg($"clear: timed out after {config.HighlightDurationMs} ms");
                ClearAll();
                return;
            }

            if (lastPlayerPos != null)
            {
                BlockPos cur = capi.World.Player.Entity.Pos.AsBlockPos;
                double clearDist = Math.Max(config.ClearDistanceBlocks, config.ScanRange);
                double clearSq = clearDist * clearDist;
                if (cur.DistanceSqTo(lastPlayerPos.X, lastPlayerPos.Y, lastPlayerPos.Z) > clearSq)
                {
                    Dbg($"clear: walked more than {clearDist} blocks from {lastPlayerPos}");
                    ClearAll();
                    return;
                }
            }
        }

        private void ClearAll()
        {
            ClearContainerHighlights();
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
            harmony?.UnpatchAll("tsearch");
            instance = null;
            renderer?.Dispose();
            whiteTex?.Dispose();
            renderer = null;
            base.Dispose();
        }
    }
}
