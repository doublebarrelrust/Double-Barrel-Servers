using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RandomGridSpawn", "Codex", "3.63.0")]
    [Description("Assigns each player a random 2x2 grid area and keeps them inside it.")]
    class RandomGridSpawn : RustPlugin
    {
        private const float GridScale = 1024f / 7f;
        private const float AreaCheckInterval = 5f;
        private const float BoundaryPadding = 0.5f;
        private const float SpawnYOffset = 0.25f;
        private const int MaxAreaSearchAttempts = 500;
        private const int GroundLayerMask = 1235288065;
        private const string HammerShortName = "hammer";
        private const string BuildingPlanShortName = "building.planner";
        private const string RockShortName = "rock";
        private const string TorchShortName = "torch";
        private const string CupboardShortName = "cupboard.tool";
        private const string NoClipPermission = "randomgridspawn.noclip";
        private const float EntKillDistance = 100f;
        private const string MapMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string VendingMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        private readonly Dictionary<ulong, AssignedArea> assignedAreas = new Dictionary<ulong, AssignedArea>();
        private readonly HashSet<string> allocatedGridCells = new HashSet<string>();
        private readonly HashSet<ulong> grantedModerators = new HashSet<ulong>();
        private readonly HashSet<ulong> noClipHintSent = new HashSet<ulong>();
        private readonly Dictionary<int, int> defaultWorkbenchLevels = new Dictionary<int, int>();
        private readonly Dictionary<ulong, int> selectedGrades = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Dictionary<int, int>> raidCounts = new Dictionary<ulong, Dictionary<int, int>>();

        private const string RaidUiName = "RandomGridSpawn.RaidUI";
        private const string CostUiName = "RandomGridSpawn.CostUI";
        private const string CircuitUiName = "RandomGridSpawn.CircuitUI";
        private const float FloorHeight = 3f;

        private class CircuitView
        {
            public int Floor = -1;
            public bool Overlay = true;
        }

        private readonly Dictionary<ulong, CircuitView> circuitViews = new Dictionary<ulong, CircuitView>();

        // Crafting-cost breakdown stops at these; everything else is expanded into its
        // blueprint ingredients recursively.
        private static readonly HashSet<string> RawCostItems = new HashSet<string>
        {
            "wood", "stones", "metal.fragments", "metal.refined", "sulfur", "charcoal", "cloth",
            "leather", "lowgradefuel", "scrap", "bone.fragments", "fat.animal", "crude.oil",
            "gears", "metalblade", "metalpipe", "metalspring", "propanetank", "roadsigns", "rope",
            "sewingkit", "sheetmetal", "smgbody", "riflebody", "semibody", "tarp", "techparts", "fuse"
        };

        private const string GradeUiName = "RandomGridSpawn.GradeUI";
        private const string SpherePrefab = "assets/prefabs/visualization/sphere.prefab";
        private const int DomeLayers = 4;
        private static readonly string[] GradeLabels = { "TWIG", "WOOD", "STONE", "METAL", "HQM" };
        private static readonly string[] GradeIcons = { "building.planner", "wood", "stones", "metal.fragments", "metal.refined" };

        // Custom tile images for the grade UI, loaded from PNG files on the server disk in
        // oxide/data/RandomGridSpawnImages/. Edit the file names here if the files are named
        // differently. A web URL pasted into GradeImageUrls overrides the local file for that
        // slot; slots with no file and no URL fall back to the item icons above.
        private const string GradeImageFolder = "RandomGridSpawnImages";
        private static readonly string[] GradeImageFiles = { "twigwall.png", "woodwall.png", "stonewall.png", "metalwall.png", "armoredwall.png" };

        // Item icons used by the raid counter and base cost panels, mapped shortname -> file in
        // the same folder. Items without an entry fall back to ImageLibrary's stock item icons.
        private static readonly Dictionary<string, string> ItemImageFiles = new Dictionary<string, string>
        {
            ["explosive.timed"] = "timedexplosivecharge.png",
            ["explosive.satchel"] = "satchelcharge.png",
            ["grenade.beancan"] = "beancangrenade.png",
            ["grenade.molotov"] = "molitov.png",
            ["ammo.rocket.basic"] = "rocket.png",
            ["ammo.rocket.hv"] = "highvelocityrocket.png",
            ["ammo.rocket.fire"] = "incendiaryrocket.png",
            ["ammo.rifle.explosive"] = "explosiveammo.png",
            ["gunpowder"] = "gunpowder.png",
            ["sulfur"] = "sulfur.png",
            ["cloth"] = "cloth.png",
            ["techparts"] = "techtrash.png",
            ["metal.fragments"] = "metalfragments.png",
            ["metal.refined"] = "highqualitymetal.png",
            ["wood"] = "wood.png",
            ["stones"] = "stones.png",
            ["gears"] = "gears.png",
            ["metalblade"] = "metalblade.png",
            ["rope"] = "rope.png",
            ["sewingkit"] = "sewingkit.png",
            ["sheetmetal"] = "sheetmetal.png",
            ["tarp"] = "tarp.png",
            ["propanetank"] = "emptypropanetank.png",
            ["ladder.wooden.wall"] = "woodenladder.png",
            ["basicblueprintfragment"] = "basicblueprintfragment.png",
            ["advancedblueprintfragment"] = "advancedblueprintfragment.png"
        };
        private static readonly string[] GradeImageUrls =
        {
            "", // TWIG
            "", // WOOD
            "", // STONE
            "", // METAL
            "", // HQM
        };

        [PluginReference]
        private Plugin ImageLibrary;

        // The only admin console commands granted players may run: spawning items for themselves.
        private static readonly HashSet<string> AllowedAdminCommands = new HashSet<string>
        {
            "inventory.give",
            "inventory.giveid",
            "inventory.givearm"
        };

        private bool areaTimerStarted;
        private int gridCount;
        private float cellSize;

        private class AssignedArea
        {
            public string Key;
            public string Label;
            public List<string> GridCells;
            public float MinX;
            public float MaxX;
            public float MinZ;
            public float MaxZ;
            public Vector3 Center;
            public float Radius;
            public Vector3 SpawnPosition;
            public List<BaseEntity> MapMarkers = new List<BaseEntity>();
            public List<BaseEntity> Spheres = new List<BaseEntity>();
        }

        #region Render service (external 3D thumbnails)

        // Points at a web service that rebuilds the base from this JSON using Facepunch's
        // glTF model library (github.com/Facepunch/RustRelay.Assets), renders it, and
        // replies with an image URL. Left blank, nothing is sent and the built-in
        // isometric thumbnail is used instead.
        private class PluginConfig
        {
            [JsonProperty("Render service URL (blank = disabled)")]
            public string RenderUrl = string.Empty;

            [JsonProperty("Render service API key")]
            public string RenderKey = string.Empty;

            [JsonProperty("Render published bases")]
            public bool RenderOnPublish = true;

            [JsonProperty("Render quicksaves and autosaves")]
            public bool RenderOnAutosave = false;
        }

        private PluginConfig config;

        protected override void LoadDefaultConfig() => config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                config = Config.ReadObject<PluginConfig>();
            }
            catch
            {
                config = null;
            }

            if (config == null)
            {
                PrintWarning("Config invalid - writing a fresh one.");
                config = new PluginConfig();
            }

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private bool RenderServiceEnabled()
        {
            return config != null && !string.IsNullOrEmpty(config.RenderUrl);
        }

        // Rendered base images can simply be dropped on the server as
        // oxide/data/RandomGridSpawnRenders/<CODE>.png - no web hosting needed. They are
        // registered with ImageLibrary from disk (file://) like the grade icons.
        private const string RenderFolder = "RandomGridSpawnRenders";
        private readonly HashSet<string> registeredRenders = new HashSet<string>();

        private string RenderFilePath(string code)
        {
            return System.IO.Path.Combine(Interface.Oxide.DataDirectory, RenderFolder, code + ".png");
        }

        private bool HasLocalRender(string code)
        {
            return !string.IsNullOrEmpty(code) && System.IO.File.Exists(RenderFilePath(code));
        }

        // Registers every dropped-in render with ImageLibrary; safe to call repeatedly.
        private void RegisterLocalRenders()
        {
            if (ImageLibrary == null)
                return;

            string folder = System.IO.Path.Combine(Interface.Oxide.DataDirectory, RenderFolder);
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
                return;
            }

            int added = 0;
            foreach (string file in System.IO.Directory.GetFiles(folder, "*.png"))
            {
                string code = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!savedBases.ContainsKey(code) || !registeredRenders.Add(code))
                    continue;

                ImageLibrary.Call("AddImage", "file://" + file, "base_" + code, 0UL);
                added++;
            }

            if (added > 0)
                Puts("Registered " + added + " base render(s) from oxide/data/" + RenderFolder + ".");
        }

        // Re-scan the folder after dropping in new renders: gridspawn.renders
        [ConsoleCommand("gridspawn.renders")]
        private void RenderScanCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && !HasNoClipPermission(player))
                return;

            registeredRenders.Clear();
            RegisterLocalRenders();
            arg.ReplyWith("Re-scanned oxide/data/" + RenderFolder + ".");

            if (player != null && menuOpen.Contains(player.userID))
                RefreshMenu(player);
        }

        // The payload the render service consumes: prefab + transform + grade per piece,
        // which is everything needed to reassemble the base from the glTF model library.
        private void RequestRender(SavedBase saved, BasePlayer requester)
        {
            if (!RenderServiceEnabled() || saved == null || saved.Entities.Count == 0)
                return;

            List<Dictionary<string, object>> pieces = new List<Dictionary<string, object>>();
            foreach (SavedEntity e in saved.Entities)
            {
                pieces.Add(new Dictionary<string, object>
                {
                    ["prefab"] = e.Prefab,
                    ["pos"] = new[] { e.X, e.Y, e.Z },
                    ["rot"] = new[] { e.RotX, e.RotY, e.RotZ },
                    ["grade"] = e.Grade,
                    ["skin"] = e.Skin
                });
            }

            string body = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["code"] = saved.Code,
                ["name"] = saved.Name,
                ["owner"] = saved.OwnerName,
                ["entities"] = pieces
            });

            Dictionary<string, string> headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            if (!string.IsNullOrEmpty(config.RenderKey))
                headers["Authorization"] = "Bearer " + config.RenderKey;

            string code = saved.Code;
            ulong requesterId = requester != null ? requester.userID.Get() : 0UL;

            webrequest.Enqueue(config.RenderUrl, body, (status, response) =>
            {
                if (status != 200 || string.IsNullOrEmpty(response))
                {
                    PrintWarning("Render service failed for " + code + " (HTTP " + status + ").");
                    return;
                }

                SavedBase target;
                if (!savedBases.TryGetValue(code, out target))
                    return;

                // Preferred path: the service sends the PNG bytes back with the reply, so
                // they go straight into the game's own file storage. Downloading the image
                // over HTTP is not an option - the client image downloader demands TLS.
                bool stored = false;
                string inlinePng = ParseRenderPng(response);
                if (!string.IsNullOrEmpty(inlinePng) && CommunityEntity.ServerInstance != null)
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(inlinePng);
                        target.ImageCrc = FileStorage.server.Store(bytes, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID).ToString();
                        target.ImageUrl = null;
                        stored = true;
                    }
                    catch (Exception ex)
                    {
                        PrintWarning("Could not store render for " + code + ": " + ex.Message);
                    }
                }

                if (!stored)
                {
                    string url = ParseRenderUrl(response);
                    if (string.IsNullOrEmpty(url))
                    {
                        PrintWarning("Render service returned no image for " + code + ".");
                        return;
                    }

                    target.ImageUrl = url;
                    if (ImageLibrary != null)
                        ImageLibrary.Call("AddImage", url, "base_" + code, 0UL);
                }

                SaveSavedBases();

                BasePlayer player = requesterId != 0UL ? BasePlayer.FindByID(requesterId) : null;
                if (player != null && player.IsConnected)
                {
                    player.SendConsoleCommand("gametip.showtoast", 0, "3D render ready for '" + target.Name + "'.", string.Empty, false);
                    if (menuOpen.Contains(player.userID))
                        RefreshMenu(player);
                }
            }, this, RequestMethod.POST, headers, 30f);
        }

        // The base64 PNG the render service returns inline: {"png":"iVBORw0..."}.
        private string ParseRenderPng(string response)
        {
            try
            {
                Dictionary<string, object> parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
                object value;
                if (parsed != null && parsed.TryGetValue("png", out value))
                    return value as string;
            }
            catch
            {
                // Not JSON - fall back to the URL path.
            }

            return null;
        }

        // Accepts {"url":"..."}, {"image":"..."} or a bare URL body.
        private string ParseRenderUrl(string response)
        {
            response = response.Trim();

            if (response.StartsWith("http"))
                return response;

            try
            {
                Dictionary<string, object> parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
                if (parsed == null)
                    return null;

                object value;
                if (parsed.TryGetValue("url", out value) || parsed.TryGetValue("image", out value) || parsed.TryGetValue("imageUrl", out value))
                    return value as string;
            }
            catch
            {
                // Not JSON - nothing usable.
            }

            return null;
        }

        // Re-render an existing save on demand: gridspawn.render <code>
        [ConsoleCommand("gridspawn.render")]
        private void RenderCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && !HasNoClipPermission(player))
                return;

            if (!RenderServiceEnabled())
            {
                arg.ReplyWith("No render service configured - set the URL in oxide/config/RandomGridSpawn.json.");
                return;
            }

            SavedBase saved;
            if (!savedBases.TryGetValue(arg.GetString(0, string.Empty), out saved))
            {
                arg.ReplyWith("No save with that code.");
                return;
            }

            RequestRender(saved, player);
            arg.ReplyWith("Render requested for '" + saved.Name + "'.");
        }

        #endregion

        private void Init()
        {
            permission.RegisterPermission(NoClipPermission, this);
            LoadSavedBases();
        }

        private void OnServerInitialized()
        {
            if (!InitializeGrid())
            {
                PrintWarning("Could not initialize map grid. RandomGridSpawn will retry when players spawn.");
                return;
            }

            StartAreaTimer();
            RegisterGradeImages();
            LoadFavourites();
            timer.Every(600f, AutosaveAll);

            // Re-register publisher-provided card images and dropped-in renders.
            if (ImageLibrary != null)
            {
                foreach (SavedBase saved in savedBases.Values)
                {
                    if (!string.IsNullOrEmpty(saved.ImageUrl))
                        ImageLibrary.Call("AddImage", saved.ImageUrl, "base_" + saved.Code, 0UL);
                }
            }

            RegisterLocalRenders();

            foreach (ItemBlueprint bp in ItemManager.GetBlueprints())
            {
                defaultWorkbenchLevels[bp.targetItem.itemid] = bp.workbenchLevelRequired;
                bp.workbenchLevelRequired = 0;
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                GrantModeratorLite(player);
                UnlockBlueprints(player);
                SendCraftMode(player);
                AssignAndTeleport(player);
                GiveBuildLoadout(player);
            }
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null)
                    continue;

                if (player.IsFlying)
                    player.Teleport(GetGroundedPosition(player.transform.position));

                DestroyGradeUi(player);
                CuiHelper.DestroyUi(player, RaidUiName);
                CuiHelper.DestroyUi(player, CostUiName);
                CuiHelper.DestroyUi(player, CircuitUiName);
                CuiHelper.DestroyUi(player, MenuUiName);
                ClearModeratorLite(player);
            }

            foreach (AssignedArea area in assignedAreas.Values)
            {
                DestroyMapMarkers(area);
                DestroyDome(area);
            }

            foreach (ItemBlueprint bp in ItemManager.GetBlueprints())
            {
                int level;
                if (defaultWorkbenchLevels.TryGetValue(bp.targetItem.itemid, out level))
                    bp.workbenchLevelRequired = level;
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            GrantModeratorLite(player);
            UnlockBlueprints(player);
            SendCraftMode(player);
        }

        // Tells the client to craft without workbench checks (the "craftMode" client RPC);
        // blueprint workbench requirements are also zeroed server-side on load. Adapted from
        // k1lly0u's NoWorkbench plugin.
        private void SendCraftMode(BasePlayer player)
        {
            if (player == null || player.net == null || player.net.connection == null)
                return;

            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(3f, () => SendCraftMode(player));
                return;
            }

            player.ClientRPC(RpcTarget.Player("craftMode", player), 1);
        }

        private void UnlockBlueprints(BasePlayer player)
        {
            if (player != null && player.blueprints != null)
                player.blueprints.UnlockAll();
        }

        // Hunger, thirst and health stay pinned at full for every real player; everything else
        // about metabolism (temperature, oxygen) keeps running normally.
        private object OnRunPlayerMetabolism(PlayerMetabolism metabolism, BaseCombatEntity ownerEntity, float delta)
        {
            BasePlayer player = ownerEntity as BasePlayer;
            if (player == null || metabolism == null || !player.userID.IsSteamId())
                return null;

            metabolism.calories.value = metabolism.calories.max;
            metabolism.hydration.value = metabolism.hydration.max;

            if (player.health < player.MaxHealth() && !godDisabled.Contains(player.userID))
                player.Heal(player.MaxHealth());

            return null;
        }

        // Granted players hold auth level 1, which would normally allow every admin console
        // command (kick, ban, status, ent kill, teleport, ...). This blocks all of them except
        // the item-give whitelist, before they execute.
        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection == null || arg.cmd == null)
                return null;

            // F is bound to the flashlight toggle by default, and that toggle runs as a server
            // command (inventory.lighttoggle, ServerUser). A hammer has no light, so F while
            // holding one also becomes the noclip toggle. The vanilla toggle is left to run so
            // a worn hat lamp still switches together with noclip.
            if (arg.cmd.FullName == "inventory.lighttoggle")
            {
                BasePlayer flyer = arg.Player();
                if (flyer != null && !flyer.IsDead() && (IsHoldingHammer(flyer) || IsHoldingBuildingPlan(flyer)) && HasNoClipPermission(flyer))
                    ToggleFlight(flyer);

                return null;
            }

            // X is bound to the vehicle seat-swap command (also ServerUser). While holding a
            // building plan and not mounted, it cycles the auto-upgrade grade instead.
            if (arg.cmd.FullName == "vehicle.swapseats")
            {
                BasePlayer builder = arg.Player();
                if (builder != null && !builder.IsDead() && !builder.isMounted && IsHoldingBuildingPlan(builder) && HasNoClipPermission(builder))
                {
                    CycleGrade(builder);
                    return false;
                }

                return null;
            }

            // This plugin's own UI button commands and SimpleSymmetry's console command. Oxide
            // registers plugin console commands with the admin flag, so the firewall below
            // would otherwise block them.
            if (arg.cmd.FullName.StartsWith("gridspawn.") || arg.cmd.Name == "symmetry")
                return null;

            if (!arg.cmd.ServerAdmin)
                return null;

            if (!grantedModerators.Contains(arg.Connection.userid))
                return null;

            if (AllowedAdminCommands.Contains(arg.cmd.FullName))
                return null;

            arg.ReplyWith("You don't have permission to use this command.");
            return false;
        }

        // God mode: every real player is immune to all damage unless they switched god mode
        // off in the menu. Suicide stays allowed so the kill command keeps working; NPCs
        // (which also derive from BasePlayer) are unaffected.
        private object OnEntityTakeDamage(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || !player.userID.IsSteamId())
                return null;

            if (godDisabled.Contains(player.userID))
                return null;

            if (info.damageTypes != null && info.damageTypes.Has(Rust.DamageType.Suicide))
                return null;

            return true;
        }

        // Building or flying inside rocks and caves would normally trigger "inside terrain"
        // antihack kicks; disable that check for everyone (TerrainViolationFix logic).
        private object OnPlayerViolation(BasePlayer player, AntiHackType type)
        {
            if (type == AntiHackType.InsideTerrain)
                return false;

            return null;
        }

        private bool IsHoldingBuildingPlan(BasePlayer player)
        {
            Item activeItem = player.GetActiveItem();
            return activeItem != null && activeItem.info != null && activeItem.info.shortname == BuildingPlanShortName;
        }

        private int GetSelectedGrade(BasePlayer player)
        {
            int grade;
            return selectedGrades.TryGetValue(player.userID, out grade) ? grade : 0;
        }

        private void CycleGrade(BasePlayer player)
        {
            selectedGrades[player.userID] = (GetSelectedGrade(player) + 1) % GradeLabels.Length;
            ShowGradeUi(player);
            ShowGradeToast(player);
        }

        // The "BGRADE: METAL" toast shown whenever the placement grade changes.
        private void ShowGradeToast(BasePlayer player)
        {
            player.SendConsoleCommand("gametip.showtoast", 0, "BGRADE: " + GradeLabels[GetSelectedGrade(player)], string.Empty, false);
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null)
                return;

            NextTick(() =>
            {
                if (player == null || player.net == null || player.net.connection == null)
                    return;

                if (IsHoldingBuildingPlan(player) && HasNoClipPermission(player))
                    ShowGradeUi(player);
                else
                    DestroyGradeUi(player);
            });
        }

        // Small material strip above the hotbar while the building plan is held; the selected
        // grade is highlighted and X cycles through them. Tiles are laid out in pixel offsets
        // as exact squares so the textures render undistorted.
        private void ShowGradeUi(BasePlayer player)
        {
            DestroyGradeUi(player);

            int selected = GetSelectedGrade(player);

            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 0.85" },
                RectTransform = { AnchorMin = "0.645 0", AnchorMax = "0.645 0", OffsetMin = "0 22", OffsetMax = "248 74" }
            }, "Hud", GradeUiName);

            container.Add(new CuiLabel
            {
                Text = { Text = "Press 'X' to change grades.", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "0 -18", OffsetMax = "0 0" }
            }, GradeUiName);

            for (int i = 0; i < GradeLabels.Length; i++)
            {
                int left = 4 + (i * 48);
                string tile = container.Add(new CuiPanel
                {
                    Image = { Color = i == selected ? "0.29 0.41 1 0.95" : "1 1 1 0.10" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = left + " 4", OffsetMax = (left + 44) + " 48" }
                }, GradeUiName);

                string png = GetCustomGradeImage(i);
                if (string.IsNullOrEmpty(png))
                    png = GetImage(GradeIcons[i]);

                if (!string.IsNullOrEmpty(png))
                {
                    container.Add(new CuiElement
                    {
                        Parent = tile,
                        Components =
                        {
                            new CuiRawImageComponent { Png = png },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "3 3", OffsetMax = "-3 -3" }
                        }
                    });
                }
                else
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = GradeLabels[i], FontSize = 10, Align = TextAnchor.MiddleCenter, Color = i == selected ? "1 1 1 1" : "1 1 1 0.6" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }, tile);
                }
            }

            CuiHelper.AddUi(player, container);
        }

        // Item icon PNGs served by the ImageLibrary plugin; null until its icon cache is ready,
        // in which case the UI falls back to text labels.
        private string GetImage(string name)
        {
            return ImageLibrary != null ? ImageLibrary.Call("GetImage", name) as string : null;
        }

        private string GradeImageKey(int index)
        {
            return "randomgridspawn.grade." + GradeLabels[index].ToLower();
        }

        // Hands the custom tile images to ImageLibrary so it loads and caches them. Local files
        // are referenced with file:// URLs pointing into the server's oxide/data folder.
        private void RegisterGradeImages()
        {
            if (ImageLibrary == null)
                return;

            string localFolder = "file:///" + Interface.Oxide.DataDirectory.Replace("\\", "/").TrimStart('/') + "/" + GradeImageFolder + "/";

            for (int i = 0; i < GradeLabels.Length; i++)
            {
                string url = GradeImageUrls[i];
                if (string.IsNullOrEmpty(url))
                    url = localFolder + GradeImageFiles[i];

                ImageLibrary.Call("AddImage", url, GradeImageKey(i), 0UL);
            }

            foreach (KeyValuePair<string, string> entry in ItemImageFiles)
                ImageLibrary.Call("AddImage", localFolder + entry.Value, "randomgridspawn.item." + entry.Key, 0UL);
        }

        // Adds an item icon element: the provided PNG when loaded, otherwise the item icon
        // rendered natively by the client from its own bundles (works for every item).
        private void AddItemIcon(CuiElementContainer container, string parent, ItemDefinition definition, string anchor, string offsetMin, string offsetMax)
        {
            if (definition == null)
                return;

            string png = GetItemImage(definition);
            CuiElement element = new CuiElement { Parent = parent };

            if (!string.IsNullOrEmpty(png))
                element.Components.Add(new CuiRawImageComponent { Png = png });
            else
                element.Components.Add(new CuiImageComponent { ItemId = definition.itemid });

            element.Components.Add(new CuiRectTransformComponent { AnchorMin = anchor, AnchorMax = anchor, OffsetMin = offsetMin, OffsetMax = offsetMax });
            container.Add(element);
        }

        // Icon for an item in the raid/cost panels: the provided PNG when loaded, otherwise
        // ImageLibrary's stock icon for that item.
        private string GetItemImage(ItemDefinition definition)
        {
            if (definition == null || ImageLibrary == null)
                return null;

            if (ItemImageFiles.ContainsKey(definition.shortname))
            {
                string key = "randomgridspawn.item." + definition.shortname;
                object has = ImageLibrary.Call("HasImage", key, 0UL);
                if (has is bool && (bool)has)
                    return ImageLibrary.Call("GetImage", key) as string;
            }

            return GetImage(definition.shortname);
        }

        // Only trust a custom image if ImageLibrary actually managed to load it; otherwise the
        // tile falls back to the vanilla item icon.
        private string GetCustomGradeImage(int index)
        {
            if (ImageLibrary == null)
                return null;

            object has = ImageLibrary.Call("HasImage", GradeImageKey(index), 0UL);
            if (!(has is bool) || !(bool)has)
                return null;

            return ImageLibrary.Call("GetImage", GradeImageKey(index)) as string;
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "ImageLibrary")
                RegisterGradeImages();
        }

        private void DestroyGradeUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, GradeUiName);
        }

        // Auto-upgrades placed building blocks to the selected grade. Core logic adapted from
        // the BGrade plugin (Ryan / Rustoria.co), with resource costs and timers removed.
        // Building and upgrading cost nothing at all: without these, the server deducts the
        // cost from the virtual stacks and the client pops a "-150 wood" notification for
        // every single piece placed. Blocking payment outright keeps it silent and free.
        private object OnPayForPlacement(BasePlayer player, Planner planner, Construction construction)
        {
            return HasNoClipPermission(player) ? (object)true : null;
        }

        private object OnPayForUpgrade(BasePlayer player, BuildingBlock block, ConstructionGrade grade)
        {
            return HasNoClipPermission(player) ? (object)true : null;
        }

        private void OnEntityBuilt(Planner plan, GameObject gameObject)
        {
            BasePlayer player = plan != null ? plan.GetOwnerPlayer() : null;
            if (player == null || gameObject == null || !HasNoClipPermission(player))
                return;

            // Record every player placement for undo (structures and deployables).
            BaseEntity built = gameObject.GetComponent<BaseEntity>();
            if (built != null)
            {
                NextTick(() =>
                {
                    if (built != null && !built.IsDestroyed)
                        RecordPlace(player, built);
                });
            }

            if (plan.isTypeDeployable)
                return;

            BuildingBlock buildingBlock = gameObject.GetComponent<BuildingBlock>();
            if (buildingBlock == null)
                return;

            int grade = GetSelectedGrade(player);
            if (grade <= 0)
                return;

            if (grade < (int)buildingBlock.grade || buildingBlock.blockDefinition.grades[grade] == null)
                return;

            buildingBlock.SetGrade((BuildingGrade.Enum)grade);
            buildingBlock.SetHealthToMax();
            buildingBlock.StartBeingRotatable();
            buildingBlock.SendNetworkUpdate();
            buildingBlock.UpdateSkin();
            buildingBlock.ResetUpkeepTime();
            buildingBlock.GetBuilding()?.Dirty();
        }

        // Hammer + Shift + left click upgrades the aimed block one tier; Ctrl + left click
        // downgrades it. Raycast-based, so it works at range (same reach as the R delete),
        // and only inside the player's own area.
        private void TryChangeTier(BasePlayer player, bool upgrade)
        {
            if (!HasNoClipPermission(player))
                return;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, EntKillDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;

            BuildingBlock block = hit.GetEntity() as BuildingBlock;
            if (block == null || block.IsDestroyed)
                return;

            // Crouch-hit on twigs has nothing to downgrade; treat it as a symmetry set so
            // crouching to aim at the floor by your feet still sets the point.
            if (!upgrade && block.grade == BuildingGrade.Enum.Twigs)
            {
                SetSymmetryAt(player, block, hit.point, area);
                return;
            }

            ChangeTier(player, block, upgrade, area);
        }

        private void ChangeTier(BasePlayer player, BuildingBlock block, bool upgrade, AssignedArea area)
        {
            if (!IsInsideArea(block.transform.position, area))
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "You can only modify structures inside your own area.", string.Empty, false);
                return;
            }

            if (HammerActionDebounced(player))
                return;

            int grade = (int)block.grade + (upgrade ? 1 : -1);
            if (grade < (int)BuildingGrade.Enum.Twigs || grade > (int)BuildingGrade.Enum.TopTier)
                return;

            if (grade >= block.blockDefinition.grades.Length || block.blockDefinition.grades[grade] == null)
                return;

            RecordGrade(player, block, (int)block.grade);
            block.SetGrade((BuildingGrade.Enum)grade);
            block.SetHealthToMax();
            block.StartBeingRotatable();
            block.SendNetworkUpdate();
            block.UpdateSkin();
            block.ResetUpkeepTime();
            block.GetBuilding()?.Dirty();
        }

        // A hammer swing that CONNECTS consumes the primary-fire press for that input tick,
        // so the OnPlayerInput raycast path only sees whiffed swings. This hook covers real
        // contact and routes to the same actions.
        private void OnHammerHit(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || !HasNoClipPermission(player))
                return;

            BuildingBlock block = info.HitEntity as BuildingBlock;
            if (block == null || block.IsDestroyed)
                return;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            bool upgrade = player.serverInput.IsDown(BUTTON.SPRINT);
            bool downgrade = player.serverInput.IsDown(BUTTON.DUCK);

            if (upgrade != downgrade)
            {
                if (downgrade && block.grade == BuildingGrade.Enum.Twigs)
                    SetSymmetryAt(player, block, info.HitPositionWorld, area);
                else
                    ChangeTier(player, block, upgrade, area);
            }
            else if (!upgrade)
                SetSymmetryAt(player, block, info.HitPositionWorld, area);
        }

        // Both hammer paths (input raycast + hit hook) can fire for one swing; whoever runs
        // first wins and the echo within 0.3s is swallowed.
        private bool HammerActionDebounced(BasePlayer player)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            float last;
            if (lastHammerAction.TryGetValue(player.userID, out last) && now - last < 0.3f)
                return true;

            lastHammerAction[player.userID] = now;
            return false;
        }

        // Placing a personal marker on the map teleports the player there; the marker itself
        // is removed right after, so it acts as a pure teleport click. Landing on water puts
        // them on the surface rather than the seabed.
        private void OnMapMarkerAdded(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (player == null || note == null || player.IsDead() || !HasNoClipPermission(player))
                return;

            Vector3 position = GetGroundedPosition(note.worldPosition);

            float waterHeight = TerrainMeta.WaterMap.GetHeight(position);
            if (waterHeight > position.y)
                position.y = waterHeight + 0.5f;

            player.Teleport(position);
            player.SendNetworkUpdateImmediate();

            // The note is stored after this hook returns; strip it on the next tick and push
            // the updated marker list so the client's pin disappears again.
            NextTick(() =>
            {
                if (player == null || player.State == null || player.State.pointsOfInterest == null)
                    return;

                List<ProtoBuf.MapNote> notes = player.State.pointsOfInterest;
                for (int i = notes.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(notes[i], note) || Vector3.Distance(notes[i].worldPosition, note.worldPosition) < 0.1f)
                    {
                        notes.RemoveAt(i);
                        break;
                    }
                }

                player.DirtyPlayerState();
                player.SendMarkersToClient();
            });
        }

        #region Raid counter

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon item)
        {
            TrackExplosive(player, item != null ? item.GetOwnerItemDefinition() : null);
        }

        private void OnExplosiveDropped(BasePlayer player, BaseEntity entity, ThrownWeapon item)
        {
            TrackExplosive(player, item != null ? item.GetOwnerItemDefinition() : null);
        }

        // Bulk grade changes across the player's own plot:
        //   /gradeall <grade>         - force every block to that grade
        //   /gradeall <from> <to>     - convert only blocks of one grade to another
        [ChatCommand("gradeall")]
        private void GradeAllCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            int targetGrade = -1;
            int fromGrade = -1;
            bool valid = args != null && args.Length >= 1 && args.Length <= 2;

            if (valid && args.Length == 1)
                valid = TryParseGrade(args[0], out targetGrade);
            else if (valid)
                valid = TryParseGrade(args[0], out fromGrade) && TryParseGrade(args[1], out targetGrade);

            if (!valid)
            {
                player.ChatMessage("Usage: /gradeall <grade> to set every block, or /gradeall <from> <to> to convert one grade into another. Grades: twig, wood, stone, metal, hqm (or 0-4).");
                return;
            }

            int changed = ApplyGradeAll(player, fromGrade, targetGrade);
            player.ChatMessage(changed + " block(s) " + (fromGrade >= 0 ? "converted from " + GradeLabels[fromGrade] + " to " : "set to ") + GradeLabels[targetGrade] + ".");
        }

        private int ApplyGradeAll(BasePlayer player, int fromGrade, int targetGrade)
        {
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return 0;

            int changed = 0;
            HashSet<BuildingManager.Building> buildings = new HashSet<BuildingManager.Building>();

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BuildingBlock block = networkable as BuildingBlock;
                if (block == null || block.IsDestroyed || !IsInsideArea(block.transform.position, area))
                    continue;

                if (fromGrade >= 0 && (int)block.grade != fromGrade)
                    continue;

                if ((int)block.grade == targetGrade)
                    continue;

                if (targetGrade >= block.blockDefinition.grades.Length || block.blockDefinition.grades[targetGrade] == null)
                    continue;

                block.SetGrade((BuildingGrade.Enum)targetGrade);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
                block.UpdateSkin();
                block.ResetUpkeepTime();

                BuildingManager.Building building = block.GetBuilding();
                if (building != null)
                    buildings.Add(building);

                changed++;
            }

            foreach (BuildingManager.Building building in buildings)
                building.Dirty();

            return changed;
        }

        private bool TryParseGrade(string value, out int grade)
        {
            switch (value.ToLower())
            {
                case "twig": case "twigs": case "0": grade = 0; return true;
                case "wood": case "1": grade = 1; return true;
                case "stone": case "stones": case "2": grade = 2; return true;
                case "metal": case "sheet": case "3": grade = 3; return true;
                case "hqm": case "armoured": case "armored": case "toptier": case "4": grade = 4; return true;
            }

            grade = -1;
            return false;
        }

        #region Server menu

        private const string MenuUiName = "RandomGridSpawn.Menu";
        private const string MenuTopName = "RandomGridSpawn.Menu.Top";
        private const string MenuBodyName = "RandomGridSpawn.Menu.Body";
        private const string MenuGridName = "RandomGridSpawn.Menu.Grid";
        private const string MenuActionsCardName = "RandomGridSpawn.Menu.Actions";
        private const string MenuBgradeCardName = "RandomGridSpawn.Menu.Bgrade";
        private const string MenuSymCardName = "RandomGridSpawn.Menu.Symmetry";
        private const string MenuSaveModalName = "RandomGridSpawn.Menu.Save";
        private const string MenuMlrsModalName = "RandomGridSpawn.Menu.Mlrs";
        private const string MenuFloorModalName = "RandomGridSpawn.Menu.Floors";
        private const string MlrsRocketPrefab = "assets/content/vehicles/mlrs/rocket_mlrs.prefab";
        // Vanilla cinematic backdrop prefabs: cinecyc_* are curved cycloramas (floor
        // sweeping up into a wall), cinebg_* are flat planes. One of each per colour.
        private const string CinewallCycPrefabFormat = "assets/bundled/prefabs/modding/cinematic/backdrops/cinecyc_{0}.prefab";
        private const string CinewallBgPrefabFormat = "assets/bundled/prefabs/modding/cinematic/backdrops/cinebg_{0}.prefab";
        private readonly HashSet<ulong> infiniteAmmo = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> pendingMlrsCount = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Dictionary<int, int>> envSelections = new Dictionary<ulong, Dictionary<int, int>>();
        private readonly Dictionary<ulong, CinewallOptions> cinewallOptions = new Dictionary<ulong, CinewallOptions>();
        private readonly Dictionary<ulong, List<BaseEntity>> cinewallEntities = new Dictionary<ulong, List<BaseEntity>>();

        // Cinewall swatch UI colours and the matching prefab colour names.
        private static readonly string[] CinewallColours =
        {
            "0.08 0.08 0.08 1", // black
            "0.2 0.35 0.85 1",  // blue
            "0.25 0.6 0.3 1",   // green
            "0.5 0.5 0.5 1",    // grey
            "0.78 0.72 0.55 1", // neutral
            "0.97 0.97 0.97 1"  // white
        };

        private static readonly string[] CinewallColourNames = { "black", "blue", "green", "grey", "neutral", "white" };

        private class CinewallOptions
        {
            public int ColourIndex = 5;
            public int Sides = 1;
            public bool Floor;
            public bool Roof;
            public float Scale = 1f;
            public float Gap = 4f;
            public float Lift = 0f;
            public float TileSize = 5f;
        }

        private class EnvSetting
        {
            public string Label;
            public string Section;
            public string Command;
            public float[] Presets;
            public float Default;
        }

        // All of these are pushed to the player's client as "<command> <value>" with the
        // value as an argument (the PersonalTime delivery pattern - the client runs pushed
        // COMMANDS for admin-flagged players; "admintime" is the pushable form of the time
        // override, env.admintime is the convar the client refuses). Per player only.
        private static readonly EnvSetting[] EnvSettingsTable =
        {
            new EnvSetting { Label = "Time", Section = "TIME", Command = "admintime", Presets = new[] { -1f, 0f, 6f, 9f, 12f, 15f, 18f, 21f }, Default = -1f },
            new EnvSetting { Label = "Fog", Section = "WEATHER", Command = "weather.fog", Presets = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }, Default = 0f },
            new EnvSetting { Label = "Rain", Section = "WEATHER", Command = "weather.rain", Presets = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }, Default = 0f },
            new EnvSetting { Label = "Rainbow", Section = "WEATHER", Command = "weather.rainbow", Presets = new[] { 0f, 0.5f, 1f }, Default = 0f },
            new EnvSetting { Label = "Thunder", Section = "WEATHER", Command = "weather.thunder", Presets = new[] { 0f, 0.5f, 1f }, Default = 0f },
            new EnvSetting { Label = "Wind", Section = "WEATHER", Command = "weather.wind", Presets = new[] { 0f, 0.5f, 1f }, Default = 0f },
            new EnvSetting { Label = "Brightness", Section = "ATMOSPHERE", Command = "atmosphere.brightness", Presets = new[] { 0.5f, 1f, 1.5f, 2f, 3f }, Default = 1f },
            new EnvSetting { Label = "Directionality", Section = "ATMOSPHERE", Command = "atmosphere.directionality", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Contrast", Section = "ATMOSPHERE", Command = "atmosphere.contrast", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Mie", Section = "ATMOSPHERE", Command = "atmosphere.mie", Presets = new[] { 0f, 0.5f, 1f, 2f, 5f }, Default = 1f },
            new EnvSetting { Label = "Rayleigh", Section = "ATMOSPHERE", Command = "atmosphere.rayleigh", Presets = new[] { 0f, 0.5f, 1f, 2f, 5f }, Default = 1f },
            new EnvSetting { Label = "Attenuation", Section = "CLOUD", Command = "cloud.attenuation", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Brightness", Section = "CLOUD", Command = "cloud.brightness", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Coloring", Section = "CLOUD", Command = "cloud.coloring", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Coverage", Section = "CLOUD", Command = "cloud.coverage", Presets = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }, Default = 0.5f },
            new EnvSetting { Label = "Opacity", Section = "CLOUD", Command = "cloud.opacity", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Saturation", Section = "CLOUD", Command = "cloud.saturation", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Scattering", Section = "CLOUD", Command = "cloud.scattering", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Sharpness", Section = "CLOUD", Command = "cloud.sharpness", Presets = new[] { 0f, 0.5f, 1f, 1.5f, 2f }, Default = 1f },
            new EnvSetting { Label = "Size", Section = "CLOUD", Command = "cloud.size", Presets = new[] { 0f, 1f, 2f, 3f, 4f }, Default = 2f }
        };
        private string symmetryPreviewPng;
        private readonly HashSet<ulong> godDisabled = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> upgradeFromSel = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> upgradeToSel = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, string> bgradeStatus = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> upgradeStatus = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> symStatus = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> menuOpen = new HashSet<ulong>();
        private readonly HashSet<ulong> awaitingPhoto = new HashSet<ulong>();
        private readonly Dictionary<ulong, Vector3> photoReturnPosition = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, List<BuildAction>> undoStacks = new Dictionary<ulong, List<BuildAction>>();
        private readonly Dictionary<ulong, List<BuildAction>> redoStacks = new Dictionary<ulong, List<BuildAction>>();
        private const int MaxUndoHistory = 60;

        // A reversible building action for undo/redo. In-memory only.
        private class BuildAction
        {
            public string Kind; // "place", "delete", "grade"
            public ulong NetId;
            public string Prefab;
            public Vector3 Position;
            public Quaternion Rotation;
            public int Grade = -1;
            public ulong Skin;
            public int OldGrade;
        }
        private readonly Dictionary<ulong, float> lastHammerAction = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, string> menuPage = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> browserTab = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> browserTagCategory = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, HashSet<string>> browserFilterTags = new Dictionary<ulong, HashSet<string>>();
        private readonly Dictionary<ulong, string> menuSearch = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> browserPage = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, string> pendingSaveName = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> pendingSaveVisibility = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, string> pendingSaveImage = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> pendingSaveYoutube = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, HashSet<string>> pendingSaveTags = new Dictionary<ulong, HashSet<string>>();
        private Dictionary<string, SavedBase> savedBases = new Dictionary<string, SavedBase>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Random CodeRandom = new System.Random();

        private class SavedEntity
        {
            public string Prefab;
            public float X;
            public float Y;
            public float Z;
            public float RotX;
            public float RotY;
            public float RotZ;
            public int Grade = -1;
            public ulong Skin;
        }

        private class SavedBase
        {
            public string Code;
            public string Name;
            public ulong OwnerId;
            public string OwnerName;
            public bool Public;          // legacy - migrated into Visibility on load
            public int Visibility;       // 0 private, 1 unlisted (code only), 2 public
            public string ImageUrl;
            public string ImageCrc;      // auto-generated blueprint thumbnail in file storage
            public string YoutubeUrl;
            public List<string> Tags = new List<string>();
            public bool IsAutosave;
            public bool IsQuicksave;
            public bool Featured;
            public int LoadCount;
            public string CreatedUtc;
            public Dictionary<string, int> Costs = new Dictionary<string, int>();
            public List<SavedEntity> Entities = new List<SavedEntity>();
        }

        // Browser categories in the left sidebar.
        private static readonly string[,] BrowserTabs =
        {
            { "mine", "MY BASES" },
            { "favourites", "FAVOURITES" },
            { "friends", "FRIEND BASES" },
            { "trending", "TRENDING" },
            { "featured", "FEATURED" },
            { "creator", "CREATOR BASES" },
            { "all", "BASE BROWSER" }
        };

        // Per-player favourited save codes.
        private Dictionary<ulong, HashSet<string>> favouriteBases = new Dictionary<ulong, HashSet<string>>();

        private class PublishTagCategory
        {
            public string Name;
            public string[] Tags;
        }

        private static readonly PublishTagCategory[] PublishTags =
        {
            new PublishTagCategory { Name = "Bunkers", Tags = new[] { "No bunkers", "Stability bunker", "Suicide bunker", "Roof bunker" } },
            new PublishTagCategory { Name = "Type", Tags = new[] { "Compound included", "Misc", "Starter", "Concept", "WIP", "Multi TC" } },
            new PublishTagCategory { Name = "Defense", Tags = new[] { "Mountain roof", "Roof defense", "Outer peeks", "Inner peeks" } },
            new PublishTagCategory { Name = "Group Size", Tags = new[] { "Solo", "Duo", "Trio", "Small group (4-6)", "Large group (7-10)", "Clan (10+)" } },
            new PublishTagCategory { Name = "Modded", Tags = new[] { "10x Modded", "5x Modded", "3x Modded", "2x Modded", "1x Vanilla" } }
        };

        [ChatCommand("menu")]
        private void MenuCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            ShowMenuUi(player);
        }

        [ConsoleCommand("gridspawn.menu")]
        private void MenuConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasNoClipPermission(player))
                return;

            switch (arg.GetString(0, string.Empty))
            {
                case "grade":
                    selectedGrades[player.userID] = Mathf.Clamp(arg.GetInt(1, 0), 0, 4);
                    if (IsHoldingBuildingPlan(player))
                        ShowGradeUi(player);
                    ShowGradeToast(player);
                    UpdateBgradeCard(player);
                    return;

                case "gradeall":
                    int changed = ApplyGradeAll(player, -1, Mathf.Clamp(arg.GetInt(1, 0), 0, 4));
                    bgradeStatus[player.userID] = changed == 0 ? "No blocks were changed." : changed + " block(s) changed.";
                    player.ChatMessage(changed + " block(s) changed.");
                    UpdateBgradeCard(player);
                    return;

                case "upfrom":
                    upgradeFromSel[player.userID] = Mathf.Clamp(arg.GetInt(1, 0), 0, 4);
                    UpdateBgradeCard(player);
                    return;

                case "upto":
                    upgradeToSel[player.userID] = Mathf.Clamp(arg.GetInt(1, 0), 0, 4);
                    UpdateBgradeCard(player);
                    return;

                case "upgradexy":
                    int converted = ApplyGradeAll(player, GetUpgradeFrom(player), GetUpgradeTo(player));
                    upgradeStatus[player.userID] = converted == 0 ? "No blocks were changed - check your FROM grade." : converted + " block(s) changed.";
                    player.ChatMessage(converted + " block(s) changed.");
                    UpdateBgradeCard(player);
                    return;

                case "tab":
                {
                    string requested = arg.GetString(1, "features");
                    menuPage[player.userID] = requested == "browser" || requested == "servers" ? requested : "features";
                    RefreshMenu(player);
                    return;
                }

                case "btab":
                    browserTab[player.userID] = arg.GetString(1, "mine");
                    browserPage[player.userID] = 0;
                    RefreshMenu(player);
                    return;

                case "btagcat":
                {
                    string category = arg.GetString(1, string.Empty);
                    string current;
                    if (browserTagCategory.TryGetValue(player.userID, out current) && current == category)
                        browserTagCategory.Remove(player.userID);
                    else
                        browserTagCategory[player.userID] = category;
                    RefreshMenu(player);
                    return;
                }

                case "btagpick":
                {
                    string tag = JoinArgs(arg, 1);
                    HashSet<string> selected;
                    if (!browserFilterTags.TryGetValue(player.userID, out selected))
                        browserFilterTags[player.userID] = selected = new HashSet<string>();

                    if (!selected.Remove(tag))
                        selected.Add(tag);

                    browserPage[player.userID] = 0;
                    RefreshMenu(player);
                    return;
                }

                case "btagclear":
                    browserFilterTags.Remove(player.userID);
                    browserPage[player.userID] = 0;
                    RefreshMenu(player);
                    return;

                case "fav":
                {
                    string favCode = arg.GetString(1, string.Empty);
                    if (!savedBases.ContainsKey(favCode))
                        return;

                    HashSet<string> favourites;
                    if (!favouriteBases.TryGetValue(player.userID, out favourites))
                        favouriteBases[player.userID] = favourites = new HashSet<string>();

                    if (!favourites.Remove(favCode))
                        favourites.Add(favCode);

                    SaveFavourites();
                    RefreshMenu(player);
                    return;
                }

                case "bpage":
                    browserPage[player.userID] = Mathf.Max(0, arg.GetInt(1, 0));
                    RefreshMenu(player);
                    return;

                case "search":
                {
                    string filter = JoinArgs(arg, 1);
                    if (string.IsNullOrEmpty(filter))
                        menuSearch.Remove(player.userID);
                    else
                        menuSearch[player.userID] = filter;

                    browserPage[player.userID] = 0;
                    menuPage[player.userID] = "browser";
                    RefreshMenu(player);
                    return;
                }

                case "createsave":
                    ShowSaveModal(player);
                    return;

                case "closesave":
                    CuiHelper.DestroyUi(player, MenuSaveModalName);
                    return;

                case "givecamera":
                {
                    Item cameraItem = null;
                    string[] cameraNames = { "tool.instant_camera", "instant_camera", "instantcamera", "tool.camera" };
                    foreach (string cameraName in cameraNames)
                    {
                        cameraItem = ItemManager.CreateByName(cameraName, 1);
                        if (cameraItem != null)
                            break;
                    }

                    if (cameraItem == null)
                    {
                        player.SendConsoleCommand("gametip.showtoast", 1, "Could not create a camera item on this server build.", string.Empty, false);
                        return;
                    }

                    CuiHelper.DestroyUi(player, MenuSaveModalName);
                    CloseMenu(player);
                    awaitingPhoto.Add(player.userID);
                    player.GiveItem(cameraItem, BaseEntity.GiveItemReason.PickedUp);

                    NextTick(() =>
                    {
                        if (player == null || !player.IsConnected)
                            return;

                        // Equip the camera and fly the player to a framing position that
                        // fits the whole base, so all they have to do is aim and click.
                        EquipItem(player, cameraItem);
                        FrameBaseForPhoto(player);
                    });
                    return;
                }

                // Text commits do NOT redraw the dialog: input fields fire their command on
                // focus loss, and a redraw right then eats the button click that caused it
                // (the old "have to click twice" bug).
                case "savename":
                    pendingSaveName[player.userID] = JoinArgs(arg, 1);
                    return;

                case "saveimg":
                    pendingSaveImage[player.userID] = JoinArgs(arg, 1);
                    return;

                case "saveyt":
                    pendingSaveYoutube[player.userID] = JoinArgs(arg, 1);
                    return;

                case "savevis":
                    pendingSaveVisibility[player.userID] = Mathf.Clamp(arg.GetInt(1, 0), 0, 2);
                    ShowSaveModal(player);
                    return;

                case "pubtag":
                {
                    int categoryIndex = arg.GetInt(1, -1);
                    int tagIndex = arg.GetInt(2, -1);
                    if (categoryIndex < 0 || categoryIndex >= PublishTags.Length)
                        return;
                    if (tagIndex < 0 || tagIndex >= PublishTags[categoryIndex].Tags.Length)
                        return;

                    HashSet<string> pendingTags;
                    if (!pendingSaveTags.TryGetValue(player.userID, out pendingTags))
                        pendingSaveTags[player.userID] = pendingTags = new HashSet<string>();

                    string tag = PublishTags[categoryIndex].Tags[tagIndex];
                    if (!pendingTags.Remove(tag))
                        pendingTags.Add(tag);

                    ShowSaveModal(player);
                    return;
                }

                case "dosave":
                {
                    string saveName;
                    pendingSaveName.TryGetValue(player.userID, out saveName);

                    SavedBase created = CaptureBase(player, saveName);
                    if (created == null)
                    {
                        player.SendConsoleCommand("gametip.showtoast", 1, "Nothing to publish - your plot is empty.", string.Empty, false);
                        return;
                    }

                    int visibility;
                    pendingSaveVisibility.TryGetValue(player.userID, out visibility);
                    created.Visibility = Mathf.Clamp(visibility, 0, 2);
                    created.Public = created.Visibility == 2;

                    string imageUrl;
                    if (pendingSaveImage.TryGetValue(player.userID, out imageUrl) && !string.IsNullOrEmpty(imageUrl))
                    {
                        created.ImageUrl = imageUrl;
                        if (ImageLibrary != null)
                            ImageLibrary.Call("AddImage", imageUrl, "base_" + created.Code, 0UL);
                    }
                    else
                    {
                        // No URL: an instant-camera photo in the inventory beats the
                        // generated blueprint thumbnail.
                        uint photoCrc = FindNewestPhotoCrc(player);
                        if (photoCrc != 0)
                            created.ImageCrc = photoCrc.ToString();
                    }

                    string youtubeUrl;
                    if (pendingSaveYoutube.TryGetValue(player.userID, out youtubeUrl) && !string.IsNullOrEmpty(youtubeUrl))
                        created.YoutubeUrl = youtubeUrl;

                    HashSet<string> chosenTags;
                    if (pendingSaveTags.TryGetValue(player.userID, out chosenTags))
                        created.Tags = new List<string>(chosenTags);

                    SaveSavedBases();

                    // No player-supplied image: ask the render service for a real 3D one.
                    if (string.IsNullOrEmpty(created.ImageUrl) && config != null && config.RenderOnPublish)
                        RequestRender(created, player);

                    CuiHelper.DestroyUi(player, MenuSaveModalName);
                    pendingSaveName.Remove(player.userID);
                    pendingSaveTags.Remove(player.userID);
                    menuPage[player.userID] = "browser";
                    browserTab[player.userID] = "mine";
                    RefreshMenu(player);
                    player.ChatMessage("Published '" + created.Name + "' - code " + created.Code + ".");
                    return;
                }

                case "load":
                    TryLoadSave(player, arg.GetString(1, string.Empty), 0, 0);
                    return;

                case "loadfound":
                    TryLoadSave(player, arg.GetString(1, string.Empty), 1, 0);
                    return;

                case "floorask":
                {
                    SavedBase floorSave;
                    if (savedBases.TryGetValue(arg.GetString(1, string.Empty), out floorSave))
                        ShowFloorModal(player, floorSave);
                    return;
                }

                case "floorclose":
                    CuiHelper.DestroyUi(player, MenuFloorModalName);
                    return;

                case "floorload":
                {
                    int floors = Mathf.Max(1, arg.GetInt(2, 1));
                    CuiHelper.DestroyUi(player, MenuFloorModalName);
                    TryLoadSave(player, arg.GetString(1, string.Empty), 2, floors);
                    return;
                }

                case "delsave":
                {
                    SavedBase saved;
                    if (savedBases.TryGetValue(arg.GetString(1, string.Empty), out saved) && saved.OwnerId == player.userID)
                    {
                        savedBases.Remove(saved.Code);
                        SaveSavedBases();
                    }

                    RefreshMenu(player);
                    return;
                }

                case "vissave":
                {
                    SavedBase saved;
                    if (savedBases.TryGetValue(arg.GetString(1, string.Empty), out saved) && saved.OwnerId == player.userID)
                    {
                        saved.Visibility = (saved.Visibility + 1) % 3;
                        saved.Public = saved.Visibility == 2;
                        SaveSavedBases();
                    }

                    RefreshMenu(player);
                    return;
                }

                case "gohome":
                {
                    AssignedArea homeArea;
                    if (!assignedAreas.TryGetValue(player.userID, out homeArea))
                    {
                        player.SendConsoleCommand("gametip.showtoast", 1, "You don't have a plot yet.", string.Empty, false);
                        return;
                    }

                    CloseMenu(player);
                    TeleportInsideArea(player, homeArea, homeArea.SpawnPosition);
                    return;
                }

                case "god":
                    if (!godDisabled.Remove(player.userID))
                        godDisabled.Add(player.userID);
                    UpdateActionsCard(player);
                    return;

                case "noclip":
                    ToggleFlight(player);
                    return;

                case "undo":
                    PerformUndo(player);
                    return;

                case "redo":
                    PerformRedo(player);
                    return;

                case "quicksave":
                    QuickSave(player);
                    return;

                case "exit":
                    CloseMenu(player);
                    return;

                case "debugcam":
                    CloseMenu(player);
                    player.SendConsoleCommand("debugcamera");
                    return;

                case "resetinv":
                {
                    player.inventory.Strip();

                    string[] starterItems = { "building.planner", "hammer", CupboardShortName };
                    foreach (string shortname in starterItems)
                    {
                        Item item = ItemManager.CreateByName(shortname, 1);
                        if (item != null && !item.MoveToContainer(player.inventory.containerBelt))
                            item.Remove();
                    }

                    player.SendConsoleCommand("gametip.showtoast", 0, "Inventory reset.", string.Empty, false);
                    return;
                }

                case "infammo":
                    if (!infiniteAmmo.Remove(player.userID))
                        infiniteAmmo.Add(player.userID);
                    UpdateActionsCard(player);
                    return;

                case "mlrs":
                    ShowMlrsModal(player);
                    return;

                case "mlrsclose":
                    CuiHelper.DestroyUi(player, MenuMlrsModalName);
                    return;

                case "mlrscount":
                {
                    int requestedRockets;
                    if (int.TryParse(arg.GetString(1, string.Empty), out requestedRockets))
                        pendingMlrsCount[player.userID] = Mathf.Clamp(requestedRockets, 1, 48);
                    ShowMlrsModal(player);
                    return;
                }

                case "mlrsfire":
                {
                    int rockets;
                    if (!pendingMlrsCount.TryGetValue(player.userID, out rockets))
                        rockets = 12;

                    CuiHelper.DestroyUi(player, MenuMlrsModalName);
                    CloseMenu(player);
                    LaunchMlrsStrike(player, rockets);
                    return;
                }

                case "envpage":
                    menuPage[player.userID] = "environment";
                    RefreshMenu(player);
                    return;

                case "cinepage":
                    menuPage[player.userID] = "cinewall";
                    RefreshMenu(player);
                    return;

                case "envset":
                {
                    int settingIndex = arg.GetInt(1, -1);
                    int presetIndex = arg.GetInt(2, -1);
                    if (settingIndex < 0 || settingIndex >= EnvSettingsTable.Length)
                        return;

                    EnvSetting setting = EnvSettingsTable[settingIndex];
                    if (presetIndex < 0 || presetIndex >= setting.Presets.Length)
                        return;

                    // Command name + value as an argument: the delivery pattern PersonalTime
                    // proved the client accepts from admin-flagged players.
                    string valueText = setting.Presets[presetIndex].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    player.SendConsoleCommand(setting.Command, valueText);

                    Dictionary<int, int> chosen;
                    if (!envSelections.TryGetValue(player.userID, out chosen))
                        envSelections[player.userID] = chosen = new Dictionary<int, int>();
                    chosen[settingIndex] = presetIndex;

                    RefreshMenu(player);
                    return;
                }

                case "envreset":
                {
                    envSelections.Remove(player.userID);
                    player.SendConsoleCommand("admintime", "-1");

                    foreach (EnvSetting setting in EnvSettingsTable)
                    {
                        if (setting.Command == "admintime")
                            continue;

                        player.SendConsoleCommand(setting.Command, setting.Default.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    RefreshMenu(player);
                    return;
                }

                case "cine":
                    ApplyCinewallAction(player, arg.GetString(1, string.Empty), arg.GetInt(2, 0));
                    return;

                case "rocketgun":
                    RocketGunCommand(player, "rocketgun", Array.Empty<string>());
                    return;

                case "circuit":
                    circuitViews[player.userID] = new CircuitView();
                    menuPage[player.userID] = "circuit";
                    RefreshMenu(player);
                    return;

                case "cost":
                    CuiHelper.DestroyUi(player, MenuUiName);
                    CostCommand(player, "cost", Array.Empty<string>());
                    return;

                case "raidreset":
                    raidCounts.Remove(player.userID);
                    CuiHelper.DestroyUi(player, RaidUiName);
                    return;

                case "sym":
                    Plugin simpleSymmetry = plugins.Find("SimpleSymmetry");
                    if (simpleSymmetry == null || !simpleSymmetry.IsLoaded)
                    {
                        player.SendConsoleCommand("gametip.showtoast", 1, "SimpleSymmetry is not loaded on the server.", string.Empty, false);
                        return;
                    }

                    string symAction = arg.GetString(1, "help");

                    // The SYMMETRY PANEL button hands over to SimpleSymmetry's own panel;
                    // close the menu so the panel is usable in-game.
                    if (symAction == "ui")
                        CloseMenu(player);

                    simpleSymmetry.Call("ChatCmdSymmetry", player, "sym", new[] { symAction });

                    // Every other action is handled from our menu - suppress the panel
                    // SimpleSymmetry pops up in the top-right corner after each action.
                    if (symAction != "ui")
                    {
                        NextTick(() =>
                        {
                            if (player == null || player.IsDestroyed)
                                return;

                            CuiHelper.DestroyUi(player, "SymmetryPanel");
                            symStatus[player.userID] = BuildSymmetryStatus(simpleSymmetry, player, symAction);
                            UpdateSymmetryCard(player);
                        });
                        timer.Once(0.3f, () =>
                        {
                            if (player != null && !player.IsDestroyed)
                                CuiHelper.DestroyUi(player, "SymmetryPanel");
                        });
                    }
                    return;
            }
        }

        // Full-screen dashboard with a top tab bar (build browser / features / servers).
        // Only the top bar and body are redrawn on navigation - the root panel holding
        // CursorEnabled stays alive so the player's cursor position is preserved.
        private void ShowMenuUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuUiName);

            CuiElementContainer container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.04 0.04 0.045 0.99" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", MenuUiName);

            CuiHelper.AddUi(player, container);
            menuOpen.Add(player.userID);

            // SimpleSymmetry redraws its own panel after every action (including on a
            // delayed timer), so just destroying it loses the race - switch its UI flag
            // off while the menu is open. The SYMMETRY PANEL button toggles it back on.
            Plugin simpleSymmetryPlugin = plugins.Find("SimpleSymmetry");
            if (simpleSymmetryPlugin != null && simpleSymmetryPlugin.IsLoaded && permission.UserHasPermission(player.UserIDString, "simplesymmetry.use"))
                simpleSymmetryPlugin.Call("ChatCmdSymmetry", player, "sym", new[] { "ui", "false" });
            CuiHelper.DestroyUi(player, "SymmetryPanel");

            RefreshMenu(player);
        }

        private void CloseMenu(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuUiName);
            menuOpen.Remove(player.userID);
        }

        private void RefreshMenu(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuTopName);
            CuiHelper.DestroyUi(player, MenuBodyName);

            CuiElementContainer container = new CuiElementContainer();

            DrawTopBar(container, player);

            string body = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 -48" }
            }, MenuUiName, MenuBodyName);

            switch (GetMenuPage(player))
            {
                case "browser":
                    DrawBrowserPage(container, body, player);
                    break;
                case "servers":
                    DrawServersPage(container, body, player);
                    break;
                case "circuit":
                    DrawCircuitPage(container, body, player);
                    break;
                case "environment":
                    DrawEnvironmentPage(container, body, player);
                    break;
                case "cinewall":
                    DrawCinewallPage(container, body, player);
                    break;
                default:
                    DrawFeaturesPage(container, body, player);
                    break;
            }

            CuiHelper.AddUi(player, container);
        }

        private void DrawTopBar(CuiElementContainer container, BasePlayer player)
        {
            string top = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -48", OffsetMax = "0 0" }
            }, MenuUiName, MenuTopName);

            container.Add(new CuiLabel
            {
                Text = { Text = "BUILD MENU", FontSize = 16, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = "20 0", OffsetMax = "280 0" }
            }, top);

            string page = GetMenuPage(player);
            string[,] tabs = { { "browser", "BUILD BROWSER" }, { "features", "FEATURES" }, { "servers", "SERVERS" } };
            int tabX = 300;
            for (int i = 0; i < 3; i++)
            {
                bool active = page == tabs[i, 0];
                int width = i == 0 ? 135 : 110;
                container.Add(new CuiButton
                {
                    Button = { Color = active ? "0.16 0.16 0.19 1" : "0 0 0 0", Command = "gridspawn.menu tab " + tabs[i, 0] },
                    Text = { Text = tabs[i, 1], FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = active ? "1 1 1 1" : "1 1 1 0.55" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = tabX + " 0", OffsetMax = (tabX + width) + " 0" }
                }, top);
                tabX += width;
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0.29 0.48 0.17 1", Command = "gridspawn.menu createsave" },
                Text = { Text = "+ PUBLISH", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 0", AnchorMax = "1 1", OffsetMin = "-520 8", OffsetMax = "-390 -8" }
            }, top);

            string filter = GetMenuSearch(player);
            string search = container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 1" },
                RectTransform = { AnchorMin = "1 0", AnchorMax = "1 1", OffsetMin = "-380 8", OffsetMax = "-105 -8" }
            }, top);

            // Fixed caption on the left, typing area after it - a label behind the input
            // field would show through whatever is being typed.
            container.Add(new CuiLabel
            {
                Text = { Text = "SEARCH:", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.35" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = "10 0", OffsetMax = "60 0" }
            }, search);

            container.Add(new CuiElement
            {
                Parent = search,
                Components =
                {
                    new CuiInputFieldComponent { Command = "gridspawn.menu search", Text = filter, FontSize = 11, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9", CharsLimit = 24, NeedsKeyboard = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "64 0", OffsetMax = "-5 0" }
                }
            });

            // A command instead of a client-side Close so the server knows the menu is
            // gone - middle mouse relies on that to toggle correctly.
            container.Add(new CuiButton
            {
                Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu exit" },
                Text = { Text = "EXIT", FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 0", AnchorMax = "1 1", OffsetMin = "-95 8", OffsetMax = "-15 -8" }
            }, top);
        }

        // The features dashboard: features column, feature cards, keybinds column.
        private void DrawFeaturesPage(CuiElementContainer container, string body, BasePlayer player)
        {
            string features = MenuCard(container, body, "0 0", "0 1", "10 10", "250 -10", "FEATURES");
            string[] featureNames = { "Actions", "Bgrade", "Symmetry", "Electricity", "Environment", "Cinewall", "Raid Counter", "Keybinds" };
            for (int i = 0; i < featureNames.Length; i++)
            {
                string row = container.Add(new CuiPanel
                {
                    Image = { Color = "1 1 1 0.05" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 " + (-78 - (i * 40)), OffsetMax = "-8 " + (-44 - (i * 40)) }
                }, features);

                container.Add(new CuiPanel
                {
                    Image = { Color = "0.29 0.41 1 1" },
                    RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "8 -5", OffsetMax = "18 5" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text = { Text = featureNames[i], FontSize = 12, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.95" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "28 0", OffsetMax = "0 0" }
                }, row);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "Free crafting and infinite\nresources are always on.", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.LowerLeft, Color = "1 1 1 0.45" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "12 10", OffsetMax = "-8 44" }
            }, features);

            // Middle card grid. The grid and the cards that refresh in place have fixed
            // names, so clicking a button only redraws that card.
            string grid = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "260 10", OffsetMax = "-260 -10" }
            }, body, MenuGridName);

            string actions = MenuCard(container, grid, "0 0.5", "0.5 1", "0 5", "-5 0", "ACTIONS", MenuActionsCardName);
            DrawActionsCard(container, actions, player);

            string bgrade = MenuCard(container, grid, "0.5 0.5", "1 1", "5 5", "0 0", "BGRADE", MenuBgradeCardName);
            DrawBgradeCard(container, bgrade, player);

            string symmetry = MenuCard(container, grid, "0 0", "0.5 0.5", "0 0", "-5 -5", "SYMMETRY", MenuSymCardName);
            DrawSymmetryCard(container, symmetry, player);

            string commands = MenuCard(container, grid, "0.5 0", "1 0.5", "5 0", "0 -5", "COMMANDS");
            DrawCommandsCard(container, commands);

            // Right keybinds column.
            string keybinds = MenuCard(container, body, "1 0", "1 1", "-250 10", "-10 -10", "KEYBINDS");
            DrawKeybindsCard(container, keybinds);
        }

        // A dashboard card: dark body, white top border and a bold title strip.
        private string MenuCard(CuiElementContainer container, string parent, string anchorMin, string anchorMax, string offsetMin, string offsetMax, string title, string name = null)
        {
            string card = container.Add(new CuiPanel
            {
                Image = { Color = "0.07 0.07 0.078 0.92" },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax, OffsetMin = offsetMin, OffsetMax = offsetMax }
            }, parent, name);

            container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.85" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -2", OffsetMax = "0 0" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 13, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -32", OffsetMax = "-12 -6" }
            }, card);

            return card;
        }

        private void MenuLine(CuiElementContainer container, string parent, int index, string text, int size = 12, string color = "1 1 1 0.9")
        {
            container.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = color },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "5 " + (-24 - (index * 24)), OffsetMax = "-5 " + (-index * 24) }
            }, parent);
        }

        // Dark action button in the dashboard-card style.
        private void MenuButton(CuiElementContainer container, string parent, int x, int y, int width, int height, string text, string command, string color = "0.13 0.13 0.16 1")
        {
            container.Add(new CuiButton
            {
                Button = { Color = color, Command = command },
                Text = { Text = text, FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = x + " " + (y - height), OffsetMax = (x + width) + " " + y }
            }, parent);
        }

        private void DrawActionsCard(CuiElementContainer container, string card, BasePlayer player)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = "QUICK ACTIONS", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.7" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -60", OffsetMax = "0 -40" }
            }, card);

            bool god = !godDisabled.Contains(player.userID);
            bool ammo = infiniteAmmo.Contains(player.userID);

            MenuButton(container, card, 12, -66, 110, 30, "NOCLIP", "gridspawn.menu noclip");
            MenuButton(container, card, 130, -66, 110, 30, "ROCKET GUN", "gridspawn.menu rocketgun");
            MenuButton(container, card, 248, -66, 110, 30, "RESET RAID", "gridspawn.menu raidreset");
            MenuButton(container, card, 12, -104, 110, 30, "CIRCUITS", "gridspawn.menu circuit");
            MenuButton(container, card, 130, -104, 110, 30, "BASE COST", "gridspawn.menu cost");
            MenuButton(container, card, 248, -104, 110, 30, god ? "GOD MODE: ON" : "GOD MODE: OFF", "gridspawn.menu god", god ? "0.29 0.41 1 0.9" : "0.13 0.13 0.16 1");
            MenuButton(container, card, 12, -142, 110, 30, "DEBUG CAMERA", "gridspawn.menu debugcam");
            MenuButton(container, card, 130, -142, 110, 30, "RESET INVENTORY", "gridspawn.menu resetinv");
            MenuButton(container, card, 248, -142, 110, 30, ammo ? "INF AMMO: ON" : "INF AMMO: OFF", "gridspawn.menu infammo", ammo ? "0.29 0.41 1 0.9" : "0.13 0.13 0.16 1");
            MenuButton(container, card, 12, -180, 110, 30, "MLRS STRIKE", "gridspawn.menu mlrs", "0.55 0.2 0.18 1");
            MenuButton(container, card, 130, -180, 110, 30, "ENVIRONMENT", "gridspawn.menu envpage");
            MenuButton(container, card, 248, -180, 110, 30, "CINEWALL", "gridspawn.menu cinepage");
            MenuButton(container, card, 12, -218, 110, 30, "UNDO", "gridspawn.menu undo");
            MenuButton(container, card, 130, -218, 110, 30, "REDO", "gridspawn.menu redo");
            MenuButton(container, card, 248, -218, 110, 30, "QUICK SAVE", "gridspawn.menu quicksave", "0.29 0.48 0.17 1");

            container.Add(new CuiLabel
            {
                Text = { Text = "MLRS rains real rockets on your plot. Infinite ammo refills any magazine.\nUndo/redo covers your placements, deletes and tier changes - bind a key\nwith:  bind z gridspawn.undo   and   bind x gridspawn.redo", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.5" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -300", OffsetMax = "-12 -252" }
            }, card);
        }

        private void DrawBgradeCard(CuiElementContainer container, string card, BasePlayer player)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = "PLACEMENT GRADE", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -56", OffsetMax = "-12 -40" }
            }, card);

            int selected = GetSelectedGrade(player);
            for (int i = 0; i < GradeLabels.Length; i++)
                GradeTile(container, card, 12 + (i * 62), -62, 56, i == selected, i, "gridspawn.menu grade " + i);

            container.Add(new CuiLabel
            {
                Text = { Text = "X also cycles this while holding the building plan.", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.5" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -136", OffsetMax = "-12 -120" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = "UPGRADE X TO Y", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -156", OffsetMax = "-12 -142" }
            }, card);

            int upgradeFrom = GetUpgradeFrom(player);
            int upgradeTo = GetUpgradeTo(player);

            container.Add(new CuiLabel
            {
                Text = { Text = "FROM", FontSize = 9, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "12 -190", OffsetMax = "44 -160" }
            }, card);

            for (int i = 0; i < GradeLabels.Length; i++)
                GradeTile(container, card, 48 + (i * 34), -160, 30, i == upgradeFrom, i, "gridspawn.menu upfrom " + i);

            container.Add(new CuiLabel
            {
                Text = { Text = "TO", FontSize = 9, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "12 -224", OffsetMax = "44 -194" }
            }, card);

            for (int i = 0; i < GradeLabels.Length; i++)
                GradeTile(container, card, 48 + (i * 34), -194, 30, i == upgradeTo, i, "gridspawn.menu upto " + i);

            MenuButton(container, card, 224, -160, 72, 64, "UPGRADE", "gridspawn.menu upgradexy", "0.29 0.41 1 0.9");

            string xyStatus;
            if (upgradeStatus.TryGetValue(player.userID, out xyStatus))
            {
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = xyStatus,
                        FontSize = 10,
                        Font = "robotocondensed-bold.ttf",
                        Align = TextAnchor.MiddleLeft,
                        Color = xyStatus.StartsWith("No") ? "0.9 0.3 0.25 1" : "0.29 0.41 1 1"
                    },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -242", OffsetMax = "-12 -226" }
                }, card);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "FORCE ALL", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -260", OffsetMax = "-12 -246" }
            }, card);

            for (int i = 0; i < GradeLabels.Length; i++)
                GradeTile(container, card, 12 + (i * 46), -262, 40, false, i, "gridspawn.menu gradeall " + i);

            string status;
            bool hasStatus = bgradeStatus.TryGetValue(player.userID, out status);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = hasStatus ? status : "Force all sets every block in your plot to that grade.",
                    FontSize = 10,
                    Font = hasStatus ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf",
                    Align = TextAnchor.MiddleLeft,
                    Color = !hasStatus ? "1 1 1 0.5" : status.StartsWith("No") ? "0.9 0.3 0.25 1" : "0.29 0.41 1 1"
                },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -322", OffsetMax = "-12 -306" }
            }, card);
        }

        // A clickable grade tile: blue frame when selected, wall icon inset inside.
        private void GradeTile(CuiElementContainer container, string parent, int x, int top, int size, bool selected, int grade, string command)
        {
            string tile = container.Add(new CuiPanel
            {
                Image = { Color = selected ? "0.29 0.41 1 0.95" : "1 1 1 0.10" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = x + " " + (top - size), OffsetMax = (x + size) + " " + top }
            }, parent);

            string png = GetCustomGradeImage(grade);
            if (string.IsNullOrEmpty(png))
                png = GetImage(GradeIcons[grade]);

            if (!string.IsNullOrEmpty(png))
            {
                container.Add(new CuiElement
                {
                    Parent = tile,
                    Components =
                    {
                        new CuiRawImageComponent { Png = png },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "2 2", OffsetMax = "-2 -2" }
                    }
                });
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = command },
                Text = { Text = string.Empty },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, tile);
        }

        private int GetUpgradeFrom(BasePlayer player)
        {
            int grade;
            return upgradeFromSel.TryGetValue(player.userID, out grade) ? grade : 0;
        }

        private int GetUpgradeTo(BasePlayer player)
        {
            int grade;
            return upgradeToSel.TryGetValue(player.userID, out grade) ? grade : 1;
        }

        // In-place refreshes for single cards: destroying and re-adding only the card keeps
        // the root panel (and with it the player's cursor position) untouched.
        private void UpdateActionsCard(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuActionsCardName);

            CuiElementContainer container = new CuiElementContainer();
            string card = MenuCard(container, MenuGridName, "0 0.5", "0.5 1", "0 5", "-5 0", "ACTIONS", MenuActionsCardName);
            DrawActionsCard(container, card, player);
            CuiHelper.AddUi(player, container);
        }

        private void UpdateBgradeCard(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuBgradeCardName);

            CuiElementContainer container = new CuiElementContainer();
            string card = MenuCard(container, MenuGridName, "0.5 0.5", "1 1", "5 5", "0 0", "BGRADE", MenuBgradeCardName);
            DrawBgradeCard(container, card, player);
            CuiHelper.AddUi(player, container);
        }

        private string GetMenuPage(BasePlayer player)
        {
            string value;
            return menuPage.TryGetValue(player.userID, out value) ? value : "features";
        }

        private string GetBrowserTab(BasePlayer player)
        {
            string value;
            return browserTab.TryGetValue(player.userID, out value) ? value : "mine";
        }

        private bool IsFavourite(BasePlayer player, string code)
        {
            HashSet<string> favourites;
            return favouriteBases.TryGetValue(player.userID, out favourites) && favourites.Contains(code);
        }

        private void SaveFavourites()
        {
            Interface.Oxide.DataFileSystem.WriteObject("RandomGridSpawnFavourites", favouriteBases);
        }

        private void LoadFavourites()
        {
            try
            {
                favouriteBases = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, HashSet<string>>>("RandomGridSpawnFavourites");
            }
            catch
            {
                favouriteBases = null;
            }

            if (favouriteBases == null)
                favouriteBases = new Dictionary<ulong, HashSet<string>>();
        }

        // Which browser category a save belongs to.
        private bool BrowserTabIncludes(string tab, SavedBase saved, BasePlayer player, ulong[] teammates)
        {
            switch (tab)
            {
                case "mine":
                    return saved.OwnerId == player.userID;

                case "favourites":
                    // Any favourited save you can still see (yours, or non-private).
                    return IsFavourite(player, saved.Code) && !saved.IsAutosave
                        && (saved.OwnerId == player.userID || saved.Visibility != 0);

                case "friends":
                    if (saved.OwnerId == player.userID || saved.Visibility == 0 || saved.IsAutosave)
                        return false;
                    return Array.IndexOf(teammates, saved.OwnerId) >= 0;

                case "featured":
                    return saved.Featured && saved.Visibility == 2 && !saved.IsAutosave;

                case "trending":
                case "creator":
                    return saved.Visibility == 2 && !saved.IsAutosave;

                default: // "all" - base browser: everything public plus your own
                    if (saved.OwnerId == player.userID)
                        return !saved.IsAutosave;
                    return saved.Visibility == 2 && !saved.IsAutosave;
            }
        }

        private string GetMenuSearch(BasePlayer player)
        {
            string value;
            return menuSearch.TryGetValue(player.userID, out value) ? value : string.Empty;
        }

        private int GetBrowserPage(BasePlayer player)
        {
            int value;
            return browserPage.TryGetValue(player.userID, out value) ? value : 0;
        }

        private string JoinArgs(ConsoleSystem.Arg arg, int start)
        {
            if (arg.Args == null || arg.Args.Length <= start)
                return string.Empty;

            return string.Join(" ", arg.Args.Skip(start).ToArray()).Trim();
        }

        private static string F(float value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // The build browser: saved bases as cards with load/delete/visibility, filtered by
        // the top-bar search and the my/all tab.
        private void DrawBrowserPage(CuiElementContainer container, string body, BasePlayer player)
        {
            string browser = MenuCard(container, body, "0 0", "0 1", "10 10", "250 -10", "BROWSER");

            string tab = GetBrowserTab(player);
            for (int i = 0; i < BrowserTabs.GetLength(0); i++)
            {
                bool active = tab == BrowserTabs[i, 0];
                string tabButton = container.Add(new CuiButton
                {
                    Button = { Color = active ? "0.16 0.16 0.19 1" : "1 1 1 0.04", Command = "gridspawn.menu btab " + BrowserTabs[i, 0] },
                    Text = { Text = BrowserTabs[i, 1], FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = active ? "1 1 1 1" : "1 1 1 0.6" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 " + (-74 - (i * 34)), OffsetMax = "-8 " + (-44 - (i * 34)) }
                }, browser);

                if (active)
                {
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "1 1 1 0.9" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = "0 0", OffsetMax = "3 0" }
                    }, tabButton);
                }
            }

            // TAGS: expandable categories that filter the grid.
            int tagTop = -74 - (BrowserTabs.GetLength(0) * 34) - 16;
            container.Add(new CuiLabel
            {
                Text = { Text = "TAGS", FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.85" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "10 " + (tagTop - 18), OffsetMax = "-8 " + tagTop }
            }, browser);
            tagTop -= 24;

            string openCategory;
            browserTagCategory.TryGetValue(player.userID, out openCategory);
            HashSet<string> selectedTags;
            browserFilterTags.TryGetValue(player.userID, out selectedTags);

            foreach (PublishTagCategory category in PublishTags)
            {
                bool open = openCategory == category.Name;
                container.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = "gridspawn.menu btagcat " + category.Name },
                    Text = { Text = (open ? "▼  " : "▶  ") + category.Name, FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "10 " + (tagTop - 18), OffsetMax = "-8 " + tagTop }
                }, browser);
                tagTop -= 22;

                if (open)
                {
                    foreach (string optionTag in category.Tags)
                    {
                        bool on = selectedTags != null && selectedTags.Contains(optionTag);
                        container.Add(new CuiButton
                        {
                            Button = { Color = on ? "0.29 0.41 1 0.85" : "1 1 1 0.05", Command = "gridspawn.menu btagpick " + optionTag },
                            Text = { Text = optionTag, FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = on ? "1 1 1 1" : "1 1 1 0.7" },
                            RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "20 " + (tagTop - 18), OffsetMax = "-8 " + tagTop }
                        }, browser);
                        tagTop -= 20;
                    }
                }
            }

            if (selectedTags != null && selectedTags.Count > 0)
            {
                tagTop -= 8;
                container.Add(new CuiLabel
                {
                    Text = { Text = "SELECTED TAGS", FontSize = 9, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.5" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "10 " + (tagTop - 14), OffsetMax = "-8 " + tagTop }
                }, browser);
                tagTop -= 16;

                container.Add(new CuiLabel
                {
                    Text = { Text = string.Join(", ", new List<string>(selectedTags).ToArray()), FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.UpperLeft, Color = "0.29 0.41 1 1" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "10 " + (tagTop - 40), OffsetMax = "-8 " + tagTop }
                }, browser);

                container.Add(new CuiButton
                {
                    Button = { Color = "0.55 0.2 0.18 1", Command = "gridspawn.menu btagclear" },
                    Text = { Text = "CLEAR TAGS", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "8 10", OffsetMax = "-8 36" }
                }, browser);
            }

            string filter = GetMenuSearch(player);

            List<SavedBase> entries = new List<SavedBase>();
            ulong[] teammates = player.Team != null ? player.Team.members.ToArray() : new ulong[0];

            foreach (SavedBase saved in savedBases.Values)
            {
                if (!BrowserTabIncludes(tab, saved, player, teammates))
                    continue;

                if (!MatchesFilter(saved, filter))
                    continue;

                if (selectedTags != null && selectedTags.Count > 0)
                {
                    bool hasAll = true;
                    foreach (string requiredTag in selectedTags)
                    {
                        if (saved.Tags == null || !saved.Tags.Contains(requiredTag))
                        {
                            hasAll = false;
                            break;
                        }
                    }

                    if (!hasAll)
                        continue;
                }

                entries.Add(saved);
            }

            // An exact code always finds a save that isn't private - that's what makes
            // UNLISTED shareable by code without being browsable.
            if (!string.IsNullOrEmpty(filter))
            {
                SavedBase codeMatch;
                if (savedBases.TryGetValue(filter.Trim(), out codeMatch) && !entries.Contains(codeMatch)
                    && (codeMatch.Visibility != 0 || codeMatch.OwnerId == player.userID))
                    entries.Insert(0, codeMatch);
            }

            if (tab == "trending")
                entries.Sort((a, b) => b.LoadCount.CompareTo(a.LoadCount));
            else
                entries.Sort((a, b) => string.CompareOrdinal(b.CreatedUtc, a.CreatedUtc));

            string content = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "260 10", OffsetMax = "-10 -10" }
            }, body);

            if (entries.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "No saves here yet. Build something and hit + PUBLISH.", FontSize = 13, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.5" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, content);
                return;
            }

            const int perPage = 6;
            int pageCount = (entries.Count + perPage - 1) / perPage;
            int pageIndex = Mathf.Clamp(GetBrowserPage(player), 0, pageCount - 1);

            for (int i = 0; i < perPage; i++)
            {
                int index = (pageIndex * perPage) + i;
                if (index >= entries.Count)
                    break;

                DrawBaseCard(container, content, entries[index], player, i % 3, i / 3);
            }

            if (pageCount > 1)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "PAGE " + (pageIndex + 1) + " / " + pageCount, FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleRight, Color = "1 1 1 0.7" },
                    RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-300 8", OffsetMax = "-150 34" }
                }, content);

                container.Add(new CuiButton
                {
                    Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu bpage " + Mathf.Max(0, pageIndex - 1) },
                    Text = { Text = "< PREV", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-140 8", OffsetMax = "-75 34" }
                }, content);

                container.Add(new CuiButton
                {
                    Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu bpage " + Mathf.Min(pageCount - 1, pageIndex + 1) },
                    Text = { Text = "NEXT >", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-70 8", OffsetMax = "-5 34" }
                }, content);
            }
        }

        private void DrawBaseCard(CuiElementContainer container, string parent, SavedBase saved, BasePlayer player, int col, int row)
        {
            string card = container.Add(new CuiPanel
            {
                Image = { Color = "0.07 0.07 0.078 0.95" },
                RectTransform =
                {
                    AnchorMin = F(col / 3f) + " " + (row == 0 ? "0.5" : "0"),
                    AnchorMax = F((col + 1) / 3f) + " " + (row == 0 ? "1" : "0.5"),
                    OffsetMin = "5 5",
                    OffsetMax = "-5 -5"
                }
            }, parent);

            container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.85" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -2", OffsetMax = "0 0" }
            }, card);

            string preview = container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.13 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "6 -148", OffsetMax = "-6 -8" }
            }, card);

            // Image priority: a dropped-in 3D render or the publisher's URL (both served
            // through ImageLibrary), then the camera photo / generated iso thumbnail.
            string previewPng = null;
            bool fullPreview = false;

            if (HasLocalRender(saved.Code) || !string.IsNullOrEmpty(saved.ImageUrl))
            {
                previewPng = GetImage("base_" + saved.Code);
                fullPreview = !string.IsNullOrEmpty(previewPng);
            }

            if (string.IsNullOrEmpty(previewPng) && !string.IsNullOrEmpty(saved.ImageCrc))
            {
                previewPng = saved.ImageCrc;
                fullPreview = true;
            }

            if (string.IsNullOrEmpty(previewPng))
            {
                previewPng = GetCustomGradeImage(2);
                if (string.IsNullOrEmpty(previewPng))
                    previewPng = GetImage(GradeIcons[2]);
            }

            if (!string.IsNullOrEmpty(previewPng))
            {
                container.Add(new CuiElement
                {
                    Parent = preview,
                    Components =
                    {
                        new CuiRawImageComponent { Png = previewPng },
                        fullPreview
                            ? new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                            : new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-32 -24", OffsetMax = "32 40" }
                    }
                });
            }

            container.Add(new CuiLabel
            {
                Text = { Text = saved.Entities.Count + " ENTITIES", FontSize = 9, Font = "robotocondensed-bold.ttf", Align = TextAnchor.LowerCenter, Color = "1 1 1 0.55" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "0 8", OffsetMax = "0 26" }
            }, preview);

            // Heart favourite toggle, top-right of the preview - transparent button so only
            // the glyph shows: solid red when favourited, faint white outline otherwise.
            bool favourited = IsFavourite(player, saved.Code);
            container.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = "gridspawn.menu fav " + saved.Code },
                Text = { Text = favourited ? "♥" : "♡", FontSize = 20, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = favourited ? "0.9 0.2 0.3 1" : "1 1 1 0.7" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-34 -34", OffsetMax = "-4 -4" }
            }, preview);

            container.Add(new CuiLabel
            {
                Text = { Text = saved.Name.ToUpper(), FontSize = 13, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 -172", OffsetMax = "-8 -152" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = TimeAgo(saved.CreatedUtc), FontSize = 9, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleRight, Color = "1 1 1 0.5" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 -172", OffsetMax = "-8 -152" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = "by " + saved.OwnerName, FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 -188", OffsetMax = "-8 -172" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = "CODE:", FontSize = 9, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.45" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "8 -206", OffsetMax = "48 -190" }
            }, card);

            container.Add(new CuiLabel
            {
                Text = { Text = saved.Code, FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.95" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "50 -206", OffsetMax = "-8 -190" }
            }, card);

            // Material cost of the build, with the item icons.
            string[] costOrder = { "wood", "stones", "metal.fragments", "metal.refined" };
            for (int c = 0; c < costOrder.Length; c++)
            {
                int amount = 0;
                if (saved.Costs != null)
                    saved.Costs.TryGetValue(costOrder[c], out amount);

                ItemDefinition costDef = ItemManager.FindItemDefinition(costOrder[c]);
                int left = 8 + (c * 78);

                if (costDef != null)
                {
                    container.Add(new CuiElement
                    {
                        Parent = card,
                        Components =
                        {
                            new CuiImageComponent { ItemId = costDef.itemid },
                            new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = left + " -232", OffsetMax = (left + 18) + " -214" }
                        }
                    });
                }

                container.Add(new CuiLabel
                {
                    Text = { Text = FormatAmount(amount), FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.85" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = (left + 22) + " -232", OffsetMax = (left + 76) + " -214" }
                }, card);
            }

            // Partial pastes: foundations only, or the base up to a floor count.
            container.Add(new CuiButton
            {
                Button = { Color = "0.13 0.13 0.16 1", Command = "gridspawn.menu loadfound " + saved.Code },
                Text = { Text = "FOUNDATION", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.5 0", OffsetMin = "6 38", OffsetMax = "-3 64" }
            }, card);

            container.Add(new CuiButton
            {
                Button = { Color = "0.13 0.13 0.16 1", Command = "gridspawn.menu floorask " + saved.Code },
                Text = { Text = "FLOOR", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "1 0", OffsetMin = "3 38", OffsetMax = "-6 64" }
            }, card);

            container.Add(new CuiButton
            {
                Button = { Color = "0.29 0.48 0.17 1", Command = "gridspawn.menu load " + saved.Code },
                Text = { Text = "LOAD", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "6 6", OffsetMax = "96 32" }
            }, card);

            if (saved.OwnerId == player.userID)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = "0.13 0.13 0.16 1", Command = "gridspawn.menu vissave " + saved.Code },
                    Text = { Text = saved.Visibility == 2 ? "PUBLIC" : saved.Visibility == 1 ? "UNLISTED" : "PRIVATE", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = saved.Visibility == 2 ? "0.55 0.78 0.25 1" : saved.Visibility == 1 ? "0.95 0.75 0.3 1" : "1 1 1 0.7" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "102 6", OffsetMax = "182 32" }
                }, card);

                container.Add(new CuiButton
                {
                    Button = { Color = "0.55 0.2 0.18 1", Command = "gridspawn.menu delsave " + saved.Code },
                    Text = { Text = "DELETE", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-86 6", OffsetMax = "-6 32" }
                }, card);
            }
        }

        // The servers page: featured plots banner with plot info and a players-online list.
        private void DrawServersPage(CuiElementContainer container, string body, BasePlayer player)
        {
            string featured = MenuCard(container, body, "0 1", "1 1", "10 -210", "-10 -10", "FEATURED");

            container.Add(new CuiLabel
            {
                Text = { Text = BasePlayer.activePlayerList.Count + " ONLINE", FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleRight, Color = "1 1 1 0.7" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -32", OffsetMax = "-12 -6" }
            }, featured);

            string plots = container.Add(new CuiPanel
            {
                Image = { Color = "0.2 0.12 0.36 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "12 12", OffsetMax = "332 160" }
            }, featured);

            container.Add(new CuiPanel
            {
                Image = { Color = "0.55 0.35 0.9 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "0 0", OffsetMax = "0 2" }
            }, plots);

            container.Add(new CuiLabel
            {
                Text = { Text = "PRIMARY", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.UpperLeft, Color = "0.95 0.75 0.2 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -30", OffsetMax = "-14 -10" }
            }, plots);

            container.Add(new CuiLabel
            {
                Text = { Text = "PLOTS", FontSize = 26, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "14 0", OffsetMax = "-14 0" }
            }, plots);

            container.Add(new CuiLabel
            {
                Text = { Text = "BUILD ON YOUR OWN PLOT", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.LowerLeft, Color = "0.72 0.55 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "14 10", OffsetMax = "-14 30" }
            }, plots);

            AssignedArea area;
            bool hasArea = assignedAreas.TryGetValue(player.userID, out area);

            container.Add(new CuiLabel
            {
                Text = { Text = "YOUR PLOT", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.5" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "350 120", OffsetMax = "700 140" }
            }, featured);

            container.Add(new CuiLabel
            {
                Text = { Text = hasArea ? area.Label : "Not assigned yet - respawn to get one.", FontSize = 15, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "350 96", OffsetMax = "700 120" }
            }, featured);

            container.Add(new CuiLabel
            {
                Text = { Text = hasArea ? "Building radius: " + Mathf.RoundToInt(area.Radius) + "m" : string.Empty, FontSize = 11, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "350 72", OffsetMax = "700 94" }
            }, featured);

            container.Add(new CuiButton
            {
                Button = { Color = "0.29 0.48 0.17 1", Command = "gridspawn.menu gohome" },
                Text = { Text = "GO TO MY PLOT", FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-200 12", OffsetMax = "-12 48" }
            }, featured);

            string playersCard = MenuCard(container, body, "0 0", "1 1", "10 10", "-10 -220", "PLAYERS ONLINE");

            int index = 0;
            foreach (BasePlayer online in BasePlayer.activePlayerList)
            {
                if (online == null || index >= 40)
                    break;

                int col = index % 4;
                int prow = index / 4;

                string row = container.Add(new CuiPanel
                {
                    Image = { Color = "1 1 1 0.04" },
                    RectTransform =
                    {
                        AnchorMin = F(col * 0.25f) + " 1",
                        AnchorMax = F((col + 1) * 0.25f) + " 1",
                        OffsetMin = "8 " + (-72 - (prow * 32)),
                        OffsetMax = "-8 " + (-44 - (prow * 32))
                    }
                }, playersCard);

                container.Add(new CuiPanel
                {
                    Image = { Color = "0.42 0.7 0.24 1" },
                    RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "8 -3", OffsetMax = "14 3" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text = { Text = online.displayName, FontSize = 11, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "22 0", OffsetMax = "-4 0" }
                }, row);

                index++;
            }
        }

        // The create-save dialog: base name input, visibility and save.
        private void ShowSaveModal(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuSaveModalName);

            CuiElementContainer container = new CuiElementContainer();

            // Full-screen backdrop so the busy menu doesn't show through the dialog.
            string backdrop = container.Add(new CuiPanel
            {
                Image = { Color = "0.03 0.03 0.035 0.98" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, MenuUiName, MenuSaveModalName);

            string modal = container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-310 -320", OffsetMax = "310 320" }
            }, backdrop);

            string header = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -38", OffsetMax = "0 0" }
            }, modal);

            container.Add(new CuiLabel
            {
                Text = { Text = "PUBLISH", FontSize = 14, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "14 0", OffsetMax = "0 0" }
            }, header);

            container.Add(new CuiButton
            {
                Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu closesave" },
                Text = { Text = "CLOSE", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 0.5", AnchorMax = "1 0.5", OffsetMin = "-66 -12", OffsetMax = "-10 12" }
            }, header);

            string pendingName;
            pendingSaveName.TryGetValue(player.userID, out pendingName);
            string pendingImage;
            pendingSaveImage.TryGetValue(player.userID, out pendingImage);
            string pendingYoutube;
            pendingSaveYoutube.TryGetValue(player.userID, out pendingYoutube);
            int visibility;
            pendingSaveVisibility.TryGetValue(player.userID, out visibility);
            HashSet<string> chosenTags;
            pendingSaveTags.TryGetValue(player.userID, out chosenTags);

            PublishInput(container, modal, -48, "BASE NAME", "gridspawn.menu savename", pendingName);

            container.Add(new CuiLabel
            {
                Text = { Text = "BASE IMAGE  -  TAKE A PICTURE OR PASTE AN IMAGE URL", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -120", OffsetMax = "-14 -104" }
            }, modal);

            container.Add(new CuiButton
            {
                Button = { Color = "0.13 0.13 0.16 1", Command = "gridspawn.menu givecamera" },
                Text = { Text = "TAKE PICTURE", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "14 -152", OffsetMax = "150 -122" }
            }, modal);

            string imageField = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "158 -152", OffsetMax = "-14 -122" }
            }, modal);

            container.Add(new CuiElement
            {
                Parent = imageField,
                Components =
                {
                    new CuiInputFieldComponent { Command = "gridspawn.menu saveimg", Text = pendingImage ?? string.Empty, FontSize = 12, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.95", CharsLimit = 128, NeedsKeyboard = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 0", OffsetMax = "-10 0" }
                }
            });

            PublishInput(container, modal, -160, "YOUTUBE LINK (OPTIONAL)", "gridspawn.menu saveyt", pendingYoutube);

            container.Add(new CuiLabel
            {
                Text = { Text = "VISIBILITY", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -232", OffsetMax = "-14 -216" }
            }, modal);

            string[] visibilityNames = { "PRIVATE", "UNLISTED", "PUBLIC" };
            for (int v = 0; v < 3; v++)
            {
                int left = 14 + (v * 202);
                container.Add(new CuiButton
                {
                    Button = { Color = visibility == v ? "0.29 0.48 0.17 1" : "0.13 0.13 0.16 1", Command = "gridspawn.menu savevis " + v },
                    Text = { Text = visibilityNames[v], FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = left + " -262", OffsetMax = (left + 194) + " -234" }
                }, modal);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "TAGS", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -288", OffsetMax = "-14 -272" }
            }, modal);

            int[] columnX = { 14, 216, 418 };
            int[][] columnCategories = { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4 } };

            for (int col = 0; col < columnCategories.Length; col++)
            {
                int y = -294;
                foreach (int categoryIndex in columnCategories[col])
                {
                    PublishTagCategory category = PublishTags[categoryIndex];

                    container.Add(new CuiLabel
                    {
                        Text = { Text = category.Name, FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.85" },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = columnX[col] + " " + (y - 16), OffsetMax = (columnX[col] + 190) + " " + y }
                    }, modal);
                    y -= 22;

                    for (int t = 0; t < category.Tags.Length; t++)
                    {
                        bool on = chosenTags != null && chosenTags.Contains(category.Tags[t]);

                        container.Add(new CuiPanel
                        {
                            Image = { Color = on ? "0.29 0.41 1 0.9" : "0.2 0.2 0.24 1" },
                            RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = columnX[col] + " " + (y - 14), OffsetMax = (columnX[col] + 14) + " " + y }
                        }, modal);

                        container.Add(new CuiLabel
                        {
                            Text = { Text = category.Tags[t], FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = on ? "1 1 1 1" : "1 1 1 0.65" },
                            RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = (columnX[col] + 20) + " " + (y - 16), OffsetMax = (columnX[col] + 190) + " " + (y + 2) }
                        }, modal);

                        container.Add(new CuiButton
                        {
                            Button = { Color = "0 0 0 0", Command = "gridspawn.menu pubtag " + categoryIndex + " " + t },
                            Text = { Text = string.Empty },
                            RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = columnX[col] + " " + (y - 16), OffsetMax = (columnX[col] + 190) + " " + (y + 2) }
                        }, modal);

                        y -= 22;
                    }

                    y -= 10;
                }
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0.29 0.48 0.17 1", Command = "gridspawn.menu dosave" },
                Text = { Text = "PUBLISH", FontSize = 13, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "-90 12", OffsetMax = "90 46" }
            }, modal);

            CuiHelper.AddUi(player, container);
        }

        // Label + dark input box; type and press ENTER to commit the value.
        private void PublishInput(CuiElementContainer container, string modal, int top, string label, string command, string value)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 " + (top - 16), OffsetMax = "-14 " + top }
            }, modal);

            string field = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 " + (top - 48), OffsetMax = "-14 " + (top - 18) }
            }, modal);

            container.Add(new CuiElement
            {
                Parent = field,
                Components =
                {
                    new CuiInputFieldComponent { Command = command, Text = value ?? string.Empty, FontSize = 12, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.95", CharsLimit = 128, NeedsKeyboard = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 0", OffsetMax = "-10 0" }
                }
            });
        }

        // One button per floor count (1..N): each pastes the base up to that level.
        private void ShowFloorModal(BasePlayer player, SavedBase saved)
        {
            CuiHelper.DestroyUi(player, MenuFloorModalName);

            int totalFloors = Mathf.Clamp(SavedFloorCount(saved), 1, 24);

            CuiElementContainer container = new CuiElementContainer();

            string modal = container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-220 -140", OffsetMax = "220 140" }
            }, MenuUiName, MenuFloorModalName);

            string header = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -38", OffsetMax = "0 0" }
            }, modal);

            container.Add(new CuiLabel
            {
                Text = { Text = "PASTE FLOORS", FontSize = 14, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "14 0", OffsetMax = "0 0" }
            }, header);

            container.Add(new CuiButton
            {
                Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu floorclose" },
                Text = { Text = "CLOSE", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 0.5", AnchorMax = "1 0.5", OffsetMin = "-66 -12", OffsetMax = "-10 12" }
            }, header);

            container.Add(new CuiLabel
            {
                Text = { Text = "'" + saved.Name + "' is " + totalFloors + " floor(s) tall. Pick how many\nto paste from the ground up - loading replaces your build.", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -76", OffsetMax = "-14 -44" }
            }, modal);

            // A button per floor count; each loads levels 1..n directly.
            for (int n = 1; n <= totalFloors; n++)
            {
                int idx = n - 1;
                int col = idx % 6;
                int rowY = idx / 6;
                int left = 14 + (col * 68);
                int top = -88 - (rowY * 44);

                container.Add(new CuiButton
                {
                    Button = { Color = n == totalFloors ? "0.29 0.48 0.17 1" : "0.13 0.13 0.16 1", Command = "gridspawn.menu floorload " + saved.Code + " " + n },
                    Text = { Text = n.ToString(), FontSize = 13, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = left + " " + (top - 38), OffsetMax = (left + 62) + " " + top }
                }, modal);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "Green = the whole base. Level 1 is the ground floor.", FontSize = 9, Font = "robotocondensed-regular.ttf", Align = TextAnchor.LowerLeft, Color = "1 1 1 0.4" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "14 12", OffsetMax = "-14 30" }
            }, modal);

            CuiHelper.AddUi(player, container);
        }

        // Asks how many MLRS rockets to rain on the player's plot.
        private void ShowMlrsModal(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuMlrsModalName);

            CuiElementContainer container = new CuiElementContainer();

            string modal = container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-200 -125", OffsetMax = "200 125" }
            }, MenuUiName, MenuMlrsModalName);

            string header = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -38", OffsetMax = "0 0" }
            }, modal);

            container.Add(new CuiLabel
            {
                Text = { Text = "MLRS STRIKE", FontSize = 14, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "14 0", OffsetMax = "0 0" }
            }, header);

            container.Add(new CuiButton
            {
                Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu mlrsclose" },
                Text = { Text = "CLOSE", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 0.5", AnchorMax = "1 0.5", OffsetMin = "-66 -12", OffsetMax = "-10 12" }
            }, header);

            container.Add(new CuiLabel
            {
                Text = { Text = "HOW MANY ROCKETS?  (1 - 48, a full pod is 12)", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -66", OffsetMax = "-14 -48" }
            }, modal);

            int pending;
            if (!pendingMlrsCount.TryGetValue(player.userID, out pending))
                pending = 12;

            string countField = container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -98", OffsetMax = "-14 -68" }
            }, modal);

            container.Add(new CuiElement
            {
                Parent = countField,
                Components =
                {
                    new CuiInputFieldComponent { Command = "gridspawn.menu mlrscount", Text = pending.ToString(), FontSize = 12, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.95", CharsLimit = 2, NeedsKeyboard = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 0", OffsetMax = "-10 0" }
                }
            });

            container.Add(new CuiLabel
            {
                Text = { Text = "Type a number and press ENTER, then hit FIRE. Real MLRS\nrockets rain on your plot - your base WILL take damage.", FontSize = 9, Font = "robotocondensed-regular.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.4" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "14 -134", OffsetMax = "-14 -102" }
            }, modal);

            container.Add(new CuiButton
            {
                Button = { Color = "0.55 0.2 0.18 1", Command = "gridspawn.menu mlrsfire" },
                Text = { Text = "FIRE " + pending + " ROCKET" + (pending == 1 ? string.Empty : "S"), FontSize = 13, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "-110 14", OffsetMax = "110 48" }
            }, modal);

            CuiHelper.AddUi(player, container);
        }

        // Rains real MLRS rockets over the plot: the same prefab and server projectile the
        // vehicle launches, spawned high above the base on a steep spread descent, fired
        // in sequence like the pod empties from the station.
        private void LaunchMlrsStrike(BasePlayer player, int count)
        {
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "You don't have a plot yet.", string.Empty, false);
                return;
            }

            Vector3 center = GetGroundedPosition(area.Center);
            player.ChatMessage("MLRS strike inbound: " + count + " rocket(s)!");

            for (int i = 0; i < count; i++)
            {
                timer.Once(i * 0.4f, () =>
                {
                    Vector2 spread = UnityEngine.Random.insideUnitCircle * 18f;
                    Vector3 target = center + new Vector3(spread.x, 0f, spread.y);
                    Vector3 start = target + new Vector3(UnityEngine.Random.Range(-20f, 20f), 220f, UnityEngine.Random.Range(-20f, 20f));
                    Vector3 velocity = (target - start).normalized * 110f;

                    BaseEntity rocket = GameManager.server.CreateEntity(MlrsRocketPrefab, start, Quaternion.LookRotation(velocity));
                    if (rocket == null)
                        return;

                    ServerProjectile serverProjectile = rocket.GetComponent<ServerProjectile>();
                    if (serverProjectile != null)
                        serverProjectile.InitializeVelocity(velocity);

                    if (player != null && !player.IsDestroyed)
                    {
                        rocket.creatorEntity = player;
                        rocket.OwnerID = player.userID;
                    }

                    rocket.Spawn();
                });
            }
        }

        // Per-player sky settings: TIME / WEATHER / ATMOSPHERE on the left, CLOUDS on the
        // right. Every preset button carries the client convar itself - the client refuses
        // convar sets pushed from the server, but runs button commands locally.
        private void DrawEnvironmentPage(CuiElementContainer container, string body, BasePlayer player)
        {
            string left = MenuCard(container, body, "0 0", "0.5 1", "10 10", "-5 -10", "ENVIRONMENT");
            string right = MenuCard(container, body, "0.5 0", "1 1", "5 10", "-10 -10", "CLOUDS");

            int leftIndex = 0;
            int rightIndex = 0;
            string currentSection = null;

            foreach (EnvSetting setting in EnvSettingsTable)
            {
                bool cloud = setting.Section == "CLOUD";
                string card = cloud ? right : left;

                if (setting.Section != currentSection && !cloud)
                {
                    currentSection = setting.Section;
                    int headerTop = -50 - (leftIndex * 30);
                    leftIndex++;

                    container.Add(new CuiLabel
                    {
                        Text = { Text = setting.Section, FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "0.29 0.41 1 1" },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "12 " + (headerTop - 26), OffsetMax = "320 " + headerTop }
                    }, card);
                }

                int rowIndex = cloud ? rightIndex++ : leftIndex++;

                int settingIndex = Array.IndexOf(EnvSettingsTable, setting);
                Dictionary<int, int> chosen;
                int chosenPreset = -1;
                if (envSelections.TryGetValue(player.userID, out chosen) && chosen.ContainsKey(settingIndex))
                    chosenPreset = chosen[settingIndex];

                EnvRow(container, card, rowIndex, setting, settingIndex, chosenPreset);
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0.55 0.2 0.18 1", Command = "gridspawn.menu envreset" },
                Text = { Text = "RESET ALL", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "12 12", OffsetMax = "150 44" }
            }, right);

            container.Add(new CuiLabel
            {
                Text = { Text = "Changes only how YOU see the world - other players keep their own\nsky. Highlighted = your current choice (or the default).", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.5" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "160 12", OffsetMax = "-12 48" }
            }, right);

            AddCircuitBackButton(container, right);
        }

        private void EnvRow(CuiElementContainer container, string card, int index, EnvSetting setting, int settingIndex, int chosenPreset)
        {
            int top = -50 - (index * 30);

            container.Add(new CuiLabel
            {
                Text = { Text = setting.Label, FontSize = 11, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "12 " + (top - 26), OffsetMax = "150 " + top }
            }, card);

            int x = 155;
            for (int p = 0; p < setting.Presets.Length; p++)
            {
                float preset = setting.Presets[p];
                bool highlighted = chosenPreset >= 0 ? p == chosenPreset : Mathf.Approximately(preset, setting.Default);
                string text = preset < 0f ? "OFF" : preset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

                container.Add(new CuiButton
                {
                    Button = { Color = highlighted ? "0.29 0.41 1 0.6" : "0.13 0.13 0.16 1", Command = "gridspawn.menu envset " + settingIndex + " " + p },
                    Text = { Text = text, FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = x + " " + (top - 24), OffsetMax = (x + 44) + " " + (top - 2) }
                }, card);

                x += 48;
            }
        }

        #endregion

        #region Cinewall

        // Real server admins (auth level 2 / RCON) flag a public save as featured so it
        // shows in the browser's FEATURED tab: gridspawn.feature <code>
        [ConsoleCommand("gridspawn.feature")]
        private void FeatureCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2)
                return;

            SavedBase saved;
            if (!savedBases.TryGetValue(arg.GetString(0, string.Empty), out saved))
            {
                arg.ReplyWith("No save with that code.");
                return;
            }

            saved.Featured = !saved.Featured;
            SaveSavedBases();
            arg.ReplyWith("'" + saved.Name + "' featured: " + saved.Featured);
        }

        // Searches the game manifest for spawnable entity prefabs matching a term, so
        // prefab paths can be found instead of guessed.
        [ConsoleCommand("gridspawn.findprefab")]
        private void FindPrefabCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasNoClipPermission(player))
                return;

            string term = arg.GetString(0, string.Empty);
            if (string.IsNullOrEmpty(term))
            {
                arg.ReplyWith("Usage: gridspawn.findprefab <search term>");
                return;
            }

            System.Text.StringBuilder reply = new System.Text.StringBuilder();
            int found = 0;

            foreach (string path in GameManifest.Current.entities)
            {
                if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                found++;
                if (found <= 25)
                    reply.AppendLine(path.ToLower());
            }

            reply.AppendLine(found + " match(es)" + (found > 25 ? ", showing first 25" : string.Empty));
            arg.ReplyWith(reply.ToString());
        }

        // Pushes a raw console command to the player's own client - used to test which
        // commands the client accepts from the server.
        [ConsoleCommand("gridspawn.pushcmd")]
        private void PushCommandCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasNoClipPermission(player))
                return;

            string command = JoinArgs(arg, 0);
            if (string.IsNullOrEmpty(command))
            {
                arg.ReplyWith("Usage: gridspawn.pushcmd <command ...>");
                return;
            }

            player.SendConsoleCommand(command);
            arg.ReplyWith("Pushed to your client: " + command);
        }

        // Experimentation helper: spawns an arbitrary prefab 8m in front of the player,
        // facing them. Used to hunt for spawnable cinematic/greenscreen prefabs.
        [ConsoleCommand("gridspawn.spawnprefab")]
        private void SpawnPrefabCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasNoClipPermission(player))
                return;

            string prefab = arg.GetString(0, string.Empty);
            if (string.IsNullOrEmpty(prefab))
            {
                arg.ReplyWith("Usage: gridspawn.spawnprefab <full prefab path>");
                return;
            }

            Vector3 position = player.transform.position + (player.eyes.BodyForward() * 8f);
            BaseEntity entity = GameManager.server.CreateEntity(prefab, position, Quaternion.LookRotation(-player.eyes.BodyForward()));
            if (entity == null)
            {
                arg.ReplyWith("Could not create: " + prefab);
                return;
            }

            entity.OwnerID = player.userID;
            entity.Spawn();
            arg.ReplyWith("Spawned " + entity.ShortPrefabName + " at " + position
                + "\nbounds center=" + entity.bounds.center + " size=" + entity.bounds.size
                + "\n(delete with Hammer + R)");
        }

        private CinewallOptions GetCinewallOptions(BasePlayer player)
        {
            CinewallOptions options;
            if (!cinewallOptions.TryGetValue(player.userID, out options))
                cinewallOptions[player.userID] = options = new CinewallOptions();

            return options;
        }

        // Cinewall page: colour swatches, sides, floor/roof toggles, scale/gap/lift
        // steppers and spawn/clear, mirroring the reference panel.
        private void DrawCinewallPage(CuiElementContainer container, string body, BasePlayer player)
        {
            string card = MenuCard(container, body, "0 0", "0 1", "10 10", "450 -10", "CINEWALL");
            string info = MenuCard(container, body, "0 0", "1 1", "460 10", "-10 -10", "ABOUT");

            CinewallOptions options = GetCinewallOptions(player);

            container.Add(new CuiLabel
            {
                Text = { Text = "COLOUR", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -58", OffsetMax = "-12 -44" }
            }, card);

            for (int i = 0; i < CinewallColours.Length; i++)
            {
                int left = 12 + (i * 44);
                if (i == options.ColourIndex)
                {
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "1 1 1 0.9" },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = (left - 2) + " -100", OffsetMax = (left + 40) + " -60" }
                    }, card);
                }

                container.Add(new CuiButton
                {
                    Button = { Color = CinewallColours[i], Command = "gridspawn.menu cine colour " + i },
                    Text = { Text = string.Empty },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = left + " -98", OffsetMax = (left + 38) + " -62" }
                }, card);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "SIDES", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -122", OffsetMax = "-12 -108" }
            }, card);

            for (int i = 1; i <= 4; i++)
            {
                int left = 12 + ((i - 1) * 50);
                MenuButton(container, card, left, -126, 44, 28, i.ToString(), "gridspawn.menu cine sides " + i, i == options.Sides ? "0.29 0.41 1 0.9" : "0.13 0.13 0.16 1");
            }

            MenuButton(container, card, 12, -166, 200, 28, "Floor: " + (options.Floor ? "ON" : "OFF"), "gridspawn.menu cine floor", options.Floor ? "0.29 0.41 1 0.9" : "0.13 0.13 0.16 1");
            MenuButton(container, card, 222, -166, 200, 28, "Roof: " + (options.Roof ? "ON" : "OFF"), "gridspawn.menu cine roof", options.Roof ? "0.29 0.41 1 0.9" : "0.13 0.13 0.16 1");

            CinewallStepperRow(container, card, 0, "Scale", options.Scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), "scale");
            CinewallStepperRow(container, card, 1, "Gap", Mathf.RoundToInt(options.Gap) + "m", "gap");
            CinewallStepperRow(container, card, 2, "Lift", options.Lift.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m", "lift");
            CinewallStepperRow(container, card, 3, "Tile", options.TileSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "m", "tile");

            MenuButton(container, card, 12, -366, 200, 32, "SPAWN WALL", "gridspawn.menu cine spawn");
            MenuButton(container, card, 222, -366, 200, 32, "CLEAR", "gridspawn.menu cine clear", "0.55 0.2 0.18 1");

            container.Add(new CuiLabel
            {
                Text = { Text = "Surrounds your build with the game's cinematic backdrops - curved\ncycloramas that sweep from the floor up into a wall, in six studio\ncolours. Sides: 1 = behind, up to 4 = fully boxed in. Gap is the distance\nfrom your build, scale widens the coverage, lift raises everything.\nSpawning again replaces the previous wall; CLEAR removes it.", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -140", OffsetMax = "-12 -44" }
            }, info);

            AddCircuitBackButton(container, info);
        }

        private void CinewallStepperRow(CuiElementContainer container, string card, int index, string label, string value, string field)
        {
            int top = -206 - (index * 38);

            container.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "12 " + (top - 28), OffsetMax = "110 " + top }
            }, card);

            MenuButton(container, card, 120, top, 36, 28, "-", "gridspawn.menu cine " + field + " -1");

            container.Add(new CuiLabel
            {
                Text = { Text = value, FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "162 " + (top - 28), OffsetMax = "330 " + top }
            }, card);

            MenuButton(container, card, 336, top, 36, 28, "+", "gridspawn.menu cine " + field + " 1");
        }

        private void ApplyCinewallAction(BasePlayer player, string field, int value)
        {
            CinewallOptions options = GetCinewallOptions(player);

            switch (field)
            {
                case "colour":
                    options.ColourIndex = Mathf.Clamp(value, 0, CinewallColours.Length - 1);
                    break;
                case "sides":
                    options.Sides = Mathf.Clamp(value, 1, 4);
                    break;
                case "floor":
                    options.Floor = !options.Floor;
                    break;
                case "roof":
                    options.Roof = !options.Roof;
                    break;
                case "scale":
                    options.Scale = Mathf.Clamp(options.Scale + (value * 0.5f), 0.5f, 3f);
                    break;
                case "gap":
                    options.Gap = Mathf.Clamp(options.Gap + (value * 2f), 0f, 30f);
                    break;
                case "lift":
                    options.Lift = Mathf.Clamp(options.Lift + value, -5f, 30f);
                    break;
                case "tile":
                    options.TileSize = Mathf.Clamp(options.TileSize + value, 1f, 50f);
                    break;
                case "spawn":
                    SpawnCinewall(player);
                    break;
                case "clear":
                    ClearCinewall(player);
                    break;
            }

            RefreshMenu(player);
        }

        // Surrounds the build with the game's own cinematic cycloramas (the curved
        // floor-into-wall backdrops Facepunch ships for filming), plus optional flat
        // floor and roof planes - the same pieces the reference servers use.
        private void SpawnCinewall(BasePlayer player)
        {
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "You don't have a plot yet.", string.Empty, false);
                return;
            }

            CinewallOptions options = GetCinewallOptions(player);
            ClearCinewall(player);

            List<BaseEntity> spawned;
            if (!cinewallEntities.TryGetValue(player.userID, out spawned))
                cinewallEntities[player.userID] = spawned = new List<BaseEntity>();

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity.OwnerID == 0 || entity is BasePlayer)
                    continue;

                if (entity.ShortPrefabName.StartsWith("cine"))
                    continue;

                if (!IsInsideArea(entity.transform.position, area))
                    continue;

                Vector3 position = entity.transform.position;
                minX = Mathf.Min(minX, position.x); maxX = Mathf.Max(maxX, position.x);
                minZ = Mathf.Min(minZ, position.z); maxZ = Mathf.Max(maxZ, position.z);
            }

            if (minX > maxX)
            {
                minX = area.Center.x - 6f; maxX = area.Center.x + 6f;
                minZ = area.Center.z - 6f; maxZ = area.Center.z + 6f;
            }

            string colour = CinewallColourNames[Mathf.Clamp(options.ColourIndex, 0, CinewallColourNames.Length - 1)];
            string planePrefab = string.Format(CinewallBgPrefabFormat, colour);

            // The backdrop plane is a square lying flat at identity rotation with its pivot
            // at a CORNER (confirmed by the pinwheel it makes when yaw-rotated). Its true
            // size can't be measured server-side (the mesh is client-only), so it's a
            // player-tunable stepper on the page. Pitched upright it hangs DOWN from the
            // pivot, so wall tiles anchor at their top corner. The box snaps to whole tiles
            // so the four walls meet exactly at the corners: one clean cube.
            float size = Mathf.Clamp(options.TileSize, 1f, 50f);

            float centerX = (minX + maxX) / 2f;
            float centerZ = (minZ + maxZ) / 2f;

            // Grounded at the BUILD's own location - anchoring to the plot center put the
            // whole box above or below ground whenever the terrain height differed.
            float baseY = TerrainMeta.HeightMap.GetHeight(new Vector3(centerX, 0f, centerZ)) + options.Lift;

            float half = Mathf.Max((maxX - minX) / 2f, (maxZ - minZ) / 2f) + options.Gap;
            int cols = Mathf.Max(1, Mathf.CeilToInt((half * 2f) / size));
            float total = cols * size;
            half = total / 2f;

            int wallRows = Mathf.Clamp(Mathf.RoundToInt(options.Scale), 1, 3);

            int sides = Mathf.Clamp(options.Sides, 1, 4);
            for (int side = 0; side < sides; side++)
            {
                for (int col = 0; col < cols; col++)
                {
                    float along = -half + (col * size);

                    for (int row = 0; row < wallRows; row++)
                    {
                        float y = baseY + ((row + 1) * size);

                        Vector3 position;
                        float yaw;
                        switch (side)
                        {
                            case 0: position = new Vector3(centerX - along, y, centerZ + half); yaw = 180f; break;
                            case 1: position = new Vector3(centerX - half, y, centerZ - along); yaw = 90f; break;
                            case 2: position = new Vector3(centerX + half, y, centerZ + along); yaw = 270f; break;
                            default: position = new Vector3(centerX + along, y, centerZ - half); yaw = 0f; break;
                        }

                        SpawnCinewallPanel(player, spawned, planePrefab, position, Quaternion.Euler(90f, yaw, 0f));
                    }
                }
            }

            if (options.Floor || options.Roof)
            {
                for (int px = 0; px < cols; px++)
                {
                    float x = centerX - half + (px * size);

                    for (int pz = 0; pz < cols; pz++)
                    {
                        if (options.Floor)
                            SpawnCinewallPanel(player, spawned, planePrefab, new Vector3(x, baseY + 0.05f, centerZ - half + (pz * size)), Quaternion.identity);

                        // The flipped roof extends +X/-Z from its pivot, so it walks in
                        // from the north edge instead.
                        if (options.Roof)
                            SpawnCinewallPanel(player, spawned, planePrefab, new Vector3(x, baseY + (wallRows * size), centerZ + half - (pz * size)), Quaternion.Euler(180f, 0f, 0f));
                    }
                }
            }

            player.SendConsoleCommand("gametip.showtoast", 0, "Cinewall spawned (" + spawned.Count + " pieces).", string.Empty, false);
        }

        private void SpawnCinewallPanel(BasePlayer player, List<BaseEntity> spawned, string prefab, Vector3 position, Quaternion rotation)
        {
            if (spawned.Count >= 100)
                return;

            BaseEntity entity = GameManager.server.CreateEntity(prefab, position, rotation);
            if (entity == null)
                return;

            entity.OwnerID = player.userID;
            entity.Spawn();
            spawned.Add(entity);
        }

        private void ClearCinewall(BasePlayer player)
        {
            List<BaseEntity> list;
            if (cinewallEntities.TryGetValue(player.userID, out list))
            {
                foreach (BaseEntity entity in list)
                {
                    if (entity != null && !entity.IsDestroyed)
                        entity.Kill();
                }

                list.Clear();
            }

            // The tracking list is in-memory only, so pieces spawned before a plugin
            // reload become orphans - sweep every cinematic piece the player owns in
            // their plot as well.
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            List<BaseEntity> orphans = new List<BaseEntity>();
            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity.OwnerID != player.userID)
                    continue;

                if (!entity.ShortPrefabName.StartsWith("cine"))
                    continue;

                if (!IsInsideArea(entity.transform.position, area))
                    continue;

                orphans.Add(entity);
            }

            foreach (BaseEntity orphan in orphans)
            {
                if (!orphan.IsDestroyed)
                    orphan.Kill();
            }
        }

        #endregion

        #region Menu leftovers

        #endregion

        #region Base saves

        private void LoadSavedBases()
        {
            try
            {
                savedBases = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, SavedBase>>("RandomGridSpawnBases");
            }
            catch
            {
                savedBases = null;
            }

            if (savedBases == null)
                savedBases = new Dictionary<string, SavedBase>(StringComparer.OrdinalIgnoreCase);

            // Saves from before the publish rework only carried the Public flag.
            foreach (SavedBase saved in savedBases.Values)
            {
                if (saved.Visibility == 0 && saved.Public)
                    saved.Visibility = 2;
            }
        }

        private void SaveSavedBases()
        {
            Interface.Oxide.DataFileSystem.WriteObject("RandomGridSpawnBases", savedBases);
        }

        // One-click manual checkpoint: a rolling private "Quicksave" slot per player,
        // overwritten each press so it never clutters MY BASES.
        private void QuickSave(BasePlayer player)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            SavedBase captured = CaptureBase(player, "Quicksave");
            if (captured == null)
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "Nothing to save - your plot is empty.", string.Empty, false);
                return;
            }

            captured.IsQuicksave = true;

            SavedBase existing = null;
            foreach (SavedBase candidate in savedBases.Values)
            {
                if (candidate.IsQuicksave && candidate.OwnerId == player.userID && candidate != captured)
                {
                    existing = candidate;
                    break;
                }
            }

            if (existing != null)
            {
                existing.Entities = captured.Entities;
                existing.Costs = captured.Costs;
                existing.ImageCrc = captured.ImageCrc;
                existing.CreatedUtc = captured.CreatedUtc;
                savedBases.Remove(captured.Code);
            }

            SaveSavedBases();
            player.SendConsoleCommand("gametip.showtoast", 0, "Quicksaved - find it in MY BASES.", string.Empty, false);

            if (config != null && config.RenderOnAutosave)
                RequestRender(existing ?? captured, player);
        }

        [ChatCommand("qs")]
        private void QuickSaveChatCommand(BasePlayer player, string command, string[] args) => QuickSave(player);

        // Every builder's plot is snapshotted on an interval into a rolling autosave named
        // "Untitled" - one per player, its code stable so MY BASES doesn't fill up.
        private void AutosaveAll()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || !HasNoClipPermission(player))
                    continue;

                SavedBase captured = CaptureBase(player, "Untitled");
                if (captured == null)
                    continue;

                SavedBase autosave = null;
                foreach (SavedBase candidate in savedBases.Values)
                {
                    if (candidate.IsAutosave && candidate.OwnerId == player.userID && candidate != captured)
                    {
                        autosave = candidate;
                        break;
                    }
                }

                if (autosave != null)
                {
                    autosave.Entities = captured.Entities;
                    autosave.Costs = captured.Costs;
                    autosave.ImageCrc = captured.ImageCrc;
                    autosave.CreatedUtc = captured.CreatedUtc;
                    savedBases.Remove(captured.Code);
                }
                else
                {
                    captured.IsAutosave = true;
                }

                if (config != null && config.RenderOnAutosave)
                    RequestRender(autosave ?? captured, null);
            }

            SaveSavedBases();
        }

        private string GenerateBaseCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            for (;;)
            {
                char[] buffer = new char[8];
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = chars[CodeRandom.Next(chars.Length)];

                string code = new string(buffer);
                if (!savedBases.ContainsKey(code))
                    return code;
            }
        }

        private bool MatchesFilter(SavedBase saved, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;

            if (string.Equals(saved.Code, filter, StringComparison.OrdinalIgnoreCase))
                return true;

            return (saved.Name != null && saved.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                || (saved.OwnerName != null && saved.OwnerName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string TimeAgo(string createdUtc)
        {
            DateTime created;
            if (!DateTime.TryParse(createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out created))
                return string.Empty;

            TimeSpan span = DateTime.UtcNow - created.ToUniversalTime();
            if (span.TotalDays >= 1)
                return (int)span.TotalDays + " days ago";
            if (span.TotalHours >= 1)
                return (int)span.TotalHours + " hours ago";

            return Math.Max(1, (int)span.TotalMinutes) + " minutes ago";
        }

        // Records every player-built entity on the plot, positioned relative to the plot
        // center so it can be rebuilt on any other plot. Child entities (locks) and loose
        // items are skipped.
        private SavedBase CaptureBase(BasePlayer player, string name)
        {
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return null;

            Vector3 anchor = GetGroundedPosition(area.Center);

            SavedBase saved = new SavedBase
            {
                Code = GenerateBaseCode(),
                Name = string.IsNullOrEmpty(name) ? "Untitled" : name,
                OwnerId = player.userID,
                OwnerName = player.displayName,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity.OwnerID == 0)
                    continue;

                if (entity is BasePlayer || entity is DroppedItem || entity is BaseCorpse || entity.HasParent())
                    continue;

                if (!IsInsideArea(entity.transform.position, area))
                    continue;

                Vector3 relative = entity.transform.position - anchor;
                Vector3 euler = entity.transform.rotation.eulerAngles;

                SavedEntity item = new SavedEntity
                {
                    Prefab = entity.PrefabName,
                    X = relative.x,
                    Y = relative.y,
                    Z = relative.z,
                    RotX = euler.x,
                    RotY = euler.y,
                    RotZ = euler.z,
                    Skin = entity.skinID
                };

                BuildingBlock block = entity as BuildingBlock;
                if (block != null)
                {
                    item.Grade = (int)block.grade;

                    // Tally the build cost while the entities are still live.
                    if (block.blockDefinition != null && item.Grade >= 0 && item.Grade < block.blockDefinition.grades.Length && block.blockDefinition.grades[item.Grade] != null)
                    {
                        foreach (ItemAmount cost in block.blockDefinition.grades[item.Grade].CostToBuild())
                        {
                            if (cost.itemDef == null)
                                continue;

                            int current;
                            saved.Costs.TryGetValue(cost.itemDef.shortname, out current);
                            saved.Costs[cost.itemDef.shortname] = current + (int)cost.amount;
                        }
                    }
                }
                else
                {
                    BaseCombatEntity combat = entity as BaseCombatEntity;
                    ItemDefinition deployDef = combat != null
                        ? (combat.pickup.itemTarget != null ? combat.pickup.itemTarget : combat.repair.itemTarget)
                        : null;

                    if (deployDef != null && deployDef.Blueprint != null && deployDef.Blueprint.ingredients != null)
                    {
                        foreach (ItemAmount cost in deployDef.Blueprint.ingredients)
                        {
                            if (cost.itemDef == null)
                                continue;

                            int current;
                            saved.Costs.TryGetValue(cost.itemDef.shortname, out current);
                            saved.Costs[cost.itemDef.shortname] = current + (int)cost.amount;
                        }
                    }
                }

                saved.Entities.Add(item);
            }

            if (saved.Entities.Count == 0)
                return null;

            // The isometric thumbnail is a main-thread rasterisation, so it is skipped when
            // the render service is doing the imaging (its render replaces it anyway) and
            // for very large bases, where it would cost a visible server hitch.
            if (!RenderServiceEnabled() && saved.Entities.Count <= 2500)
            {
                try
                {
                    saved.ImageCrc = GenerateBaseThumbnail(saved);
                }
                catch
                {
                    // A failed thumbnail never blocks the save itself.
                }
            }

            savedBases[saved.Code] = saved;
            SaveSavedBases();
            return saved;
        }

        // Renders an isometric (2:1 dimetric) shaded thumbnail of the captured base into
        // file storage - a real 3D render needs a client GPU the headless server lacks, so
        // this is the automatic card image whenever no photo/URL is provided. Each piece is
        // a shaded cube, painted back-to-front so nearer pieces occlude the ones behind.
        private string GenerateBaseThumbnail(SavedBase saved)
        {
            const int width = 320;
            const int height = 240;

            // Isometric projection into "metre space": u across, v up (higher y = higher).
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (SavedEntity e in saved.Entities)
            {
                float u = (e.X - e.Z) * 0.866f;
                float v = e.Y - ((e.X + e.Z) * 0.5f);
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
            }

            if (minU > maxU)
                return null;

            float scale = Mathf.Min((width - 40f) / Mathf.Max(4f, maxU - minU), (height - 60f) / Mathf.Max(4f, maxV - minV));
            float offU = (width / 2f) - (((minU + maxU) / 2f) * scale);
            float offV = (height / 2f) - (((minV + maxV) / 2f) * scale);

            Color32 background = new Color32(20, 24, 34, 255);
            Color32[] pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = background;

            // Back-to-front: larger (x+z) is farther away, drawn first; ties broken low-to-high.
            List<SavedEntity> ordered = new List<SavedEntity>(saved.Entities);
            ordered.Sort((a, b) =>
            {
                float da = a.X + a.Z, db = b.X + b.Z;
                if (Mathf.Abs(da - db) > 0.05f)
                    return db.CompareTo(da);
                return a.Y.CompareTo(b.Y);
            });

            foreach (SavedEntity e in ordered)
            {
                bool isTile = e.Prefab.Contains("foundation") || e.Prefab.Contains("floor");
                bool isBlock = !isTile && e.Prefab.Contains("/building core/");
                bool isDeployable = !isTile && !isBlock;

                float halfM = isTile ? 1.5f : isBlock ? 0.55f : 0.5f;
                float depthM = isTile ? 0.4f : isBlock ? 3f : 0.9f;

                float sx = (((e.X - e.Z) * 0.866f) * scale) + offU;
                float sy = ((e.Y - ((e.X + e.Z) * 0.5f)) * scale) + offV;

                Color32 top = isDeployable ? new Color32(74, 105, 255, 255) : ThumbnailGradeColour(e.Grade, false);
                DrawIsoCube(pixels, width, height, sx, sy, halfM * 0.866f * scale, halfM * 0.5f * scale, depthM * scale, top);
            }

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.Destroy(texture);

            if (png == null || CommunityEntity.ServerInstance == null)
                return null;

            return FileStorage.server.Store(png, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID).ToString();
        }

        // Draws one isometric cube: a bright top-face diamond plus two shaded side faces
        // dropping toward the viewer, giving the massing model its 3D read.
        private void DrawIsoCube(Color32[] pixels, int width, int height, float cx, float cy, float hx, float hy, float depth, Color32 colour)
        {
            hx = Mathf.Max(1.5f, hx);
            hy = Mathf.Max(0.9f, hy);
            depth = Mathf.Max(1f, depth);

            Vector2 topN = new Vector2(cx, cy + hy);
            Vector2 right = new Vector2(cx + hx, cy);
            Vector2 bottom = new Vector2(cx, cy - hy);
            Vector2 left = new Vector2(cx - hx, cy);

            Color32 leftFace = ShadeColour(colour, 0.55f);
            Color32 rightFace = ShadeColour(colour, 0.78f);

            // Left face (down from the left/bottom edges) then right, then the lit top last.
            FillIsoPolygon(pixels, width, height, new[] { left, bottom, new Vector2(bottom.x, bottom.y - depth), new Vector2(left.x, left.y - depth) }, leftFace);
            FillIsoPolygon(pixels, width, height, new[] { bottom, right, new Vector2(right.x, right.y - depth), new Vector2(bottom.x, bottom.y - depth) }, rightFace);
            FillIsoPolygon(pixels, width, height, new[] { topN, right, bottom, left }, colour);
        }

        private Color32 ShadeColour(Color32 c, float f)
        {
            return new Color32((byte)(c.r * f), (byte)(c.g * f), (byte)(c.b * f), 255);
        }

        // Scanline-fills a convex screen polygon (y measured from the bottom of the image).
        private void FillIsoPolygon(Color32[] pixels, int width, int height, Vector2[] poly, Color32 colour)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (Vector2 p in poly)
            {
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }

            int y0 = Mathf.Clamp(Mathf.FloorToInt(minY), 0, height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(maxY), 0, height - 1);

            for (int y = y0; y <= y1; y++)
            {
                float scanY = y + 0.5f;
                float xMin = float.MaxValue, xMax = float.MinValue;

                for (int i = 0; i < poly.Length; i++)
                {
                    Vector2 a = poly[i];
                    Vector2 b = poly[(i + 1) % poly.Length];
                    if ((a.y <= scanY && b.y > scanY) || (b.y <= scanY && a.y > scanY))
                    {
                        float t = (scanY - a.y) / (b.y - a.y);
                        float x = a.x + (t * (b.x - a.x));
                        xMin = Mathf.Min(xMin, x);
                        xMax = Mathf.Max(xMax, x);
                    }
                }

                if (xMin > xMax)
                    continue;

                int x0 = Mathf.Clamp(Mathf.RoundToInt(xMin), 0, width - 1);
                int x1 = Mathf.Clamp(Mathf.RoundToInt(xMax), 0, width - 1);
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                    pixels[row + x] = colour;
            }
        }

        // Blueprint palette per building grade: twig, wood, stone, metal, hqm.
        private static readonly Color32[] ThumbnailGradeColours =
        {
            new Color32(168, 142, 92, 255),
            new Color32(146, 108, 70, 255),
            new Color32(150, 150, 155, 255),
            new Color32(96, 102, 112, 255),
            new Color32(122, 144, 176, 255)
        };

        private Color32 ThumbnailGradeColour(int grade, bool darker)
        {
            Color32 colour = grade >= 0 && grade < ThumbnailGradeColours.Length
                ? ThumbnailGradeColours[grade]
                : new Color32(120, 130, 150, 255);

            return darker ? ShadeColour(colour, 0.6f) : colour;
        }

        // When a player who took the publisher's camera snaps a photo, hand control back:
        // the camera is removed and the publish dialog reopens with the photo ready.
        private void OnEntitySpawned(PhotoEntity photo)
        {
            if (photo == null || awaitingPhoto.Count == 0)
                return;

            timer.Once(1f, () =>
            {
                if (photo == null || photo.IsDestroyed)
                    return;

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected || !awaitingPhoto.Contains(player.userID))
                        continue;

                    if (!PlayerHasPhoto(player, photo.net.ID))
                        continue;

                    awaitingPhoto.Remove(player.userID);
                    RemoveCameraItems(player);

                    // Put them back where they were standing before we framed the shot.
                    Vector3 home;
                    if (photoReturnPosition.TryGetValue(player.userID, out home))
                    {
                        photoReturnPosition.Remove(player.userID);
                        player.Teleport(home);
                        player.SendNetworkUpdateImmediate();
                    }

                    ShowMenuUi(player);
                    ShowSaveModal(player);
                    player.SendConsoleCommand("gametip.showtoast", 0, "Photo captured - it will be used as the base image.", string.Empty, false);
                    return;
                }
            });
        }

        private void EquipItem(BasePlayer player, Item item)
        {
            if (item == null || item.uid.IsValid == false)
                return;

            player.UpdateActiveItem(item.uid);
        }

        // Measures the player's build and flies them to a spot that fits the whole thing in
        // frame: backed off far enough for the base's radius at Rust's field of view, and
        // lifted to a 3/4 angle. The shutter press stays theirs - a photo is rendered by
        // the client, so the server can neither take it nor aim their view.
        private void FrameBaseForPhoto(BasePlayer player)
        {
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity.OwnerID == 0 || entity is BasePlayer)
                    continue;

                if (entity.ShortPrefabName.StartsWith("cine") || !IsInsideArea(entity.transform.position, area))
                    continue;

                min = Vector3.Min(min, entity.transform.position);
                max = Vector3.Max(max, entity.transform.position);
                any = true;
            }

            if (!any)
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "Nothing to photograph - build something first.", string.Empty, false);
                return;
            }

            photoReturnPosition[player.userID] = player.transform.position;

            Vector3 center = (min + max) / 2f;
            Vector3 size = max - min;

            // Back off by the base's radius scaled for a ~70 degree field of view, then a
            // little more so nothing is clipped at the edges.
            float radius = Mathf.Max(6f, new Vector2(size.x, size.z).magnitude * 0.5f);
            float distance = Mathf.Max(12f, ((radius + (size.y * 0.5f)) / Mathf.Tan(35f * Mathf.Deg2Rad)) * 1.25f);

            // Fixed 3/4 view, lifted to look slightly down over the roof line.
            Vector3 direction = new Vector3(0.75f, 0f, 0.75f).normalized;
            Vector3 camera = center + (direction * distance);
            camera.y = center.y + (size.y * 0.5f) + (distance * 0.45f);

            if (!player.IsFlying)
                player.SendConsoleCommand("noclip");

            player.Teleport(camera);
            player.SendNetworkUpdateImmediate();

            player.SendConsoleCommand("gametip.showtoast", 0, "Framed your base - look at it and LEFT CLICK to snap the photo.", string.Empty, false);
        }

        private bool PlayerHasPhoto(BasePlayer player, NetworkableId photoId)
        {
            ItemContainer[] containers = { player.inventory.containerBelt, player.inventory.containerMain };
            foreach (ItemContainer containerItems in containers)
            {
                if (containerItems == null)
                    continue;

                foreach (Item item in containerItems.itemList)
                {
                    if (item != null && item.info != null && item.info.shortname == "photo"
                        && item.instanceData != null && item.instanceData.subEntity == photoId)
                        return true;
                }
            }

            return false;
        }

        private void RemoveCameraItems(BasePlayer player)
        {
            List<Item> cameras = new List<Item>();
            ItemContainer[] containers = { player.inventory.containerBelt, player.inventory.containerMain };

            foreach (ItemContainer containerItems in containers)
            {
                if (containerItems == null)
                    continue;

                foreach (Item item in containerItems.itemList)
                {
                    if (item != null && item.info != null && (item.info.shortname == "tool.instant_camera" || item.info.shortname == "instant_camera" || item.info.shortname == "instantcamera" || item.info.shortname == "tool.camera"))
                        cameras.Add(item);
                }
            }

            foreach (Item item in cameras)
            {
                item.RemoveFromContainer();
                item.Remove();
            }
        }

        // Instant-camera photos are uploaded into server file storage by the client; the
        // photo item carries the image id, so the newest photo becomes the card image.
        private uint FindNewestPhotoCrc(BasePlayer player)
        {
            uint crc = 0;
            ItemContainer[] containers = { player.inventory.containerBelt, player.inventory.containerMain };

            foreach (ItemContainer containerItems in containers)
            {
                if (containerItems == null)
                    continue;

                foreach (Item item in containerItems.itemList)
                {
                    if (item == null || item.info == null || item.info.shortname != "photo" || item.instanceData == null)
                        continue;

                    // Old storage put the image id directly on the item...
                    if (item.instanceData.dataInt != 0)
                    {
                        crc = (uint)item.instanceData.dataInt;
                        continue;
                    }

                    // ...current photos reference a PhotoEntity that owns the uploaded image.
                    PhotoEntity photo = BaseNetworkable.serverEntities.Find(item.instanceData.subEntity) as PhotoEntity;
                    if (photo != null && photo.ImageCrc != 0)
                        crc = photo.ImageCrc;
                }
            }

            return crc;
        }

        // Shared entry for full, foundation-only (mode 1) and floor-limited (mode 2) loads.
        private void TryLoadSave(BasePlayer player, string code, int loadMode, int floorCount)
        {
            SavedBase saved;
            if (!savedBases.TryGetValue(code, out saved))
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "That save no longer exists.", string.Empty, false);
                return;
            }

            if (saved.Visibility == 0 && saved.OwnerId != player.userID)
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "That save is private.", string.Empty, false);
                return;
            }

            int spawnedCount = LoadBase(player, saved, loadMode, floorCount);
            if (spawnedCount < 0)
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "You don't have a plot yet.", string.Empty, false);
                return;
            }

            if (loadMode == 0)
            {
                saved.LoadCount++;
                SaveSavedBases();
            }

            CloseMenu(player);

            AssignedArea homeArea;
            if (assignedAreas.TryGetValue(player.userID, out homeArea))
                TeleportInsideArea(player, homeArea, homeArea.SpawnPosition);

            player.ChatMessage("Loaded '" + saved.Name + "' (" + spawnedCount + " entities).");
        }

        // How many 3m levels tall the save is, ground floor included.
        private int SavedFloorCount(SavedBase saved)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (SavedEntity entity in saved.Entities)
            {
                minY = Mathf.Min(minY, entity.Y);
                maxY = Mathf.Max(maxY, entity.Y);
            }

            if (minY > maxY)
                return 0;

            return Mathf.Max(1, Mathf.RoundToInt((maxY - minY) / 3f) + 1);
        }

        // Clears the player's plot, then rebuilds the save around the plot center. Returns
        // the number of entities spawned, or -1 when the player has no plot. loadMode 1
        // pastes only foundations; loadMode 2 pastes levels 0..floorCount-1.
        private int LoadBase(BasePlayer player, SavedBase saved, int loadMode = 0, int floorCount = 0)
        {
            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return -1;

            Vector3 anchor = GetGroundedPosition(area.Center);

            List<BaseEntity> toKill = new List<BaseEntity>();
            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity is BasePlayer)
                    continue;

                // Everything player-built goes, plus ownerless leftovers (loot bags,
                // corpses, dropped items) - the plot must be truly empty before the
                // saved build is placed. Natural terrain entities and the plot's own
                // dome/markers stay (they carry no owner and are none of these types).
                bool leftovers = entity is DroppedItemContainer || entity is DroppedItem || entity is BaseCorpse;
                if (entity.OwnerID == 0 && !leftovers)
                    continue;

                if (!IsInsideArea(entity.transform.position, area))
                    continue;

                toKill.Add(entity);
            }

            foreach (BaseEntity entity in toKill)
            {
                if (entity.IsDestroyed)
                    continue;

                // SimpleSymmetry blocks killing cupboards that still hold items.
                StorageContainer storage = entity as StorageContainer;
                if (storage != null && storage.inventory != null)
                    storage.inventory.Clear();

                entity.Kill();
            }

            uint buildingId = BuildingManager.server.NewBuildingID();
            int spawned = 0;

            float baseMinY = float.MaxValue;
            foreach (SavedEntity item in saved.Entities)
                baseMinY = Mathf.Min(baseMinY, item.Y);

            // Anchor vertically to the actual terrain height (GetGroundedPosition raycasts
            // downward and can hit an existing build, giving an inconsistent ground level
            // between capture and load - which sank pastes into the terrain). The lowest
            // saved piece is pinned to the terrain so the base always rests on the ground.
            float terrainY = TerrainMeta.HeightMap != null
                ? TerrainMeta.HeightMap.GetHeight(new Vector3(area.Center.x, 0f, area.Center.z))
                : anchor.y;

            foreach (SavedEntity item in saved.Entities)
            {
                if (loadMode == 1 && !item.Prefab.Contains("foundation"))
                    continue;

                if (loadMode == 2)
                {
                    int floorIndex = Mathf.Max(0, Mathf.RoundToInt((item.Y - baseMinY) / 3f));
                    if (floorIndex >= floorCount)
                        continue;
                }

                Vector3 position = new Vector3(anchor.x + item.X, terrainY + (item.Y - baseMinY), anchor.z + item.Z);
                BaseEntity entity = GameManager.server.CreateEntity(item.Prefab, position, Quaternion.Euler(item.RotX, item.RotY, item.RotZ));
                if (entity == null)
                    continue;

                entity.OwnerID = player.userID;
                entity.skinID = item.Skin;

                StabilityEntity stability = entity as StabilityEntity;
                if (stability != null)
                    stability.grounded = true;

                entity.Spawn();

                DecayEntity decay = entity as DecayEntity;
                if (decay != null)
                    decay.AttachToBuilding(buildingId);

                BuildingBlock block = entity as BuildingBlock;
                if (block != null)
                {
                    if (item.Grade >= 0)
                        block.SetGrade((BuildingGrade.Enum)item.Grade);
                    block.SetHealthToMax();
                    block.UpdateSkin();
                    block.SendNetworkUpdate();
                }

                BuildingPrivlidge cupboard = entity as BuildingPrivlidge;
                if (cupboard != null)
                {
                    cupboard.authorizedPlayers.Add(player.userID);
                    StockCupboard(cupboard);
                    cupboard.SendNetworkUpdate();
                }

                spawned++;
            }

            return spawned;
        }

        // Styled like the SimpleSymmetry in-game widget: live shape preview with the current
        // type, cycle arrow, dark button stack and a status line fed by the last action.
        private void DrawSymmetryCard(CuiElementContainer container, string card, BasePlayer player)
        {
            Plugin simpleSymmetry = plugins.Find("SimpleSymmetry");
            Dictionary<string, object> info = simpleSymmetry != null && simpleSymmetry.IsLoaded
                ? simpleSymmetry.Call("GetSymmetryInfo", player) as Dictionary<string, object>
                : null;

            bool enabled = false;
            string typeName = null;
            string png = null;

            if (info != null)
            {
                object value;
                if (info.TryGetValue("enabled", out value) && value is bool)
                    enabled = (bool)value;
                if (info.TryGetValue("type", out value))
                    typeName = value as string;
                if (info.TryGetValue("image", out value))
                    png = value as string;
            }

            if (string.IsNullOrEmpty(png))
                png = GetSymmetryAssetPng("Square.png");

            string box = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.7" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "12 -180", OffsetMax = "146 -46" }
            }, card);

            if (!string.IsNullOrEmpty(png))
            {
                container.Add(new CuiElement
                {
                    Parent = box,
                    Components =
                    {
                        new CuiRawImageComponent { Png = png },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "6 6", OffsetMax = "-6 -6" }
                    }
                });
            }

            container.Add(new CuiLabel
            {
                Text = { Text = info == null ? "SimpleSymmetry\nnot loaded" : enabled ? typeName : "Symmetry Not Set", FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, box);

            MenuButton(container, card, 152, -46, 26, 134, ">", "gridspawn.menu sym cycle", "0 0 0 0.7");
            MenuButton(container, card, 186, -46, 110, 40, "Set\nSymmetry", "gridspawn.menu sym set", "0 0 0 0.7");
            MenuButton(container, card, 186, -93, 110, 40, "Toggle\nSymmetry", "gridspawn.menu sym toggle", "0 0 0 0.7");
            MenuButton(container, card, 186, -140, 110, 40, "Delete\nSymmetry", "gridspawn.menu sym delete", "0 0 0 0.7");

            MenuButton(container, card, 12, -188, 134, 26, "SHOW POINT", "gridspawn.menu sym show", "0 0 0 0.7");
            MenuButton(container, card, 152, -188, 144, 26, "SYMMETRY PANEL", "gridspawn.menu sym ui", "0 0 0 0.7");

            string status;
            if (symStatus.TryGetValue(player.userID, out status) && !string.IsNullOrEmpty(status))
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = status, FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = status.StartsWith("No") ? "0.9 0.3 0.25 1" : "0.29 0.41 1 1" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -234", OffsetMax = "-12 -218" }
                }, card);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "Set Symmetry uses where you stand; a plain hammer hit on a foundation\nor floor sets the point there instead. Build and blocks are mirrored.\nTypes: N2S / N3S / N4S / N6S normal, M2S / M4S mirrored (/sym N4S).", FontSize = 10, Font = "robotocondensed-regular.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.5" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -296", OffsetMax = "-12 -240" }
            }, card);
        }

        private void UpdateSymmetryCard(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, MenuSymCardName);

            CuiElementContainer container = new CuiElementContainer();
            string card = MenuCard(container, MenuGridName, "0 0", "0.5 0.5", "0 0", "-5 -5", "SYMMETRY", MenuSymCardName);
            DrawSymmetryCard(container, card, player);
            CuiHelper.AddUi(player, container);
        }

        // Turns the state after a menu symmetry action into a status line. Error messages
        // start with "No" so the card can color them red.
        private string BuildSymmetryStatus(Plugin simpleSymmetry, BasePlayer player, string action)
        {
            Dictionary<string, object> info = simpleSymmetry.Call("GetSymmetryInfo", player) as Dictionary<string, object>;
            if (info == null)
                return null;

            object value;
            bool set = info.TryGetValue("set", out value) && value is bool && (bool)value;
            bool enabled = info.TryGetValue("enabled", out value) && value is bool && (bool)value;
            string typeName = info.TryGetValue("type", out value) ? value as string : null;

            switch (action)
            {
                case "set":
                    if (!set)
                        return "No center found - stand on the middle foundation(s) and try again.";
                    return enabled ? "Symmetry point set - " + typeName + "." : "Point set - press TOGGLE SYMMETRY to enable.";

                case "toggle":
                    if (!set)
                        return "No symmetry point - use SET SYMMETRY first.";
                    return enabled ? "Symmetry is now ON (" + typeName + ")." : "Symmetry is now OFF.";

                case "cycle":
                    if (!set)
                        return "No symmetry point - use SET SYMMETRY first.";
                    return "Type changed to " + typeName + ".";

                case "delete":
                    return "Symmetry point deleted.";

                case "show":
                    if (!set)
                        return "No symmetry point - use SET SYMMETRY first.";
                    return "Point shown in the world as a blue sphere.";
            }

            return null;
        }

        private void DrawCommandsCard(CuiElementContainer container, string card)
        {
            MenuLine(container, card, 2, "/menu  -  this menu", 11);
            MenuLine(container, card, 3, "/sym  -  symmetry help and exact types", 11);
            MenuLine(container, card, 4, "/gradeall <grade>  or  /gradeall <from> <to>", 11);
            MenuLine(container, card, 5, "/circuit  -  wiring diagram", 11);
            MenuLine(container, card, 6, "/cost  -  construction, deployables and upkeep", 11);
            MenuLine(container, card, 7, "/rocketgun  -  infinite launcher", 11);
            MenuLine(container, card, 8, "/raid  -  reset the raid counter", 11);
            MenuLine(container, card, 9, "+ PUBLISH (top bar)  -  publish and share your base", 11);
            MenuLine(container, card, 10, "Everything is free: F1 spawns items, nothing", 11, "1 1 1 0.5");
            MenuLine(container, card, 11, "depletes and no workbench is needed.", 11, "1 1 1 0.5");
        }

        private void DrawKeybindsCard(CuiElementContainer container, string card)
        {
            KeybindRow(container, card, 0, "Open / Close Menu", "Middle-Click");
            KeybindRow(container, card, 1, "Toggle Noclip", "F  (hammer/plan)");
            KeybindRow(container, card, 2, "Remove Entity", "Hammer + R");
            KeybindRow(container, card, 3, "Upgrade Block", "Shift + Hit");
            KeybindRow(container, card, 4, "Downgrade Block", "Ctrl + Hit");
            KeybindRow(container, card, 5, "Cycle Build Grade", "X  (plan)");
            KeybindRow(container, card, 6, "Set Symmetry", "Hammer + Hit");
            KeybindRow(container, card, 7, "Teleport", "Map Marker");
            KeybindRow(container, card, 8, "Spawn Items", "F1 Menu");
            KeybindRow(container, card, 9, "Undo / Redo", "/undo  /redo");
        }

        private void KeybindRow(CuiElementContainer container, string parent, int index, string name, string bind)
        {
            string row = container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.04" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 " + (-74 - (index * 38)), OffsetMax = "-8 " + (-40 - (index * 38)) }
            }, parent);

            container.Add(new CuiLabel
            {
                Text = { Text = name, FontSize = 11, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.55 1", OffsetMin = "8 0", OffsetMax = "0 0" }
            }, row);

            container.Add(new CuiLabel
            {
                Text = { Text = bind, FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleRight, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.4 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "-8 0" }
            }, row);
        }

        // SimpleSymmetry stores its uploaded asset image ids in oxide/data/SimpleSymmetry.json;
        // reuse the square shape as the preview in the symmetry card.
        private string GetSymmetryAssetPng(string name)
        {
            if (!string.IsNullOrEmpty(symmetryPreviewPng))
                return symmetryPreviewPng;

            try
            {
                var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, Dictionary<string, string>>>("SimpleSymmetry");
                Dictionary<string, string> images;
                string png;
                if (stored != null && stored.TryGetValue("CommonImages", out images) && images != null && images.TryGetValue(name, out png) && !string.IsNullOrEmpty(png))
                    symmetryPreviewPng = png;
            }
            catch
            {
                // Data file absent or in an unexpected shape - the preview box just stays empty.
            }

            return symmetryPreviewPng;
        }

        #endregion

        // Gives a rocket launcher that never needs reloading: the magazine is refilled right
        // after every launch (one tick later, after vanilla has decremented it).
        [ChatCommand("rocketgun")]
        private void RocketGunCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            Item item = ItemManager.CreateByName("rocket.launcher", 1);
            if (item == null)
                return;

            item.name = "Rocket Gun";

            BaseProjectile launcher = item.GetHeldEntity() as BaseProjectile;
            if (launcher != null && launcher.primaryMagazine != null)
                launcher.primaryMagazine.contents = launcher.primaryMagazine.capacity;

            if (!player.inventory.GiveItem(item))
            {
                item.Remove();
                player.ChatMessage("Free some inventory space first.");
                return;
            }

            player.ChatMessage("Rocket Gun added: infinite rockets, no reload.");
        }

        private void RefillRocketGun(BasePlayer player)
        {
            if (player == null)
                return;

            Item held = player.GetActiveItem();
            if (held == null || held.name != "Rocket Gun")
                return;

            BaseProjectile launcher = held.GetHeldEntity() as BaseProjectile;
            if (launcher == null || launcher.primaryMagazine == null)
                return;

            launcher.primaryMagazine.contents = launcher.primaryMagazine.capacity;
            launcher.SendNetworkUpdateImmediate();
        }

        private void OnRocketLaunched(BasePlayer player, BaseEntity entity)
        {
            NextTick(() => RefillRocketGun(player));

            if (entity == null)
                return;

            string ammo = null;
            switch (entity.ShortPrefabName)
            {
                case "rocket_basic": ammo = "ammo.rocket.basic"; break;
                case "rocket_hv": ammo = "ammo.rocket.hv"; break;
                case "rocket_fire": ammo = "ammo.rocket.fire"; break;
                case "rocket_smoke": ammo = "ammo.rocket.smoke"; break;
                case "40mm_grenade_he": ammo = "ammo.grenadelauncher.he"; break;
            }

            if (ammo != null)
                TrackExplosive(player, ItemManager.FindItemDefinition(ammo));
        }

        private void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot projectileShoot)
        {
            if (projectile == null || player == null || projectile.primaryMagazine == null)
                return;

            ItemDefinition ammo = projectile.primaryMagazine.ammoType;
            if (ammo != null && ammo.shortname == "ammo.rifle.explosive")
                TrackExplosive(player, ammo);

            // Infinite ammo: refill the magazine one tick after the shot decrements it.
            if (infiniteAmmo.Contains(player.userID))
            {
                BaseProjectile weapon = projectile;
                NextTick(() =>
                {
                    if (weapon == null || weapon.IsDestroyed || weapon.primaryMagazine == null)
                        return;

                    weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                    weapon.SendNetworkUpdateImmediate();
                });
            }
        }

        private void TrackExplosive(BasePlayer player, ItemDefinition definition)
        {
            if (player == null || definition == null || !player.userID.IsSteamId())
                return;

            Dictionary<int, int> counts;
            if (!raidCounts.TryGetValue(player.userID, out counts))
                raidCounts[player.userID] = counts = new Dictionary<int, int>();

            int current;
            counts.TryGetValue(definition.itemid, out current);
            counts[definition.itemid] = current + 1;

            ShowRaidUi(player, counts);
        }

        // Expands an item into raw resources through its blueprint (e.g. C4 down to sulfur,
        // charcoal, cloth and tech trash).
        private void AddCraftCost(ItemDefinition definition, int amount, Dictionary<ItemDefinition, int> totals)
        {
            if (definition == null || amount <= 0)
                return;

            ItemBlueprint blueprint = RawCostItems.Contains(definition.shortname) ? null : ItemManager.FindBlueprint(definition);
            if (blueprint == null || blueprint.ingredients == null || blueprint.ingredients.Count == 0)
            {
                int current;
                totals.TryGetValue(definition, out current);
                totals[definition] = current + amount;
                return;
            }

            foreach (ItemAmount ingredient in blueprint.ingredients)
            {
                int needed = Mathf.CeilToInt(ingredient.amount * amount / blueprint.amountToCreate);
                AddCraftCost(ingredient.itemDef, needed, totals);
            }
        }

        private string FormatAmount(int amount)
        {
            if (amount >= 1000000)
                return (amount / 1000000f).ToString("0.##") + "M";

            if (amount >= 1000)
                return (amount / 1000f).ToString("0.#") + "k";

            return amount.ToString();
        }

        private void ShowRaidUi(BasePlayer player, Dictionary<int, int> counts)
        {
            CuiHelper.DestroyUi(player, RaidUiName);
            if (counts.Count == 0)
                return;

            Dictionary<ItemDefinition, int> totals = new Dictionary<ItemDefinition, int>();
            List<KeyValuePair<ItemDefinition, string>> lines = new List<KeyValuePair<ItemDefinition, string>>();

            foreach (KeyValuePair<int, int> entry in counts)
            {
                ItemDefinition definition = ItemManager.FindItemDefinition(entry.Key);
                if (definition == null)
                    continue;

                lines.Add(new KeyValuePair<ItemDefinition, string>(definition, definition.displayName.english + " x" + entry.Value));
                AddCraftCost(definition, entry.Value, totals);
            }

            lines.Add(new KeyValuePair<ItemDefinition, string>(null, "--- cost ---"));
            foreach (KeyValuePair<ItemDefinition, int> entry in totals.OrderByDescending(t => t.Value))
                lines.Add(new KeyValuePair<ItemDefinition, string>(entry.Key, entry.Key.displayName.english + ": " + FormatAmount(entry.Value)));

            DrawLinesPanel(player, RaidUiName, "RAID COST", lines, "0 0.5", "16 0", 200, "/raid resets");
        }

        [ChatCommand("raid")]
        private void RaidCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            raidCounts.Remove(player.userID);
            CuiHelper.DestroyUi(player, RaidUiName);
            player.ChatMessage("Raid counter reset.");
        }

        #endregion

        #region Base cost UI

        [ChatCommand("cost")]
        private void CostCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            int blockCount = 0;
            Dictionary<ItemDefinition, int> buildCost = new Dictionary<ItemDefinition, int>();
            Dictionary<ItemDefinition, int> deployableCost = new Dictionary<ItemDefinition, int>();
            Dictionary<ItemDefinition, float> upkeep = new Dictionary<ItemDefinition, float>();

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity is BasePlayer)
                    continue;

                if (!IsInsideArea(entity.transform.position, area))
                    continue;

                BuildingBlock block = entity as BuildingBlock;
                if (block != null)
                {
                    ConstructionGrade grade = block.blockDefinition.grades[(int)block.grade];
                    if (grade != null)
                    {
                        float fraction = UpkeepFraction(blockCount);
                        foreach (ItemAmount cost in grade.CostToBuild())
                        {
                            int current;
                            buildCost.TryGetValue(cost.itemDef, out current);
                            buildCost[cost.itemDef] = current + (int)cost.amount;

                            float currentUpkeep;
                            upkeep.TryGetValue(cost.itemDef, out currentUpkeep);
                            upkeep[cost.itemDef] = currentUpkeep + (cost.amount * fraction);
                        }
                    }

                    blockCount++;
                    continue;
                }

                if (entity.OwnerID == 0)
                    continue;

                BaseCombatEntity deployable = entity as BaseCombatEntity;
                if (deployable == null)
                    continue;

                ItemDefinition itemDefinition = deployable.pickup.itemTarget != null ? deployable.pickup.itemTarget : deployable.repair.itemTarget;
                if (itemDefinition != null)
                    AddCraftCost(itemDefinition, 1, deployableCost);
            }

            Dictionary<ItemDefinition, int> upkeepRounded = new Dictionary<ItemDefinition, int>();
            foreach (KeyValuePair<ItemDefinition, float> entry in upkeep)
                upkeepRounded[entry.Key] = Mathf.CeilToInt(entry.Value);

            ShowCostSidebar(player, buildCost, deployableCost, upkeepRounded);
            timer.Once(20f, () =>
            {
                if (player != null)
                    CuiHelper.DestroyUi(player, CostUiName);
            });
        }

        // The materials sidebar: three stacked panels (construction, deployables, upkeep) on
        // the right edge of the screen, styled after the provided design.
        private void ShowCostSidebar(BasePlayer player, Dictionary<ItemDefinition, int> construction, Dictionary<ItemDefinition, int> deployables, Dictionary<ItemDefinition, int> upkeep)
        {
            CuiHelper.DestroyUi(player, CostUiName);

            List<KeyValuePair<ItemDefinition, int>> constructionRows = BuildRows(
                new[] { "wood", "stones", "metal.fragments", "metal.refined" }, construction);
            List<KeyValuePair<ItemDefinition, int>> deployableRows = BuildRows(
                new[] { "wood", "stones", "metal.fragments", "metal.refined", "rope", "cloth", "lowgradefuel", "gears", "sewingkit", "sheetmetal", "tarp", "glue" }, deployables);
            List<KeyValuePair<ItemDefinition, int>> upkeepRows = BuildRows(
                new[] { "wood", "stones", "metal.fragments", "metal.refined" }, upkeep);

            int constructionHeight = 27 + (constructionRows.Count * 24);
            int deployablesHeight = 27 + (deployableRows.Count * 24);
            int upkeepHeight = 37 + (upkeepRows.Count * 24);
            int total = constructionHeight + 8 + deployablesHeight + 8 + upkeepHeight;

            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-132 " + (-104 - total), OffsetMax = "-8 -104" }
            }, "Overlay", CostUiName);

            int top = 0;
            top = DrawSidebarPanel(container, "CONSTRUCTION", top, null, constructionRows, false) - 8;
            top = DrawSidebarPanel(container, "DEPLOYABLES", top, null, deployableRows, false) - 8;
            DrawSidebarPanel(container, "UPKEEP", top, "COST PER 24 HOURS", upkeepRows, true);

            CuiHelper.AddUi(player, container);
        }

        private List<KeyValuePair<ItemDefinition, int>> BuildRows(string[] candidates, Dictionary<ItemDefinition, int> amounts)
        {
            List<KeyValuePair<ItemDefinition, int>> rows = new List<KeyValuePair<ItemDefinition, int>>();
            HashSet<ItemDefinition> used = new HashSet<ItemDefinition>();

            foreach (string shortname in candidates)
            {
                ItemDefinition definition = ItemManager.FindItemDefinition(shortname);
                if (definition == null)
                    continue;

                int amount;
                amounts.TryGetValue(definition, out amount);
                rows.Add(new KeyValuePair<ItemDefinition, int>(definition, amount));
                used.Add(definition);
            }

            foreach (KeyValuePair<ItemDefinition, int> entry in amounts.OrderByDescending(a => a.Value))
            {
                if (entry.Value > 0 && !used.Contains(entry.Key))
                    rows.Add(entry);
            }

            return rows;
        }

        private int DrawSidebarPanel(CuiElementContainer container, string title, int top, string subLabel, List<KeyValuePair<ItemDefinition, int>> rows, bool showLabels)
        {
            int rowsTop = string.IsNullOrEmpty(subLabel) ? 25 : 35;
            int height = rowsTop + (rows.Count * 24) + 2;

            string panel = container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 0.85" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "0 " + (top - height), OffsetMax = "124 " + top }
            }, CostUiName);

            container.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 9, Font = "robotocondensed-bold.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "6 -16", OffsetMax = "118 -5" }
            }, panel);

            container.Add(new CuiPanel
            {
                Image = { Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "6 -21", OffsetMax = "118 -20" }
            }, panel);

            if (!string.IsNullOrEmpty(subLabel))
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = subLabel, FontSize = 6, Font = "robotocondensed-bold.ttf", Align = TextAnchor.UpperLeft, Color = "1 1 1 0.55" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "6 -31", OffsetMax = "118 -25" }
                }, panel);
            }

            for (int i = 0; i < rows.Count; i++)
            {
                int rowTop = -(rowsTop + (i * 24));
                int rowBottom = rowTop - 20;

                string row = container.Add(new CuiPanel
                {
                    Image = { Color = "1 1 1 0.04" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "6 " + rowBottom, OffsetMax = "118 " + rowTop }
                }, panel);

                AddItemIcon(container, row, rows[i].Key, "0 1", "4 -17", "18 -3");

                if (showLabels)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = rows[i].Key.displayName.english.ToUpper(), FontSize = 6, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.85" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "22 0", OffsetMax = "-26 0" }
                    }, row);
                }

                bool hasAmount = rows[i].Value > 0;
                container.Add(new CuiLabel
                {
                    Text = { Text = rows[i].Value.ToString("N0"), FontSize = 8, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleRight, Color = hasAmount ? "0.29 0.41 1 1" : "1 1 1 0.45" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "20 0", OffsetMax = "-5 0" }
                }, row);
            }

            return top - height;
        }

        #region Circuit viewer

        // Opens the build menu straight on the electricity page.
        [ChatCommand("circuit")]
        private void CircuitCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            circuitViews[player.userID] = new CircuitView();
            menuPage[player.userID] = "circuit";
            ShowMenuUi(player);
        }

        [ConsoleCommand("gridspawn.circuit")]
        private void CircuitConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasNoClipPermission(player))
                return;

            CircuitView view;
            if (!circuitViews.TryGetValue(player.userID, out view))
            {
                view = new CircuitView();
                circuitViews[player.userID] = view;
            }

            switch (arg.GetString(0, string.Empty))
            {
                case "floor":
                    view.Floor = arg.GetInt(1, -1);
                    break;

                case "overlay":
                    view.Overlay = !view.Overlay;
                    break;
            }

            RefreshMenu(player);
        }

        private int FloorIndex(float y, float baseY)
        {
            return Mathf.Max(0, Mathf.RoundToInt((y - baseY) / FloorHeight));
        }

        // Top-down wiring diagram of the player's plot, embedded as a page of the build menu:
        // component icons at their real positions, L-shaped wire traces with the wattage
        // flowing through them, floor tabs, and a faint footprint overlay.
        private void DrawCircuitPage(CuiElementContainer container, string body, BasePlayer player)
        {
            string card = MenuCard(container, body, "0 0", "1 1", "10 10", "-10 -10", "ELECTRICITY  -  CIRCUIT");

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
            {
                AddCircuitBackButton(container, card);
                return;
            }

            CircuitView view;
            if (!circuitViews.TryGetValue(player.userID, out view))
            {
                view = new CircuitView();
                circuitViews[player.userID] = view;
            }

            List<IOEntity> ioEntities = new List<IOEntity>();
            List<BuildingBlock> tiles = new List<BuildingBlock>();

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || !IsInsideArea(entity.transform.position, area))
                    continue;

                IOEntity io = entity as IOEntity;
                if (io != null)
                {
                    ioEntities.Add(io);
                    continue;
                }

                BuildingBlock block = entity as BuildingBlock;
                if (block != null)
                {
                    // Exact tile pieces only; loose matching would drag in steps and frames.
                    string prefab = block.ShortPrefabName;
                    if (prefab == "foundation" || prefab == "floor" || prefab == "foundation.triangle" || prefab == "floor.triangle")
                        tiles.Add(block);
                }
            }

            if (ioEntities.Count == 0)
            {
                // Kept clear of the header strip - CUI text swallows clicks over its whole
                // rect, so a full-card label would block the back button.
                container.Add(new CuiLabel
                {
                    Text = { Text = "No electrical components found in your plot.\nPlace something electrical and reopen this page.", FontSize = 13, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.5" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 -40" }
                }, card);
                AddCircuitBackButton(container, card);
                return;
            }

            float baseY = float.MaxValue;
            foreach (IOEntity io in ioEntities)
                baseY = Mathf.Min(baseY, io.transform.position.y);

            List<int> floors = ioEntities.Select(e => FloorIndex(e.transform.position.y, baseY)).Distinct().OrderBy(f => f).ToList();

            List<IOEntity> visible = view.Floor < 0
                ? ioEntities
                : ioEntities.Where(e => FloorIndex(e.transform.position.y, baseY) == view.Floor).ToList();

            List<BuildingBlock> visibleTiles = !view.Overlay
                ? new List<BuildingBlock>()
                : (view.Floor < 0 ? tiles : tiles.Where(t => FloorIndex(t.transform.position.y, baseY) == view.Floor).ToList());

            // Align the diagram with the base: all positions are rotated by the building's yaw
            // so the footprint and wiring render as a straight grid, however the base is turned.
            // Plots can hold several free-placed structures with different yaws, so the grid
            // angle is decided by a vote of every SQUARE piece (triangles sit at 30/60 degree
            // yaws that would skew the diagram) - the dominant building wins.
            // The mean yaw of the winning bucket is used, not the rounded bucket key: a
            // half-degree residual tilts every "straight" wire by a few pixels, which the
            // trace renderer then draws as jagged steps.
            Dictionary<int, int> yawVotes = new Dictionary<int, int>();
            Dictionary<int, float> yawOffsets = new Dictionary<int, float>();
            foreach (BuildingBlock tile in tiles)
            {
                if (tile.ShortPrefabName != "foundation" && tile.ShortPrefabName != "floor")
                    continue;

                float yaw = tile.transform.eulerAngles.y % 90f;
                int bucket = Mathf.RoundToInt(yaw) % 90;

                float diff = yaw - bucket;
                if (diff > 45f)
                    diff -= 90f;
                else if (diff < -45f)
                    diff += 90f;

                int votes;
                yawVotes.TryGetValue(bucket, out votes);
                yawVotes[bucket] = votes + 1;

                float offsetSum;
                yawOffsets.TryGetValue(bucket, out offsetSum);
                yawOffsets[bucket] = offsetSum + diff;
            }

            float gridYaw = tiles.Count > 0 ? tiles[0].transform.eulerAngles.y % 90f : 0f;
            int bestVotes = -1;
            foreach (KeyValuePair<int, int> vote in yawVotes)
            {
                if (vote.Value > bestVotes)
                {
                    bestVotes = vote.Value;
                    gridYaw = vote.Key + (yawOffsets[vote.Key] / vote.Value);
                }
            }

            // Unity yaw is clockwise, which in standard 2D math is a rotation by -yaw, so
            // undoing it needs +yaw here. The opposite sign DOUBLES the skew instead of
            // cancelling it (invisible for bases near 0/90 degrees, a 45-degree diamond
            // for bases placed near 22 degrees).
            float yawCos = Mathf.Cos(gridYaw * Mathf.Deg2Rad);
            float yawSin = Mathf.Sin(gridYaw * Mathf.Deg2Rad);

            // Project each tile's real corners (rotated by its own orientation) so pieces
            // are drawn where they actually sit. Squares aligned with the grid become exact
            // cells; anything at an odd angle gets its true bounding box instead of a
            // misplaced fixed-size square.
            List<Vector2[]> tileCorners = new List<Vector2[]>();
            foreach (BuildingBlock tile in visibleTiles)
            {
                Vector3 position = tile.transform.position;
                Quaternion rotation = tile.transform.rotation;
                Vector2[] corners;

                if (tile.ShortPrefabName.EndsWith("triangle"))
                {
                    // Triangle pieces anchor at the base-edge midpoint, apex forward.
                    corners = new Vector2[3];
                    corners[0] = RotateFlat(position + (rotation * new Vector3(-1.5f, 0f, 0f)), yawCos, yawSin);
                    corners[1] = RotateFlat(position + (rotation * new Vector3(1.5f, 0f, 0f)), yawCos, yawSin);
                    corners[2] = RotateFlat(position + (rotation * new Vector3(0f, 0f, 2.598f)), yawCos, yawSin);
                }
                else
                {
                    // Wound in path order so the polygon test below works.
                    corners = new Vector2[4];
                    corners[0] = RotateFlat(position + (rotation * new Vector3(-1.5f, 0f, -1.5f)), yawCos, yawSin);
                    corners[1] = RotateFlat(position + (rotation * new Vector3(1.5f, 0f, -1.5f)), yawCos, yawSin);
                    corners[2] = RotateFlat(position + (rotation * new Vector3(1.5f, 0f, 1.5f)), yawCos, yawSin);
                    corners[3] = RotateFlat(position + (rotation * new Vector3(-1.5f, 0f, 1.5f)), yawCos, yawSin);
                }

                tileCorners.Add(corners);
            }

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (IOEntity io in visible)
            {
                Vector2 rotated = RotateFlat(io.transform.position, yawCos, yawSin);
                minX = Mathf.Min(minX, rotated.x); maxX = Mathf.Max(maxX, rotated.x);
                minZ = Mathf.Min(minZ, rotated.y); maxZ = Mathf.Max(maxZ, rotated.y);
            }
            foreach (Vector2[] corners in tileCorners)
            {
                foreach (Vector2 corner in corners)
                {
                    minX = Mathf.Min(minX, corner.x); maxX = Mathf.Max(maxX, corner.x);
                    minZ = Mathf.Min(minZ, corner.y); maxZ = Mathf.Max(maxZ, corner.y);
                }
            }

            if (minX > maxX)
            {
                Vector2 rotatedCenter = RotateFlat(area.Center, yawCos, yawSin);
                minX = maxX = rotatedCenter.x;
                minZ = maxZ = rotatedCenter.y;
            }

            const int canvasWidth = 1100;
            const int canvasHeight = 540;
            float scale = Mathf.Min(
                (canvasWidth - 60) / Mathf.Max(1f, maxX - minX),
                (canvasHeight - 60) / Mathf.Max(1f, maxZ - minZ));

            float worldCenterX = (minX + maxX) / 2f;
            float worldCenterZ = (minZ + maxZ) / 2f;

            container.Add(new CuiLabel
            {
                Text = { Text = view.Floor < 0 ? "ALL FLOORS" : "FLOOR " + (view.Floor + 1), FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleRight, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-260 -34", OffsetMax = "-100 -8" }
            }, card);

            int buttonX = 12;
            AddCircuitButton(container, card, ref buttonX, "ALL", "gridspawn.circuit floor -1", view.Floor < 0);
            foreach (int floor in floors)
                AddCircuitButton(container, card, ref buttonX, (floor + 1).ToString(), "gridspawn.circuit floor " + floor, view.Floor == floor);

            AddCircuitButton(container, card, ref buttonX, "OVERLAY " + (view.Overlay ? "ON" : "OFF"), "gridspawn.circuit overlay", view.Overlay);

            string canvas = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.5" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = (-canvasWidth / 2) + " 12", OffsetMax = (canvasWidth / 2) + " " + (12 + canvasHeight) }
            }, card);

            // The footprint is drawn as the UNION of all tile polygons: rasterized onto a
            // fine grid, then merged into horizontal strips. Overlapping translucent panels
            // (one per tile) showed double-bright intersections and stray outlines whenever
            // pieces were rotated relative to each other; a union renders as one flat shape.
            if (tileCorners.Count > 0)
            {
                float rasterStep = Mathf.Max(0.375f, Mathf.Max(maxX - minX, maxZ - minZ) / 150f);
                int cellsX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / rasterStep));
                int cellsZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / rasterStep));
                bool[,] covered = new bool[cellsX, cellsZ];

                foreach (Vector2[] corners in tileCorners)
                {
                    float tMinX = float.MaxValue, tMaxX = float.MinValue, tMinZ = float.MaxValue, tMaxZ = float.MinValue;
                    foreach (Vector2 corner in corners)
                    {
                        tMinX = Mathf.Min(tMinX, corner.x); tMaxX = Mathf.Max(tMaxX, corner.x);
                        tMinZ = Mathf.Min(tMinZ, corner.y); tMaxZ = Mathf.Max(tMaxZ, corner.y);
                    }

                    int cx0 = Mathf.Clamp(Mathf.FloorToInt((tMinX - minX) / rasterStep), 0, cellsX - 1);
                    int cx1 = Mathf.Clamp(Mathf.CeilToInt((tMaxX - minX) / rasterStep), 0, cellsX - 1);
                    int cz0 = Mathf.Clamp(Mathf.FloorToInt((tMinZ - minZ) / rasterStep), 0, cellsZ - 1);
                    int cz1 = Mathf.Clamp(Mathf.CeilToInt((tMaxZ - minZ) / rasterStep), 0, cellsZ - 1);

                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            if (covered[cx, cz])
                                continue;

                            Vector2 center = new Vector2(minX + ((cx + 0.5f) * rasterStep), minZ + ((cz + 0.5f) * rasterStep));
                            if (PointInPolygon(center, corners))
                                covered[cx, cz] = true;
                        }
                    }
                }

                for (int cz = 0; cz < cellsZ; cz++)
                {
                    int runStart = -1;
                    for (int cx = 0; cx <= cellsX; cx++)
                    {
                        bool on = cx < cellsX && covered[cx, cz];
                        if (on && runStart < 0)
                            runStart = cx;

                        if (!on && runStart >= 0)
                        {
                            int px0 = CanvasX(minX + (runStart * rasterStep), worldCenterX, scale, canvasWidth);
                            int px1 = CanvasX(minX + (cx * rasterStep), worldCenterX, scale, canvasWidth);
                            int py0 = CanvasY(minZ + (cz * rasterStep), worldCenterZ, scale, canvasHeight);
                            int py1 = CanvasY(minZ + ((cz + 1) * rasterStep), worldCenterZ, scale, canvasHeight);

                            container.Add(new CuiPanel
                            {
                                Image = { Color = "1 1 1 0.1" },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = px0 + " " + py0, OffsetMax = px1 + " " + py1 }
                            }, canvas);

                            runStart = -1;
                        }
                    }
                }
            }

            HashSet<IOEntity> visibleSet = new HashSet<IOEntity>(visible);
            foreach (IOEntity io in visible)
            {
                if (io.outputs == null)
                    continue;

                for (int slot = 0; slot < io.outputs.Length; slot++)
                {
                    IOEntity target = io.outputs[slot].connectedTo.Get();
                    if (target == null || !visibleSet.Contains(target))
                        continue;

                    int watts = io.GetPassthroughAmount(slot);
                    string wireRgb = WireColourRgb((int)io.outputs[slot].wireColour);
                    string color = wireRgb + (watts > 0 ? " 0.85" : " 0.3");

                    Vector2 rotatedFrom = RotateFlat(io.transform.position, yawCos, yawSin);
                    Vector2 rotatedTo = RotateFlat(target.transform.position, yawCos, yawSin);
                    int ax = CanvasX(rotatedFrom.x, worldCenterX, scale, canvasWidth);
                    int ay = CanvasY(rotatedFrom.y, worldCenterZ, scale, canvasHeight);
                    int bx = CanvasX(rotatedTo.x, worldCenterX, scale, canvasWidth);
                    int by = CanvasY(rotatedTo.y, worldCenterZ, scale, canvasHeight);

                    int labelX = bx;
                    int labelY = ay;

                    // Follow the actual wire routing: linePoints hold the bend positions of
                    // the laid wire. They can live on the output slot or on the far input
                    // slot, and are stored in the owning entity's LOCAL space (the client
                    // renders them with TransformPoint, rotation included) or world space -
                    // pick whichever interpretation puts the first point near its owner.
                    Vector3[] linePoints = io.outputs[slot].linePoints;
                    IOEntity routeOwner = io;

                    if ((linePoints == null || linePoints.Length < 2) && target.inputs != null)
                    {
                        int inputSlot = io.outputs[slot].connectedToSlot;
                        if (inputSlot >= 0 && inputSlot < target.inputs.Length)
                        {
                            Vector3[] alternative = target.inputs[inputSlot].linePoints;
                            if (alternative != null && alternative.Length >= 2)
                            {
                                linePoints = alternative;
                                routeOwner = target;
                            }
                        }
                    }

                    bool useRouting = linePoints != null && linePoints.Length >= 2;
                    bool pointsAreLocal = false;

                    if (useRouting)
                    {
                        // A laid wire starts or ends at the slot handle of its owner, so the
                        // right coordinate space is whichever puts one END of the points near
                        // the owner - point order depends on which end the wire was laid from.
                        Vector3 ownerPosition = routeOwner.transform.position;
                        Vector3 first = linePoints[0];
                        Vector3 last = linePoints[linePoints.Length - 1];

                        float worldEnd = Mathf.Min(Vector3.Distance(first, ownerPosition), Vector3.Distance(last, ownerPosition));
                        float localEnd = Mathf.Min(
                            Vector3.Distance(routeOwner.transform.TransformPoint(first), ownerPosition),
                            Vector3.Distance(routeOwner.transform.TransformPoint(last), ownerPosition));

                        if (localEnd <= worldEnd && localEnd < 10f)
                            pointsAreLocal = true;
                        else if (worldEnd < 10f)
                            pointsAreLocal = false;
                        else
                            useRouting = false;
                    }

                    if (useRouting)
                    {
                        List<Vector3> routePoints = new List<Vector3>(linePoints.Length);
                        for (int p = 0; p < linePoints.Length; p++)
                            routePoints.Add(pointsAreLocal ? routeOwner.transform.TransformPoint(linePoints[p]) : linePoints[p]);

                        // Wires can be laid from either end - walk the points starting from
                        // whichever end sits at the SOURCE component, otherwise the trace
                        // doubles back across the diagram and reads as a second wire.
                        if (Vector3.Distance(routePoints[0], io.transform.position) > Vector3.Distance(routePoints[routePoints.Count - 1], io.transform.position))
                            routePoints.Reverse();

                        // Climbing a wall stacks several bends on nearly the same ground
                        // position; in this top-down view they scribble little loops, so
                        // collapse bends that barely move horizontally.
                        for (int p = routePoints.Count - 1; p > 0; p--)
                        {
                            float dx = routePoints[p].x - routePoints[p - 1].x;
                            float dz = routePoints[p].z - routePoints[p - 1].z;
                            if ((dx * dx) + (dz * dz) < 1.44f)
                                routePoints.RemoveAt(p);
                        }

                        int previousX = ax;
                        int previousY = ay;

                        foreach (Vector3 point in routePoints)
                        {
                            Vector2 rotatedPoint = RotateFlat(point, yawCos, yawSin);

                            int cx = Mathf.Clamp(CanvasX(rotatedPoint.x, worldCenterX, scale, canvasWidth), 8, canvasWidth - 8);
                            int cy = Mathf.Clamp(CanvasY(rotatedPoint.y, worldCenterZ, scale, canvasHeight), 8, canvasHeight - 8);

                            // Micro-moves come from slack in the laid wire; skipping them
                            // keeps the trace from wobbling around its own line.
                            if (Mathf.Abs(cx - previousX) < 6 && Mathf.Abs(cy - previousY) < 6)
                                continue;

                            AddWireTrace(container, canvas, previousX, previousY, cx, cy, color);
                            previousX = cx;
                            previousY = cy;
                        }

                        AddWireTrace(container, canvas, previousX, previousY, bx, by, color);

                        Vector2 rotatedMiddle = RotateFlat(routePoints[routePoints.Count / 2], yawCos, yawSin);
                        labelX = Mathf.Clamp(CanvasX(rotatedMiddle.x, worldCenterX, scale, canvasWidth), 20, canvasWidth - 20);
                        labelY = Mathf.Clamp(CanvasY(rotatedMiddle.y, worldCenterZ, scale, canvasHeight), 10, canvasHeight - 10);
                    }
                    else
                    {
                        AddWireTrace(container, canvas, ax, ay, bx, by, color);
                    }

                    if (watts > 0)
                    {
                        container.Add(new CuiLabel
                        {
                            Text = { Text = watts.ToString(), FontSize = 9, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = wireRgb + " 1" },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = (labelX - 16) + " " + (labelY - 8), OffsetMax = (labelX + 16) + " " + (labelY + 8) }
                        }, canvas);
                    }
                }
            }

            foreach (IOEntity io in visible)
            {
                Vector2 rotatedNode = RotateFlat(io.transform.position, yawCos, yawSin);
                int px = CanvasX(rotatedNode.x, worldCenterX, scale, canvasWidth);
                int py = CanvasY(rotatedNode.y, worldCenterZ, scale, canvasHeight);

                ItemDefinition itemDefinition = io.pickup.itemTarget != null ? io.pickup.itemTarget : io.repair.itemTarget;
                string png = itemDefinition != null ? GetItemImage(itemDefinition) : null;

                if (!string.IsNullOrEmpty(png))
                {
                    container.Add(new CuiElement
                    {
                        Parent = canvas,
                        Components =
                        {
                            new CuiRawImageComponent { Png = png },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = (px - 12) + " " + (py - 12), OffsetMax = (px + 12) + " " + (py + 12) }
                        }
                    });
                }
                else if (itemDefinition != null)
                {
                    // The client renders the item icon from its own bundles; works for every item.
                    container.Add(new CuiElement
                    {
                        Parent = canvas,
                        Components =
                        {
                            new CuiImageComponent { ItemId = itemDefinition.itemid },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = (px - 12) + " " + (py - 12), OffsetMax = (px + 12) + " " + (py + 12) }
                        }
                    });
                }
                else
                {
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.8 0.8 0.8 0.7" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = (px - 5) + " " + (py - 5), OffsetMax = (px + 5) + " " + (py + 5) }
                    }, canvas);
                }

                int energy = io.GetCurrentEnergy();
                if (energy > 0)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = energy + "rW", FontSize = 8, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.8" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = (px - 20) + " " + (py - 26), OffsetMax = (px + 20) + " " + (py - 12) }
                    }, canvas);
                }
            }

            AddCircuitBackButton(container, card);
        }

        // Drawn last on the circuit page so no label or panel can sit above it and
        // swallow its clicks.
        private void AddCircuitBackButton(CuiElementContainer container, string card)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "0.16 0.16 0.19 1", Command = "gridspawn.menu tab features" },
                Text = { Text = "BACK", FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-92 -34", OffsetMax = "-12 -8" }
            }, card);
        }

        private void AddCircuitButton(CuiElementContainer container, string parent, ref int x, string label, string command, bool active)
        {
            int width = 34 + (label.Length * 5);
            container.Add(new CuiButton
            {
                Button = { Command = command, Color = active ? "0.29 0.41 1 0.8" : "1 1 1 0.12" },
                Text = { Text = label, FontSize = 10, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = x + " -62", OffsetMax = (x + width) + " -40" }
            }, parent);

            x += width + 6;
        }

        // Rotates a world position around the vertical axis onto a flat 2D plane, used to align
        // the circuit diagram with the building's orientation.
        private Vector2 RotateFlat(Vector3 world, float cos, float sin)
        {
            return new Vector2((world.x * cos) - (world.z * sin), (world.x * sin) + (world.z * cos));
        }

        // Even-odd ray cast; polygon corners must be in path order.
        private bool PointInPolygon(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > point.y) != (polygon[j].y > point.y) &&
                    point.x < (((polygon[j].x - polygon[i].x) * (point.y - polygon[i].y)) / (polygon[j].y - polygon[i].y)) + polygon[i].x)
                    inside = !inside;
            }

            return inside;
        }

        private int CanvasX(float worldX, float centerX, float scale, int canvasWidth)
        {
            return Mathf.RoundToInt((canvasWidth / 2f) + ((worldX - centerX) * scale));
        }

        private int CanvasY(float worldZ, float centerZ, float scale, int canvasHeight)
        {
            return Mathf.RoundToInt((canvasHeight / 2f) + ((worldZ - centerZ) * scale));
        }

        // In-game wire colours mapped to CUI colours.
        private string WireColourRgb(int colour)
        {
            switch (colour)
            {
                case 1: return "0.9 0.25 0.2";    // red
                case 2: return "0.35 0.85 0.3";   // green
                case 3: return "0.25 0.55 0.9";   // blue
                case 4: return "0.95 0.85 0.3";   // yellow
                case 5: return "0.95 0.45 0.85";  // pink
                case 6: return "0.6 0.35 0.85";   // purple
                case 7: return "0.95 0.55 0.2";   // orange
                case 8: return "0.95 0.95 0.95";  // white
                case 9: return "0.35 0.85 0.9";   // light blue
                default: return "0.55 0.78 0.25"; // default wire
            }
        }

        // Draws one wire run between two bend points, schematic style: nearly straight
        // runs become one segment, anything else a clean elbow along the dominant axis.
        // Pixel staircases read as jagged noise, so diagonals are not subdivided.
        private void AddWireTrace(CuiElementContainer container, string canvas, int x1, int y1, int x2, int y2, string color)
        {
            if (Mathf.Abs(x2 - x1) <= 6 || Mathf.Abs(y2 - y1) <= 6)
            {
                AddWireSegment(container, canvas, x1, y1, x2, y2, color);
                return;
            }

            if (Mathf.Abs(x2 - x1) >= Mathf.Abs(y2 - y1))
            {
                AddWireSegment(container, canvas, x1, y1, x2, y1, color);
                AddWireSegment(container, canvas, x2, y1, x2, y2, color);
            }
            else
            {
                AddWireSegment(container, canvas, x1, y1, x1, y2, color);
                AddWireSegment(container, canvas, x1, y2, x2, y2, color);
            }
        }

        private void AddWireSegment(CuiElementContainer container, string canvas, int x1, int y1, int x2, int y2, string color)
        {
            if (x1 == x2 && y1 == y2)
                return;

            int minX = Mathf.Min(x1, x2) - 1;
            int maxX = Mathf.Max(x1, x2) + 1;
            int minY = Mathf.Min(y1, y2) - 1;
            int maxY = Mathf.Max(y1, y2) + 1;

            container.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = minX + " " + minY, OffsetMax = maxX + " " + maxY }
            }, canvas);
        }

        #endregion

        // Vanilla decay brackets: the first blocks pay a small fraction of their build cost per
        // day, later blocks pay progressively more.
        private float UpkeepFraction(int blockIndex)
        {
            if (blockIndex < ConVar.Decay.bracket_0_blockcount)
                return ConVar.Decay.bracket_0_costfraction;

            if (blockIndex < ConVar.Decay.bracket_0_blockcount + ConVar.Decay.bracket_1_blockcount)
                return ConVar.Decay.bracket_1_costfraction;

            if (blockIndex < ConVar.Decay.bracket_0_blockcount + ConVar.Decay.bracket_1_blockcount + ConVar.Decay.bracket_2_blockcount)
                return ConVar.Decay.bracket_2_costfraction;

            return ConVar.Decay.bracket_3_costfraction;
        }

        private void DrawLinesPanel(BasePlayer player, string uiName, string title, List<KeyValuePair<ItemDefinition, string>> lines, string anchor, string offset, int width, string titleSuffix)
        {
            CuiHelper.DestroyUi(player, uiName);

            int height = 42 + (lines.Count * 18);
            string[] offsetParts = offset.Split(' ');
            int offsetX = int.Parse(offsetParts[0]);

            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.063 0.9" },
                RectTransform = { AnchorMin = anchor, AnchorMax = anchor, OffsetMin = offsetX + " " + (-height / 2), OffsetMax = (offsetX + width) + " " + (height / 2) }
            }, "Hud", uiName);

            container.Add(new CuiPanel
            {
                Image = { Color = "0.086 0.086 0.09 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -26", OffsetMax = "0 0" }
            }, uiName);

            container.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 -26", OffsetMax = "-8 0" }
            }, uiName);

            if (!string.IsNullOrEmpty(titleSuffix))
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = titleSuffix, FontSize = 9, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleRight, Color = "1 1 1 0.5" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 -26", OffsetMax = "-8 0" }
                }, uiName);
            }

            container.Add(new CuiPanel
            {
                Image = { Color = "0.29 0.41 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 -29", OffsetMax = "-8 -28" }
            }, uiName);

            for (int i = 0; i < lines.Count; i++)
            {
                int top = -35 - (i * 18);
                int bottom = top - 16;
                bool hasIcon = lines[i].Key != null;

                if (hasIcon)
                    AddItemIcon(container, uiName, lines[i].Key, "0 1", "8 " + bottom, "24 " + top);

                container.Add(new CuiLabel
                {
                    Text = { Text = lines[i].Value, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = (hasIcon ? 28 : 8) + " " + bottom, OffsetMax = "-8 " + top }
                }, uiName);
            }

            CuiHelper.AddUi(player, container);
        }

        #endregion

        // Nothing is ever used up: placed deployables stay in the inventory (in the same slot),
        // and fuel, ammo and consumables do not deplete. Returning 0 overrides the amount
        // consumed by every stack-consuming action on the server.
        private object OnItemUse(Item item, int amountToUse)
        {
            return 0;
        }

        // Items never lose durability either, so weapons and tools cannot break.
        private object OnLoseCondition(Item item, float amount)
        {
            if (item != null && item.info.condition.enabled)
                item.condition = item.info.condition.max;

            return true;
        }

        // A freshly placed tool cupboard comes stocked with 1M of each upkeep resource.
        private void OnItemDeployed(Deployer deployer, BaseEntity entity)
        {
            BuildingPrivlidge cupboard = entity as BuildingPrivlidge;
            if (cupboard == null)
                return;

            BasePlayer player = deployer != null ? deployer.GetOwnerPlayer() : null;
            if (player == null || !HasNoClipPermission(player))
                return;

            StockCupboard(cupboard);
        }

        // Belt and braces with OnItemDeployed: hook signatures drift between Rust builds,
        // but a cupboard always spawns. Stocks one tick later so the deploy has finished
        // and the owner id is set.
        private void OnEntitySpawned(BuildingPrivlidge cupboard)
        {
            if (cupboard == null)
                return;

            NextTick(() =>
            {
                if (cupboard == null || cupboard.IsDestroyed)
                    return;

                BasePlayer owner = BasePlayer.FindByID(cupboard.OwnerID);
                if (owner == null || !HasNoClipPermission(owner))
                    return;

                StockCupboard(cupboard);
            });
        }

        // When a player-built container dies (raid sim, MLRS, explosions), its contents
        // vanish with it instead of dropping as a lootable bag - stocked cupboards would
        // otherwise litter plots with million-resource loot containers.
        private void OnEntityDeath(StorageContainer container, HitInfo info)
        {
            if (container == null || container.inventory == null || container.OwnerID == 0)
                return;

            container.inventory.Clear();
        }

        private void StockCupboard(BuildingPrivlidge cupboard)
        {
            // Only ever fills an empty cupboard, so the deploy hook, the spawn hook and
            // base loading cannot double-stock it.
            if (cupboard == null || cupboard.inventory == null || cupboard.inventory.itemList.Count > 0)
                return;

            string[] resources = { "wood", "stones", "metal.fragments", "metal.refined" };
            foreach (string shortname in resources)
            {
                Item item = ItemManager.CreateByName(shortname, 1000000);
                if (item != null && !item.MoveToContainer(cupboard.inventory))
                    item.Remove();
            }
        }

        // Dropped items despawn immediately instead of littering the plots. Killed on the next
        // tick so the drop routine finishes with a valid entity first.
        private void OnItemDropped(Item item, BaseEntity entity)
        {
            NextTick(() =>
            {
                if (entity != null && !entity.IsDestroyed)
                    entity.Kill();
            });
        }

        // Suppress the "[SERVER] gave ..." chat notices produced by F1 item spawning.
        private object OnServerMessage(string message, string name)
        {
            if (message != null && message.Contains("gave") && name == "SERVER")
                return true;

            return null;
        }

        private object OnPlayerActionBroadcast(BasePlayer player, string action)
        {
            if (action != null && action.Contains("gave"))
                return true;

            return null;
        }

        private object OnPlayerRespawn(BasePlayer player, BasePlayer.SpawnPoint spawnPoint)
        {
            if (player == null || !EnsureGridReady())
                return null;

            AssignedArea area = GetOrCreateArea(player);
            return new BasePlayer.SpawnPoint
            {
                pos = area.SpawnPosition,
                rot = Quaternion.identity
            };
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            NextTick(() =>
            {
                AssignAndTeleport(player);
                GiveBuildLoadout(player);
            });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            NextTick(() =>
            {
                AssignAndTeleport(player);
                GiveBuildLoadout(player);
            });
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || player.IsDead())
                return;

            // Middle mouse toggles the menu (FIRE_THIRD is the middle mouse button).
            if (input.WasJustPressed(BUTTON.FIRE_THIRD) && HasNoClipPermission(player))
            {
                if (menuOpen.Contains(player.userID))
                    CloseMenu(player);
                else
                    ShowMenuUi(player);
            }

            if (IsHoldingHammer(player))
            {
                if (input.WasJustPressed(BUTTON.RELOAD))
                    TryEntKill(player);

                if (input.WasJustPressed(BUTTON.FIRE_PRIMARY))
                {
                    bool upgrade = input.IsDown(BUTTON.SPRINT);
                    bool downgrade = input.IsDown(BUTTON.DUCK);
                    if (upgrade != downgrade)
                        TryChangeTier(player, upgrade);
                    else if (!upgrade)
                        TrySetSymmetryByHit(player);
                }
            }
        }

        // A plain hammer hit on a foundation or floor sets the symmetry point there - the
        // aimed equivalent of the menu's SET SYMMETRY. Hitting any other piece does nothing,
        // so normal hammering stays harmless. The hit point (not the block center) is used,
        // so hitting where 4 squares meet detects the 2x2 center, like standing there would.
        private void TrySetSymmetryByHit(BasePlayer player)
        {
            if (!HasNoClipPermission(player))
                return;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, EntKillDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;

            BuildingBlock block = hit.GetEntity() as BuildingBlock;
            if (block == null || block.IsDestroyed)
                return;

            SetSymmetryAt(player, block, hit.point, area);
        }

        private void SetSymmetryAt(BasePlayer player, BuildingBlock block, Vector3 point, AssignedArea area)
        {
            string prefab = block.ShortPrefabName;
            if (prefab != "foundation" && prefab != "floor" && prefab != "foundation.triangle" && prefab != "floor.triangle")
                return;

            if (!IsInsideArea(block.transform.position, area))
                return;

            if (HammerActionDebounced(player))
                return;

            Plugin simpleSymmetry = plugins.Find("SimpleSymmetry");
            if (simpleSymmetry == null || !simpleSymmetry.IsLoaded)
                return;

            simpleSymmetry.Call("SetSymmetryAtPosition", player, point);
        }

        // Hammer + R deletes what the player is aiming at, like the admin "ent kill", but only
        // inside their own area and never on players.
        private void TryEntKill(BasePlayer player)
        {
            if (!HasNoClipPermission(player))
                return;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return;

            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, EntKillDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;

            BaseEntity entity = hit.GetEntity();
            if (entity == null || entity.IsDestroyed || entity is BasePlayer)
                return;

            if (!IsInsideArea(entity.transform.position, area))
            {
                player.SendConsoleCommand("gametip.showtoast", 1, "You can only delete things inside your own area.", string.Empty, false);
                return;
            }

            // Mirror the delete when the player has symmetry enabled - killing directly
            // skips the demolish hooks SimpleSymmetry listens to, so hand it the entity
            // through its remover-tool entry point (it no-ops with symmetry off).
            Plugin simpleSymmetry = plugins.Find("SimpleSymmetry");
            if (simpleSymmetry != null && simpleSymmetry.IsLoaded)
                simpleSymmetry.Call("OnNormalRemovedEntity", player, entity);

            RecordDelete(player, entity);

            // SimpleSymmetry blocks killing cupboards that still hold items; empty it first so
            // auto-stocked cupboards can be deleted.
            StorageContainer storage = entity as StorageContainer;
            if (storage != null && storage.inventory != null)
                storage.inventory.Clear();

            entity.Kill();
        }

        #region Undo / redo

        private BuildAction SnapshotEntity(BaseEntity entity)
        {
            BuildAction action = new BuildAction
            {
                Prefab = entity.PrefabName,
                Position = entity.transform.position,
                Rotation = entity.transform.rotation,
                Skin = entity.skinID
            };

            BuildingBlock block = entity as BuildingBlock;
            action.Grade = block != null ? (int)block.grade : -1;
            return action;
        }

        private List<BuildAction> UndoStack(ulong id)
        {
            List<BuildAction> stack;
            if (!undoStacks.TryGetValue(id, out stack))
                undoStacks[id] = stack = new List<BuildAction>();
            return stack;
        }

        private List<BuildAction> RedoStack(ulong id)
        {
            List<BuildAction> stack;
            if (!redoStacks.TryGetValue(id, out stack))
                redoStacks[id] = stack = new List<BuildAction>();
            return stack;
        }

        // A fresh player action clears the redo history, like every editor.
        private void PushUndo(BasePlayer player, BuildAction action)
        {
            List<BuildAction> stack = UndoStack(player.userID);
            stack.Add(action);
            if (stack.Count > MaxUndoHistory)
                stack.RemoveAt(0);

            RedoStack(player.userID).Clear();
        }

        private void RecordPlace(BasePlayer player, BaseEntity entity)
        {
            BuildAction action = SnapshotEntity(entity);
            action.Kind = "place";
            action.NetId = entity.net.ID.Value;
            PushUndo(player, action);
        }

        private void RecordDelete(BasePlayer player, BaseEntity entity)
        {
            BuildAction action = SnapshotEntity(entity);
            action.Kind = "delete";
            PushUndo(player, action);
        }

        private void RecordGrade(BasePlayer player, BuildingBlock block, int oldGrade)
        {
            PushUndo(player, new BuildAction { Kind = "grade", NetId = block.net.ID.Value, OldGrade = oldGrade });
        }

        private BaseEntity FindByNetId(ulong id)
        {
            return BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
        }

        private BaseEntity RecreateEntity(BasePlayer player, BuildAction action)
        {
            BaseEntity entity = GameManager.server.CreateEntity(action.Prefab, action.Position, action.Rotation);
            if (entity == null)
                return null;

            entity.OwnerID = player.userID;
            entity.skinID = action.Skin;
            entity.Spawn();

            BuildingBlock block = entity as BuildingBlock;
            if (block != null && action.Grade >= 0)
            {
                block.SetGrade((BuildingGrade.Enum)action.Grade);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
                block.UpdateSkin();
            }

            return entity;
        }

        // Reverses one action and returns the action that reverses THAT (for the opposite
        // stack), so undo and redo share this logic. Returns null if it can't be applied.
        private BuildAction ReverseAction(BasePlayer player, BuildAction action)
        {
            switch (action.Kind)
            {
                case "place":
                {
                    BaseEntity entity = FindByNetId(action.NetId);
                    if (entity == null || entity.IsDestroyed)
                        return null;

                    BuildAction snap = SnapshotEntity(entity);
                    snap.Kind = "delete";

                    StorageContainer storage = entity as StorageContainer;
                    if (storage != null && storage.inventory != null)
                        storage.inventory.Clear();
                    entity.Kill();
                    return snap;
                }

                case "delete":
                {
                    BaseEntity entity = RecreateEntity(player, action);
                    if (entity == null)
                        return null;

                    return new BuildAction { Kind = "place", NetId = entity.net.ID.Value };
                }

                case "grade":
                {
                    BuildingBlock block = FindByNetId(action.NetId) as BuildingBlock;
                    if (block == null || block.IsDestroyed)
                        return null;

                    int current = (int)block.grade;
                    block.SetGrade((BuildingGrade.Enum)Mathf.Clamp(action.OldGrade, 0, (int)BuildingGrade.Enum.TopTier));
                    block.SetHealthToMax();
                    block.SendNetworkUpdate();
                    block.UpdateSkin();
                    block.GetBuilding()?.Dirty();
                    return new BuildAction { Kind = "grade", NetId = action.NetId, OldGrade = current };
                }
            }

            return null;
        }

        private void PerformUndo(BasePlayer player)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            List<BuildAction> stack = UndoStack(player.userID);
            while (stack.Count > 0)
            {
                BuildAction action = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);

                BuildAction inverse = ReverseAction(player, action);
                if (inverse != null)
                {
                    RedoStack(player.userID).Add(inverse);
                    player.SendConsoleCommand("gametip.showtoast", 0, "Undo", string.Empty, false);
                    return;
                }
            }

            player.SendConsoleCommand("gametip.showtoast", 0, "Nothing to undo.", string.Empty, false);
        }

        private void PerformRedo(BasePlayer player)
        {
            if (player == null || !HasNoClipPermission(player))
                return;

            List<BuildAction> stack = RedoStack(player.userID);
            while (stack.Count > 0)
            {
                BuildAction action = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);

                BuildAction inverse = ReverseAction(player, action);
                if (inverse != null)
                {
                    UndoStack(player.userID).Add(inverse);
                    player.SendConsoleCommand("gametip.showtoast", 0, "Redo", string.Empty, false);
                    return;
                }
            }

            player.SendConsoleCommand("gametip.showtoast", 0, "Nothing to redo.", string.Empty, false);
        }

        [ChatCommand("undo")]
        private void UndoChatCommand(BasePlayer player, string command, string[] args) => PerformUndo(player);

        [ChatCommand("redo")]
        private void RedoChatCommand(BasePlayer player, string command, string[] args) => PerformRedo(player);

        [ConsoleCommand("gridspawn.undo")]
        private void UndoConsoleCommand(ConsoleSystem.Arg arg) => PerformUndo(arg.Player());

        [ConsoleCommand("gridspawn.redo")]
        private void RedoConsoleCommand(ConsoleSystem.Arg arg) => PerformRedo(arg.Player());

        #endregion

        [ChatCommand("noclip")]
        private void NoClipChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || player.IsDead())
                return;

            ToggleFlight(player);
        }

        // The client rejects server-pushed binds, so players who want a key for this bind it
        // themselves once (F1 console: bind f hammernoclip).
        [ConsoleCommand("hammernoclip")]
        private void HammerNoClipCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || player.IsDead())
                return;

            if (!IsHoldingHammer(player))
                return;

            ToggleFlight(player);
        }

        private void ToggleFlight(BasePlayer player)
        {
            if (!HasNoClipPermission(player))
            {
                player.ChatMessage("You don't have permission to use noclip.");
                return;
            }

            if (player.IsDead() || player.net == null || player.net.connection == null)
                return;

            player.SendConsoleCommand("noclip");
        }

        private bool HasNoClipPermission(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, NoClipPermission);
        }

        // Players may roam the whole map, but placements are rejected outside their own circle.
        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            BasePlayer player = planner != null ? planner.GetOwnerPlayer() : null;
            if (player == null)
                return null;

            AssignedArea area;
            if (!assignedAreas.TryGetValue(player.userID, out area))
                return null;

            Vector3 position = target.entity != null && target.socket != null ? target.GetWorldPosition() : target.position;
            if (IsInsideArea(position, area))
                return null;

            // Red toast banner, same style as the vanilla "NOT IN TERRAIN" placement error.
            player.SendConsoleCommand("gametip.showtoast", 1, "You can only build inside your own area.", string.Empty, false);
            return false;
        }

        // Ingredient availability and consumption are handled by the ItemRetriever and
        // VirtualItems plugins (players virtually own 1B of each resource, which never
        // depletes and shows in the crafting UI). This hook makes every craft instant: the
        // queued task is cancelled and the crafted items are handed over immediately.
        // Adapted from the Instant Craft plugin (Vlad-0003 / Orange / rostov114).
        private object OnItemCraft(ItemCraftTask task, BasePlayer owner)
        {
            if (task == null || owner == null || task.cancelled || task.blueprint == null)
                return null;

            List<int> stacks = GetStacks(task.blueprint.targetItem, task.amount * task.blueprint.amountToCreate);
            int slots = FreeSlots(owner);

            if (slots < stacks.Count)
            {
                task.cancelled = true;
                owner.ChatMessage("You don't have enough space to craft! Need " + stacks.Count + " slots, have " + slots + ".");
                GiveRefund(task, owner);
                Interface.CallHook("OnItemCraftCancelled", task, owner.inventory.crafting);
                return false;
            }

            if (!GiveCraftedItems(task, owner, stacks))
                return null;

            return true;
        }

        private void GiveRefund(ItemCraftTask task, BasePlayer owner)
        {
            if (task.takenItems == null)
                return;

            foreach (Item item in task.takenItems)
                owner.inventory.GiveItem(item, null);
        }

        private bool GiveCraftedItems(ItemCraftTask task, BasePlayer owner, List<int> stacks)
        {
            ulong skin = ItemDefinition.FindSkin(task.blueprint.targetItem.itemid, task.skinID);
            int iteration = 0;

            foreach (int stack in stacks)
            {
                if (!GiveCraftedStack(task, owner, stack, skin) && iteration <= 0)
                    return false;

                iteration++;
            }

            task.cancelled = true;
            return true;
        }

        private bool GiveCraftedStack(ItemCraftTask task, BasePlayer owner, int amount, ulong skin)
        {
            Item item = null;
            try
            {
                item = ItemManager.CreateByItemID(task.blueprint.targetItem.itemid, amount, skin);
            }
            catch (Exception e)
            {
                PrintError("Exception creating item " + task.blueprint.targetItem.shortname + " x" + amount + ": " + e);
            }

            if (item == null)
                return false;

            if (item.hasCondition && task.conditionScale != 1f)
            {
                item.maxCondition *= task.conditionScale;
                item.condition = item.maxCondition;
            }

            item.OnVirginSpawn(owner);
            item.SetItemOwnership(owner, ItemOwnershipPhrases.CraftedPhrase);

            if (task.instanceData != null)
                item.instanceData = task.instanceData;

            Interface.CallHook("OnItemCraftFinished", task, item, owner.inventory.crafting);

            if (owner.inventory.GiveItem(item))
            {
                owner.Command("note.inv", item.info.itemid, amount);
                return true;
            }

            ItemContainer container = owner.inventory.crafting.containers.First();
            owner.Command("note.inv", item.info.itemid, item.amount);
            owner.Command("note.inv", item.info.itemid, -item.amount);
            item.Drop(container.dropPosition, container.dropVelocity, default(Quaternion));
            return true;
        }

        private int FreeSlots(BasePlayer player)
        {
            int slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
            int taken = player.inventory.containerMain.itemList.Count + player.inventory.containerBelt.itemList.Count;
            return slots - taken;
        }

        private List<int> GetStacks(ItemDefinition item, int amount)
        {
            List<int> list = new List<int>();
            int maxStack = item.stackable == 0 ? 1 : item.stackable;

            while (amount > maxStack)
            {
                amount -= maxStack;
                list.Add(maxStack);
            }

            list.Add(amount);
            return list;
        }

        private object CanAffordToPlace(BasePlayer player, Planner planner, Construction construction)
        {
            return true;
        }

        private object OnPayForPlacement(BasePlayer player, Planner planner, Construction construction)
        {
            return true;
        }

        private object CanAffordUpgrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade)
        {
            return true;
        }

        private object OnPayForUpgrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade)
        {
            return true;
        }

        private object CanAffordToRepair(BasePlayer player, BaseCombatEntity entity)
        {
            return true;
        }

        private object OnPayForRepair(BasePlayer player, BaseCombatEntity entity)
        {
            return true;
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            menuOpen.Remove(player.userID);
            undoStacks.Remove(player.userID);
            redoStacks.Remove(player.userID);
            awaitingPhoto.Remove(player.userID);

            AssignedArea area;
            if (assignedAreas.TryGetValue(player.userID, out area))
            {
                foreach (string cell in area.GridCells)
                    allocatedGridCells.Remove(cell);

                DestroyMapMarkers(area);
                DestroyDome(area);
            }

            ClearModeratorLite(player);
            assignedAreas.Remove(player.userID);
            noClipHintSent.Remove(player.userID);
            selectedGrades.Remove(player.userID);
            raidCounts.Remove(player.userID);
            circuitViews.Remove(player.userID);
        }

        private void AssignAndTeleport(BasePlayer player)
        {
            if (player == null || !EnsureGridReady() || player.IsDead())
                return;

            AssignedArea area = GetOrCreateArea(player);
            TeleportInsideArea(player, area, area.SpawnPosition);
        }

        private void GiveBuildLoadout(BasePlayer player)
        {
            if (player == null || player.IsDead() || player.inventory == null)
                return;

            GrantModeratorLite(player);
            RemoveItems(player, RockShortName);
            RemoveItems(player, TorchShortName);

            EnsureItemInBelt(player, BuildingPlanShortName);
            EnsureItemInBelt(player, CupboardShortName);
            Item hammer = EnsureItemInBelt(player, HammerShortName);

            if (hammer != null && hammer.parent == player.inventory.containerBelt)
                player.UpdateActiveItem(hammer.uid);

            SendNoClipInstructions(player);
            player.inventory.ServerUpdate(0f);
            player.SendNetworkUpdateImmediate();
        }

        private void SendNoClipInstructions(BasePlayer player)
        {
            if (player == null || player.net == null || player.net.connection == null || !HasNoClipPermission(player))
                return;

            if (!noClipHintSent.Add(player.userID))
                return;

            player.ChatMessage("Welcome to Double Barrel's Sandbox!");
            player.ChatMessage("/menu (MM3) • discord.gg/doublebarrelrust");
        }

        private void OnUserPermissionGranted(string id, string permName)
        {
            if (permName != NoClipPermission)
                return;

            BasePlayer player = BasePlayer.Find(id);
            if (player == null)
                return;

            GrantModeratorLite(player);
            SendNoClipInstructions(player);
        }

        private void OnUserPermissionRevoked(string id, string permName)
        {
            if (permName != NoClipPermission)
                return;

            BasePlayer player = BasePlayer.Find(id);
            if (player == null)
                return;

            if (player.IsFlying)
                player.Teleport(GetGroundedPosition(player.transform.position));

            ClearModeratorLite(player);
        }

        private Item EnsureItemInBelt(BasePlayer player, string shortName)
        {
            Item item = FindItem(player, shortName);
            if (item == null)
                item = ItemManager.CreateByName(shortName, 1);

            if (item == null)
                return null;

            if (item.parent == player.inventory.containerBelt)
                return item;

            if (!item.MoveToContainer(player.inventory.containerBelt, -1, true))
                player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);

            return item;
        }

        private Item FindItem(BasePlayer player, string shortName)
        {
            return GetPlayerItems(player).FirstOrDefault(item => item.info != null && item.info.shortname == shortName);
        }

        private void RemoveItems(BasePlayer player, string shortName)
        {
            foreach (Item item in GetPlayerItems(player).Where(item => item.info != null && item.info.shortname == shortName).ToList())
            {
                item.RemoveFromContainer();
                item.Remove();
            }
        }

        private IEnumerable<Item> GetPlayerItems(BasePlayer player)
        {
            if (player == null || player.inventory == null)
                yield break;

            foreach (Item item in GetContainerItems(player.inventory.containerMain))
                yield return item;

            foreach (Item item in GetContainerItems(player.inventory.containerBelt))
                yield return item;

            foreach (Item item in GetContainerItems(player.inventory.containerWear))
                yield return item;
        }

        private IEnumerable<Item> GetContainerItems(ItemContainer container)
        {
            if (container == null || container.itemList == null)
                yield break;

            foreach (Item item in container.itemList)
                yield return item;
        }

        private bool IsHoldingHammer(BasePlayer player)
        {
            Item activeItem = player.GetActiveItem();
            return activeItem != null && activeItem.info != null && activeItem.info.shortname == HammerShortName;
        }

        // Auth level 1 (moderator) plus the admin player flag unlocks the client's native noclip,
        // the F1 item menu, and makes the antihack ignore the player. OnServerCommand blocks every
        // admin console command except the item-give whitelist, so flying and spawning items for
        // themselves is all this actually grants. Real staff (auth level already above 0) are
        // left untouched.
        private void GrantModeratorLite(BasePlayer player)
        {
            if (player == null || player.net == null || player.net.connection == null || !HasNoClipPermission(player))
                return;

            if (player.net.connection.authLevel > 0 && !grantedModerators.Contains(player.userID))
                return;

            if (!grantedModerators.Add(player.userID))
                return;

            player.net.connection.authLevel = 1u;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
            // The developer flag unlocks the client-side sky/cinematic convars the
            // environment page pushes (atmosphere.*, cloud.*, env.admintime) - the admin
            // flag alone does not cover all of them.
            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, true);
            player.SendNetworkUpdateImmediate();
        }

        private void ClearModeratorLite(BasePlayer player)
        {
            if (player == null || !grantedModerators.Remove(player.userID))
                return;

            if (player.net != null && player.net.connection != null)
                player.net.connection.authLevel = 0u;

            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, false);
            player.SendNetworkUpdateImmediate();
        }

        private AssignedArea GetOrCreateArea(BasePlayer player)
        {
            AssignedArea area;
            if (assignedAreas.TryGetValue(player.userID, out area))
                return area;

            area = CreateRandomArea(true);
            assignedAreas[player.userID] = area;

            foreach (string cell in area.GridCells)
                allocatedGridCells.Add(cell);

            CreateMapMarkers(player, area);
            CreateDome(area);
            Puts(player.displayName + " was assigned build grid " + area.Label + ".");

            return area;
        }

        private AssignedArea CreateRandomArea(bool avoidAllocatedAreas)
        {
            AssignedArea fallback = null;

            for (int i = 0; i < MaxAreaSearchAttempts; i++)
            {
                AssignedArea area = CreateArea(UnityEngine.Random.Range(0, gridCount - 1), UnityEngine.Random.Range(0, gridCount - 1));

                if (fallback == null)
                    fallback = area;

                if (avoidAllocatedAreas && AreaOverlapsAllocatedCells(area))
                    continue;

                if (IsDryGround(area.SpawnPosition))
                    return area;
            }

            if (avoidAllocatedAreas)
                return CreateRandomArea(false);

            return fallback ?? CreateArea(0, 0);
        }

        private AssignedArea CreateArea(int column, int row)
        {
            column = Mathf.Clamp(column, 0, gridCount - 2);
            row = Mathf.Clamp(row, 0, gridCount - 2);

            Vector3 topLeft = PositionFromGridIntersection(column, row);
            Vector3 bottomRight = PositionFromGridIntersection(column + 2, row + 2);
            Vector3 center = PositionFromGridIntersection(column + 1, row + 1);
            center = GetGroundedPosition(center);

            return new AssignedArea
            {
                Key = column + ":" + row,
                Label = GridLabel(column) + row + "-" + GridLabel(column + 1) + (row + 1),
                GridCells = new List<string>
                {
                    CellKey(column, row),
                    CellKey(column + 1, row),
                    CellKey(column, row + 1),
                    CellKey(column + 1, row + 1)
                },
                MinX = Mathf.Min(topLeft.x, bottomRight.x),
                MaxX = Mathf.Max(topLeft.x, bottomRight.x),
                MinZ = Mathf.Min(topLeft.z, bottomRight.z),
                MaxZ = Mathf.Max(topLeft.z, bottomRight.z),
                Center = center,
                Radius = cellSize,
                SpawnPosition = center
            };
        }

        // Safety sweep: players who somehow have no plot yet (e.g. they were online before the
        // plugin loaded) get one assigned.
        private void EnsureAreasAssigned()
        {
            if (!EnsureGridReady())
                return;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping())
                    continue;

                if (!assignedAreas.ContainsKey(player.userID))
                    AssignAndTeleport(player);
            }
        }

        // The translucent grey dome everyone sees, exactly matching the buildable circle;
        // several layers make it more visible.
        private void CreateDome(AssignedArea area)
        {
            if (area == null || area.Spheres.Count > 0)
                return;

            for (int i = 0; i < DomeLayers; i++)
            {
                SphereEntity sphere = GameManager.server.CreateEntity(SpherePrefab, area.Center, Quaternion.identity) as SphereEntity;
                if (sphere == null)
                    continue;

                sphere.currentRadius = area.Radius * 2f;
                sphere.lerpRadius = area.Radius * 2f;
                sphere.lerpSpeed = 0f;
                sphere.enableSaving = false;
                sphere.Spawn();
                area.Spheres.Add(sphere);
            }
        }

        private void DestroyDome(AssignedArea area)
        {
            if (area == null)
                return;

            foreach (BaseEntity sphere in area.Spheres)
            {
                if (sphere != null && !sphere.IsDestroyed)
                    sphere.Kill();
            }

            area.Spheres.Clear();
        }

        // A green circle on the in-game map covering the plot, plus a center marker whose hover
        // text names the owner. Everyone can see every plot.
        private void CreateMapMarkers(BasePlayer player, AssignedArea area)
        {
            if (player == null || area == null || area.MapMarkers.Count > 0)
                return;

            MapMarkerGenericRadius circle = GameManager.server.CreateEntity(MapMarkerPrefab, area.SpawnPosition, Quaternion.identity) as MapMarkerGenericRadius;
            if (circle != null)
            {
                circle.alpha = 0.6f;
                circle.color1 = Color.green;
                circle.color2 = Color.white;
                circle.radius = MapMarkerRadiusValue(cellSize);
                circle.enableSaving = false;
                circle.Spawn();
                circle.SendUpdate();
                area.MapMarkers.Add(circle);
            }

            VendingMachineMapMarker label = GameManager.server.CreateEntity(VendingMarkerPrefab, area.SpawnPosition, Quaternion.identity) as VendingMachineMapMarker;
            if (label != null)
            {
                label.markerShopName = player.displayName + "'s area";
                label.enableSaving = false;
                label.Spawn();
                area.MapMarkers.Add(label);
            }
        }

        // Radius markers scale with world size (map grids are a fixed 150m); this converts a
        // size in world meters to the marker's radius units.
        private float MapMarkerRadiusValue(float meters)
        {
            return meters * (Mathf.Sqrt(100f / 6f) / 2f) / (World.Size / 1000f) / 100f;
        }

        private void DestroyMapMarkers(AssignedArea area)
        {
            if (area == null)
                return;

            foreach (BaseEntity marker in area.MapMarkers)
            {
                if (marker != null && !marker.IsDestroyed)
                    marker.Kill();
            }

            area.MapMarkers.Clear();
        }

        private bool AreaOverlapsAllocatedCells(AssignedArea area)
        {
            foreach (string cell in area.GridCells)
            {
                if (allocatedGridCells.Contains(cell))
                    return true;
            }

            return false;
        }

        private string CellKey(int column, int row)
        {
            return column + ":" + row;
        }

        private void TeleportInsideArea(BasePlayer player, AssignedArea area, Vector3 position)
        {
            if (player == null || area == null)
                return;

            position = ClampToArea(position, area);
            position = GetGroundedPosition(position);

            player.Teleport(position);
            player.SendNetworkUpdateImmediate();
        }

        // The plot is a circle matching the green map marker, as wide as the old 2x2 grid area.
        private bool IsInsideArea(Vector3 position, AssignedArea area)
        {
            Vector2 offset = new Vector2(position.x - area.Center.x, position.z - area.Center.z);
            return offset.sqrMagnitude <= area.Radius * area.Radius;
        }

        private Vector3 ClampToArea(Vector3 position, AssignedArea area)
        {
            Vector2 offset = new Vector2(position.x - area.Center.x, position.z - area.Center.z);
            float limit = area.Radius - BoundaryPadding;

            if (offset.sqrMagnitude <= limit * limit)
                return position;

            offset = offset.normalized * limit;
            return new Vector3(area.Center.x + offset.x, position.y, area.Center.z + offset.y);
        }

        private bool EnsureGridReady()
        {
            if (gridCount > 2 && cellSize > 0f)
            {
                StartAreaTimer();
                return true;
            }

            bool initialized = InitializeGrid();
            if (initialized)
                StartAreaTimer();

            return initialized;
        }

        private void StartAreaTimer()
        {
            if (areaTimerStarted)
                return;

            timer.Every(AreaCheckInterval, EnsureAreasAssigned);
            areaTimerStarted = true;
        }

        private bool InitializeGrid()
        {
            if (World.Size <= 0)
                return false;

            gridCount = Mathf.FloorToInt((float)World.Size / GridScale);
            if (gridCount < 2)
                return false;

            cellSize = (float)World.Size / gridCount;
            return true;
        }

        // Same coordinate approach as GridAPI: grid intersections are measured from the map's top-left corner.
        private Vector3 PositionFromGridIntersection(float column, float row)
        {
            return new Vector3(
                (-(World.Size / 2f)) + (column * cellSize),
                0f,
                (World.Size / 2f) - (row * cellSize)
            );
        }

        private Vector3 GetGroundedPosition(Vector3 position)
        {
            position.y = TerrainMeta.HeightMap.GetHeight(position);
            return AboveRock(position);
        }

        private Vector3 AboveRock(Vector3 position)
        {
            RaycastHit[] hits = Physics.RaycastAll(position + new Vector3(0f, 20f, 0f), Vector3.down, 19.9f, GroundLayerMask);
            if (hits.Any())
                return hits.OrderByDescending(hit => hit.point.y).First().point + (Vector3.up * SpawnYOffset);

            return position + (Vector3.up * SpawnYOffset);
        }

        private bool IsDryGround(Vector3 position)
        {
            float groundHeight = TerrainMeta.HeightMap.GetHeight(position);
            float waterHeight = TerrainMeta.WaterMap.GetHeight(position);
            return groundHeight > waterHeight + 0.5f;
        }

        private string GridLabel(int column)
        {
            column++;
            string label = string.Empty;

            while (column > 0)
            {
                column--;
                label = (char)('A' + (column % 26)) + label;
                column /= 26;
            }

            return label;
        }
    }
}
