using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("Virtual Items", "WhiteThunder", "0.5.1")]
    [Description("Removes resource costs of specific ingredients for crafting and building.")]
    internal class VirtualItems : CovalencePlugin
    {
        #region Fields

        private const string PermissionRulesetPrefix = "virtualitems.ruleset";

        [PluginReference]
        private readonly Plugin ItemRetriever;

        private Configuration _config;
        private readonly RulesetManager _rulesetManager;

        private readonly object True = true;

        public VirtualItems()
        {
            _rulesetManager = new RulesetManager(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _config.Init(this);

            if (!_config.AnyRulesetHasFreeDeployables)
            {
                Unsubscribe(nameof(OnPayForPlacement));
            }
        }

        private void OnServerInitialized()
        {
            if (ItemRetriever == null)
            {
                LogError($"{nameof(ItemRetriever)} is not installed. This plugin will not function until {nameof(ItemRetriever)} loads.");
                return;
            }

            RegisterAsItemSupplier();
            UpdatePlayerInventories();
        }

        private void Unload()
        {
            _rulesetManager.Unload();
            UpdatePlayerInventories();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == nameof(ItemRetriever))
            {
                RegisterAsItemSupplier();
                UpdatePlayerInventories();
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            _rulesetManager.Remove(player);
        }

        private void OnGroupPermissionGranted(string groupName, string perm)
        {
            if (perm.StartsWith(PermissionRulesetPrefix))
            {
                _rulesetManager.Clear();
            }
        }

        private void OnGroupPermissionRevoked(string groupName, string perm)
        {
            if (perm.StartsWith(PermissionRulesetPrefix))
            {
                _rulesetManager.Clear();
            }
        }

        private void OnUserPermissionGranted(string userId, string perm)
        {
            if (perm.StartsWith(PermissionRulesetPrefix))
            {
                _rulesetManager.Clear();
            }
        }

        private void OnUserPermissionRevoked(string userId, string perm)
        {
            if (perm.StartsWith(PermissionRulesetPrefix))
            {
                _rulesetManager.Clear();
            }
        }

        private object OnPayForPlacement(BasePlayer player, Planner planner)
        {
            if (!planner.isTypeDeployable)
                return null;

            var item = planner.GetItem();
            if (item == null)
                return null;

            var ruleset = _rulesetManager.Get(player);
            if (ruleset == null)
                return null;

            return ruleset.HasFreeDeployable(item)
                ? True
                : null;
        }

        #endregion

        #region Helper Methods

        public static void LogDebug(string message) => Interface.Oxide.LogDebug($"[Virtual Items] {message}");
        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Virtual Items] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Virtual Items] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Virtual Items] {message}");

        private static void SendInventoryUpdate(BasePlayer player)
        {
            player.inventory.SendUpdatedInventory(PlayerInventory.Type.Main, player.inventory.containerMain);
        }

        private static void UpdatePlayerInventories()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                player.Invoke(() => SendInventoryUpdate(player), UnityEngine.Random.Range(0f, 1f));
            }
        }

        private void RegisterAsItemSupplier()
        {
            ItemRetriever?.Call("API_AddSupplier", this, new Dictionary<string, object>
            {
                ["Priority"] = -10,

                ["SumPlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int>((player, rawItemQuery) =>
                {
                    var ruleset = _rulesetManager.Get(player);
                    if (ruleset == null)
                        return 0;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    return ruleset.SumItems(ref itemQuery);
                }),

                // For Item Retriever v0.6.5.
                ["TakePlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>((player, rawItemQuery, amount, collect) =>
                {
                    var ruleset = _rulesetManager.Get(player);
                    if (ruleset == null)
                        return 0;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    return ruleset.TakeItems(ref itemQuery, amount, collect);
                }),

                // For Item Retriever v0.7.0+.
                ["TakePlayerItemsV2"] = new Func<BasePlayer, Dictionary<string, object>, int, List<Item>, ItemCraftTask, int>((player, rawItemQuery, amount, collect, itemCraftTask) =>
                {
                    var ruleset = _rulesetManager.Get(player);
                    if (ruleset == null)
                        return 0;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    if (itemCraftTask != null)
                    {
                        // Don't actually create items for crafting. Simply return up to the amount allowed.
                        return Math.Min(amount, ruleset.SumItems(ref itemQuery));
                    }

                    return ruleset.TakeItems(ref itemQuery, amount, collect);
                }),

                ["FindPlayerItems"] = new Action<BasePlayer, Dictionary<string, object>, List<Item>>((player, rawItemQuery, collect) =>
                {
                    var ruleset = _rulesetManager.Get(player);
                    if (ruleset == null)
                        return;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    ruleset.FindItems(ref itemQuery, collect);
                }),

                ["SerializeForNetwork"] = new Action<BasePlayer, List<ProtoBuf.Item>>((player, saveList) =>
                {
                    _rulesetManager.Get(player)?.SerializeForNetwork(saveList);
                }),
            });
        }

        #endregion

        #region Item Pool

        private class ItemPool
        {
            private readonly int _itemId;
            private readonly List<Item> _availableItems = new List<Item>();
            private readonly List<Item> _takenItems = new List<Item>();

            public ItemPool(int itemId)
            {
                _itemId = itemId;
            }

            public Item Take()
            {
                ReturnUnusedItems();

                Item item;

                if (_availableItems.Count > 0)
                {
                    item = _availableItems[_availableItems.Count - 1];
                    _availableItems.RemoveAt(_availableItems.Count - 1);
                }
                else
                {
                    item = ItemManager.CreateByItemID(_itemId);
                }

                _takenItems.Add(item);
                return item;
            }

            public void ReturnUnusedItems()
            {
                for (var i = _takenItems.Count - 1; i >= 0; i--)
                {
                    var item = _takenItems[i];
                    if (!IsUnused(item))
                        continue;

                    _takenItems.RemoveAt(i);
                    _availableItems.Add(item);
                }
            }

            public void Unload()
            {
                foreach (var item in _availableItems)
                {
                    item.Remove();
                }

                foreach (var item in _takenItems)
                {
                    if (IsUnused(item))
                    {
                        item.Remove();
                    }
                }
            }

            private bool IsUnused(Item item)
            {
                return item.parent == null && (object)item.GetWorldEntity() == null;
            }
        }

        #endregion

        #region Ruleset Manager

        private class RulesetManager
        {
            private readonly VirtualItems _plugin;
            private readonly Dictionary<ulong, Ruleset> _rulesetByPlayer = new Dictionary<ulong, Ruleset>();

            public RulesetManager(VirtualItems plugin)
            {
                _plugin = plugin;
            }

            public Ruleset Get(BasePlayer player)
            {
                Ruleset ruleset;
                if (!_rulesetByPlayer.TryGetValue(player.userID, out ruleset))
                {
                    ruleset = _plugin._config.DetermineBestRuleset(_plugin.permission, player);
                    _rulesetByPlayer[player.userID] = ruleset;
                }

                return ruleset;
            }

            public void Remove(BasePlayer player)
            {
                _rulesetByPlayer.Remove(player.userID);
            }

            public void Clear()
            {
                _rulesetByPlayer.Clear();
            }

            public void Unload()
            {
                foreach (var ruleset in _rulesetByPlayer.Values)
                {
                    // Ruleset may be cached as null, for players with no assigned ruleset.
                    ruleset?.Unload();
                }
            }
        }

        #endregion

        #region Item Query

        private struct ItemQuery
        {
            public static ItemQuery Parse(Dictionary<string, object> raw)
            {
                var itemQuery = new ItemQuery();

                GetOption(raw, "BlueprintId", out itemQuery.BlueprintId);
                GetOption(raw, "DisplayName", out itemQuery.DisplayName);
                GetOption(raw, "DataInt", out itemQuery.DataInt);
                GetOption(raw, "FlagsContain", out itemQuery.FlagsContain);
                GetOption(raw, "FlagsEqual", out itemQuery.FlagsEqual);
                GetOption(raw, "ItemId", out itemQuery.ItemId);
                GetOption(raw, "SkinId", out itemQuery.SkinId);

                return itemQuery;
            }

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                object value;
                result = dict.TryGetValue(key, out value) && value is T
                    ? (T)value
                    : default(T);
            }

            public int? BlueprintId;
            public int? DataInt;
            public string DisplayName;
            public Item.Flag? FlagsContain;
            public Item.Flag? FlagsEqual;
            public int? ItemId;
            public ulong? SkinId;
        }

        #endregion

        #region Configuration

        [JsonObject(MemberSerialization.OptIn)]
        private class Ruleset
        {
            private class ItemInfo
            {
                public int Amount { get; }
                private ItemDefinition _itemDefinition;
                private ProtoBuf.Item _itemData;

                public ItemInfo(ItemDefinition itemDefinition, int amount)
                {
                    _itemDefinition = itemDefinition;
                    Amount = amount;
                }

                public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
                {
                    if (_itemData == null)
                    {
                        _itemData = new ProtoBuf.Item();
                        _itemData.ShouldPool = false;
                        _itemData.itemid = _itemDefinition.itemid;
                        _itemData.amount = Amount;
                    }

                    saveList.Add(_itemData);
                }

                public Item Create(int amount)
                {
                    return ItemManager.Create(_itemDefinition, amount);
                }
            }

            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("All deployables are free")]
            public bool AllDeployablesFree;

            [JsonProperty("Free deployables")]
            public string[] FreeDeployables = Array.Empty<string>();

            [JsonProperty("Items")]
            public Dictionary<string, int> ItemAmounts = new Dictionary<string, int>();

            [JsonIgnore]
            private HashSet<int> _freeDeployableIds = new HashSet<int>();

            [JsonIgnore]
            private Dictionary<int, ItemInfo> _itemCacheById = new Dictionary<int, ItemInfo>();

            [JsonIgnore]
            private List<ItemInfo> _itemCacheList = new List<ItemInfo>();

            [JsonIgnore]
            private readonly Dictionary<int, ItemPool> _itemPoolByItemId = new Dictionary<int, ItemPool>();

            [JsonIgnore]
            public string Permission { get; private set; }

            [JsonIgnore]
            public bool HasAnyFreeDeployables => AllDeployablesFree || _freeDeployableIds.Count > 0;

            public void Init(VirtualItems plugin)
            {
                if (string.IsNullOrWhiteSpace(Name))
                    return;

                Permission = $"{PermissionRulesetPrefix}.{Name}";
                plugin.permission.RegisterPermission(Permission, plugin);

                foreach (var itemShortName in FreeDeployables)
                {
                    ItemDefinition itemDefinition;
                    if (!VerifyValidItem(itemShortName, out itemDefinition))
                        continue;

                    _freeDeployableIds.Add(itemDefinition.itemid);
                }

                foreach (var itemAmount in ItemAmounts)
                {
                    var itemShortName = itemAmount.Key;
                    var amount = itemAmount.Value;

                    ItemDefinition itemDefinition;
                    if (!VerifyValidItem(itemShortName, out itemDefinition))
                        continue;

                    if (_itemCacheById.ContainsKey(itemDefinition.itemid))
                    {
                        LogWarning($"Duplicate item in ruleset {Name}: {itemShortName}");
                        continue;
                    }

                    var itemInfo = new ItemInfo(itemDefinition, amount);
                    _itemCacheById[itemDefinition.itemid] = itemInfo;
                    _itemCacheList.Add(itemInfo);
                }
            }

            public bool HasFreeDeployable(Item item)
            {
                return AllDeployablesFree || _freeDeployableIds.Contains(item.info.itemid);
            }

            public int SumItems(ref ItemQuery itemQuery)
            {
                return GetItemInfo(ref itemQuery)?.Amount ?? 0;
            }

            public int TakeItems(ref ItemQuery itemQuery, int amount, List<Item> collect)
            {
                var itemInfo = GetItemInfo(ref itemQuery);
                if (itemInfo == null)
                    return 0;

                amount = Math.Min(amount, itemInfo.Amount);
                collect?.Add(itemInfo.Create(amount));
                return amount;
            }

            public void FindItems(ref ItemQuery itemQuery, List<Item> collect)
            {
                // Only support item ids for now since only expecting Rust to call this.
                if (!itemQuery.ItemId.HasValue)
                    return;

                var itemInfo = GetItemInfo(ref itemQuery);
                if (itemInfo == null)
                    return;

                var item = GetItemPool(itemQuery.ItemId.Value).Take();
                item.amount = itemInfo.Amount;
                collect.Add(item);
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
            {
                for (var i = 0; i < _itemCacheList.Count; i++)
                {
                    _itemCacheList[i].SerializeForNetwork(saveList);
                }
            }

            public void Unload()
            {
                foreach (var itemPool in _itemPoolByItemId.Values)
                {
                    itemPool.Unload();
                }
            }

            private bool VerifyValidItem(string itemShortName, out ItemDefinition itemDefinition)
            {
                itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                if (itemDefinition != null)
                    return true;

                LogError($"Invalid item short name in config: {itemShortName}");
                return false;
            }

            private ItemInfo GetItemInfo(ref ItemQuery itemQuery)
            {
                // If a plugin is not searching by item id, we can't consider any item a match.
                if (!itemQuery.ItemId.HasValue)
                    return null;

                // If a plugin is searching by other criteria, we can't consider any item a match.
                if (itemQuery.SkinId.HasValue && itemQuery.SkinId.Value != 0)
                    return null;

                if (itemQuery.BlueprintId.HasValue)
                    return null;

                if (itemQuery.DataInt.HasValue && itemQuery.DataInt != 0)
                    return null;

                if (itemQuery.FlagsContain.HasValue && itemQuery.FlagsContain != 0)
                    return null;

                if (itemQuery.FlagsEqual.HasValue && itemQuery.FlagsEqual != 0)
                    return null;

                if (itemQuery.DisplayName != null)
                    return null;

                ItemInfo itemInfo;
                return _itemCacheById.TryGetValue(itemQuery.ItemId.Value, out itemInfo)
                    ? itemInfo
                    : null;
            }

            private ItemPool GetItemPool(int itemId)
            {
                ItemPool itemPool;
                if (!_itemPoolByItemId.TryGetValue(itemId, out itemPool))
                {
                    itemPool = new ItemPool(itemId);
                    _itemPoolByItemId[itemId] = itemPool;
                }

                return itemPool;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Rulesets")]
            private Ruleset[] Rulesets =
            {
                new Ruleset
                {
                    Name = "build",
                    ItemAmounts =
                    {
                        ["metal.fragments"] = 100000,
                        ["metal.refined"] = 100000,
                        ["stones"] = 100000,
                        ["wood"] = 100000,
                    }
                },
                new Ruleset
                {
                    Name = "unlimited_ammo",
                    ItemAmounts =
                    {
                        ["ammo.grenadelauncher.buckshot"] = 100000,
                        ["ammo.grenadelauncher.he"] = 100000,
                        ["ammo.grenadelauncher.smoke"] = 100000,
                        ["ammo.handmade.shell"] = 100000,
                        ["ammo.nailgun.nails"] = 100000,
                        ["ammo.pistol"] = 100000,
                        ["ammo.pistol.fire"] = 100000,
                        ["ammo.pistol.hv"] = 100000,
                        ["ammo.rifle"] = 100000,
                        ["ammo.rifle.explosive"] = 100000,
                        ["ammo.rifle.hv"] = 100000,
                        ["ammo.rifle.incendiary"] = 100000,
                        ["ammo.rocket.basic"] = 100000,
                        ["ammo.rocket.fire"] = 100000,
                        ["ammo.rocket.hv"] = 100000,
                        ["ammo.rocket.smoke"] = 100000,
                        ["ammo.shotgun"] = 100000,
                        ["ammo.shotgun.fire"] = 100000,
                        ["ammo.shotgun.slug"] = 100000,
                        ["arrow.bone"] = 100000,
                        ["arrow.fire"] = 100000,
                        ["arrow.hv"] = 100000,
                        ["arrow.wooden"] = 100000,
                        ["snowball"] = 100000,
                        ["speargun.spear"] = 100000,
                    },
                },
                new Ruleset
                {
                    Name = "craft_most_items",
                    ItemAmounts =
                    {
                        ["bone.fragments"] = 100000,
                        ["can.tuna.empty"] = 100000,
                        ["cloth"] = 100000,
                        ["electric.rf.broadcaster"] = 100000,
                        ["electric.rf.receiver"] = 100000,
                        ["fat.animal"] = 100000,
                        ["gears"] = 100000,
                        ["ladder.wooden.wall"] = 100000,
                        ["leather"] = 100000,
                        ["lowgradefuel"] = 100000,
                        ["metal.fragments"] = 100000,
                        ["metal.refined"] = 100000,
                        ["metalblade"] = 100000,
                        ["metalpipe"] = 100000,
                        ["metalspring"] = 100000,
                        ["propanetank"] = 100000,
                        ["pumpkin"] = 100000,
                        ["riflebody"] = 100000,
                        ["roadsigns"] = 100000,
                        ["rope"] = 100000,
                        ["semibody"] = 100000,
                        ["sewingkit"] = 100000,
                        ["sheetmetal"] = 100000,
                        ["skull.human"] = 100000,
                        ["skull.wolf"] = 100000,
                        ["smgbody"] = 100000,
                        ["spear.wooden"] = 100000,
                        ["stash.small"] = 100000,
                        ["stones"] = 100000,
                        ["syringe.medical"] = 100000,
                        ["targeting.computer"] = 100000,
                        ["tarp"] = 100000,
                        ["wood"] = 100000,
                    }
                },
                new Ruleset
                {
                    Name = "craft_all_items",
                    ItemAmounts =
                    {
                        ["bone.fragments"] = 100000,
                        ["can.tuna.empty"] = 100000,
                        ["cctv.camera"] = 100000,
                        ["charcoal"] = 100000,
                        ["cloth"] = 100000,
                        ["electric.rf.broadcaster"] = 100000,
                        ["electric.rf.receiver"] = 100000,
                        ["explosives"] = 100000,
                        ["fat.animal"] = 100000,
                        ["gears"] = 100000,
                        ["grenade.beancan"] = 100000,
                        ["gunpowder"] = 100000,
                        ["ladder.wooden.wall"] = 100000,
                        ["leather"] = 100000,
                        ["lowgradefuel"] = 100000,
                        ["metal.fragments"] = 100000,
                        ["metal.refined"] = 100000,
                        ["metalblade"] = 100000,
                        ["metalpipe"] = 100000,
                        ["metalspring"] = 100000,
                        ["propanetank"] = 100000,
                        ["pumpkin"] = 100000,
                        ["riflebody"] = 100000,
                        ["roadsigns"] = 100000,
                        ["rope"] = 100000,
                        ["scrap"] = 100000,
                        ["semibody"] = 100000,
                        ["sewingkit"] = 100000,
                        ["sheetmetal"] = 100000,
                        ["skull.human"] = 100000,
                        ["skull.wolf"] = 100000,
                        ["smgbody"] = 100000,
                        ["spear.wooden"] = 100000,
                        ["stash.small"] = 100000,
                        ["stones"] = 100000,
                        ["sulfur"] = 100000,
                        ["syringe.medical"] = 100000,
                        ["targeting.computer"] = 100000,
                        ["tarp"] = 100000,
                        ["techparts"] = 100000,
                        ["wood"] = 100000,
                    }
                },
                new Ruleset
                {
                    Name = "craft_all_items_unlimited_ammo",
                    ItemAmounts =
                    {
                        ["ammo.grenadelauncher.buckshot"] = 100000,
                        ["ammo.grenadelauncher.he"] = 100000,
                        ["ammo.grenadelauncher.smoke"] = 100000,
                        ["ammo.handmade.shell"] = 100000,
                        ["ammo.nailgun.nails"] = 100000,
                        ["ammo.pistol"] = 100000,
                        ["ammo.pistol.fire"] = 100000,
                        ["ammo.pistol.hv"] = 100000,
                        ["ammo.rifle"] = 100000,
                        ["ammo.rifle.explosive"] = 100000,
                        ["ammo.rifle.hv"] = 100000,
                        ["ammo.rifle.incendiary"] = 100000,
                        ["ammo.rocket.basic"] = 100000,
                        ["ammo.rocket.fire"] = 100000,
                        ["ammo.rocket.hv"] = 100000,
                        ["ammo.shotgun"] = 100000,
                        ["ammo.shotgun.fire"] = 100000,
                        ["ammo.shotgun.slug"] = 100000,
                        ["arrow.bone"] = 100000,
                        ["arrow.fire"] = 100000,
                        ["arrow.hv"] = 100000,
                        ["arrow.wooden"] = 100000,
                        ["bone.fragments"] = 100000,
                        ["can.tuna.empty"] = 100000,
                        ["cctv.camera"] = 100000,
                        ["charcoal"] = 100000,
                        ["cloth"] = 100000,
                        ["electric.rf.broadcaster"] = 100000,
                        ["electric.rf.receiver"] = 100000,
                        ["explosives"] = 100000,
                        ["fat.animal"] = 100000,
                        ["gears"] = 100000,
                        ["grenade.beancan"] = 100000,
                        ["gunpowder"] = 100000,
                        ["ladder.wooden.wall"] = 100000,
                        ["leather"] = 100000,
                        ["lowgradefuel"] = 100000,
                        ["metal.fragments"] = 100000,
                        ["metal.refined"] = 100000,
                        ["metalblade"] = 100000,
                        ["metalpipe"] = 100000,
                        ["metalspring"] = 100000,
                        ["propanetank"] = 100000,
                        ["pumpkin"] = 100000,
                        ["riflebody"] = 100000,
                        ["roadsigns"] = 100000,
                        ["rope"] = 100000,
                        ["scrap"] = 100000,
                        ["semibody"] = 100000,
                        ["sewingkit"] = 100000,
                        ["sheetmetal"] = 100000,
                        ["skull.human"] = 100000,
                        ["skull.wolf"] = 100000,
                        ["smgbody"] = 100000,
                        ["snowball"] = 100000,
                        ["spear.wooden"] = 100000,
                        ["speargun.spear"] = 100000,
                        ["stash.small"] = 100000,
                        ["stones"] = 100000,
                        ["sulfur"] = 100000,
                        ["syringe.medical"] = 100000,
                        ["targeting.computer"] = 100000,
                        ["tarp"] = 100000,
                        ["techparts"] = 100000,
                        ["wood"] = 100000,
                    }
                }
            };

            [JsonIgnore]
            public bool AnyRulesetHasFreeDeployables
            {
                get
                {
                    foreach (var ruleset in Rulesets)
                    {
                        if (ruleset.HasAnyFreeDeployables)
                            return true;
                    }

                    return false;
                }
            }

            public void Init(VirtualItems plugin)
            {
                foreach (var ruleset in Rulesets)
                {
                    ruleset.Init(plugin);
                }
            }

            public Ruleset DetermineBestRuleset(Permission permission, BasePlayer player)
            {
                if (Rulesets == null)
                    return null;

                for (var i = Rulesets.Length - 1; i >= 0; i--)
                {
                    var ruleset = Rulesets[i];
                    if (ruleset.Permission != null && permission.UserHasPermission(player.UserIDString, ruleset.Permission))
                        return ruleset;
                }

                return null;
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            private string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigSection(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigSection(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigSection(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    PrintWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                PrintError(e.Message);
                PrintWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion
    }
}
