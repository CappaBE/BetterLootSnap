using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("BetterLootSnap", "AI_Developer", "6.3.0")]
    [Description("Container loot overview and editor for BetterLoot 4.5.x (probability multipliers, min/max, rarity).")]
    public class BetterLootSnap : RustPlugin
    {
        private const string AdminPermission = "betterlootsnap.admin";
        private const string DataFileName = "BetterLoot/LootTables";
        private const string BackupFileName = "BetterLoot/LootTables_BetterLootSnap_backup";
        private const string UiName = "BLSnap_MainUI";
        private const string ScrollName = "BLSnap_ItemScroll";
        private const string NowNamePrefix = "BLSnap_Now_";

        private const int PageSize = 30;
        private const float ItemRowHeight = 46f;
        private const float NodeRowHeight = 38f;

        // ScrollView-rect binnen het hoofdpaneel.
        private const float ScrollXMin = 0.035f;
        private const float ScrollXMax = 0.965f;

        // Kolommen ITEMS-weergave (0..1 van de ScrollView-breedte).
        private const float ColItemMin = 0.015f;
        private const float ColItemMax = 0.24f;
        private const float ColSourceMin = 0.25f;
        private const float ColSourceMax = 0.50f;
        private const float ColMinMin = 0.52f;
        private const float ColMinMax = 0.62f;
        private const float ColMaxMin = 0.64f;
        private const float ColMaxMax = 0.74f;
        private const float ColRarityMin = 0.76f;
        private const float ColRarityMax = 0.97f;

        // Kolommen LOOT-weergave.
        private const float NEntryMin = 0.015f;
        private const float NEntryMax = 0.31f;
        private const float NContMin = 0.32f;
        private const float NContMax = 0.58f;
        private const float NBeforeMin = 0.59f;
        private const float NBeforeMax = 0.67f;
        private const float NNowMin = 0.68f;
        private const float NNowMax = 0.77f;
        private const float NMultMin = 0.80f;
        private const float NMultMax = 0.97f;

        private enum ViewMode
        {
            Loot,
            Items
        }

        private readonly Dictionary<ulong, EditSession> sessions =
            new Dictionary<ulong, EditSession>();

        private static readonly string[] Rarities =
        {
            "None",
            "Common",
            "Uncommon",
            "Rare",
            "VeryRare"
        };

        // Eén item-vermelding (Min / Max / Rarity).
        private class ItemEdit
        {
            // Pad vanaf het prefab-object: string = propertynaam, int = array-index.
            public List<object> Segments;
            public bool IsDirect;

            public string Shortname;
            public string Source;

            public int Min;
            public int Max;
            public string Rarity;

            public int OrigMin;
            public int OrigMax;
            public string OrigRarity;

            public bool MinChanged { get { return Min != OrigMin; } }
            public bool MaxChanged { get { return Max != OrigMax; } }
            public bool RarityChanged { get { return Rarity != OrigRarity; } }

            public bool IsDirty
            {
                get { return MinChanged || MaxChanged || RarityChanged; }
            }
        }

        // Eén lootslot (Probability) of lootgroep (Weight).
        private class NodeEdit
        {
            public List<object> Segments;

            public bool IsSlot;
            public bool IsRoot;
            public bool Editable;

            public string Name;
            public string RawName;
            public int Depth;
            public int SlotNumber;
            public string Contains;

            public double OrigValue;      // Probability (slot) of Weight (groep)
            public double Mult = 1.0;     // vermenigvuldiger t.o.v. origineel

            public NodeEdit Parent;
            public List<NodeEdit> Children = new List<NodeEdit>();

            public double SumOrig;
            public double SumNow;
            public double ChanceOrig;
            public double ChanceNow;

            public double CurrentValue
            {
                get
                {
                    if (IsSlot)
                        return Math.Min(1.0, Math.Round(OrigValue * Mult, 3));

                    return Math.Max(
                        0.0,
                        Math.Round(OrigValue * Mult, MidpointRounding.AwayFromZero));
                }
            }

            public bool IsDirty
            {
                get { return Editable && Math.Abs(Mult - 1.0) > 1e-9; }
            }
        }

        private class EditSession
        {
            public string PrefabName;
            public List<ItemEdit> Items = new List<ItemEdit>();
            public List<NodeEdit> Nodes = new List<NodeEdit>();
            public ViewMode Mode = ViewMode.Loot;
            public int ItemPage;
            public int NodePage;
        }

        #region Oxide hooks

        private void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiName);

            sessions.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
                sessions.Remove(player.userID);
        }

        #endregion

        #region Helpers

        private bool HasPermission(BasePlayer player)
        {
            return player != null &&
                (player.IsAdmin ||
                 permission.UserHasPermission(player.UserIDString, AdminPermission));
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Anchor(float x)
        {
            return F(x) + " 1";
        }

        private static string OffsetTop(float value)
        {
            return "0 -" + F(value);
        }

        private static string Pct(double chance)
        {
            return (chance * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        private static string CanonicalRarity(string rarity)
        {
            if (string.IsNullOrEmpty(rarity))
                return null;

            foreach (string value in Rarities)
            {
                if (string.Equals(value, rarity, StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            return null;
        }

        private static int ReadInt(JToken token, int fallback)
        {
            double value = ReadDouble(token, double.NaN);

            if (double.IsNaN(value))
                return fallback;

            return (int)Math.Round(value);
        }

        private static double ReadDouble(JToken token, double fallback)
        {
            JValue jv = token as JValue;

            if (jv == null || jv.Value == null)
                return fallback;

            try
            {
                return Convert.ToDouble(jv.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static List<object> Append(List<object> path, params object[] extra)
        {
            List<object> result = new List<object>(path);
            result.AddRange(extra);
            return result;
        }

        private static string ShortenLeft(string text)
        {
            const int max = 46;

            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text;

            return "..." + text.Substring(text.Length - (max - 3));
        }

        private static string ShortenRight(string text)
        {
            const int max = 52;

            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text;

            return text.Substring(0, max - 3) + "...";
        }

        private static JToken Navigate(JToken start, List<object> segments)
        {
            JToken current = start;

            foreach (object segment in segments)
            {
                if (current == null)
                    return null;

                string name = segment as string;

                if (name != null)
                {
                    JObject obj = current as JObject;

                    if (obj == null)
                        return null;

                    current = obj[name];
                }
                else
                {
                    int index = (int)segment;
                    JArray array = current as JArray;

                    if (array == null || index < 0 || index >= array.Count)
                        return null;

                    current = array[index];
                }
            }

            return current;
        }

        // Berekent per rij de kans (per slot-roll) vóór en na de aanpassingen.
        private static void Recompute(EditSession session)
        {
            foreach (NodeEdit n in session.Nodes)
            {
                n.SumOrig = 0;
                n.SumNow = 0;
            }

            foreach (NodeEdit n in session.Nodes)
            {
                if (n.Parent != null && !n.IsRoot)
                {
                    n.Parent.SumOrig += n.OrigValue;
                    n.Parent.SumNow += n.CurrentValue;
                }
            }

            foreach (NodeEdit n in session.Nodes)
            {
                if (n.IsSlot)
                {
                    n.ChanceOrig = n.OrigValue;
                    n.ChanceNow = n.CurrentValue;
                }
                else if (n.IsRoot)
                {
                    n.ChanceOrig = n.Parent.ChanceOrig;
                    n.ChanceNow = n.Parent.ChanceNow;
                }
                else
                {
                    n.ChanceOrig = n.Parent.SumOrig > 0
                        ? n.Parent.ChanceOrig * n.OrigValue / n.Parent.SumOrig
                        : 0.0;

                    n.ChanceNow = n.Parent.SumNow > 0
                        ? n.Parent.ChanceNow * n.CurrentValue / n.Parent.SumNow
                        : 0.0;
                }
            }
        }

        private static int TotalPages(int count)
        {
            return Math.Max(1, (count + PageSize - 1) / PageSize);
        }

        #endregion

        #region Chat command

        [ChatCommand("blsnap")]
        private void CmdBLSnap(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasPermission(player))
            {
                SendReply(player, "<color=red>Je hebt geen permissie.</color>");
                return;
            }

            RaycastHit hit;

            if (!Physics.Raycast(
                player.eyes.HeadRay(),
                out hit,
                5f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            {
                SendReply(player, "<color=yellow>Kijk binnen 5 meter naar een lootcontainer.</color>");
                return;
            }

            StorageContainer container = hit.GetEntity() as StorageContainer;

            if (container == null)
            {
                SendReply(player, "<color=yellow>Dit is geen geldige lootcontainer.</color>");
                return;
            }

            string prefabName = container.PrefabName;

            if (string.IsNullOrEmpty(prefabName))
            {
                SendReply(player, "<color=red>Prefabnaam kon niet worden gelezen.</color>");
                return;
            }

            try
            {
                JObject data = Interface.Oxide.DataFileSystem.ReadObject<JObject>(DataFileName);

                if (data == null || !(data["LootTables"] is JObject))
                {
                    SendReply(player, "<color=red>BetterLoot LootTables.json niet gevonden of ongeldig.</color>");
                    return;
                }

                JObject tables = (JObject)data["LootTables"];
                JObject prefab = tables[prefabName] as JObject;

                if (prefab == null)
                {
                    SendReply(player,
                        "<color=yellow>Deze container staat niet in BetterLoot:</color> " + prefabName);
                    return;
                }

                EditSession session = new EditSession();
                session.PrefabName = prefabName;

                // Vanilla Loot Slots + geneste SubSpawn-groepen.
                JArray slots = prefab["Vanilla Loot Slots"] as JArray;

                if (slots != null)
                {
                    for (int s = 0; s < slots.Count; s++)
                    {
                        JObject slot = slots[s] as JObject;

                        if (slot == null)
                            continue;

                        JObject loot = slot["Loot"] as JObject;

                        if (loot == null)
                            continue;

                        List<object> slotPath = new List<object>();
                        slotPath.Add("Vanilla Loot Slots");
                        slotPath.Add(s);

                        NodeEdit slotNode = new NodeEdit();
                        slotNode.Segments = slotPath;
                        slotNode.IsSlot = true;
                        slotNode.SlotNumber = s + 1;
                        slotNode.Depth = 0;

                        JToken probToken = slot["Probability"];
                        slotNode.Editable = probToken is JValue;
                        slotNode.OrigValue = ReadDouble(probToken, 1.0);

                        JToken lootName = loot["Name"];
                        slotNode.Name = lootName != null ? lootName.ToString() : "Loot";

                        slotNode.Contains =
                            "Number To Spawn: " + ReadInt(slot["Number To Spawn"], 1);

                        session.Nodes.Add(slotNode);

                        ReadNode(
                            loot,
                            Append(slotPath, "Loot"),
                            "Slot " + (s + 1),
                            slotNode,
                            1,
                            true,
                            session);
                    }
                }

                // Guaranteed Items en Ungrouped Items.
                ReadDirectItems(prefab, "Guaranteed Items", session);
                ReadDirectItems(prefab, "Ungrouped Items", session);

                if (session.Items.Count == 0)
                {
                    SendReply(player,
                        "<color=yellow>Geen items gevonden in de lootconfiguratie van deze container.</color>");
                    return;
                }

                Recompute(session);

                sessions[player.userID] = session;

                OpenLootUi(player);
            }
            catch (Exception ex)
            {
                PrintError("BetterLootSnap read error: " + ex);

                SendReply(player,
                    "<color=red>Fout bij het uitlezen van BetterLoot. Controleer de serverconsole.</color>");
            }
        }

        #endregion

        #region JSON reading

        private void ReadNode(
            JObject node,
            List<object> nodePath,
            string source,
            NodeEdit parent,
            int depth,
            bool isRoot,
            EditSession session)
        {
            NodeEdit edit = new NodeEdit();
            edit.Segments = nodePath;
            edit.IsRoot = isRoot;
            edit.Depth = depth;
            edit.Parent = parent;

            JToken nameToken = node["Name"];
            edit.RawName = nameToken != null ? nameToken.ToString() : null;
            edit.Name = edit.RawName != null
                ? edit.RawName
                : (isRoot ? "Loot" : "SubSpawn");

            JToken weightToken = node["Weight"];
            edit.Editable = !isRoot && weightToken is JValue;
            edit.OrigValue = ReadDouble(weightToken, 0.0);

            session.Nodes.Add(edit);
            parent.Children.Add(edit);

            List<string> shortnames = new List<string>();

            JArray items = node["Items"] as JArray;

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    JObject item = items[i] as JObject;

                    if (item == null)
                        continue;

                    string shortname = AddItem(
                        item,
                        Append(nodePath, "Items", i),
                        source,
                        false,
                        null,
                        session);

                    if (shortname != null)
                        shortnames.Add(shortname);
                }
            }

            JArray subSpawn = node["SubSpawn"] as JArray;

            if (subSpawn != null)
            {
                for (int i = 0; i < subSpawn.Count; i++)
                {
                    JObject child = subSpawn[i] as JObject;

                    if (child == null)
                        continue;

                    string childName = "SubSpawn";

                    if (child["Name"] != null)
                        childName = child["Name"].ToString();

                    ReadNode(
                        child,
                        Append(nodePath, "SubSpawn", i),
                        source + " / " + childName,
                        edit,
                        depth + 1,
                        false,
                        session);
                }
            }

            if (shortnames.Count > 0)
                edit.Contains = ShortenRight(string.Join(", ", shortnames.ToArray()));
            else if (edit.Children.Count > 0)
                edit.Contains = edit.Children.Count + " sub-groups";
            else
                edit.Contains = string.Empty;
        }

        private void ReadDirectItems(JObject prefab, string section, EditSession session)
        {
            JObject group = prefab[section] as JObject;

            if (group == null)
                return;

            foreach (JProperty property in group.Properties())
            {
                JObject item = property.Value as JObject;

                if (item == null)
                    continue;

                List<object> path = new List<object>();
                path.Add(section);
                path.Add(property.Name);

                AddItem(item, path, section, true, property.Name, session);
            }
        }

        private string AddItem(
            JObject obj,
            List<object> path,
            string source,
            bool isDirect,
            string directShortname,
            EditSession session)
        {
            string shortname = directShortname;

            if (!isDirect)
            {
                JToken shortToken = obj["Shortname"];
                shortname = shortToken != null ? shortToken.ToString() : null;
            }

            if (string.IsNullOrEmpty(shortname))
                return null;

            int min = ReadInt(obj["Item Minimum"], 1);
            int max = ReadInt(obj["Item Maximum"], min);

            JToken rarityToken = obj["Rarity"];
            string rarity = CanonicalRarity(rarityToken != null ? rarityToken.ToString() : null);

            if (rarity == null)
                rarity = "None";

            session.Items.Add(new ItemEdit
            {
                Segments = path,
                IsDirect = isDirect,
                Shortname = shortname,
                Source = source,
                Min = min,
                Max = max,
                Rarity = rarity,
                OrigMin = min,
                OrigMax = max,
                OrigRarity = rarity
            });

            return shortname;
        }

        #endregion

        #region UI

        private void OpenLootUi(BasePlayer player)
        {
            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            bool nodeMode = session.Mode == ViewMode.Loot;

            if (nodeMode)
                Recompute(session);

            int count = nodeMode ? session.Nodes.Count : session.Items.Count;
            int totalPages = TotalPages(count);
            int page = nodeMode ? session.NodePage : session.ItemPage;

            if (page < 0)
                page = 0;

            if (page >= totalPages)
                page = totalPages - 1;

            if (nodeMode)
                session.NodePage = page;
            else
                session.ItemPage = page;

            int startIndex = page * PageSize;
            int endIndex = Math.Min(count, startIndex + PageSize);
            int rowCount = endIndex - startIndex;
            float rowHeight = nodeMode ? NodeRowHeight : ItemRowHeight;

            CuiHelper.DestroyUi(player, UiName);

            CuiElementContainer ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.12 0.97" },
                RectTransform =
                {
                    AnchorMin = "0.08 0.08",
                    AnchorMax = "0.92 0.92"
                },
                CursorEnabled = true
            }, "Overlay", UiName);

            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = "BETTERLOOT 4.5.x - CONTAINER LOOT EDITOR",
                    FontSize = 18,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 0.55 0.15 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.91",
                    AnchorMax = "0.97 0.98"
                }
            }, UiName);

            AddPanelButton(ui, "LOOT", "blui.tab loot",
                "0.03 0.925", "0.12 0.975",
                nodeMode ? "0.80 0.40 0.10 1" : "0.25 0.30 0.40 1", 12);

            AddPanelButton(ui, "ITEMS", "blui.tab items",
                "0.13 0.925", "0.22 0.975",
                nodeMode ? "0.25 0.30 0.40 1" : "0.80 0.40 0.10 1", 12);

            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Prefab: " + session.PrefabName,
                    FontSize = 10,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.8 0.8 0.8 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.875",
                    AnchorMax = "0.97 0.91"
                }
            }, UiName);

            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = nodeMode
                        ? "PROB x = multiplier on the original value (1 = unchanged, 0.5 = half as likely, 0 = never). NOW = resulting chance per slot roll."
                        : "Min / max amount and rarity per loot entry.",
                    FontSize = 9,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.6 0.6 0.6 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.03 0.85",
                    AnchorMax = "0.97 0.875"
                }
            }, UiName);

            if (nodeMode)
            {
                AddHeader(ui, "ENTRY", NEntryMin, NEntryMax, TextAnchor.MiddleLeft);
                AddHeader(ui, "CONTAINS", NContMin, NContMax, TextAnchor.MiddleLeft);
                AddHeader(ui, "BEFORE", NBeforeMin, NBeforeMax, TextAnchor.MiddleCenter);
                AddHeader(ui, "NOW", NNowMin, NNowMax, TextAnchor.MiddleCenter);
                AddHeader(ui, "PROB x", NMultMin, NMultMax, TextAnchor.MiddleCenter);
            }
            else
            {
                AddHeader(ui, "ITEM", ColItemMin, ColItemMax, TextAnchor.MiddleLeft);
                AddHeader(ui, "SOURCE", ColSourceMin, ColSourceMax, TextAnchor.MiddleLeft);
                AddHeader(ui, "MIN", ColMinMin, ColMinMax, TextAnchor.MiddleCenter);
                AddHeader(ui, "MAX", ColMaxMin, ColMaxMax, TextAnchor.MiddleCenter);
                AddHeader(ui, "RARITY", ColRarityMin, ColRarityMax, TextAnchor.MiddleCenter);
            }

            float contentHeight = Math.Max(1f, rowCount * rowHeight + 8f);

            ui.Add(new CuiElement
            {
                Name = ScrollName,
                Parent = UiName,
                Components =
                {
                    new CuiImageComponent
                    {
                        Color = "0.04 0.05 0.07 0.35"
                    },

                    new CuiRectTransformComponent
                    {
                        AnchorMin = F(ScrollXMin) + " 0.17",
                        AnchorMax = F(ScrollXMax) + " 0.79"
                    },

                    new CuiScrollViewComponent
                    {
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "1 1",
                            OffsetMin = "0 -" + F(contentHeight),
                            OffsetMax = "0 0"
                        },

                        Vertical = true,
                        Horizontal = false,

                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                        Elasticity = 0.1f,
                        Inertia = true,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 25f,

                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = false,
                            Size = Math.Min(
                                1f,
                                0.62f / Math.Max(1f, rowCount / 8f)
                            ),
                            HandleColor = "0.85 0.85 0.85 0.8",
                            HighlightColor = "1 1 1 0.95",
                            PressedColor = "1 1 1 1",
                            TrackColor = "0 0 0 0.35"
                        }
                    }
                }
            });

            for (int i = startIndex; i < endIndex; i++)
            {
                float rowTop = 8f + (i - startIndex) * rowHeight;
                float rowBottom = rowTop + rowHeight - 5f;

                if (nodeMode)
                    AddNodeRow(ui, session, i, rowTop, rowBottom);
                else
                    AddItemRow(ui, session, i, rowTop, rowBottom);
            }

            // Navigatie en acties.
            AddPanelButton(ui, "PREV", "blui.page prev",
                "0.04 0.045", "0.14 0.12",
                page > 0 ? "0.25 0.30 0.40 1" : "0.18 0.18 0.20 1", 13);

            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Page " + (page + 1) + " / " + totalPages +
                           "  (" + count + (nodeMode ? " entries)" : " items)"),
                    FontSize = 11,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.8 0.8 0.8 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.15 0.045",
                    AnchorMax = "0.33 0.12"
                }
            }, UiName);

            AddPanelButton(ui, "NEXT", "blui.page next",
                "0.34 0.045", "0.44 0.12",
                page < totalPages - 1 ? "0.25 0.30 0.40 1" : "0.18 0.18 0.20 1", 13);

            AddPanelButton(ui, "CANCEL", "blui.close",
                "0.52 0.045", "0.73 0.12", "0.55 0.18 0.18 1", 13);

            AddPanelButton(ui, "SAVE", "blui.save",
                "0.75 0.045", "0.96 0.12", "0.18 0.55 0.25 1", 13);

            CuiHelper.AddUi(player, ui);
        }

        // Vernieuwt alleen de NOW-labels, zodat de scrollpositie behouden blijft.
        private void RefreshChanceLabels(BasePlayer player, EditSession session)
        {
            Recompute(session);

            int count = session.Nodes.Count;
            int startIndex = session.NodePage * PageSize;
            int endIndex = Math.Min(count, startIndex + PageSize);

            CuiElementContainer ui = new CuiElementContainer();

            for (int i = startIndex; i < endIndex; i++)
            {
                float rowTop = 8f + (i - startIndex) * NodeRowHeight;
                float rowBottom = rowTop + NodeRowHeight - 5f;

                CuiHelper.DestroyUi(player, NowNamePrefix + i);
                AddNowLabel(ui, session.Nodes[i], i, rowTop, rowBottom);
            }

            CuiHelper.AddUi(player, ui);
        }

        private void AddHeader(
            CuiElementContainer ui,
            string text,
            float colMin,
            float colMax,
            TextAnchor alignment)
        {
            float width = ScrollXMax - ScrollXMin;
            float xMin = ScrollXMin + width * colMin;
            float xMax = ScrollXMin + width * colMax;

            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = text,
                    FontSize = 11,
                    Align = alignment,
                    Color = "0.7 0.7 0.7 1"
                },
                RectTransform =
                {
                    AnchorMin = F(xMin) + " 0.80",
                    AnchorMax = F(xMax) + " 0.85"
                }
            }, UiName);
        }

        private void AddRowLabel(
            CuiElementContainer ui,
            string text,
            int fontSize,
            string color,
            TextAnchor align,
            float colMin,
            float colMax,
            float topOffset,
            float bottomOffset,
            string name)
        {
            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = text ?? string.Empty,
                    FontSize = fontSize,
                    Align = align,
                    Color = color
                },
                RectTransform =
                {
                    AnchorMin = Anchor(colMin),
                    AnchorMax = Anchor(colMax),
                    OffsetMin = OffsetTop(bottomOffset),
                    OffsetMax = OffsetTop(topOffset)
                }
            }, ScrollName, name);
        }

        private void AddItemRow(
            CuiElementContainer ui,
            EditSession session,
            int index,
            float rowTop,
            float rowBottom)
        {
            ItemEdit item = session.Items[index];

            AddRowLabel(ui, item.Shortname, 12, "1 1 1 1", TextAnchor.MiddleLeft,
                ColItemMin, ColItemMax, rowTop, rowBottom, null);

            AddRowLabel(ui, ShortenLeft(item.Source), 9, "0.62 0.62 0.62 1", TextAnchor.MiddleLeft,
                ColSourceMin, ColSourceMax, rowTop, rowBottom, null);

            AddInput(ui, "blui.set " + index + " min",
                item.Min.ToString(CultureInfo.InvariantCulture),
                ColMinMin, ColMinMax, rowTop, rowBottom);

            AddInput(ui, "blui.set " + index + " max",
                item.Max.ToString(CultureInfo.InvariantCulture),
                ColMaxMin, ColMaxMax, rowTop, rowBottom);

            AddRarityButton(ui, index, item.Rarity, rowTop, rowBottom);
        }

        private void AddNodeRow(
            CuiElementContainer ui,
            EditSession session,
            int index,
            float rowTop,
            float rowBottom)
        {
            NodeEdit n = session.Nodes[index];

            if (n.IsSlot)
            {
                ui.Add(new CuiPanel
                {
                    Image = { Color = "0.20 0.14 0.08 0.55" },
                    RectTransform =
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin = OffsetTop(rowBottom),
                        OffsetMax = OffsetTop(rowTop)
                    }
                }, ScrollName);
            }

            float entryMin = Math.Min(0.15f, NEntryMin + n.Depth * 0.012f);

            string nameText = n.IsSlot
                ? "SLOT " + n.SlotNumber + "  " + n.Name
                : n.Name;

            string nameColor = n.IsSlot
                ? "1 0.75 0.4 1"
                : (n.IsRoot ? "0.85 0.85 0.85 1" : "1 1 1 1");

            AddRowLabel(ui, nameText, 11, nameColor, TextAnchor.MiddleLeft,
                entryMin, NEntryMax, rowTop, rowBottom, null);

            AddRowLabel(ui, n.Contains, 9, "0.62 0.62 0.62 1", TextAnchor.MiddleLeft,
                NContMin, NContMax, rowTop, rowBottom, null);

            AddRowLabel(ui, Pct(n.ChanceOrig), 11, "0.7 0.7 0.7 1", TextAnchor.MiddleCenter,
                NBeforeMin, NBeforeMax, rowTop, rowBottom, null);

            AddNowLabel(ui, n, index, rowTop, rowBottom);

            if (n.Editable)
            {
                AddInput(ui, "blui.mult " + index,
                    n.Mult.ToString("0.##", CultureInfo.InvariantCulture),
                    NMultMin, NMultMax, rowTop, rowBottom);
            }
        }

        private void AddNowLabel(
            CuiElementContainer ui,
            NodeEdit n,
            int index,
            float rowTop,
            float rowBottom)
        {
            bool changed = Math.Abs(n.ChanceNow - n.ChanceOrig) > 1e-9;

            AddRowLabel(
                ui,
                Pct(n.ChanceNow),
                11,
                changed ? "1 0.75 0.2 1" : "1 1 1 1",
                TextAnchor.MiddleCenter,
                NNowMin,
                NNowMax,
                rowTop,
                rowBottom,
                NowNamePrefix + index);
        }

        private void AddInput(
            CuiElementContainer ui,
            string command,
            string value,
            float colMin,
            float colMax,
            float topOffset,
            float bottomOffset)
        {
            // Achtergrond als apart zusterelement. Het InputField zelf krijgt GEEN
            // CuiImageComponent op hetzelfde element (zoals in de werkende 5.1.0).
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.13 0.15 0.19 1" },
                RectTransform =
                {
                    AnchorMin = Anchor(colMin),
                    AnchorMax = Anchor(colMax),
                    OffsetMin = OffsetTop(bottomOffset),
                    OffsetMax = OffsetTop(topOffset)
                }
            }, ScrollName);

            CuiElement element = new CuiElement();

            element.Name = CuiHelper.GetGuid();
            element.Parent = ScrollName;

            element.Components.Add(new CuiInputFieldComponent
            {
                Text = value,
                Command = command,
                Color = "1 1 1 1",
                Font = "robotocondensed-regular.ttf",
                Align = TextAnchor.MiddleCenter,
                CharsLimit = 7
            });

            element.Components.Add(new CuiRectTransformComponent
            {
                AnchorMin = Anchor(colMin),
                AnchorMax = Anchor(colMax),
                OffsetMin = OffsetTop(bottomOffset),
                OffsetMax = OffsetTop(topOffset)
            });

            ui.Add(element);
        }

        private void AddRarityButton(
            CuiElementContainer ui,
            int itemIndex,
            string rarity,
            float topOffset,
            float bottomOffset)
        {
            string color = "0.25 0.35 0.48 0.95";

            if (rarity == "None")
                color = "0.30 0.30 0.30 0.95";
            else if (rarity == "Common")
                color = "0.25 0.45 0.25 0.95";
            else if (rarity == "Uncommon")
                color = "0.25 0.40 0.65 0.95";
            else if (rarity == "Rare")
                color = "0.55 0.30 0.65 0.95";
            else if (rarity == "VeryRare")
                color = "0.75 0.35 0.15 0.95";

            ui.Add(new CuiButton
            {
                Button =
                {
                    Color = color,
                    Command = "blui.rarity " + itemIndex
                },

                RectTransform =
                {
                    AnchorMin = Anchor(ColRarityMin),
                    AnchorMax = Anchor(ColRarityMax),
                    OffsetMin = OffsetTop(bottomOffset),
                    OffsetMax = OffsetTop(topOffset)
                },

                Text =
                {
                    Text = rarity + "  \u25BC",
                    FontSize = 11,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                }
            }, ScrollName);
        }

        private void AddPanelButton(
            CuiElementContainer ui,
            string text,
            string command,
            string anchorMin,
            string anchorMax,
            string color,
            int fontSize)
        {
            ui.Add(new CuiButton
            {
                Button =
                {
                    Color = color,
                    Command = command
                },
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax
                },
                Text =
                {
                    Text = text,
                    FontSize = fontSize,
                    Align = TextAnchor.MiddleCenter
                }
            }, UiName);
        }

        #endregion

        #region Console commands

        [ConsoleCommand("blui.set")]
        private void CmdUISet(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (!HasPermission(player))
                return;

            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            if (arg.Args == null || arg.Args.Length < 3)
                return;

            int itemIndex;
            int value;

            if (!int.TryParse(arg.Args[0].ToString(), out itemIndex))
                return;

            if (!int.TryParse(arg.Args[2].ToString(), out value))
                return;

            if (itemIndex < 0 || itemIndex >= session.Items.Count)
                return;

            ItemEdit edit = session.Items[itemIndex];
            string field = arg.Args[1].ToString();

            value = Math.Max(1, Math.Min(1000000, value));

            if (field == "min")
                edit.Min = value;
            else if (field == "max")
                edit.Max = value;
            else
                return;

            if (edit.Min > edit.Max)
            {
                if (field == "min")
                    edit.Max = edit.Min;
                else
                    edit.Min = edit.Max;
            }
        }

        [ConsoleCommand("blui.mult")]
        private void CmdUIMult(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (!HasPermission(player))
                return;

            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            if (arg.Args == null || arg.Args.Length < 2)
                return;

            int nodeIndex;

            if (!int.TryParse(arg.Args[0].ToString(), out nodeIndex))
                return;

            if (nodeIndex < 0 || nodeIndex >= session.Nodes.Count)
                return;

            NodeEdit node = session.Nodes[nodeIndex];

            if (!node.Editable)
                return;

            string text = arg.Args[1].ToString().Replace(',', '.');
            double value;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            node.Mult = Math.Max(0.0, Math.Min(100.0, value));

            if (session.Mode == ViewMode.Loot)
                RefreshChanceLabels(player, session);
        }

        [ConsoleCommand("blui.rarity")]
        private void CmdUIRarity(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (!HasPermission(player))
                return;

            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            if (arg.Args == null || arg.Args.Length < 1)
                return;

            int itemIndex;

            if (!int.TryParse(arg.Args[0].ToString(), out itemIndex))
                return;

            if (itemIndex < 0 || itemIndex >= session.Items.Count)
                return;

            ItemEdit edit = session.Items[itemIndex];

            int currentIndex = 0;

            for (int i = 0; i < Rarities.Length; i++)
            {
                if (Rarities[i] == edit.Rarity)
                {
                    currentIndex = i;
                    break;
                }
            }

            currentIndex++;

            if (currentIndex >= Rarities.Length)
                currentIndex = 0;

            edit.Rarity = Rarities[currentIndex];

            OpenLootUi(player);
        }

        [ConsoleCommand("blui.tab")]
        private void CmdUITab(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (!HasPermission(player))
                return;

            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            if (arg.Args == null || arg.Args.Length < 1)
                return;

            string tab = arg.Args[0].ToString();

            if (tab == "loot")
                session.Mode = ViewMode.Loot;
            else if (tab == "items")
                session.Mode = ViewMode.Items;
            else
                return;

            OpenLootUi(player);
        }

        [ConsoleCommand("blui.page")]
        private void CmdUIPage(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (!HasPermission(player))
                return;

            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            if (arg.Args == null || arg.Args.Length < 1)
                return;

            string direction = arg.Args[0].ToString();
            int delta;

            if (direction == "next")
                delta = 1;
            else if (direction == "prev")
                delta = -1;
            else
                return;

            if (session.Mode == ViewMode.Loot)
                session.NodePage += delta;
            else
                session.ItemPage += delta;

            OpenLootUi(player);
        }

        [ConsoleCommand("blui.save")]
        private void CmdUISave(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (!HasPermission(player))
                return;

            EditSession session;

            if (!sessions.TryGetValue(player.userID, out session))
                return;

            try
            {
                int dirtyCount = 0;

                foreach (ItemEdit item in session.Items)
                {
                    if (item.IsDirty)
                        dirtyCount++;
                }

                foreach (NodeEdit node in session.Nodes)
                {
                    if (node.IsDirty)
                        dirtyCount++;
                }

                if (dirtyCount == 0)
                {
                    SendReply(player, "<color=yellow>Geen wijzigingen om op te slaan.</color>");
                    return;
                }

                // Verse kopie van het bestand inlezen; de sessie bevat alleen paden.
                JObject data = Interface.Oxide.DataFileSystem.ReadObject<JObject>(DataFileName);

                if (data == null || !(data["LootTables"] is JObject))
                {
                    SendReply(player,
                        "<color=red>LootTables.json heeft niet de verwachte BetterLoot-structuur. Er is niets opgeslagen.</color>");
                    return;
                }

                JObject tables = (JObject)data["LootTables"];
                JObject prefab = tables[session.PrefabName] as JObject;

                if (prefab == null)
                {
                    SendReply(player,
                        "<color=red>Prefab niet gevonden in BetterLoot. Er is niets opgeslagen.</color>");
                    return;
                }

                // Eerst alles valideren en toepassen in het geheugen. Pas daarna schrijven.
                foreach (ItemEdit item in session.Items)
                {
                    if (!item.IsDirty)
                        continue;

                    JObject target = Navigate(prefab, item.Segments) as JObject;

                    if (target == null)
                    {
                        SendReply(player,
                            "<color=red>Een itempad kon niet worden teruggevonden (bestand gewijzigd?). Er is niets opgeslagen.</color>");
                        return;
                    }

                    if (!item.IsDirect)
                    {
                        JToken shortToken = target["Shortname"];

                        if (shortToken == null || shortToken.ToString() != item.Shortname)
                        {
                            SendReply(player,
                                "<color=red>Item op opgeslagen locatie komt niet overeen met '" +
                                item.Shortname + "' (bestand gewijzigd?). Er is niets opgeslagen.</color>");
                            return;
                        }
                    }

                    if (item.MinChanged)
                        target["Item Minimum"] = item.Min;

                    if (item.MaxChanged)
                        target["Item Maximum"] = item.Max;

                    if (item.RarityChanged)
                        target["Rarity"] = item.Rarity;
                }

                foreach (NodeEdit node in session.Nodes)
                {
                    if (!node.IsDirty)
                        continue;

                    JObject target = Navigate(prefab, node.Segments) as JObject;

                    if (target == null)
                    {
                        SendReply(player,
                            "<color=red>Een lootgroep kon niet worden teruggevonden (bestand gewijzigd?). Er is niets opgeslagen.</color>");
                        return;
                    }

                    string key = node.IsSlot ? "Probability" : "Weight";

                    double fileValue = ReadDouble(target[key], double.NaN);

                    if (double.IsNaN(fileValue) || Math.Abs(fileValue - node.OrigValue) > 1e-6)
                    {
                        SendReply(player,
                            "<color=red>Waarde van '" + node.Name + "' is in het bestand gewijzigd sinds het openen. Er is niets opgeslagen.</color>");
                        return;
                    }

                    if (!node.IsSlot)
                    {
                        JToken nameToken = target["Name"];
                        string fileName = nameToken != null ? nameToken.ToString() : null;

                        if (fileName != node.RawName)
                        {
                            SendReply(player,
                                "<color=red>Lootgroep '" + node.Name + "' komt niet overeen met het bestand. Er is niets opgeslagen.</color>");
                            return;
                        }

                        target["Weight"] = (int)node.CurrentValue;
                    }
                    else
                    {
                        target["Probability"] = node.CurrentValue;
                    }
                }

                // Backup van het ORIGINELE bestand, voordat er iets wordt geschreven.
                string sourcePath = System.IO.Path.Combine(
                    Interface.Oxide.DataDirectory,
                    "BetterLoot",
                    "LootTables.json");

                string backupPath = System.IO.Path.Combine(
                    Interface.Oxide.DataDirectory,
                    "BetterLoot",
                    "LootTables_BetterLootSnap_backup.json");

                if (!File.Exists(sourcePath))
                {
                    SendReply(player,
                        "<color=red>Origineel LootTables.json niet gevonden voor backup. Er is niets opgeslagen.</color>");
                    return;
                }

                File.Copy(sourcePath, backupPath, true);

                Interface.Oxide.DataFileSystem.WriteObject(DataFileName, data);

                SendReply(player, "<color=green>BetterLoot instellingen succesvol opgeslagen!</color>");
                SendReply(player, "Prefab: " + session.PrefabName);
                SendReply(player, "Aantal gewijzigde regels: " + dirtyCount);
                SendReply(player, "Backup: oxide/data/" + BackupFileName + ".json");
                SendReply(player, "Herlaad BetterLoot indien nodig: oxide.reload BetterLoot");

                CuiHelper.DestroyUi(player, UiName);
                sessions.Remove(player.userID);
            }
            catch (Exception ex)
            {
                PrintError("BetterLootSnap save error: " + ex);

                SendReply(player, "<color=red>Opslaan mislukt. Controleer de serverconsole.</color>");
            }
        }

        [ConsoleCommand("blui.close")]
        private void CmdUIClose(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (player == null)
                return;

            CuiHelper.DestroyUi(player, UiName);
            sessions.Remove(player.userID);
        }

        #endregion
    }
}
