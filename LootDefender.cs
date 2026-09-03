using Facepunch;
using Facepunch.Math;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Loot Defender", "Author Egor Blagov, Maintainer nivex", "2.2.8")]
    [Description("Defends loot from other players who dealt less damage than you.")]
    internal class LootDefender : RustPlugin
    {
        [PluginReference]
        Plugin PersonalHeli, Friends, Clans, RustRewards, HelpfulSupply, ShoppyStock, XLevels, XPerience, SkillTree;

        private const string ZEROMEMBERID = "0";
        private const ulong RR_EVENT = 8675309;
        private const ulong CONVOY_EVENT = 755446;
        private const ulong HARBOR_EVENT = 81182151852251420;
        private const ulong CARGO_TRAIN_EVENT = 1337422;
        private const ulong DEFENDER_SKIN_ID = 3790355587;
        private Dictionary<NetworkableId, List<DamageKey>> _apcAttackers = new();
        private Dictionary<NetworkableId, List<DamageKey>> _heliAttackers = new();
        private Dictionary<NetworkableId, ulong> _locked = new();
        private HashSet<NetworkableId> _heliCriticalHits = new();
        private List<NetworkableId> _personal = new();
        private List<NetworkableId> _boss = new();
        private StoredData data = new();
        private MonumentInfo launchSite;
        private List<MonumentInfo> harbors = new();
        private List<ulong> ownerids = new() { 0, CARGO_TRAIN_EVENT, 3566257, 123425345634634 };

        public enum DamageEntryType { Bradley, Corpse, Heli, NPC, None }

        public class Lockout
        {
            public double Bradley;

            public double Heli;

            public bool Any(int current) => Bradley > current || Heli > current;
        }

        private class StoredData
        {
            public Dictionary<string, Lockout> Lockouts = new();
            public Dictionary<string, UiHandler.Info> UI = new();
            [JsonConverter(typeof(NetworkableIdConverter<DamageInfo>))]
            public Dictionary<NetworkableId, DamageInfo> Damage = new();
            [JsonConverter(typeof(NetworkableIdConverter<LockInfo>))]
            public Dictionary<NetworkableId, LockInfo> LootLock = new();
            public Dictionary<string, HashSet<ulong>> Dinosaurs = new();
            internal LootDefender Instance;
            internal Configuration config => Instance.config;

            internal bool IsTypeEnabled(DamageEntryType damageEntryType) => damageEntryType switch { DamageEntryType.NPC => config.Npc.IsEnabledWithThreshold, DamageEntryType.Bradley => config.Bradley.IsEnabled, DamageEntryType.Heli => config.Helicopter.IsEnabled, _ => false };

            public void EnsureInitialized()
            {
                Dinosaurs ??= new();
                Lockouts ??= new();
                UI ??= new();
                foreach (var info in UI.Values)
                {
                    info?.EnsureInitialized();
                }
                LootLock ??= new();
                Damage ??= new();
            }

            public void ClearEntityOwners()
            {
                foreach (var (id, damageInfo) in Damage)
                {
                    ClearEntityOwner(id, damageInfo?.OwnerID ?? 0uL);
                }

                foreach (var (id, lockInfo) in LootLock)
                {
                    ClearEntityOwner(id, lockInfo?.damageInfo?.OwnerID ?? 0uL);
                }
            }

            private static void ClearEntityOwner(NetworkableId id, ulong ownerid)
            {
                if (!ownerid.IsSteamId())
                {
                    return;
                }
                BaseEntity entity = BaseNetworkable.serverEntities.Find(id) as BaseEntity;
                ClearEntityOwner(entity, ownerid);
            }

            private static void ClearEntityOwner(BaseEntity entity, ulong ownerid)
            {
                if (entity != null && !entity.IsDestroyed && entity.OwnerID == ownerid)
                {
                    entity.OwnerID = 0uL;
                }
            }

            public void Sanitize()
            {
                EnsureInitialized();

                using var dps = Pool.Get<PooledList<KeyValuePair<NetworkableId, DamageInfo>>>();
                dps.AddRange(Damage);

                foreach (var (id, damageInfo) in dps)
                {
                    if (damageInfo == null)
                    {
                        Damage.Remove(id);
                        continue;
                    }

                    damageInfo.Instance = Instance;
                    BaseEntity entity = BaseNetworkable.serverEntities.Find(id) as BaseEntity;

                    if (damageInfo.damageKeys == null)
                    {
                        ClearEntityOwner(entity, damageInfo.OwnerID);
                        Damage.Remove(id);
                        Instance._locked.Remove(id);
                        continue;
                    }

                    damageInfo.damageKeys.RemoveAll(x => x == null || x.damageEntry == null || !x.userid.IsSteamId());

                    if (!HasNetworkId(entity) || damageInfo.damageKeys.Count == 0 || !IsTypeEnabled(damageInfo.damageEntryType))
                    {
                        if (damageInfo.OwnerID.IsSteamId())
                        {
                            ClearEntityOwner(entity, damageInfo.OwnerID);
                        }

                        Damage.Remove(id);
                        Instance._locked.Remove(id);
                        continue;
                    }

                    foreach (var x in damageInfo.damageKeys)
                    {
                        x.attacker = RelationshipManager.FindByID(x.userid);
                    }

                    damageInfo._id = id;
                    damageInfo._entity = entity;
                    damageInfo.maxHealth = entity.MaxHealth();
                    damageInfo._position = entity.transform.position;

                    if (damageInfo.OwnerID.IsSteamId())
                    {
                        entity.OwnerID = damageInfo.OwnerID;
                        Instance._locked[id] = damageInfo.OwnerID;
                    }
                    else damageInfo.OwnerID = 0uL;

                    damageInfo.Start();
                }

                using var locks = Pool.Get<PooledList<KeyValuePair<NetworkableId, LockInfo>>>();
                locks.AddRange(LootLock);

                foreach (var (id, lockInfo) in locks)
                {
                    BaseEntity entity = BaseNetworkable.serverEntities.Find(id) as BaseEntity;

                    if (lockInfo?.damageInfo != null)
                    {
                        lockInfo.damageInfo.Instance = Instance;
                    }

                    if (lockInfo?.damageInfo?.damageKeys != null)
                    {
                        lockInfo.damageInfo.damageKeys.RemoveAll(x => x == null || x.damageEntry == null || !x.userid.IsSteamId());
                    }

                    if (!HasNetworkId(entity) || !TryGetOwner(lockInfo, out ulong ownerid) || !IsTypeEnabled(lockInfo.damageInfo.damageEntryType))
                    {
                        Instance.RemoveLootLock(id, entity, lockInfo);
                        continue;
                    }

                    foreach (var x in lockInfo.damageInfo.damageKeys)
                    {
                        x.attacker = RelationshipManager.FindByID(x.userid);
                    }

                    lockInfo.damageInfo._position = entity.transform.position;
                    entity.OwnerID = ownerid;
                    Instance.ScheduleLootLock(id, entity, lockInfo);
                }
            }
        }

        private class NetworkableIdConverter<TValue> : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Dictionary<NetworkableId, TValue>);
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return new Dictionary<NetworkableId, TValue>();
                }

                if (reader.TokenType != JsonToken.StartObject)
                {
                    throw new JsonSerializationException($"Expected an object while reading {objectType.Name}, but found {reader.TokenType}.");
                }

                var dict = existingValue as Dictionary<NetworkableId, TValue> ?? new();
                dict.Clear();

                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndObject)
                    {
                        return dict;
                    }

                    if (reader.TokenType != JsonToken.PropertyName)
                    {
                        throw new JsonSerializationException($"Expected a NetworkableId property name, but found {reader.TokenType}.");
                    }

                    string key = reader.Value as string;

                    if (!reader.Read())
                    {
                        throw new JsonSerializationException("Unexpected end while reading a NetworkableId dictionary value.");
                    }

                    if (!TryParse(key, out ulong value))
                    {
                        reader.Skip();
                        continue;
                    }

                    dict[new NetworkableId(value)] = (TValue)serializer.Deserialize(reader, typeof(TValue));
                }

                throw new JsonSerializationException("Unexpected end while reading a NetworkableId dictionary.");
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value == null)
                {
                    writer.WriteNull();
                    return;
                }

                var dict = (Dictionary<NetworkableId, TValue>)value;

                writer.WriteStartObject();

                foreach (var pair in dict)
                {
                    writer.WritePropertyName(pair.Key.Value.ToString(CultureInfo.InvariantCulture));

                    serializer.Serialize(writer, pair.Value);
                }

                writer.WriteEndObject();
            }
        }

        private class DamageEntry
        {
            public float DamageDealt;
            public DateTime Timestamp;
            public string MemberId;

            public DamageEntry() { }

            public DamageEntry(string memberId)
            {
                Timestamp = DateTime.Now;
                MemberId = memberId;
            }

            public bool IsOutdated(int timeout) => timeout > 0 && DateTime.Now.Subtract(Timestamp).TotalSeconds >= timeout;
        }

        private class DamageKey
        {
            public ulong userid;
            public string name;
            public DamageEntry damageEntry;
            internal BasePlayer attacker;
            public DamageKey() { }

            public DamageKey(BasePlayer attacker)
            {
                this.attacker = attacker;
                userid = attacker.userID;
                name = attacker.displayName;
            }
            public BasePlayer GetAttacker()
            {
                if (attacker == null) attacker = RelationshipManager.FindByID(userid);
                return attacker;
            }
            public bool TryGetAttacker(out BasePlayer result)
            {
                result = GetAttacker();
                return result != null;
            }
        }

        private class DamageInfo
        {
            public List<DamageKey> damageKeys = new();
            internal Dictionary<ulong, BasePlayer> interact = new();
            internal List<ulong> participants = new();
            public DamageEntryType damageEntryType = DamageEntryType.None;
            public string NPCName;
            public ulong OwnerID;
            public ulong SkinID;
            public DateTime start;
            internal int _lockTime;
            internal BaseEntity _entity;
            internal Vector3 _position;
            internal Vector3 lastAttackedPosition;
            internal NetworkableId _id;
            internal float maxHealth;
            internal Timer _timer;
            internal List<DamageKey> keys = new();
            internal List<DamageGroup> damageGroups;
            internal LootDefender Instance;
            internal Configuration config => Instance.config;

            internal float FullDamage
            {
                get
                {
                    float damage = 0;
                    for (int i = 0; i < damageKeys.Count; i++)
                    {
                        damage += damageKeys[i].damageEntry.DamageDealt;
                    }
                    return damage;
                }
            }

            public DamageInfo() { }

            public DamageInfo(LootDefender instance, DamageEntryType damageEntryType, string NPCName, BaseEntity entity, DateTime start)
            {
                Instance = instance;
                SkinID = entity.skinID;
                _entity = entity;
                _id = entity.net.ID;
                maxHealth = entity.MaxHealth();
                this.damageEntryType = damageEntryType;
                this.NPCName = NPCName;
                this.start = start;

                Start();
            }

            public void Start()
            {
                DestroyTimer();
                _lockTime = Instance.GetLockTime(damageEntryType);

                if (_lockTime > 0)
                {
                    _timer = Instance.timer.Every(1f, CheckExpiration);
                }
            }

            public void DestroyTimer()
            {
                if (_timer is { Destroyed: false })
                {
                    _timer.Destroy();
                }
                _timer = null;
            }

            private void CheckExpiration()
            {
                foreach (var x in damageKeys)
                {
                    if (x.damageEntry.IsOutdated(_lockTime))
                    {
                        if (x.userid == OwnerID)
                        {
                            Unlock();
                        }

                        keys.Add(x);
                    }
                }

                foreach (var x in keys)
                {
                    damageKeys.Remove(x);
                }

                keys.Clear();

                if (damageKeys.Count == 0)
                {
                    Instance.RemoveDamageInfo(_id, this);
                }
            }

            public void Unlock()
            {
                if (!Instance._locked.Remove(_id, out ulong userid))
                {
                    OwnerID = 0;
                    return;
                }

                bool isDestroyed = !HasNetworkId(_entity);
                if (!isDestroyed && _entity.OwnerID == userid) // don't reset the owner when another plugin changed ours
                {
                    _entity.OwnerID = 0;
                }

                OwnerID = 0;

                Interface.CallHook("OnUnlockedEntity", _entity, userid, _id.Value, isDestroyed);

                string grid = PositionToGrid(_position);

                if (damageEntryType == DamageEntryType.Bradley && config.Bradley.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "BradleyUnlocked", grid);
                    }
                }

                if (damageEntryType == DamageEntryType.Heli && config.Helicopter.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "HeliUnlocked", grid);
                    }
                }

                if (damageEntryType == DamageEntryType.NPC && config.Npc.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "NpcUnlocked", NPCName, grid);
                    }
                }
            }

            private void Lock(BaseEntity entity, ulong userid)
            {
                Instance._locked[_id] = entity.OwnerID = OwnerID = userid;
                _position = entity.transform.position;

                Interface.CallHook("OnLockedEntity", entity, OwnerID, _id.Value, _position);
            }

            public void AddDamage(BaseCombatEntity entity, BasePlayer attacker, DamageEntry entry, float amount)
            {
                entry.DamageDealt += amount;
                entry.Timestamp = DateTime.Now;
                _position = entity.transform.position;
                lastAttackedPosition = attacker.transform.position;

                if (SkinID == 0)
                {
                    SkinID = entity.skinID;
                }

                if (damageEntryType == DamageEntryType.NPC && !Instance.CanLockNpc(entity))
                {
                    return;
                }

                if (entity.OwnerID.IsSteamId())
                {
                    OwnerID = entity.OwnerID;
                }

                if (OwnerID != 0uL)
                {
                    return;
                }

                float damage = 0f;
                var grid = PositionToGrid(entity.transform.position);

                if (entry.MemberId != ZEROMEMBERID)
                {
                    foreach (var x in damageKeys)
                    {
                        if (x.damageEntry.MemberId == entry.MemberId)
                        {
                            damage += x.damageEntry.DamageDealt;
                        }
                    }
                }
                else damage = entry.DamageDealt;

                if (config.Helicopter.IsEnabled && entity is PatrolHelicopter)
                {
                    if (damage >= maxHealth * config.Helicopter.Threshold && !Instance.HasPermission(attacker, "lootdefender.bypasshelilock"))
                    {
                        if (config.Helicopter.Messages.NotifyLocked == true)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                CreateMessage(target, "Locked Heli", grid, attacker.displayName);
                            }
                        }

                        Lock(entity, attacker.userID);
                    }
                }
                else if (config.Bradley.IsEnabled && entity is BradleyAPC)
                {
                    if (damage >= maxHealth * config.Bradley.Threshold && !Instance.HasPermission(attacker, "lootdefender.bypassbradleylock"))
                    {
                        if (config.Bradley.Messages.NotifyLocked == true)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                CreateMessage(target, "Locked Bradley", grid, attacker.displayName);
                            }
                        }

                        Lock(entity, attacker.userID);
                    }
                }
                else if (config.Npc.Enabled && entity is BasePlayer npc && Instance.CanLockNpc(npc))
                {
                    if (!npc.userID.IsSteamId() && damage >= maxHealth * config.Npc.Threshold && !Instance.HasPermission(attacker, "lootdefender.bypassnpclock"))
                    {
                        if (config.Npc.Messages.NotifyLocked == true)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                CreateMessage(target, "Locked Npc", grid, npc.displayName, attacker.displayName);
                            }
                        }
                        Lock(entity, attacker.userID);
                    }
                }
            }

            public bool TryGet(ulong userid, out DamageEntry damageEntry)
            {
                foreach (var x in damageKeys)
                {
                    if (x.userid == userid)
                    {
                        damageEntry = x.damageEntry;
                        return true;
                    }
                }
                damageEntry = null;
                return false;
            }

            public DamageEntry Get(BasePlayer attacker)
            {
                if (!TryGet(attacker.userID, out var entry))
                {
                    string id = Instance.GetMemberId(attacker);
                    damageKeys.Add(new(attacker)
                    {
                        damageEntry = entry = new(id),
                    });
                }
                return entry;
            }

            public bool isKilled;

            public void OnKilled(Vector3 position, HitInfo info, float distance)
            {
                if (isKilled) return;
                isKilled = true;
                SetCanInteract();
                DisplayDamageReport();
                FindLooters(position, info, distance);
            }

            private void FindLooters(Vector3 position, HitInfo info, float distance)
            {
                var weapon = info?.Weapon?.GetItem()?.info?.shortname ?? info?.WeaponPrefab?.ShortPrefabName ?? "";
                HashSet<ulong> looters = new();
                HashSet<ulong> users = new();

                foreach (var x in damageKeys)
                {
                    if (CanInteract(x.GetAttacker(), x.userid))
                    {
                        if (TryGet(x.userid, out var entry) && entry.DamageDealt > 0)
                        {
                            users.Add(x.userid);
                        }
                        looters.Add(x.userid);
                    }
                }

                foreach (var userid in users)
                {
                    Instance.GiveXpReward(_entity, this, userid, weapon, distance, users.Count);
                    Instance.GiveRustReward(_entity, this, userid, weapon, users.Count);
                    Instance.GiveShopReward(_entity, this, userid, weapon, distance, users.Count);
                }

                if (damageEntryType is DamageEntryType.Bradley or DamageEntryType.Heli)
                {
                    Instance.LockoutLooters(looters, position, damageEntryType, SkinID);
                }
            }

            public void DisplayDamageReport()
            {
                if (damageEntryType is DamageEntryType.Bradley or DamageEntryType.Heli)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        if (CanDisplayReport(target))
                        {
                            Instance.Message(target, GetDamageReport(target.userID));
                        }
                    }
                }
                else if (damageEntryType == DamageEntryType.NPC)
                {
                    foreach (var x in damageKeys)
                    {
                        if (CanDisplayReport(x.GetAttacker()))
                        {
                            Instance.Message(x.GetAttacker(), GetDamageReport(x.userid));
                        }
                    }
                }
            }

            private bool CanDisplayReport(BasePlayer target)
            {
                if (target == null || !target.IsConnected)
                {
                    return false;
                }

                if (damageEntryType == DamageEntryType.Bradley)
                {
                    if (config.Bradley.Messages.NotifyKiller && IsParticipant(target))
                    {
                        return true;
                    }

                    return config.Bradley.Messages.NotifyChat;
                }

                if (damageEntryType == DamageEntryType.Heli)
                {
                    if (config.Helicopter.Messages.NotifyKiller && IsParticipant(target))
                    {
                        return true;
                    }

                    return config.Helicopter.Messages.NotifyChat;
                }

                if (damageEntryType == DamageEntryType.NPC)
                {
                    if (config.Npc.Messages.NotifyKiller && IsParticipant(target))
                    {
                        return true;
                    }

                    return config.Npc.Messages.NotifyChat;
                }

                return false;
            }

            public void SetCanInteract()
            {
                var damageGroups = GetDamageGroups();
                if (damageGroups.Count > 0)
                {
                    using var ownerDamageGroups = GetOwnerDamageGroups(damageGroups);
                    foreach (var damageGroup in damageGroups)
                    {
                        if (ownerDamageGroups.Contains(damageGroup) || Instance.IsAlly(damageGroup.FirstDamagerDealer.attacker, damageGroup.FirstDamagerDealer.userid, OwnerID))
                        {
                            interact[damageGroup.FirstDamagerDealer.userid] = damageGroup.FirstDamagerDealer.GetAttacker();
                        }
                        else
                        {
                            var damage = TryGet(damageGroup.FirstDamagerDealer.userid, out var x) ? x.DamageDealt : 0f;
                            var ratio = damage > 0 && FullDamage > 0 ? damage / FullDamage : 0;
                            float threshold = config.Npc.Threshold;
                            if (damageEntryType == DamageEntryType.Bradley)
                            {
                                threshold = config.Bradley.Threshold;
                            }
                            else if (damageEntryType == DamageEntryType.Heli)
                            {
                                threshold = config.Helicopter.Threshold;
                            }
                            if (OwnerID == 0 && ratio >= threshold)
                            {
                                OwnerID = damageGroup.FirstDamagerDealer.userid;
                                interact[damageGroup.FirstDamagerDealer.userid] = damageGroup.FirstDamagerDealer.GetAttacker();
                            }
                            else
                            {
                                participants.Add(damageGroup.FirstDamagerDealer.userid);
                            }
                        }
                    }
                }
                this.damageGroups = damageGroups;
            }

            private string Localize(string key, ulong id, params object[] args) => Instance.Localize(key, id.ToString(), args);

            private string Localize(string key, string id, params object[] args) => Instance.Localize(key, id, args);

            private void CreateMessage(BasePlayer player, string key, params object[] args) => Instance.CreateMessage(player, key, args);

            private StringBuilder sb = new();
            public string GetDamageReport(ulong userid)
            {
                var nameKey = damageEntryType == DamageEntryType.Bradley ? Localize("BradleyAPC", userid) : damageEntryType == DamageEntryType.Heli ? Localize("Helicopter", userid) : NPCName;
                var firstDamageDealer = string.Empty;

                sb.Length = 0;
                sb.AppendLine($"{Localize("DamageReport", userid, $"<color={config.Report.Ok}>{nameKey}</color>")}:");

                if (damageEntryType is DamageEntryType.Bradley or DamageEntryType.Heli)
                {
                    var seconds = Math.Ceiling((DateTime.Now - start).TotalSeconds);

                    sb.AppendLine($"{Localize("DamageTime", userid, nameKey, seconds)}");
                }

                if (damageGroups.Count > 0)
                {
                    foreach (var damageGroup in damageGroups)
                    {
                        if (interact.ContainsKey(damageGroup.FirstDamagerDealer.userid))
                        {
                            sb.Append($"<color={config.Report.Ok}>√</color> ");
                            firstDamageDealer = damageGroup.FirstDamagerDealer.name;
                        }
                        else
                        {
                            sb.Append($"<color={config.Report.NotOk}>X</color> ");
                        }

                        sb.Append($"{damageGroup.ToReport(Instance, damageGroup.FirstDamagerDealer, this)}\n");
                    }

                    if (damageEntryType == DamageEntryType.NPC && damageGroups.Count > 1 && !string.IsNullOrEmpty(firstDamageDealer))
                    {
                        sb.Append($" {Localize("FirstLock", userid, firstDamageDealer, config.Npc.Threshold * 100f)}");
                    }
                }

                return sb.ToString();
            }

            public bool IsParticipant(BasePlayer player)
            {
                return participants.Contains(player.userID) || CanInteract(player, player.userID);
            }

            public bool CanInteract(BasePlayer player, ulong userid)
            {
                if (damageEntryType == DamageEntryType.NPC && !config.Npc.Enabled)
                {
                    return true;
                }

                if (damageGroups == null)
                {
                    interact.Clear();
                    participants.Clear();
                    SetCanInteract();
                }

                if (interact.Count == 0 || interact.ContainsKey(userid))
                {
                    return true;
                }

                if (Instance.IsAlly(player, userid, OwnerID))
                {
                    interact.Add(userid, player);
                    return true;
                }

                return false;
            }

            private PooledList<DamageGroup> GetOwnerDamageGroups(List<DamageGroup> damageGroups)
            {
                var ownerDamageGroups = Pool.Get<PooledList<DamageGroup>>();

                if (damageGroups.Count == 0)
                {
                    return ownerDamageGroups;
                }

                foreach (var damageGroup in damageGroups)
                {
                    foreach (var userid in damageGroup.Players)
                    {
                        BasePlayer player = RelationshipManager.FindByID(userid);
                        if (Instance.IsAlly(player, userid, OwnerID))
                        {
                            ownerDamageGroups.Add(damageGroup);
                            break;
                        }
                    }
                }

                return ownerDamageGroups;
            }

            private List<DamageGroup> GetDamageGroups()
            {
                List<DamageGroup> damageGroups = new();

                foreach (var x in damageKeys)
                {
                    damageGroups.Add(new(x));
                }

                damageGroups.Sort((x, y) => y.TotalDamage.CompareTo(x.TotalDamage));

                return damageGroups;
            }
        }

        private class LockInfo
        {
            public DamageInfo damageInfo;

            public DateTime ExpiresAt;

            internal bool IsLockOutdated => ExpiresAt != default && DateTime.Now >= ExpiresAt;

            public LockInfo() { }

            public LockInfo(DamageInfo damageInfo, DateTime expiresAt)
            {
                ExpiresAt = expiresAt;
                this.damageInfo = damageInfo;
            }

            public bool CanInteract(BasePlayer target) => damageInfo.CanInteract(target, target.userID);

            public string GetDamageReport(ulong userId) => damageInfo.GetDamageReport(userId);
        }

        private class DamageGroup
        {
            public float TotalDamage;

            public DamageKey FirstDamagerDealer;

            private List<ulong> additionalPlayers = new();

            internal List<ulong> Players
            {
                get
                {
                    List<ulong> players = new()
                    {
                        FirstDamagerDealer.userid
                    };

                    foreach (var userid in additionalPlayers)
                    {
                        if (!players.Contains(userid))
                        {
                            players.Add(userid);
                        }
                    }

                    return players;
                }
            }

            public DamageGroup() { }

            public DamageGroup(DamageKey x)
            {
                TotalDamage = x.damageEntry.DamageDealt;
                FirstDamagerDealer = x;

                if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(x.userid, out var team))
                {
                    for (int i = 0; i < team.members.Count; i++)
                    {
                        ulong member = team.members[i];

                        if (member == x.userid || additionalPlayers.Contains(member))
                        {
                            continue;
                        }

                        additionalPlayers.Add(member);
                    }
                }

                if (x.TryGetAttacker(out BasePlayer attacker) && attacker.clanId != 0 && TryGetClan(attacker, out IClan clan))
                {
                    for (int i = 0; i < clan.Members.Count; i++)
                    {
                        ulong member = clan.Members[i].SteamId;

                        if (member == x.userid || additionalPlayers.Contains(member))
                        {
                            continue;
                        }

                        additionalPlayers.Add(member);
                    }

                    if (clan.Creator != x.userid && !additionalPlayers.Contains(clan.Creator))
                    {
                        additionalPlayers.Add(clan.Creator);
                    }
                }
            }

            public string ToReport(LootDefender Instance, DamageKey damageKey, DamageInfo damageInfo)
            {
                var damage = damageInfo.TryGet(damageKey.userid, out var x) ? x.DamageDealt : 0f;
                var percent = damage > 0 && damageInfo.FullDamage > 0 ? damage / damageInfo.FullDamage * 100 : 0;
                var color = additionalPlayers.Count == 0 ? Instance.config.Report.SinglePlayer : Instance.config.Report.Team;
                var damageLine = Instance.Localize("Format", damageKey.userid.ToString(), damage, percent);

                return $"<color={color}>{damageKey.name}</color> {damageLine}";
            }
        }

        public string GetMemberId(BasePlayer attacker)
        {
            string id = attacker switch
            {
                { clanId: not 0 } => attacker.clanId.ToString(),
                { currentTeam: not 0 } => attacker.currentTeam.ToString(),
                _ => Clans?.Call("GetClanOf", attacker.userID.Get()) as string
            };
            if (string.IsNullOrEmpty(id)) return ZEROMEMBERID;
            return id;
        }

        public void UpdateMemberId(BasePlayer player, string id)
        {
            if (player == null)
            {
                return;
            }
            if (string.IsNullOrEmpty(id) || id == ZEROMEMBERID)
            {
                id = GetMemberId(player);
            }
            if (id == ZEROMEMBERID)
            {
                return;
            }
            allyLookupTimes.Clear();
            foreach (DamageInfo damageInfo in data.Damage.Values)
            {
                if (damageInfo.TryGet(player.userID, out DamageEntry damageEntry) && (string.IsNullOrEmpty(damageEntry.MemberId) || damageEntry.MemberId == ZEROMEMBERID))
                {
                    damageEntry.MemberId = id;
                }
            }
        }

        #region Hooks

        private void OnClanMemberJoined(string tag, ulong joining, List<ulong> members)
        {
            BasePlayer player = RelationshipManager.FindByID(joining);
            if (player != null)
            {
                UpdateMemberId(player, tag);
            }
        }

        private void OnTeamCreated(BasePlayer player, RelationshipManager.PlayerTeam team)
        {
            if (player != null)
            {
                UpdateMemberId(player, team.teamID.ToString());
            }
        }

        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (player == null)
            {
                return;
            }
            player.Invoke(() =>
            {
                if (!player.IsDestroyed && player.currentTeam == team?.teamID)
                {
                    UpdateMemberId(player, team.teamID.ToString());
                }
            }, 0.01f);
        }

        private object OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player) => HandleTeam(player?.userID ?? 0uL) ? (object)true : null;

        private object OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong targetId) => HandleTeam(targetId) ? (object)true : null;

        private void OnServerSave()
        {
            timer.Once(15f, SaveData);
        }

        private void Init()
        {
            UI = new() { Instance = this };
            Unsubscribe(nameof(OnEventTrigger));
            Unsubscribe();
            if (!string.IsNullOrEmpty(config.Lockout.Command))
                AddCovalenceCommand(config.Lockout.Command, nameof(CommandLockouts));
            if (!string.IsNullOrEmpty(config.UI.Command))
                AddCovalenceCommand(config.UI.Command, nameof(CommandUI));
            AddCovalenceCommand("lo", nameof(CommandLootDefender));
            AddCovalenceCommand("lootdefender", nameof(CommandLootDefender));
            RegisterPermissions();
            LoadData();
        }

        private void OnServerInitialized(bool initial)
        {
            if (initial)
            {
                data.ClearEntityOwners();
                data.Damage.Clear();
                data.LootLock.Clear();
                _locked.Clear();
                SaveData();
            }
            else
            {
                data.Sanitize();
            }

            if (config.Hackable.Enabled)
            {
                Subscribe(nameof(CanHackCrate));
                Subscribe(nameof(OnGuardedCrateEventEnded));
            }

            if (config.SupplyDrop.Lock)
            {
                if (config.SupplyDrop.LockTime > 0)
                {
                    if (config.SupplyDrop.NpcRandomRaids)
                    {
                        Subscribe(nameof(OnRandomRaidWin));
                    }
                    Subscribe(nameof(OnSupplyDropLanded));
                }

                if (config.SupplyDrop.Excavator)
                {
                    Subscribe(nameof(OnExcavatorSuppliesRequested));
                }

                if (config.SupplyDrop.HelpfulSupply && HelpfulSupply != null)
                {
                    Subscribe(nameof(OnEntitySpawned));
                }

                Subscribe(nameof(OnExplosiveDropped));
                Subscribe(nameof(OnExplosiveThrown));
                Subscribe(nameof(OnSupplyDropDropped));
                Subscribe(nameof(OnCargoPlaneSignaled));
            }

            if (config.SupplyDrop.DestroyTime > 0f || config.CH47Gibs)
            {
                Subscribe(nameof(OnEntitySpawned));
            }

            if (config.Npc.BossMonster)
            {
                Unsubscribe(nameof(OnBossSpawn));
                Unsubscribe(nameof(OnBossKilled));
            }

            if (!config.Bradley.LockPersonal)
            {
                Subscribe(nameof(OnPersonalApcSpawned));
            }

            if (!config.Helicopter.LockPersonal)
            {
                Subscribe(nameof(OnPersonalHeliSpawned));
            }

            if (config.UI.Bradley.Enabled || config.UI.Heli.Enabled)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    UI.ShowLockouts(player);
                }

                Subscribe(nameof(OnPlayerSleepEnded));
                Subscribe(nameof(OnCuiDraggableDrag));
            }

            if (config.BradleyOrHelicopterIsEnabled)
            {
                Subscribe(nameof(OnCrateSpawned));
            }

            if (config.BradleyOrHelicopterIsEnabled || config.Hackable.Laptop)
            {
                Subscribe(nameof(OnPlayerAttack));
            }

            if (config.Lockout.F15)
            {
                Subscribe(nameof(OnEventTrigger));
            }

            if (config.Helicopter.IsEnabled)
            {
                Subscribe(nameof(OnPatrolHelicopterKill));
            }

            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnEntityDeath));
            Subscribe(nameof(OnEntityKill));
            Subscribe(nameof(CanLootEntity));
            SetupLaunchSite();
        }

        private bool IsF15EventActive;

        private void OnEventTrigger(TriggeredEventPrefab prefab)
        {
            if (config.Lockout.F15 && !IsF15EventActive && prefab.name == "assets/bundled/prefabs/world/event_f15e.prefab")
            {
                Puts("F15 event has started; bypassing player lockouts!");
                IsF15EventActive = true;
            }
        }

        private void Unload()
        {
            UI.DestroyAllLockoutUI();
            SaveData();
        }

        private void OnPlayerSleepEnded(BasePlayer player) => UI.ShowLockouts(player);

        private void OnCuiDraggableDrag(BasePlayer player, string name, Vector3 position, CommunityEntity.DraggablePositionSendType dragType)
        {
            if (!HasNetworkId(player))
            {
                return;
            }

            UiType uiType = UI.GetUiType(name);
            if (uiType == UiType.Invalid)
            {
                return;
            }

            UiOffsets offsets = UI.GetOffsets(player.UserIDString, uiType, true);

            switch (dragType)
            {
                case CommunityEntity.DraggablePositionSendType.NormalizedScreen:
                    offsets.SetAnchor(new Vector2(position.x, 1f - position.y));
                    break;
                case CommunityEntity.DraggablePositionSendType.NormalizedParent:
                    offsets.SetAnchor(new Vector2(position.x, position.y));
                    break;
                case CommunityEntity.DraggablePositionSendType.Relative:
                case CommunityEntity.DraggablePositionSendType.RelativeAnchor:
                    offsets.Move(new Vector2(position.x, position.y));
                    break;
                default:
                    return;
            }

            UI.SaveOffsetData();
            UI.UpdateLockoutUI(player, uiType);
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || HasPermission(attacker, "lootdefender.bypass.damage") || info == null)
            {
                return null;
            }

            if (config.Hackable.Laptop && info.HitBone == 242862488 && info.HitEntity is HackableLockedCrate crate && IsDefended(crate)) // laptopcollision
            {
                info.HitBone = 0;
                return null;
            }

            if (!config.BradleyOrHelicopterIsEnabled)
            {
                return null;
            }

            if (!info.HitEntity.Is(out ServerGib gibs) || !TryGetNetworkId(gibs, out NetworkableId id))
            {
                return null;
            }

            if (!data.LootLock.TryGetValue(id, out var lockInfo))
            {
                return null;
            }

            if (!TryGetOwner(lockInfo, out ulong ownerid))
            {
                RemoveLootLock(id, gibs, lockInfo);
                return null;
            }

            if (gibs.OwnerID != ownerid)
            {
                if (DebugMode) Puts("Restored ownership of '{0}' to {1} [was={2}, now={3}]", gibs.ShortPrefabName, GetPlayerName(ownerid), gibs.OwnerID, ownerid);
                gibs.OwnerID = ownerid;
            }

            if (lockInfo.CanInteract(attacker))
            {
                return null;
            }

            if (CanMessage(attacker))
            {
                CreateMessage(attacker, "CannotMine");
                Message(attacker, lockInfo.GetDamageReport(attacker.userID));
            }

            CancelDamage(info);
            return true;
        }

        private object OnPatrolHelicopterKill(PatrolHelicopter heli, HitInfo info)
        {
            if (info == null || !config.Helicopter.IsEnabled || !TryGetNetworkId(heli, out NetworkableId id) || heli.myAI == null || heli.myAI.isDead || !ShouldHandleHeli(heli, id))
            {
                return null;
            }

            if (!TryGetPlayerAttacker(info, out BasePlayer attacker))
            {
                return null;
            }

            if (CanTakeDamage(heli, id, info, attacker, DamageEntryType.Heli, _heliAttackers, false) is false)
            {
                return true;
            }

            _heliCriticalHits.Add(id);
            NextTick(() => _heliCriticalHits.Remove(id));

            return null;
        }

        private bool IsDamageBlocked(BaseCombatEntity entity, NetworkableId id, BasePlayer attacker, HitInfo info, DamageEntryType damageEntryType)
        {
            if (!_locked.TryGetValue(id, out ulong ownerid) || HasPermission(attacker, "lootdefender.bypass.damage") || IsAlly(attacker, ownerid) || !BlockDamage(damageEntryType))
            {
                return false;
            }

            if (CanMessage(attacker))
            {
                CreateMessage(attacker, "CannotDamageThis");
            }

            CancelDamage(info);
            return true;
        }

        private object CanTakeDamage(BaseCombatEntity entity, NetworkableId id, HitInfo info, BasePlayer attacker, DamageEntryType damageEntryType, Dictionary<NetworkableId, List<DamageKey>> source, bool add = true)
        {
            if (info.damageTypes.Total() <= 0f)
            {
                return null;
            }

            if (IsDamageBlocked(entity, id, attacker, info, damageEntryType))
            {
                return false;
            }

            if (UI.GetLockoutTime(damageEntryType) <= 0d)
            {
                return null;
            }

            if (HasLockout(attacker, damageEntryType, entity.skinID))
            {
                CancelDamage(info);
                return false;
            }

            if (add)
            {
                AddDamageAttacker(source, id, attacker);
            }

            return null;
        }

        private void AddDamageAttacker(Dictionary<NetworkableId, List<DamageKey>> source, NetworkableId id, BasePlayer attacker)
        {
            if (!source.TryGetValue(id, out var attackers))
            {
                source[id] = attackers = new();
            }

            for (int i = 0; i < attackers.Count; i++)
            {
                if (attackers[i].userid == attacker.userID)
                {
                    return;
                }
            }

            attackers.Add(new(attacker));
        }

        private object OnEntityTakeDamage(PatrolHelicopter heli, HitInfo info)
        {
            if (info == null || !config.Helicopter.IsEnabled || !TryGetNetworkId(heli, out NetworkableId id) || heli.myAI == null || !ShouldHandleHeli(heli, id))
            {
                return null;
            }

            bool criticalHit = _heliCriticalHits.Remove(id);

            if (!criticalHit && heli.myAI.isDead)
            {
                return null;
            }

            if (!TryGetPlayerAttacker(info, out BasePlayer attacker))
            {
                return null;
            }

            if (criticalHit)
            {
                if (config.Lockout.Heli > 0d && info.damageTypes.Total() > 0f)
                {
                    AddDamageAttacker(_heliAttackers, id, attacker);
                }
            }
            else if (CanTakeDamage(heli, id, info, attacker, DamageEntryType.Heli, _heliAttackers) is false)
            {
                return true;
            }

            return OnEntityTakeDamageHandler(heli, id, info, attacker, DamageEntryType.Heli, string.Empty);
        }

        private object OnEntityTakeDamage(BradleyAPC apc, HitInfo info)
        {
            if (info == null || !config.Bradley.IsEnabled || !TryGetNetworkId(apc, out NetworkableId id) || !ShouldHandleBradley(apc, id))
            {
                return null;
            }

            if (!TryGetPlayerAttacker(info, out BasePlayer attacker))
            {
                return null;
            }

            if (CanTakeDamage(apc, id, info, attacker, DamageEntryType.Bradley, _apcAttackers) is false)
            {
                return true;
            }

            return OnEntityTakeDamageHandler(apc, id, info, attacker, DamageEntryType.Bradley, string.Empty);
        }

        private object OnEntityTakeDamage(BasePlayer player, HitInfo info)
        {
            if (info == null || !config.Npc.IsEnabledWithThreshold || !TryGetNetworkId(player, out NetworkableId id) || player.userID.IsSteamId() || !CanLockNpc(player))
            {
                return null;
            }

            if (config.Npc.Min > 0 && player.startHealth < config.Npc.Min)
            {
                return null;
            }

            if (!TryGetPlayerAttacker(info, out BasePlayer attacker))
            {
                return null;
            }

            if (IsDamageBlocked(player, id, attacker, info, DamageEntryType.NPC))
            {
                return true;
            }

            return OnEntityTakeDamageHandler(player, id, info, attacker, DamageEntryType.NPC, player.displayName);
        }

        private object OnEntityTakeDamageHandler(BaseCombatEntity entity, NetworkableId id, HitInfo info, BasePlayer attacker, DamageEntryType damageEntryType, string npcName)
        {
            float damage = info.damageTypes.Total();

            if (damage <= 0f)
            {
                return null;
            }

            if (info.isHeadshot)
            {
                damage *= 2f;
            }

            if (!data.Damage.TryGetValue(id, out var damageInfo))
            {
                data.Damage[id] = damageInfo = new(this, damageEntryType, npcName, entity, DateTime.Now);
            }

            damageInfo.AddDamage(entity, attacker, damageInfo.Get(attacker), damage);

            return null;
        }

        private bool BlockDamage(DamageEntryType damageEntryType)
        {
            return damageEntryType switch
            {
                DamageEntryType.NPC => !config.Npc.LootingOnly,
                DamageEntryType.Heli => !config.Helicopter.LootingOnly,
                DamageEntryType.Bradley => !config.Bradley.LootingOnly,
                _ => true
            };
        }

        private static bool TryGetNetworkId(BaseEntity entity, out NetworkableId id)
        {
            if (!HasNetworkId(entity))
            {
                id = default;
                return false;
            }
            id = entity.net.ID;
            return id.IsValid;
        }

        private static bool HasNetworkId(BaseEntity entity) => entity.IsValid() && !entity.IsDestroyed;

        private static bool HasNetworkConnection(BasePlayer player) => player != null && !player.IsDestroyed && player.IsConnected;

        private bool TryGetPlayerAttacker(HitInfo info, out BasePlayer attacker)
        {
            if (info.Initiator == null)
            {
                attacker = null;
                return false;
            }

            attacker = info.Initiator switch
            {
                BasePlayer player => player,
                TimedExplosive te when te.creatorPlayer != null => te.creatorPlayer,
                { creatorEntity: BasePlayer player } => player,
                { OwnerID: > 76561197960265728L } => BasePlayer.FindByID(info.Initiator.OwnerID),
                _ => null
            };

            return attacker != null && attacker.userID.IsSteamId();
        }

        private void OnEntityDeath(PatrolHelicopter heli, HitInfo info)
        {
            if (!TryGetNetworkId(heli, out NetworkableId id))
            {
                return;
            }

            _heliCriticalHits.Remove(id);
            _heliAttackers.Remove(id);
            OnEntityDeathHandler(heli, id, DamageEntryType.Heli, info);
            _personal.Remove(id);
        }

        private void OnEntityKill(PatrolHelicopter heli)
        {
            OnEntityDeath(heli, null);

            if (!TryGetNetworkId(heli, out NetworkableId id))
            {
                return;
            }

            QueueFallback(heli, id, DamageEntryType.Heli);

            if (data.Damage.TryGetValue(id, out var damageInfo))
            {
                RemoveDamageInfo(id, damageInfo);
            }
            else
            {
                _locked.Remove(id);
            }
        }

        private void OnEntityDeath(BradleyAPC apc, HitInfo info)
        {
            if (!TryGetNetworkId(apc, out NetworkableId id))
            {
                return;
            }

            _apcAttackers.Remove(id);
            OnEntityDeathHandler(apc, id, DamageEntryType.Bradley, info);
            _personal.Remove(id);
        }

        private void OnEntityKill(BradleyAPC apc)
        {
            if (!TryGetNetworkId(apc, out NetworkableId id))
            {
                return;
            }

            QueueFallback(apc, id, DamageEntryType.Bradley);

            _apcAttackers.Remove(id);
            _personal.Remove(id);

            if (data.Damage.TryGetValue(id, out var damageInfo))
            {
                RemoveDamageInfo(id, damageInfo);
            }
            else
            {
                _locked.Remove(id);
            }
        }

        private void OnEntityDeath(BasePlayer player, HitInfo info)
        {
            if (!config.Npc.Enabled || !TryGetNetworkId(player, out NetworkableId id) || player.userID.IsSteamId())
            {
                return;
            }

            OnEntityDeathHandler(player, id, DamageEntryType.NPC, info);
        }

        private void OnEntityDeath(NPCPlayerCorpse corpse, HitInfo info)
        {
            if (!config.Npc.Enabled || !TryGetNetworkId(corpse, out NetworkableId id))
            {
                return;
            }

            OnEntityDeathHandler(corpse, id, DamageEntryType.Corpse, info);
        }

        private void OnEntityKill(NPCPlayerCorpse corpse) => OnEntityDeath(corpse, null);

        private bool IsInBounds(MonumentInfo monument, Vector3 target)
        {
            return monument.IsInBounds(target) || new OBB(monument.transform.position, monument.transform.rotation, new Bounds(monument.Bounds.center, new Vector3(300f, 300f, 300f))).Contains(target);
        }

        private bool ShouldHandleBradley(BradleyAPC apc, NetworkableId id) => _locked.ContainsKey(id) || data.Damage.ContainsKey(id) || CanLockBradley(apc, id);

        private bool ShouldHandleHeli(PatrolHelicopter heli, NetworkableId id) => _locked.ContainsKey(id) || data.Damage.ContainsKey(id) || CanLockHeli(heli, id);

        private bool CanLockBradley(BaseEntity entity, NetworkableId id)
        {
            if (!config.Bradley.IsEnabled || _personal.Contains(id))
            {
                return false;
            }

            if (entity.name.Contains($"BradleyApc[{id}]"))
            {
                return config.Bradley.LockBradleyTiers;
            }

            if (entity.skinID != 0uL)
            {
                if (entity.skinID == CONVOY_EVENT)
                {
                    return config.Bradley.LockConvoy;
                }

                if (entity.skinID == HARBOR_EVENT)
                {
                    return config.Bradley.LockHarbor;
                }

                if (entity.skinID == RR_EVENT)
                {
                    return config.Bradley.LockMonument;
                }

                TrackReviewableSkin(entity, config.Bradley.IncludedSkins, config.Bradley.ReviewableSkins, DamageEntryType.Bradley);

                return config.Bradley.CanLockSkin(entity.skinID);
            }

            if (launchSite != null && IsInBounds(launchSite, entity.ServerPosition))
            {
                return config.Bradley.LockLaunchSite;
            }

            if (harbors.Exists(monument => IsInBounds(monument, entity.ServerPosition)))
            {
                return config.Bradley.LockHarbor;
            }

            return config.Bradley.CanLockSkin(entity.skinID);
        }

        private bool CanLockHeli(BaseCombatEntity entity, NetworkableId id)
        {
            if (!config.Helicopter.IsEnabled || _personal.Contains(id))
            {
                return false;
            }

            if (entity.skinID != 0uL)
            {
                if (entity.skinID == CONVOY_EVENT)
                {
                    return config.Helicopter.LockConvoy;
                }

                if (entity.skinID == HARBOR_EVENT)
                {
                    return config.Helicopter.LockHarbor == true;
                }

                TrackReviewableSkin(entity, config.Helicopter.IncludedSkins, config.Helicopter.ReviewableSkins, DamageEntryType.Heli);
            }

            return config.Helicopter.CanLockSkin(entity.skinID);
        }

        private void TrackReviewableSkin(BaseEntity entity, HashSet<ulong> include, Dictionary<string, HashSet<ulong>> review, DamageEntryType damageEntryType)
        {
            if (review.ContainsKey("none"))
            {
                return;
            }

            string typeName = entity.GetType().Name;

            data.Dinosaurs ??= new();

            if (!data.Dinosaurs.TryGetValue(typeName, out var fossilRecord) || fossilRecord == null)
            {
                data.Dinosaurs[typeName] = fossilRecord = new();
            }

            if (include.Contains(entity.skinID))
            {
                fossilRecord.Add(entity.skinID);
                return;
            }

            if (!review.TryGetValue(typeName, out var skins) || skins == null)
            {
                review[typeName] = skins = new();
            }

            if (skins.Contains(entity.skinID))
            {
                fossilRecord.Add(entity.skinID);
                return;
            }

            if (!fossilRecord.Add(entity.skinID))
            {
                return;
            }

            skins.Add(entity.skinID);

            Puts("[INFO] A new skin has been found for {0} with skin ID {1}. To enable or remove support for this skin or another, type: lootdefender toggleskin {2} {0} {1}", typeName, entity.skinID, damageEntryType);

            SaveConfig();
        }

        private bool TryToggleSkin(string[] args, IPlayer user)
        {
            if (args.Length == 0 || !args[0].Equals("toggleskin", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string GetUsage(int index)
            {
                if (index >= args.Length)
                {
                    return index == 1 ? "<Bradley|Heli>" : index == 2 ? "<entity type>" : "<skin ID>";
                }

                string value = args[index];

                if (index == 1 && !value.Equals("Bradley", StringComparison.OrdinalIgnoreCase) && !value.Equals("Heli", StringComparison.OrdinalIgnoreCase))
                {
                    return "<Bradley|Heli>";
                }

                if (index == 2 && string.IsNullOrWhiteSpace(value))
                {
                    return "<entity type>";
                }

                if (index == 3 && !TryParse(value, out ulong _))
                {
                    return "<skin ID>";
                }

                return value;
            }

            if (args.Length != 4)
            {
                user.Reply(args.Length switch
                {
                    <= 1 => "Usage: lootdefender toggleskin <Bradley|Heli> <entity type> <skin ID>",
                    2 => $"Missing entity type and skin ID.\nUsage: lootdefender toggleskin {GetUsage(1)} <entity type> <skin ID>",
                    3 => $"Missing skin ID.\nUsage: lootdefender toggleskin {GetUsage(1)} {GetUsage(2)} <skin ID>",
                    _ => $"Too many arguments.\nUsage: lootdefender toggleskin {GetUsage(1)} {GetUsage(2)} {GetUsage(3)}"
                });

                return true;
            }

            if (!Enum.TryParse(args[1], true, out DamageEntryType damageEntryType) || damageEntryType != DamageEntryType.Bradley && damageEntryType != DamageEntryType.Heli)
            {
                user.Reply($"Unsupported type specified: {args[1]}\nUsage: lootdefender toggleskin <Bradley|Heli> <entity type> <skin ID>");
                return true;
            }

            if (!TryParse(args[3], out ulong skinID))
            {
                user.Reply($"Invalid skin ID specified: {args[3]}\nUsage: lootdefender toggleskin {args[1]} {GetUsage(2)} <skin ID>");
                return true;
            }

            if (skinID == 0uL)
            {
                user.Reply($"Skin ID 0 is controlled by the '{(damageEntryType == DamageEntryType.Bradley ? "Lock Bradley From Everywhere Else" : "Lock Heli From Everywhere Else")}' option.");
                return true;
            }

            var review = damageEntryType == DamageEntryType.Bradley ? config.Bradley.ReviewableSkins : config.Helicopter.ReviewableSkins;
            var include = damageEntryType == DamageEntryType.Bradley ? config.Bradley.IncludedSkins : config.Helicopter.IncludedSkins;
            string typeName = args[2];

            if (review.TryGetValue(typeName, out var skins) && skins != null)
            {
                skins.Remove(skinID);

                if (skins.Count == 0)
                {
                    review.Remove(typeName);
                }
            }

            data.Dinosaurs ??= new();

            if (!data.Dinosaurs.TryGetValue(typeName, out var fossilRecord) || fossilRecord == null)
            {
                data.Dinosaurs[typeName] = fossilRecord = new();
            }

            fossilRecord.Add(skinID);

            if (include.Add(skinID))
            {
                user.Reply($"Added skin {skinID} to {damageEntryType}.");
            }
            else
            {
                include.Remove(skinID);
                user.Reply($"Removed skin {skinID} from {damageEntryType}.");
            }

            SaveConfig();
            return true;
        }

        private bool CanLockNpc(BaseEntity entity)
        {
            if (entity.OwnerID.ToString().Length == 5)
            {
                return false;
            }
            return config.Npc.IsEnabledWithThreshold && !_boss.Contains(entity.net.ID);
        }

        private void OnCrateSpawned(BradleyAPC bradleyApc, LockedByEntCrate crate) => OnCrateSpawnedHandler(bradleyApc, crate, DamageEntryType.Bradley);

        private void OnCrateSpawned(PatrolHelicopter patrolHelicopter, LockedByEntCrate crate) => OnCrateSpawnedHandler(patrolHelicopter, crate, DamageEntryType.Heli);

        private void OnCrateSpawnedHandler(BaseCombatEntity entity, LockedByEntCrate crate, DamageEntryType damageEntryType)
        {
            if (!TryGetNetworkId(crate, out NetworkableId crateId) || !TryGetNetworkId(entity, out NetworkableId entityId))
            {
                return;
            }

            if (!data.Damage.TryGetValue(entityId, out var damageInfo) || !damageInfo.isKilled)
            {
                return;
            }

            LockLootEntity(crate, crateId, new(damageInfo, GetLootLockDateTime(damageEntryType)));
            RemoveFireFromCrate(crate, damageEntryType);
        }

        private void QueueFallback(BaseCombatEntity entity, NetworkableId id, DamageEntryType damageEntryType)
        {
            if (!data.Damage.TryGetValue(id, out var damageInfo) || !damageInfo.isKilled)
            {
                return;
            }

            Vector3 position = entity.transform.position;

            timer.Once(0.1f, () =>
            {
                LockInRadius(position, new(damageInfo, GetLootLockDateTime(damageEntryType)), damageEntryType);
            });
        }

        private void RemoveDamageInfo(NetworkableId id, DamageInfo damageInfo)
        {
            damageInfo.DestroyTimer();
            data.Damage.Remove(id);
            _locked.Remove(id);
        }

        private void OnEntityDeathHandler(BaseCombatEntity entity, NetworkableId id, DamageEntryType damageEntryType, HitInfo info)
        {
            if (data.Damage.TryGetValue(id, out var damageInfo) && !damageInfo.isKilled)
            {
                if (damageEntryType == DamageEntryType.NPC && !CanLockNpc(entity))
                {
                    RemoveDamageInfo(id, damageInfo);
                    return;
                }

                if (damageEntryType is DamageEntryType.Bradley or DamageEntryType.Heli)
                {
                    var position = entity.transform.position;

                    if (entity is PatrolHelicopter heli && !heli.IsDead())
                    {
                        RemoveDamageInfo(id, damageInfo);
                        return;
                    }

                    damageInfo.OnKilled(position, info, info?.ProjectileDistance ?? Vector3.Distance(position, damageInfo.lastAttackedPosition));
                    damageInfo.DestroyTimer();
                }
                else if (damageEntryType == DamageEntryType.NPC && config.Npc.Enabled && entity is BasePlayer npc)
                {
                    var position = entity.transform.position;
                    var npcUserId = npc.userID;

                    damageInfo.OnKilled(position, info, info?.ProjectileDistance ?? Vector3.Distance(position, damageInfo.lastAttackedPosition));
                    damageInfo.DestroyTimer();

                    timer.Once(0.1f, () => LockInRadius(position, id, damageInfo, npcUserId));
                }
                else if (damageEntryType == DamageEntryType.NPC && config.Npc.Enabled && entity is LootableCorpse corpse)
                {
                    var position = entity.transform.position;
                    var npcId = corpse.playerSteamID;

                    damageInfo.OnKilled(position, info, info?.ProjectileDistance ?? Vector3.Distance(position, damageInfo.lastAttackedPosition));
                    damageInfo.DestroyTimer();

                    timer.Once(0.1f, () => LockInRadius(position, id, damageInfo, npcId));
                }
            }

            if (data.LootLock.Remove(id, out var lockInfo2) && damageEntryType == DamageEntryType.Corpse && config.Npc.Enabled && entity is LootableCorpse corpse2)
            {
                var corpsePos = corpse2.transform.position;
                var corpseId = corpse2.playerSteamID;

                timer.Once(0.1f, () => LockInRadius(corpsePos, lockInfo2, corpseId));
            }
        }

        private void GiveRustReward(BaseEntity entity, DamageInfo damageInfo, ulong userid, string weapon, int total)
        {
            if (RustRewards == null || !BasePlayer.TryFindByID(userid, out BasePlayer attacker))
            {
                return;
            }

            var amount = damageInfo.damageEntryType == DamageEntryType.Bradley ? config.Bradley.RRP : damageInfo.damageEntryType == DamageEntryType.Heli ? config.Helicopter.RRP : config.Npc.RRP;

            if (amount <= 0.0)
            {
                return;
            }

            var distance = Vector3.Distance(attacker.transform.position, entity.transform.position);

            ApplyWeaponMultiplierReward(damageInfo, weapon, ref amount, distance);

            if (amount <= 0) return;

            RustRewards?.Call("GiveRustReward", attacker, 0, amount, entity, weapon, distance, entity.ShortPrefabName);
        }

        void GiveXpReward(BaseEntity entity, DamageInfo damageInfo, ulong userid, string weapon, float distance, int total)
        {
            var amount = damageInfo.damageEntryType == DamageEntryType.Bradley ? config.Bradley.XP : damageInfo.damageEntryType == DamageEntryType.Heli ? config.Helicopter.XP : config.Npc.XP;

            if (amount <= 0.0 || !userid.IsSteamId())
            {
                return;
            }

            var attacker = BasePlayer.FindByID(userid);

            ApplyWeaponMultiplierReward(damageInfo, weapon, ref amount, distance);

            if (amount <= 0) return;

            if (SkillTree != null)
            {
                if (attacker) SkillTree?.Call("AwardXP", attacker, amount, Name);
                else SkillTree?.Call("AwardXP", userid, amount, Name);
            }

            if (XPerience != null)
            {
                XPerience?.Call("GiveXPID", userid, amount);
            }

            if (XLevels != null && attacker != null)
            {
                XLevels?.Call("API_GiveXP", attacker, (float)amount);
            }
        }

        void GiveShopReward(BaseEntity entity, DamageInfo damageInfo, ulong userid, string weapon, float distance, int total)
        {
            if (ShoppyStock == null)
            {
                return;
            }

            var amount = damageInfo.damageEntryType == DamageEntryType.Bradley ? config.Bradley.SS : damageInfo.damageEntryType == DamageEntryType.Heli ? config.Helicopter.SS : config.Npc.SS;

            if (amount <= 0.0)
            {
                return;
            }

            var storeName = damageInfo.damageEntryType == DamageEntryType.Bradley ? config.Bradley.ShoppyStockShopName : damageInfo.damageEntryType == DamageEntryType.Heli ? config.Helicopter.ShoppyStockShopName : config.Npc.ShoppyStockShopName;

            if (string.IsNullOrEmpty(storeName))
            {
                return;
            }

            ApplyWeaponMultiplierReward(damageInfo, weapon, ref amount, distance);

            if (amount <= 0) return;

            amount /= total;
            amount = Math.Round(amount, 0);

            ShoppyStock?.Call("GiveCurrency", storeName, userid, Mathf.Max(1, (int)amount));

            if (!(BasePlayer.FindByID(userid) is BasePlayer attacker) || !attacker.IsConnected)
                return;

            CreateMessage(attacker, "ShoppyStockReward", amount, storeName);
        }

        private void ApplyWeaponMultiplierReward(DamageInfo damageInfo, string weapon, ref double amount, float distance)
        {
            if (damageInfo.damageEntryType == DamageEntryType.NPC)
            {
                if (distance > 400) distance = 401;
                var distanceMulti = config.Npc.Distance.GetDistanceMult(distance);
                amount = Math.Round(distanceMulti * amount, 0);
                if (config.Npc.WeaponMultipliers.TryGetValue(weapon, out double weaponMulti))
                {
                    amount = Math.Round(weaponMulti * amount, 0);
                    if (amount < 1) amount = 1;
                }
            }
        }

        //private void OnMonumentUnlock(string name, Vector3 position, ulong occupiedBy, ulong networkID)
        //{
        //    BaseEntity entity = BaseNetworkable.serverEntities.Find(new(networkID)) as BaseEntity;
        //    if (entity != null && entity.OwnerID.IsSteamId())
        //    {
        //        entity.OwnerID = 0;
        //    }
        //    data.Damage.Remove(networkID);
        //    data.LootLock.Remove(networkID);
        //    _locked.Remove(networkID);
        //}

        private string GetPlayerName(ulong playerId)
        {
            BasePlayer player = RelationshipManager.FindByID(playerId);
            if (player != null)
            {
                return player.displayName;
            }
            var username = ConVar.Admin.GetPlayerName(playerId);
            if (username == null || username == "[unknown]")
            {
                var user = covalence.Players.FindPlayerById(playerId.ToString());
                if (user != null)
                {
                    return user.Name;
                }
            }
            return username;
        }

        private object OnAutoPickupEntity(BasePlayer player, BaseEntity entity) => CanLootEntityHandler(player, entity);

        private object CanLootEntity(BasePlayer player, DroppedItemContainer container) => CanLootEntityHandler(player, container);

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse) => CanLootEntityHandler(player, corpse);

        private object CanLootEntity(BasePlayer player, StorageContainer container) => CanLootEntityHandler(player, container);

        private bool DebugMode;
        private object CanLootEntityHandler(BasePlayer player, BaseEntity entity)
        {
            if (player == null || !TryGetNetworkId(entity, out NetworkableId id) || HasPermission(player, "lootdefender.bypass.loot"))
            {
                return null;
            }

            if (data.LootLock.TryGetValue(id, out var lockInfo))
            {
                if (!TryGetOwner(lockInfo, out ulong ownerid))
                {
                    RemoveLootLock(id, entity, lockInfo);
                    return null;
                }

                if (entity.OwnerID != ownerid)
                {
                    if (DebugMode) Puts("Restored ownership of '{0}' to {1} [was={2}, now={3}]", entity.ShortPrefabName, GetPlayerName(ownerid), entity.OwnerID, ownerid);
                    entity.OwnerID = ownerid;
                }

                if (lockInfo.CanInteract(player) || Interface.CallHook("OnLootLockedEntity", player, entity) is true)
                {
                    return null;
                }

                if (CanMessage(player))
                {
                    CreateMessage(player, "CannotLoot");
                    Message(player, lockInfo.GetDamageReport(player.userID));
                }

                return true;
            }

            if (entity.OwnerID == 0 || ownerids.Contains(entity.OwnerID))
            {
                return null;
            }

            if ((entity is SupplyDrop && entity.skinID == DEFENDER_SKIN_ID) || (config.Hackable.Enabled && entity is HackableLockedCrate crate && IsDefended(crate)))
            {
                if (Interface.CallHook("OnLootLockedEntity", player, entity) is true)
                {
                    return null;
                }

                if (!IsAlly(player, entity.OwnerID))
                {
                    if (CanMessage(player))
                    {
                        CreateMessage(player, entity is SupplyDrop ? "CannotLootIt" : "CannotLootCrate");
                    }

                    return true;
                }
            }

            return null;
        }

        private void OnBossSpawn(ScientistNPC boss)
        {
            if (TryGetNetworkId(boss, out NetworkableId id))
            {
                _boss.Add(id);
            }
        }

        private void OnBossKilled(ScientistNPC boss, BasePlayer attacker)
        {
            if (TryGetNetworkId(boss, out NetworkableId id))
            {
                _boss.Remove(id);
            }
        }

        private void OnPersonalHeliSpawned(BasePlayer player, PatrolHelicopter heli)
        {
            if (TryGetNetworkId(heli, out NetworkableId id))
            {
                _personal.Add(id);
            }
        }

        private void OnPersonalApcSpawned(BasePlayer player, BradleyAPC apc)
        {
            if (TryGetNetworkId(apc, out NetworkableId id))
            {
                _personal.Add(id);
            }
        }

        private void OnEntitySpawned(CH47Helicopter heli)
        {
            if (!config.CH47Gibs || heli == null) return;
            heli.serverGibs.guid = string.Empty;
        }

        #region SupplyDrops

        private void OnExplosiveDropped(BasePlayer player, SupplySignal ss, ThrownWeapon tw) => OnExplosiveThrown(player, ss, tw);

        private void OnExplosiveThrown(BasePlayer player, SupplySignal ss, ThrownWeapon tw)
        {
            if (player == null || ss == null || tw == null || !config.SupplyDrop.CanLockSkin(tw.skinID))
            {
                return;
            }

            if (tw.GetItem() is Item item && !config.SupplyDrop.CanLockSkin(item.skin))
            {
                return;
            }

            ss.OwnerID = player.userID;
            ss.skinID = DEFENDER_SKIN_ID;

            if (config.SupplyDrop.Bypass && !player.IsNearEnemyBase(ss.WorldSpaceBounds()))
            {
                var userid = player.userID;
                var position = ss.transform.position;
                var resourcePath = ss.EntityToCreate.resourcePath;

                ss.CancelInvoke(ss.Explode);
                ss.Invoke(() => Explode(ss, userid, position, resourcePath, player), 3f);
            }

            if (config.SupplyDrop.NotifyChat && !thrown.Contains(player.userID))
            {
                if (config.SupplyDrop.NotifyCooldown > 0)
                {
                    var userid = player.userID;
                    thrown.Add(userid);
                    timer.In(config.SupplyDrop.NotifyCooldown, () => thrown.Remove(userid));
                }
                foreach (var target in BasePlayer.activePlayerList)
                {
                    if (config.SupplyDrop.ThrownAt)
                    {
                        CreateMessage(target, "ThrownSupplySignalAt", player.displayName, PositionToGrid(player.transform.position));
                    }
                    else CreateMessage(target, "ThrownSupplySignal", player.displayName);
                }
            }

            if (config.SupplyDrop.NotifyConsole)
            {
                Puts(Localize("ThrownSupplySignalAt", null, player.displayName, PositionToGrid(player.transform.position)));
            }

            Interface.CallHook("OnModifiedSupplySignal", player, ss, tw);
        }

        private List<ulong> crateLock = new();
        private List<ulong> thrown = new();

        private void Explode(SupplySignal ss, ulong userid, Vector3 position, string resourcePath, BasePlayer player)
        {
            if (!ss.IsDestroyed)
            {
                var smokeDuration = config.SupplyDrop.Smoke > -1 ? config.SupplyDrop.Smoke : 4.5f;
                position = ss.transform.position;
                if (smokeDuration > 0f)
                {
                    ss.Invoke(ss.FinishUp, smokeDuration);
                    ss.SetFlagLocal(BaseEntity.Flags.On, true);
                    ss.SendNetworkUpdateImmediate();
                }
                else ss.FinishUp();
            }

            if (GameManager.server.CreateEntity(StringPool.Get(3632568684), position) is SupplyDrop drop)
            {
                drop.OwnerID = userid;
                drop.skinID = DEFENDER_SKIN_ID;
                drop.Spawn();
                drop.Invoke(() =>
                {
                    if (drop.IsDestroyed) return;
                    drop.OwnerID = userid;
                    drop.MakeLootable();
                    drop.RemoveParachute();
                }, 1f);

                if (config.SupplyDrop.LockTime > 0)
                {
                    OnSupplyDropLanded(drop);
                }
                else DelayedDestroySupplyDrop(drop);
            }
        }

        private void DelayedDestroySupplyDrop(SupplyDrop drop)
        {
            if (config.SupplyDrop.DestroyTime > 0f)
            {
                drop.Invoke(() =>
                {
                    if (!drop.IsDestroyed)
                    {
                        drop.Kill();
                    }
                }, config.SupplyDrop.DestroyTime);
            }
        }

        private void OnExcavatorSuppliesRequested(ExcavatorSignalComputer computer, BasePlayer player, CargoPlane plane)
        {
            SetupCargoPlane(plane, computer, player.userID);

            cargoPlanes.Add(plane);
        }

        private void OnRandomRaidWin(SupplyDrop drop, List<ulong> playerID)
        {
            if (drop)
            {
                if (playerID.Count > 0 && !drop.OwnerID.IsSteamId())
                {
                    drop.OwnerID = playerID[0];
                }
                drop.skinID = DEFENDER_SKIN_ID;
                OnSupplyDropLanded(drop);
            }
        }

        private void OnCargoPlaneSignaled(CargoPlane plane, SupplySignal ss)
        {
            if (ss?.skinID != DEFENDER_SKIN_ID)
            {
                return;
            }

            SetupCargoPlane(plane, ss, ss.OwnerID);

            if (config.SupplyDrop.Smoke > -1)
            {
                if (config.SupplyDrop.Smoke < 1)
                {
                    ss.FinishUp();
                }
                else NextTick(() =>
                {
                    if (ss != null && !ss.IsDestroyed)
                    {
                        ss.CancelInvoke(ss.FinishUp);
                        ss.Invoke(ss.FinishUp, config.SupplyDrop.Smoke);
                    }
                });
            }

            cargoPlanes.Add(plane);

            Interface.CallHook("OnModifiedCargoPlaneSignaled", plane, ss);
        }

        private void SetupCargoPlane(CargoPlane plane, BaseEntity entity, ulong userid)
        {
            float y = plane.transform.position.y;
            float j = config.SupplyDrop.DistanceFromSignal;

            if (config.SupplyDrop.LowDrop) y /= Core.Random.Range(2, 4); // Change Y, fast drop

            plane.transform.position = new Vector3(plane.transform.position.x, y, plane.transform.position.z);
            plane.startPos = new Vector3(plane.startPos.x, y, plane.startPos.z);

            if (j > -1)
            {
                plane.dropPosition = entity.transform.position + new Vector3(UnityEngine.Random.Range(-j, j), 0f, UnityEngine.Random.Range(-j, j));
                plane.endPos = plane.dropPosition + (plane.endPos - plane.startPos).normalized * (plane.dropPosition - plane.startPos).magnitude;
                //Vector3 b = plane.dropPosition - plane.startPos;
                //plane.endPos = plane.dropPosition + b.normalized * b.magnitude;
                plane.endPos.y = y;
            }
            else
            {
                plane.endPos = new Vector3(plane.endPos.x, y, plane.endPos.z);
                plane.dropPosition = entity.transform.position;
            }

            plane.dropPosition.y = 0f;
            plane.secondsToTake = Vector3.Distance(plane.startPos, plane.endPos) / Mathf.Clamp(config.SupplyDrop.Speed, 40f, World.Size);
            plane.OwnerID = userid;
            plane.skinID = DEFENDER_SKIN_ID;
        }

        private void OnSupplyDropDropped(SupplyDrop drop, CargoPlane plane)
        {
            if (plane?.skinID != DEFENDER_SKIN_ID)
            {
                return;
            }

            if (drop.TryGetComponent(out Rigidbody rb))
            {
                rb.linearDamping = Mathf.Clamp(config.SupplyDrop.Drag, 0.1f, 3f);
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }

            DelayedDestroySupplyDrop(drop);

            drop.OwnerID = plane.OwnerID;
            drop.skinID = DEFENDER_SKIN_ID;

            Interface.CallHook("OnModifiedSupplyDropDropped", drop, plane);
        }

        private void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (drop?.skinID != DEFENDER_SKIN_ID)
            {
                return;
            }

            DelayedDestroySupplyDrop(drop);

            drop.Invoke(() => drop.OwnerID = 0, config.SupplyDrop.LockTime);

            Interface.CallHook("OnModifiedSupplyDropLanded", drop);
        }

        private List<CargoPlane> cargoPlanes = new();

        private void OnEntitySpawned(SupplyDrop drop)
        {
            if (drop.skinID == DEFENDER_SKIN_ID)
            {
                DelayedDestroySupplyDrop(drop);
            }
            else OnHelpfulSupplyDropped(drop);
        }

        private void OnHelpfulSupplyDropped(SupplyDrop drop)
        {
            if (!config.SupplyDrop.HelpfulSupply || HelpfulSupply == null) return;
            if (drop == null || drop.IsDestroyed) return;
            foreach (var x in BasePlayer.allPlayerList) { if (x?.userID == drop.OwnerID) return; }
            cargoPlanes.RemoveAll(x => x == null || x.IsDestroyed || !x.OwnerID.IsSteamId());
            if (cargoPlanes.Count == 0) return;
            cargoPlanes.Sort((x, y) => x.Distance(drop).CompareTo(y.Distance(drop)));
            drop.OwnerID = cargoPlanes[0].OwnerID;
            drop.skinID = cargoPlanes[0].skinID;
            DelayedDestroySupplyDrop(drop);
        }

        #endregion SupplyDrops

        private void OnGuardedCrateEventEnded(BasePlayer player, HackableLockedCrate crate)
        {
            NextTick(() =>
            {
                if (crate != null && crate.OwnerID == 0 && CanLockHackableCrate(player, crate))
                {
                    crate.OwnerID = player.userID;

                    SetupHackableCrate(player, crate);
                }
            });
        }

        private bool CanLockHackableCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (!config.Hackable.Harbor && harbors.Exists(mi => IsInBounds(mi, crate.ServerPosition)))
            {
                return false;
            }
            return Interface.CallHook("OnLootLockedEntity", player, crate) is not true;
        }

        private void CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (crate != null && crate.OwnerID == 0 && CanLockHackableCrate(player, crate))
            {
                crate.OwnerID = player.userID;

                SetupHackableCrate(player, crate);
            }
        }

        #endregion Hooks

        #region Helpers

        private void SetupLaunchSite()
        {
            if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null || TerrainMeta.Path.Monuments.Count == 0)
            {
                timer.Once(10f, SetupLaunchSite);
                return;
            }

            foreach (var mi in TerrainMeta.Path.Monuments)
            {
                if (mi.name.Contains("harbor_1") || mi.name.Contains("harbor_2")) harbors.Add(mi);
                else if (mi.name.Contains("launch_site", CompareOptions.OrdinalIgnoreCase)) launchSite = mi;
            }
        }

        private void SetupHackableCrate(BasePlayer owner, HackableLockedCrate crate)
        {
            float hackSeconds = 0f;

            if (config.Hackable.Seconds && config.Hackable.Permissions.Count > 0)
            {
                foreach (var entry in config.Hackable.Permissions)
                {
                    if (permission.UserHasPermission(owner.UserIDString, entry.Permission))
                    {
                        if (entry.Value < HackableLockedCrate.requiredHackSeconds - hackSeconds)
                        {
                            hackSeconds = HackableLockedCrate.requiredHackSeconds - entry.Value;
                        }
                    }
                }

                crate.hackSeconds = hackSeconds;
            }

            NetworkableId id = crate.net.ID;
            ulong userid = owner.userID;
            string username = owner.displayName;
            string grid = PositionToGrid(owner.transform.position);

            _locked[id] = crate.OwnerID;

            if (config.Hackable.LockTime > 0f)
            {
                crate.Invoke(() =>
                {
                    crate.OwnerID = 0;
                    _locked.Remove(id);
                    Interface.CallHook("OnUnlockedEntity", crate, userid, id.Value, !HasNetworkId(crate));

                    if (config.Hackable.NotifyUnlocked && !crateLock.Contains(userid) && crate.inventory != null && !crate.inventory.IsEmpty())
                    {
                        if (config.Hackable.NotifyCooldown > 0)
                        {
                            crateLock.Add(userid);
                            timer.In(config.Hackable.NotifyCooldown, () => crateLock.Remove(userid));
                        }
                        foreach (var target in BasePlayer.activePlayerList)
                        {
                            CreateMessage(target, "CrateUnlocked", grid, username);
                        }
                    }
                }, config.Hackable.LockTime + (HackableLockedCrate.requiredHackSeconds - hackSeconds));
            }


            if (config.Hackable.NotifyLocked && !crateLock.Contains(userid))
            {
                if (config.Hackable.NotifyCooldown > 0)
                {
                    crateLock.Add(userid);
                    timer.In(config.Hackable.NotifyCooldown, () => crateLock.Remove(userid));
                }
                foreach (var target in BasePlayer.activePlayerList)
                {
                    CreateMessage(target, "CrateLocked", username, grid);
                }
            }
        }

        private void CancelDamage(HitInfo info)
        {
            info.damageTypes.Clear();
            info.DoHitEffects = false;
            info.DidHit = false;
        }

        private double nextMessageCooldownCleanupTime;
        private Dictionary<ulong, double> messageCooldowns = new();
        private bool CanMessage(BasePlayer player, double length = 10d)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextMessageCooldownCleanupTime)
            {
                nextMessageCooldownCleanupTime = now + 60d;
                using var userids = Pool.Get<PooledList<ulong>>();
                foreach (var pair in messageCooldowns)
                {
                    if (pair.Value <= now)
                    {
                        userids.Add(pair.Key);
                    }
                }
                foreach (ulong userid in userids)
                {
                    messageCooldowns.Remove(userid);
                }
            }
            if (messageCooldowns.TryGetValue(player.userID, out double expiresAt) && expiresAt > now)
            {
                return false;
            }
            messageCooldowns[player.userID] = now + length;
            return true;
        }

        public bool HasLockout(BasePlayer player, DamageEntryType damageEntryType, ulong skinid)
        {
            if (IsF15EventActive || config.Lockout.Exceptions.Contains(skinid))
            {
                return false;
            }

            if (!data.Lockouts.TryGetValue(player.UserIDString, out var lo))
            {
                return false;
            }

            double time = UI.GetLockoutTime(damageEntryType, lo, player.UserIDString);
            if (time <= 0d || HasPermission(player, "lootdefender.bypass.lockouts"))
            {
                return false;
            }

            if (CanMessage(player))
            {
                CreateMessage(player, damageEntryType == DamageEntryType.Bradley ? "LockedOutBradley" : "LockedOutHeli", FormatTime(time));
            }

            return true;
        }

        private string FormatTime(double seconds)
        {
            if (seconds < 0)
            {
                return "0s";
            }

            var ts = TimeSpan.FromSeconds(seconds);
            string format = "{0:D2}h {1:D2}m {2:D2}s";

            return string.Format(format, ts.Hours, ts.Minutes, ts.Seconds);
        }

        private void ApplyLockouts(DamageEntryType damageEntryType)
        {
            using var lockouts = Pool.Get<PooledList<KeyValuePair<string, Lockout>>>();
            double time = UI.GetLockoutTime(damageEntryType);
            lockouts.AddRange(data.Lockouts);
            foreach (var lo in lockouts)
            {
                bool update = false;
                int current = Epoch.Current;

                switch (damageEntryType)
                {
                    case DamageEntryType.Bradley:
                        {
                            if (lo.Value.Bradley - current > time)
                            {
                                lo.Value.Bradley = current + time;

                                update = true;
                            }
                            break;
                        }
                    case DamageEntryType.Heli:
                        {
                            if (lo.Value.Heli - current > time)
                            {
                                lo.Value.Heli = current + time;

                                update = true;
                            }
                            break;
                        }
                }

                if (!lo.Value.Any(current))
                {
                    data.Lockouts.Remove(lo.Key);
                    update = true;
                }

                if (update && TryParse(lo.Key, out ulong userid) && BasePlayer.TryFindByID(userid, out BasePlayer player) && player.IsConnected)
                {
                    UI.UpdateLockoutUI(player, damageEntryType);
                }
            }
        }

        public void TrySetLockout(string userid, BasePlayer player, DamageEntryType damageEntryType, ulong skinID)
        {
            if (IsF15EventActive || config.Lockout.Exceptions.Contains(skinID))
            {
                return;
            }

            if (permission.UserHasPermission(userid, "lootdefender.bypass.lockouts"))
            {
                return;
            }

            double time = UI.GetLockoutTime(damageEntryType);

            if (time <= 0)
            {
                return;
            }

            if (!data.Lockouts.TryGetValue(userid, out var lo))
            {
                data.Lockouts[userid] = lo = new();
            }

            int current = Epoch.Current;

            switch (damageEntryType)
            {
                case DamageEntryType.Bradley:
                    {
                        if (lo.Bradley <= current)
                        {
                            lo.Bradley = current + time;
                        }

                        break;
                    }
                case DamageEntryType.Heli:
                    {
                        if (lo.Heli <= current)
                        {
                            lo.Heli = current + time;
                        }

                        break;
                    }
            }

            if (lo.Any(current))
            {
                UI.UpdateLockoutUI(player, damageEntryType);
            }
            else
            {
                data.Lockouts.Remove(userid);
            }
        }

        private void LockoutLooters(HashSet<ulong> looters, Vector3 position, DamageEntryType damageEntryType, ulong skinID)
        {
            if (looters.Count == 0)
            {
                return;
            }

            HashSet<ulong> members = new(looters);
            HashSet<string> usernames = new();

            foreach (ulong looterId in looters)
            {
                var looter = RelationshipManager.FindByID(looterId);

                if (looter != null) usernames.Add(looter.displayName);

                TrySetLockout(looterId.ToString(), looter, damageEntryType, skinID);
                LockoutTeam(members, looterId, damageEntryType, skinID);
                LockoutClan(members, looter, looterId, damageEntryType, skinID);
            }

            SendDiscordMessage(members, usernames, position, damageEntryType);
        }

        private void LockoutTeam(HashSet<ulong> members, ulong looterId, DamageEntryType damageEntryType, ulong skinID)
        {
            if (!config.Lockout.Team || !RelationshipManager.ServerInstance.playerToTeam.TryGetValue(looterId, out var team))
            {
                return;
            }

            foreach (var memberId in team.members)
            {
                if (members.Contains(memberId))
                {
                    continue;
                }

                var member = RelationshipManager.FindByID(memberId);

                if (config.Lockout.Time > 0 && member != null && member.secondsSleeping > config.Lockout.Time * 60f)
                {
                    continue;
                }

                TrySetLockout(memberId.ToString(), member, damageEntryType, skinID);

                members.Add(memberId);
            }
        }

        private void LockoutClan(HashSet<ulong> members, BasePlayer looter, ulong looterId, DamageEntryType damageEntryType, ulong skinID)
        {
            if (!config.Lockout.Clan)
            {
                return;
            }

            if (looter != null && TryGetClan(looter, out IClan clan))
            {
                using var native = Pool.Get<PooledHashSet<ulong>>();
                foreach (var member in clan.Members)
                {
                    native.Add(member.SteamId);
                }
                native.Add(clan.Creator);

                foreach (var memberId in native)
                {
                    if (members.Contains(memberId))
                    {
                        continue;
                    }

                    var member = RelationshipManager.FindByID(memberId);

                    if (config.Lockout.Time > 0 && member != null && !member.IsConnected && member.secondsSleeping > config.Lockout.Time * 60f)
                    {
                        continue;
                    }

                    TrySetLockout(memberId.ToString(), member, damageEntryType, skinID);

                    members.Add(memberId);
                }
            }

            if (Clans?.Call("GetClanMembers", looterId) is not List<string> clanMembers)
            {
                return;
            }

            foreach (var memberIdString in clanMembers)
            {
                if (!TryParse(memberIdString, out ulong memberId) || members.Contains(memberId))
                {
                    continue;
                }

                var member = RelationshipManager.FindByID(memberId);

                if (config.Lockout.Time > 0 && member != null && !member.IsConnected && member.secondsSleeping > config.Lockout.Time * 60f)
                {
                    continue;
                }

                TrySetLockout(memberIdString, member, damageEntryType, skinID);

                members.Add(memberId);
            }
        }

        private bool HandleTeam(ulong userid)
        {
            if (!userid.IsSteamId())
            {
                return false;
            }

            if (TryBlockTeamChange(_apcAttackers, userid, "CannotLeaveBradley") || TryBlockTeamChange(_heliAttackers, userid, "CannotLeaveHeli"))
            {
                return true;
            }

            return false;
        }

        private bool TryBlockTeamChange(Dictionary<NetworkableId, List<DamageKey>> attackers, ulong userid, string messageKey)
        {
            foreach (var pair in attackers)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    var x = pair.Value[i];

                    if (x.userid != userid)
                    {
                        continue;
                    }

                    CreateMessage(x.GetAttacker(), messageKey);
                    return true;
                }
            }

            return false;
        }

        private bool IsDefended(PatrolHelicopter heli) => TryGetNetworkId(heli, out NetworkableId id) && (data.LootLock.ContainsKey(id) || data.Damage.ContainsKey(id));

        private bool IsDefended(BaseCombatEntity victim) => TryGetNetworkId(victim, out NetworkableId id) && (_locked.ContainsKey(id) || data.LootLock.ContainsKey(id));

        private void DoLockoutRemoves()
        {
            using var lockouts = Pool.Get<PooledList<KeyValuePair<string, Lockout>>>();
            lockouts.AddRange(data.Lockouts);
            int current = Epoch.Current;
            foreach (var (userid, lo) in lockouts)
            {
                if (lo.Bradley - current <= 0)
                {
                    lo.Bradley = 0;
                }

                if (lo.Heli - current <= 0)
                {
                    lo.Heli = 0;
                }

                if (!lo.Any(current))
                {
                    data.Lockouts.Remove(userid);
                }
            }
        }

        private void Unsubscribe()
        {
            Unsubscribe(nameof(OnGuardedCrateEventEnded));
            Unsubscribe(nameof(CanHackCrate));
            Unsubscribe(nameof(OnPlayerSleepEnded));
            Unsubscribe(nameof(OnCuiDraggableDrag));
            Unsubscribe(nameof(OnCrateSpawned));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnSupplyDropLanded));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnSupplyDropDropped));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnPlayerAttack));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(OnExplosiveDropped));
            Unsubscribe(nameof(OnExplosiveThrown));
            Unsubscribe(nameof(OnExcavatorSuppliesRequested));
            Unsubscribe(nameof(OnCargoPlaneSignaled));
            Unsubscribe(nameof(OnPersonalApcSpawned));
            Unsubscribe(nameof(OnPersonalHeliSpawned));
            Unsubscribe(nameof(OnPatrolHelicopterKill));
            Unsubscribe(nameof(OnRandomRaidWin));
        }

        private void SaveData()
        {
            DoLockoutRemoves();
            Interface.Oxide.DataFileSystem.WriteObject(Name, data, true);
        }

        private void LoadData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); } catch { }

            if (data == null)
            {
                data = new();
                SaveData();
            }

            data.Instance = this;
            data.EnsureInitialized();
        }

        private void RegisterPermissions()
        {
            foreach (var entry in config.Hackable.Permissions)
            {
                permission.RegisterPermission(entry.Permission, this);
            }

            permission.RegisterPermission("lootdefender.bypass.loot", this);
            permission.RegisterPermission("lootdefender.bypass.damage", this);
            permission.RegisterPermission("lootdefender.bypass.lockouts", this);
            permission.RegisterPermission("lootdefender.bypassnpclock", this);
            permission.RegisterPermission("lootdefender.bypasshelilock", this);
            permission.RegisterPermission("lootdefender.bypassbradleylock", this);

        }

        private static IClan GetClan(BasePlayer player)
        {
            if (player.clanId == 0)
            {
                return null;
            }
            if (player.serverClan == null)
            {
                ClanManager.ServerInstance?.Backend?.TryGet(player.clanId, out player.serverClan);
            }
            return player.serverClan;
        }

        private static bool TryGetClan(BasePlayer player, out IClan clan)
        {
            clan = GetClan(player);
            return clan != null;
        }

        private bool IsAlly(BasePlayer player, ulong first, ulong second)
        {
            if (player == null) player = RelationshipManager.FindByID(first);
            return player == null ? IsAlly(first, second) : IsAlly(player, second);
        }

        private bool IsAlly(BasePlayer player, ulong second)
        {
            if (config.Clans && TryGetClan(player, out IClan clan))
            {
                foreach (var member in clan.Members)
                {
                    if (member.SteamId == second)
                    {
                        return true;
                    }
                }
                if (clan.Creator == second)
                {
                    return true;
                }
            }
            return IsAlly(player.userID, second);
        }

        private struct LookupTime
        {
            public bool Result;
            public long ExpiresAt;
            public LookupTime(bool result, long expiresAt)
            {
                Result = result;
                ExpiresAt = expiresAt;
            }
        }

        private Dictionary<(ulong first, ulong second), LookupTime> allyLookupTimes = new();
        private bool IsAlly(ulong first, ulong second, long length = 1, int maxCount = 10)
        {
            if (first == second)
                return true;

            if (!config.UseAlly)
                return false;

            var now = Stopwatch.GetTimestamp();
            var t = first < second ? (first, second) : (second, first);
            if (allyLookupTimes.TryGetValue(t, out var lookup) && lookup.ExpiresAt > now) return lookup.Result;
            if (allyLookupTimes.Count >= maxCount) allyLookupTimes.Clear();

            bool result = first switch
            {
                _ when config.Teams && RelationshipManager.ServerInstance.playerToTeam.TryGetValue(first, out var team) && team.members.Contains(second) => true,
                _ when config.Clans && Clans != null && Clans?.Call(config.ClansHook, first, second) is true => true,
                _ when config.Friends && Friends != null && Friends?.Call("AreFriends", first, second) is true => true,
                _ => false
            };

            allyLookupTimes[t] = new(result, now + Stopwatch.Frequency * length);
            return result;
        }

        private static PooledList<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1) where T : BaseEntity
        {
            PooledList<T> entities = Pool.Get<PooledList<T>>();
            Vis.Entities(a, n, entities, m, QueryTriggerInteraction.Collide);
            entities.RemoveAll(x => x == null || x.IsDestroyed);
            return entities;
        }

        private bool CanRemoveFire(DamageEntryType damageEntryType)
        {
            if (damageEntryType == DamageEntryType.Bradley && !config.Bradley.RemoveFireFromCrates)
            {
                return false;
            }

            if (damageEntryType == DamageEntryType.Heli && !config.Helicopter.RemoveFireFromCrates)
            {
                return false;
            }

            return true;
        }

        private void RemoveFireFromCrate(LockedByEntCrate crate, DamageEntryType damageEntryType)
        {
            if (!CanRemoveFire(damageEntryType))
            {
                return;
            }

            BaseEntity lockingEntity = crate.lockingEnt;

            crate.SetLockingEnt(null);

            if (lockingEntity != null && !lockingEntity.IsDestroyed)
            {
                lockingEntity.Kill();
            }
        }

        private static bool TryGetOwner(LockInfo lockInfo, out ulong ownerid)
        {
            ownerid = lockInfo?.damageInfo?.OwnerID ?? 0uL;

            return ownerid.IsSteamId() && lockInfo?.damageInfo?.damageKeys?.Count > 0 && !lockInfo.IsLockOutdated;
        }

        private void RemoveLootLock(NetworkableId id, BaseEntity entity, LockInfo lockInfo)
        {
            data?.LootLock.Remove(id);

            ulong ownerid = lockInfo?.damageInfo?.OwnerID ?? 0uL;

            if (ownerid != 0uL && HasNetworkId(entity) && entity.OwnerID == ownerid)
            {
                entity.OwnerID = 0uL;
            }
        }

        private void ScheduleLootLock(NetworkableId id, BaseEntity entity, LockInfo lockInfo)
        {
            if (!TryGetOwner(lockInfo, out _))
            {
                RemoveLootLock(id, entity, lockInfo);
                return;
            }

            float time = lockInfo.ExpiresAt == default ? float.PositiveInfinity : (float)(lockInfo.ExpiresAt - DateTime.Now).TotalSeconds;

            if (lockInfo.ExpiresAt == default || time > 1f)
            {
                timer.Repeat(1f, 5, () =>
                {
                    if (entity == null || entity.IsDestroyed || !data.LootLock.ContainsKey(id) || !TryGetOwner(lockInfo, out ulong ownerid))
                    {
                        return;
                    }

                    entity.OwnerID = ownerid;
                });
            }

            if (lockInfo.ExpiresAt == default)
            {
                return;
            }

            if (time <= 0f)
            {
                RemoveLootLock(id, entity, lockInfo);
                return;
            }

            timer.Once(time, () =>
            {
                if (data.LootLock.ContainsKey(id))
                {
                    RemoveLootLock(id, entity, lockInfo);
                }
            });
        }

        private void LockLootEntity(BaseEntity entity, NetworkableId id, LockInfo lockInfo)
        {
            if (data.LootLock.ContainsKey(id) || !TryGetOwner(lockInfo, out ulong ownerid))
            {
                return;
            }

            entity.OwnerID = ownerid;
            data.LootLock[id] = lockInfo;
            ScheduleLootLock(id, entity, lockInfo);
        }

        private void LockInRadius(Vector3 position, LockInfo lockInfo, DamageEntryType damageEntryType)
        {
            bool canRemoveFire = CanRemoveFire(damageEntryType);
            float tooHotUntil = damageEntryType == DamageEntryType.Heli ? config.Helicopter.TooHotUntil : config.Bradley.TooHotUntil;
            using var entities = FindEntitiesOfType<BaseEntity>(position, damageEntryType == DamageEntryType.Heli ? 50f : 25f);
            foreach (var entity in entities)
            {
                if (entity.Is(out HelicopterDebris debris))
                {
                    if (!TryGetNetworkId(debris, out NetworkableId id))
                    {
                        continue;
                    }

                    LockLootEntity(debris, id, lockInfo);

                    if (tooHotUntil > -1f && tooHotUntil != HelicopterDebris.coolDownTime)
                    {
                        debris.CancelInvoke(debris.OnCooledDown);
                        debris.Invoke(debris.OnCooledDown, tooHotUntil);
                    }
                }
                else if (canRemoveFire && entity.Is(out FireBall fireball))
                {
                    fireball.Extinguish();
                }
            }
        }

        private void LockInRadius(Vector3 position, NetworkableId entityId, DamageInfo damageInfo, ulong playerSteamID)
        {
            var lockInfo = new LockInfo(damageInfo, GetLootLockDateTime(DamageEntryType.NPC));

            using var corpses = FindEntitiesOfType<LootableCorpse>(position, 3f);
            foreach (var corpse in corpses)
            {
                if (TryGetNetworkId(corpse, out NetworkableId corpseId) && corpse.playerSteamID == playerSteamID && !data.LootLock.ContainsKey(corpseId))
                {
                    LockLootEntity(corpse, corpseId, lockInfo);
                }
            }

            timer.Once(3f, () =>
            {
                if (data.Damage.ContainsKey(entityId))
                {
                    RemoveDamageInfo(entityId, damageInfo);
                }
            });
        }

        private void LockInRadius(Vector3 position, LockInfo lockInfo, ulong playerSteamID)
        {
            using var containers = FindEntitiesOfType<DroppedItemContainer>(position, 3f);
            foreach (var container in containers)
            {
                if (TryGetNetworkId(container, out NetworkableId id) && container.playerSteamID == playerSteamID && !data.LootLock.ContainsKey(id))
                {
                    LockLootEntity(container, id, lockInfo);
                }
            }
        }

        private int GetLockTime(DamageEntryType damageEntryType)
        {
            return damageEntryType == DamageEntryType.Bradley ? config.Bradley.LockTime : damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Npc.LockTime;
        }

        private int GetLootLockTime(DamageEntryType damageEntryType)
        {
            return damageEntryType == DamageEntryType.Bradley ? config.Bradley.CratesLockTime : damageEntryType == DamageEntryType.Heli ? config.Helicopter.CratesLockTime : config.Npc.LootLockTime;
        }

        private DateTime GetLootLockDateTime(DamageEntryType damageEntryType)
        {
            int seconds = GetLootLockTime(damageEntryType);
            return seconds <= 0 ? default : DateTime.Now.AddSeconds(seconds);
        }

        private static bool TryParse(StringView value, out float result) => TryParse(value.ToString(), out result);

        private static bool TryParse(string value, out int result) => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        private static bool TryParse(string value, out ulong result) => ulong.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result);

        private static bool TryParse(string value, out float result) => float.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        private static bool TryParse(string value, out double result) => double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        public static bool TryParse(string str, out Vector2 result) => TryParse((StringView)str, out result);

        public static bool TryParse(StringView value, out Vector2 result)
        {
            result = default;
            value = value.Trim('(', ')', ' ');
            int num = value.IndexOfAny(" ,");
            if (num == -1) return false;
            StringView x = value.Substring(0, num).Trim(' ', ',');
            StringView y = value.Substring(num + 1).Trim(' ', ',');
            return TryParse(x, out result.x) && TryParse(y, out result.y);
        }

        #endregion Helpers

        #region UI

        public enum UiType { Bradley, Heli, Invalid }

        private UiHandler UI;

        public class Vector2Converter : JsonConverter
        {
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return Vector2.zero;
                }

                if (reader.TokenType == JsonToken.String && TryParse(reader.Value as string, out Vector2 vector))
                {
                    return vector;
                }

                if (reader.TokenType == JsonToken.StartObject)
                {
                    var values = serializer.Deserialize(reader, typeof(Dictionary<string, float>)) as Dictionary<string, float>;
                    if (values != null && values.TryGetValue("x", out float x) && values.TryGetValue("y", out float y))
                    {
                        return new Vector2(x, y);
                    }
                }

                throw new JsonSerializationException($"Invalid Vector2 value for {objectType.Name}.");
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteValue(UiHandler.Vec2ToString((Vector2)value));
            }

            public override bool CanConvert(Type objectType) => objectType == typeof(Vector2);
        }

        public class UiOffsets
        {
            [JsonConverter(typeof(Vector2Converter))]
            public Vector2 Min { get; set; }

            [JsonConverter(typeof(Vector2Converter))]
            public Vector2 Max { get; set; }

            [JsonConverter(typeof(Vector2Converter))]
            public Vector2 NormalizedAnchor { get; set; }

            public UiOffsets()
            {
                Min = Vector2.zero;
                Max = Vector2.zero;
                NormalizedAnchor = Vector2.zero;
            }

            public UiOffsets(Vector2 min, Vector2 max, Vector2 normalizedAnchor)
            {
                Min = min;
                Max = max;
                NormalizedAnchor = normalizedAnchor;
            }

            public UiOffsets Clone() => new(Min, Max, NormalizedAnchor);

            public void Center()
            {
                float halfWidth = (Max.x - Min.x) * 0.5f;
                float halfHeight = (Max.y - Min.y) * 0.5f;
                Min = new Vector2(-halfWidth, -halfHeight);
                Max = new Vector2(halfWidth, halfHeight);
            }

            public void Move(Vector2 delta)
            {
                Min += delta;
                Max += delta;
            }

            public void SetAnchor(Vector2 anchor)
            {
                Center();
                NormalizedAnchor = anchor;
            }

            internal string MinString => UiHandler.Vec2ToString(Min);
            internal string MaxString => UiHandler.Vec2ToString(Max);
        }

        public class UiHandler
        {
            private const string BradleyPanelName = "Lockouts_UI_Bradley";
            private const string HeliPanelName = "Lockouts_UI_Heli";

            public string LOCKOUT_PARENT = "Overlay";
            internal LootDefender Instance;
            private StoredData data => Instance.data;
            private Configuration config => Instance.config;
            private Dictionary<ulong, Timers> InvokeTimers = new();
            private Timer SaveOffsetDataTimer;

            public static void AddCuiPanel(CuiElementContainer container, string color, string amin, string amax, string omin, string omax, string parent, string name, bool cursor = false, bool draggable = false)
            {
                var panel = new CuiPanel
                {
                    CursorEnabled = cursor,
                    Image = { Color = color },
                    RectTransform = { AnchorMin = amin, AnchorMax = amax, OffsetMin = omin, OffsetMax = omax }
                };

                if (!draggable)
                {
                    container.Add(panel, parent, name, name);
                    return;
                }

                var host = new CuiElement
                {
                    Name = name,
                    Parent = parent,
                    DestroyUi = name
                };

                if (panel.Image != null)
                {
                    host.Components.Add(panel.Image);
                }

                if (panel.RawImage != null)
                {
                    host.Components.Add(panel.RawImage);
                }

                if (panel.RectTransform != null)
                {
                    host.Components.Add(panel.RectTransform);
                }

                if (panel.CursorEnabled)
                {
                    host.Components.Add(new CuiNeedsCursorComponent());
                }

                if (panel.KeyboardEnabled)
                {
                    host.Components.Add(new CuiNeedsKeyboardComponent());
                }

                host.Components.Add(new CuiDraggableComponent
                {
                    LimitToParent = true,
                    MaxDistance = -1f,
                    AllowSwapping = false,
                    DropAnywhere = true,
                    DragAlpha = 0.98f,
                    ParentLimitIndex = 1,
                    Filter = null,
                    ParentPadding = "0 0",
                    AnchorOffset = "0 0",
                    KeepOnTop = false,
                    PositionRPC = CommunityEntity.DraggablePositionSendType.RelativeAnchor
                });

                container.Add(host);
            }

            public static void AddCuiElement(CuiElementContainer container, string text, int fontSize, TextAnchor align, string textColor, string amin, string amax, string omin, string omax, string parent, string name, bool bold = true)
            {
                container.Add(new CuiElement
                {
                    Name = name,
                    Parent = parent,
                    Components =
                    {
                        new CuiTextComponent { Text = text, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf", FontSize = fontSize, Align = align, Color = textColor },
                        new CuiRectTransformComponent { AnchorMin = amin, AnchorMax = amax, OffsetMin = omin, OffsetMax = omax }
                    }
                });
            }

            public static double ParseHexComponent(string hex, int index)
            {
                hex = hex?.Trim().TrimStart('#');
                return hex?.Length >= index + 2 && int.TryParse(hex.AsSpan(index, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value) ? value : 255d;
            }

            public static string ConvertHexToRGBA(string hex, float alpha) => FormattableString.Invariant($"{ParseHexComponent(hex, 0) / 255d} {ParseHexComponent(hex, 2) / 255d} {ParseHexComponent(hex, 4) / 255d} {Mathf.Clamp(alpha, 0f, 1f)}");

            public static string Vec2ToString(Vector2 vector) => FormattableString.Invariant($"{vector.x:R} {vector.y:R}");

            public UiType GetUiType(string name) => name switch { BradleyPanelName => UiType.Bradley, HeliPanelName => UiType.Heli, _ => UiType.Invalid };

            private string GetPanelName(UiType uiType) => uiType == UiType.Bradley ? BradleyPanelName : HeliPanelName;

            private DamageEntryType GetDamageEntryType(UiType uiType) => uiType == UiType.Bradley ? DamageEntryType.Bradley : DamageEntryType.Heli;

            private bool IsEnabled(UiType uiType) => uiType == UiType.Bradley ? config.UI.Bradley.Enabled : config.UI.Heli.Enabled;

            private UiOffsets GetConfigOffsets(UiType uiType) => uiType == UiType.Bradley ? config.UI.Bradley.Position : config.UI.Heli.Position;

            private string GetBackgroundColor(UiType uiType) => uiType == UiType.Bradley ? config.UI.Bradley.BackgroundColor : config.UI.Heli.BackgroundColor;

            private string GetTextColor(UiType uiType) => uiType == UiType.Bradley ? config.UI.Bradley.TextColor : config.UI.Heli.TextColor;

            private float GetAlpha(UiType uiType) => uiType == UiType.Bradley ? config.UI.Bradley.Alpha : config.UI.Heli.Alpha;

            private int GetFontSize(UiType uiType) => uiType == UiType.Bradley ? config.UI.Bradley.FontSize : config.UI.Heli.FontSize;

            private string GetMessageKey(UiType uiType) => uiType == UiType.Bradley ? "Time" : "Heli Time";

            public UiOffsets GetOffsets(string userid, UiType uiType, bool create = false)
            {
                Info info = GetSettings(userid);
                if (info.Offsets.TryGetValue(uiType, out UiOffsets offsets) && offsets != null)
                {
                    return offsets;
                }

                UiOffsets defaults = GetConfigOffsets(uiType);
                if (!create)
                {
                    return defaults;
                }

                info.Offsets[uiType] = offsets = defaults.Clone();
                return offsets;
            }

            public void SaveOffsetData()
            {
                if (SaveOffsetDataTimer is { Destroyed: false })
                {
                    SaveOffsetDataTimer.Reset();
                }
                else
                {
                    SaveOffsetDataTimer = Instance.timer.Once(5f, Instance.SaveData);
                }
            }

            private void Create(BasePlayer player, UiType uiType, string text)
            {
                UiOffsets offsets = GetOffsets(player.UserIDString, uiType);
                string anchor = Vec2ToString(offsets.NormalizedAnchor);
                string panelName = GetPanelName(uiType);
                var container = new CuiElementContainer();
                AddCuiPanel(container, ConvertHexToRGBA(GetBackgroundColor(uiType), GetAlpha(uiType)), anchor, anchor, offsets.MinString, offsets.MaxString, LOCKOUT_PARENT, panelName, false, true);
                AddCuiElement(container, text, GetFontSize(uiType), TextAnchor.MiddleCenter, ConvertHexToRGBA(GetTextColor(uiType), 1f), "0 0", "1 1", "0 0", "0 0", panelName, $"{panelName}_Text");
                CuiHelper.AddUi(player, container);
            }

            private void ShowLockoutUi(BasePlayer player, UiType uiType, Lockout lockout)
            {
                if (!IsEnabled(uiType))
                {
                    DestroyLockoutUI(player, uiType);
                    return;
                }

                double time = GetLockoutTime(GetDamageEntryType(uiType), lockout, player.UserIDString);
                if (time <= 0d)
                {
                    DestroyLockoutUI(player, uiType);
                    return;
                }

                string minutes = Math.Floor(TimeSpan.FromSeconds(time).TotalMinutes).ToString(CultureInfo.InvariantCulture);
                Create(player, uiType, Localize(GetMessageKey(uiType), player.UserIDString, minutes));
                SetLockoutUpdate(player, uiType);
            }

            public void ShowLockouts(BasePlayer player)
            {
                if (!HasNetworkConnection(player))
                {
                    return;
                }

                Info info = GetSettings(player.UserIDString);
                if (!info.Enabled || !info.Lockouts)
                {
                    DestroyLockoutUI(player);
                    return;
                }

                if (Instance.IsF15EventActive || Instance.HasPermission(player, "lootdefender.bypass.lockouts"))
                {
                    data.Lockouts.Remove(player.UserIDString);
                    DestroyLockoutUI(player);
                    return;
                }

                if (!data.Lockouts.TryGetValue(player.UserIDString, out Lockout lockout))
                {
                    DestroyLockoutUI(player);
                    return;
                }

                ShowLockoutUi(player, UiType.Bradley, lockout);
                ShowLockoutUi(player, UiType.Heli, lockout);
            }

            public void UpdateLockoutUI(BasePlayer player)
            {
                if (!HasNetworkConnection(player))
                {
                    return;
                }

                ShowLockouts(player);
            }

            public void UpdateLockoutUI(BasePlayer player, DamageEntryType damageEntryType) => UpdateLockoutUI(player, damageEntryType switch { DamageEntryType.Bradley => UiType.Bradley, DamageEntryType.Heli => UiType.Heli, _ => UiType.Invalid });

            public void UpdateLockoutUI(BasePlayer player, UiType uiType)
            {
                if (uiType == UiType.Invalid || !HasNetworkConnection(player))
                {
                    return;
                }

                Info info = GetSettings(player.UserIDString);
                if (!info.Enabled || !info.Lockouts)
                {
                    DestroyLockoutUI(player);
                    return;
                }

                if (Instance.IsF15EventActive || Instance.HasPermission(player, "lootdefender.bypass.lockouts"))
                {
                    data.Lockouts.Remove(player.UserIDString);
                    DestroyLockoutUI(player);
                    return;
                }

                if (!data.Lockouts.TryGetValue(player.UserIDString, out Lockout lockout))
                {
                    DestroyLockoutUI(player);
                    return;
                }

                ShowLockoutUi(player, uiType, lockout);
                if (!data.Lockouts.ContainsKey(player.UserIDString))
                {
                    DestroyLockoutUI(player, uiType == UiType.Bradley ? UiType.Heli : UiType.Bradley);
                }
            }

            public double GetLockoutTime(DamageEntryType damageEntryType) => damageEntryType switch { DamageEntryType.Bradley => config.Lockout.Bradley * 60d, DamageEntryType.Heli => config.Lockout.Heli * 60d, _ => 0d };

            public double GetLockoutTime(DamageEntryType damageEntryType, Lockout lockout, string playerId)
            {
                int current = Epoch.Current;
                double time = damageEntryType switch { DamageEntryType.Bradley => lockout.Bradley - current, DamageEntryType.Heli => lockout.Heli - current, _ => 0d };

                if (time <= 0d)
                {
                    if (damageEntryType == DamageEntryType.Bradley)
                    {
                        lockout.Bradley = 0d;
                    }
                    else if (damageEntryType == DamageEntryType.Heli)
                    {
                        lockout.Heli = 0d;
                    }
                }

                if (!lockout.Any(current))
                {
                    data.Lockouts.Remove(playerId);
                }

                return Math.Max(0d, time);
            }

            private void SetLockoutUpdate(BasePlayer player, UiType uiType)
            {
                if (!InvokeTimers.TryGetValue(player.userID, out Timers timers))
                {
                    InvokeTimers[player.userID] = timers = new();
                }

                Timer refresh = timers.Get(uiType);
                if (refresh == null || refresh.Destroyed)
                {
                    ulong userid = player.userID;
                    refresh = Instance.timer.Once(60f, () =>
                    {
                        if (InvokeTimers.TryGetValue(userid, out Timers current))
                        {
                            current.Set(uiType, null);
                            if (current.IsEmpty)
                            {
                                InvokeTimers.Remove(userid);
                            }
                        }

                        UpdateLockoutUI(player, uiType);
                    });
                    timers.Set(uiType, refresh);
                }
                else
                {
                    refresh.Reset();
                }
            }

            private void DestroyLockoutUpdate(ulong userid, UiType uiType)
            {
                if (!InvokeTimers.TryGetValue(userid, out Timers timers))
                {
                    return;
                }

                timers.Set(uiType, null);
                if (timers.IsEmpty)
                {
                    InvokeTimers.Remove(userid);
                }
            }

            public void DestroyLockoutUI(BasePlayer player, UiType uiType)
            {
                if (uiType == UiType.Invalid || !HasNetworkConnection(player))
                {
                    return;
                }

                DestroyLockoutUpdate(player.userID, uiType);
                CuiHelper.DestroyUi(player, GetPanelName(uiType));
            }

            public void DestroyLockoutUI(BasePlayer player)
            {
                if (!HasNetworkConnection(player))
                {
                    return;
                }

                DestroyLockoutUI(player, UiType.Bradley);
                DestroyLockoutUI(player, UiType.Heli);
            }

            public void DestroyAllLockoutUI()
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    CuiHelper.DestroyUi(player, BradleyPanelName);
                    CuiHelper.DestroyUi(player, HeliPanelName);
                }

                foreach (Timers timers in InvokeTimers.Values)
                {
                    timers.Bradley?.Destroy();
                    timers.Heli?.Destroy();
                }

                InvokeTimers.Clear();
                SaveOffsetDataTimer?.Destroy();
                SaveOffsetDataTimer = null;
            }

            public Info GetSettings(string playerId)
            {
                if (!data.UI.TryGetValue(playerId, out Info info) || info == null)
                {
                    data.UI[playerId] = info = new();
                }

                info.EnsureInitialized();
                return info;
            }

            public class Timers
            {
                public Timer Bradley;
                public Timer Heli;
                public bool IsEmpty => Bradley == null && Heli == null;
                public Timer Get(UiType uiType) => uiType == UiType.Bradley ? Bradley : Heli;
                public void Set(UiType uiType, Timer current)
                {
                    Timer previous = Get(uiType);
                    if (previous is { Destroyed: false } && current != previous) previous.Destroy();
                    if (uiType == UiType.Bradley) Bradley = current;
                    else Heli = current;
                }
            }

            public class Info
            {
                public bool Enabled = true;
                public bool Lockouts = true;
                public Dictionary<UiType, UiOffsets> Offsets = new();

                public void EnsureInitialized() => Offsets ??= new();
            }

            private string Localize(string key, string id, params object[] args) => Instance.Localize(key, id, args);
        }

        private void CommandUI(IPlayer user, string command, string[] args)
        {
            var player = user.Object as BasePlayer;
            if (player == null)
            {
                return;
            }

            var uii = UI.GetSettings(user.Id);
            uii.Enabled = !uii.Enabled;

            if (!uii.Enabled)
            {
                UI.DestroyLockoutUI(player);
            }
            else
            {
                UI.UpdateLockoutUI(player);
            }
        }

        private void CommandLootDefender(IPlayer user, string command, string[] args)
        {
            var player = user.Object as BasePlayer;

            if (user.IsServer || user.IsAdmin)
            {
                if (TryToggleSkin(args, user))
                {
                    return;
                }

                if (args.Length == 2)
                {
                    if (args[0] == "setbradleytime")
                    {
                        if (TryParse(args[1], out double time))
                        {
                            config.Lockout.Bradley = time;
                            SaveConfig();

                            user.Reply($"Cooldown changed to {time} minutes");
                            ApplyLockouts(DamageEntryType.Bradley);
                        }
                        else user.Reply($"The specified time '{args[1]}' is not a valid number.");
                    }
                    if (args[0] == "sethelitime")
                    {
                        if (TryParse(args[1], out double time))
                        {
                            config.Lockout.Heli = time;
                            SaveConfig();

                            user.Reply($"Cooldown changed to {time} minutes");
                            ApplyLockouts(DamageEntryType.Heli);
                        }
                        else user.Reply($"The specified time '{args[1]}' is not a valid number.");
                    }
                    else if (args[0] == "reset")
                    {
                        var value = args[1];

                        if (data.Lockouts.Remove(value))
                        {
                            UI.DestroyLockoutUI(RustCore.FindPlayerByIdString(value));
                            user.Reply($"Removed lockout for {value}");
                        }
                        else if (!value.IsSteamId())
                        {
                            user.Reply("You must specify a steam ID");
                        }
                        else user.Reply("Target not found");
                    }

                    if (args[0] == "debug") ToggleDebugMode(user, args[1]);
                }
                else if (args.Length == 1)
                {
                    if (args[0] == "unlock" && player != null)
                    {
                        UnlockNearbyLoot(player);
                    }
                    else if (args[0] == "debug")
                    {
                        ToggleDebugMode(user, null);
                    }
                }
            }

        }

        public void UnlockNearbyLoot(BasePlayer player)
        {
            using var locks = Pool.Get<PooledList<KeyValuePair<NetworkableId, LockInfo>>>();
            locks.AddRange(data.LootLock);

            foreach (var pair in locks)
            {
                LockInfo lockInfo = pair.Value;
                DamageInfo damageInfo = lockInfo?.damageInfo;
                BaseEntity entity = BaseNetworkable.serverEntities.Find(pair.Key) as BaseEntity;

                if (damageInfo == null)
                {
                    RemoveLootLock(pair.Key, entity, lockInfo);
                    continue;
                }

                Vector3 position = HasNetworkId(entity) ? entity.transform.position : damageInfo._position;
                if (player.Distance(position) >= 25f && !lockInfo.CanInteract(player))
                {
                    continue;
                }

                RemoveLootLock(pair.Key, entity, lockInfo);
                Message(player, $"Unlocked {(damageInfo.damageEntryType == DamageEntryType.Bradley ? "bradley" : damageInfo.damageEntryType == DamageEntryType.NPC ? "npc" : damageInfo.damageEntryType == DamageEntryType.Heli ? "heli" : "corpse")}");
            }
        }

        private void ToggleDebugMode(IPlayer user, string action)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                user.Reply($"Loot Defender (v{Version})");
                DebugMode = !DebugMode;
                string mode = DebugMode ? "On" : "Off";
                string toggle = DebugMode ? "off" : "on";
                user.Reply($"Debug mode: {mode}\nType again to toggle {toggle}.");
                return;
            }
            action = action.ToLowerInvariant();
            if (action is "on" or "1" or "true")
            {
                if (!DebugMode)
                {
                    DebugMode = true;
                    user.Reply("Debug mode: On");
                }
                else user.Reply("Debug mode was on already.");
            }
            else if (action is "off" or "0" or "false")
            {
                if (DebugMode)
                {
                    DebugMode = false;
                    user.Reply("Debug mode: Off");
                }
                else user.Reply("Debug mode was off already.");
            }
        }

        private void CommandLockouts(IPlayer user, string command, string[] args)
        {
            var player = user.Object as BasePlayer;

            if (data.Lockouts.TryGetValue(player.UserIDString, out var lo))
            {
                double time1 = UI.GetLockoutTime(DamageEntryType.Bradley, lo, player.UserIDString);

                if (time1 > 0f)
                {
                    CreateMessage(player, "LockedOutBradley", FormatTime(time1));
                }

                double time2 = UI.GetLockoutTime(DamageEntryType.Heli, lo, player.UserIDString);

                if (time2 > 0f)
                {
                    CreateMessage(player, "LockedOutHeli", FormatTime(time2));
                }
            }
            else
            {
                CreateMessage(player, "NoLockouts");
            }
        }

        #endregion UI

        #region Discord Messages

        private bool CanSendDiscordMessage()
        {
            if (string.IsNullOrEmpty(config.DiscordMessages.WebhookUrl) || config.DiscordMessages.WebhookUrl == "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks")
            {
                return false;
            }

            return true;
        }

        private static string PositionToGrid(Vector3 position) => MapHelper.PositionToString(position);

        private void SendDiscordMessage(HashSet<ulong> members, HashSet<string> usernames, Vector3 position, DamageEntryType damageEntryType)
        {
            if (config.DiscordMessages.NotifyConsole)
            {
                Puts($"{damageEntryType} killed by {string.Join(", ", usernames)} at {position}");
            }

            if (!CanSendDiscordMessage())
            {
                return;
            }

            Dictionary<string, string> players = new();

            foreach (ulong memberId in members)
            {
                var memberIdString = memberId.ToString();
                var memberName = covalence.Players.FindPlayerById(memberIdString)?.Name ?? memberIdString;

                if (config.DiscordMessages.BattleMetrics)
                {
                    players[memberName] = $"https://www.battlemetrics.com/rcon/players?filter%5Bsearch%5D={memberIdString}&filter%5Bservers%5D=false&filter%5BplayerFlags%5D=&sort=score&showServers=true";
                }
                else players[memberName] = memberIdString;
            }

            SendDiscordMessage(players, position, damageEntryType == DamageEntryType.Bradley ? Localize("BradleyKilled") : Localize("HeliKilled"));
        }

        private void SendDiscordMessage(Dictionary<string, string> members, Vector3 position, string text)
        {
            string grid = $"{PositionToGrid(position)} {position}";
            StringBuilder log = new();

            foreach (var member in members)
            {
                log.AppendLine($"[{DateTime.Now}] {member.Key} {member.Value} @ {grid}): {text}");
            }

            LogToFile("kills", log.ToString(), this);

            List<object> _fields = new();

            foreach (var member in members)
            {
                _fields.Add(new
                {
                    name = config.DiscordMessages.EmbedMessagePlayer,
                    value = $"[{member.Key}]({member.Value})",
                    inline = true
                });
            }

            _fields.Add(new
            {
                name = config.DiscordMessages.EmbedMessageMessage,
                value = text,
                inline = false
            });

            _fields.Add(new
            {
                name = ConVar.Server.hostname,
                value = grid,
                inline = false
            });

            _fields.Add(new
            {
                name = config.DiscordMessages.EmbedMessageServer,
                value = $"steam://connect/{ConVar.Server.ip}:{ConVar.Server.port}",
                inline = false
            });

            string json = JsonConvert.SerializeObject(_fields.ToArray());

            Interface.CallHook("API_SendFancyMessage", config.DiscordMessages.WebhookUrl, config.DiscordMessages.EmbedMessageTitle, config.DiscordMessages.MessageColor, json, null, this);
        }

        #endregion Discord Messages

        #region L10N

        private class NotifySettings
        {
            [JsonProperty(PropertyName = "Broadcast Kill Notification To Chat")]
            public bool NotifyChat = true;

            [JsonProperty(PropertyName = "Broadcast Kill Notification To Killer")]
            public bool NotifyKiller = true;

            [JsonProperty(PropertyName = "Broadcast Locked Notification To Chat", NullValueHandling = NullValueHandling.Ignore)]
            public bool? NotifyLocked = true;
        }

        private class HackPermission
        {
            [JsonProperty(PropertyName = "Permission")]
            public string Permission;

            [JsonProperty(PropertyName = "Hack Time")]
            public float Value;
        }

        private static List<HackPermission> DefaultHackPermissions
        {
            get
            {
                return new()
                {
                    new() { Permission = "lootdefender.hackedcrates.regular", Value = 750f },
                    new() { Permission = "lootdefender.hackedcrates.elite", Value = 500f },
                    new() { Permission = "lootdefender.hackedcrates.legend", Value = 300f },
                    new() { Permission = "lootdefender.hackedcrates.vip", Value = 120f },
                };
            }
        }

        private class HackableSettings
        {
            [JsonProperty(PropertyName = "Permissions", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<HackPermission> Permissions = DefaultHackPermissions;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled;

            [JsonProperty(PropertyName = "Permissions Enabled To Set Required Hack Seconds")]
            public bool Seconds = true;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime = 900;

            [JsonProperty(PropertyName = "Lock Hackable Crates At Harbor")]
            public bool Harbor;

            [JsonProperty(PropertyName = "Block Timer Increase On Damage To Laptop")]
            public bool Laptop = true;

            [JsonProperty(PropertyName = "Broadcast Locked Notification To Chat", NullValueHandling = NullValueHandling.Ignore)]
            public bool NotifyLocked;

            [JsonProperty(PropertyName = "Broadcast Unlocked Notification To Chat", NullValueHandling = NullValueHandling.Ignore)]
            public bool NotifyUnlocked;

            [JsonProperty(PropertyName = "Cooldown Between Notifications For Each Player")]
            public float NotifyCooldown;
        }

        private class BradleySettings
        {
            [JsonProperty(PropertyName = "Allow Locking Bradley With These Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HashSet<ulong> IncludedSkins = new();

            internal bool CanLockSkin(ulong skin) => skin == 0uL ? LockWorldly : IncludedSkins.Contains(skin);

            [JsonProperty(PropertyName = "Automatically Detected Skins (Review Only - Does Not Enable Locking)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, HashSet<ulong>> ReviewableSkins = new(); // key=entity.GetType().Name, values=any skins associated with that type name.

            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages = new();

            [JsonProperty(PropertyName = "Damage Lock Threshold")]
            public float Threshold = 0.2f;

            internal bool IsEnabled => Threshold > 0f;

            [JsonProperty(PropertyName = "Harvest Too Hot Until (0 = Never)")]
            public float TooHotUntil = 480f;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime = 900;

            [JsonProperty(PropertyName = "Crates Lock For X Seconds (0 = Forever)")]
            public int CratesLockTime = int.MinValue;

            [JsonProperty(PropertyName = "Remove Fire From Crates")]
            public bool RemoveFireFromCrates = true;

            [JsonProperty(PropertyName = "Lock Bradley At Launch Site")]
            public bool LockLaunchSite = true;

            [JsonProperty(PropertyName = "Lock Bradley At Harbor")]
            public bool LockHarbor;

            [JsonProperty(PropertyName = "Lock Bradley From Personal Apc Plugin")]
            public bool LockPersonal = true;

            [JsonProperty(PropertyName = "Lock Bradley From Monument Bradley Plugin")]
            public bool LockMonument = true;

            [JsonProperty(PropertyName = "Lock Bradley From Convoy Plugin")]
            public bool LockConvoy = true;

            [JsonProperty(PropertyName = "Lock Bradley From Bradley Tiers Plugin")]
            public bool LockBradleyTiers;

            [JsonProperty(PropertyName = "Lock Bradley From Everywhere Else")]
            public bool LockWorldly = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly;

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP = 0.0;

            [JsonProperty(PropertyName = "XP Reward")]
            public double XP = 0.0;

            [JsonProperty(PropertyName = "ShoppyStock Reward Value")]
            public double SS;

            [JsonProperty(PropertyName = "ShoppyStock Shop Name")]
            public string ShoppyStockShopName = "";
        }

        private class HelicopterSettings
        {
            [JsonProperty(PropertyName = "Allow Locking Heli With These Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HashSet<ulong> IncludedSkins = new() { 420 };

            internal bool CanLockSkin(ulong skin) => skin == 0uL ? LockWorldly : IncludedSkins.Contains(skin);

            [JsonProperty(PropertyName = "Automatically Detected Skins (Review Only - Does Not Enable Locking)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, HashSet<ulong>> ReviewableSkins = new(); // key=entity.GetType().Name, values=any skins associated with that type name.

            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages = new();

            [JsonProperty(PropertyName = "Damage Lock Threshold")]
            public float Threshold = 0.2f;

            internal bool IsEnabled => Threshold > 0f;

            [JsonProperty(PropertyName = "Harvest Too Hot Until (0 = Never)")]
            public float TooHotUntil = 480f;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime = 900;

            [JsonProperty(PropertyName = "Crates Lock For X Seconds (0 = Forever)")]
            public int CratesLockTime = int.MinValue;

            [JsonProperty(PropertyName = "Remove Fire From Crates")]
            public bool RemoveFireFromCrates = true;

            [JsonProperty(PropertyName = "Lock Heli From Convoy Plugin")]
            public bool LockConvoy = true;

            [JsonProperty(PropertyName = "Lock Heli At Harbor")]
            public bool? LockHarbor = null;

            [JsonProperty(PropertyName = "Lock Heli From Personal Heli Plugin")]
            public bool LockPersonal = true;

            [JsonProperty(PropertyName = "Lock Heli From Everywhere Else")]
            public bool LockWorldly = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly;

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP = 0.0;

            [JsonProperty(PropertyName = "XP Reward")]
            public double XP = 0.0;

            [JsonProperty(PropertyName = "ShoppyStock Reward Value")]
            public double SS;

            [JsonProperty(PropertyName = "ShoppyStock Shop Name")]
            public string ShoppyStockShopName = "";
        }

        private class NpcSettings
        {
            [JsonProperty(PropertyName = "Reward Distance Multiplier")]
            public DistanceMultiplierSettings Distance = new();

            [JsonProperty(PropertyName = "Reward Weapon Multiplier", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, double> WeaponMultipliers = new()
            {
                { "knife.skinning", 1.0 },
                { "gun.water", 1.0 },
                { "pistol.water", 1.0 },
                { "candycaneclub", 1.0 },
                { "snowball", 1.0 },
                { "snowballgun", 1.0 },
                { "rifle.ak", 1.0 },
                { "rifle.ak.diver", 1.0 },
                { "rifle.ak.ice", 1.0 },
                { "grenade.beancan", 1.0 },
                { "rifle.bolt", 1.0 },
                { "bone.club", 1.0 },
                { "knife.bone", 1.0 },
                { "bow.hunting", 1.0 },
                { "salvaged.cleaver", 1.0 },
                { "bow.compound", 1.0 },
                { "crossbow", 1.0 },
                { "shotgun.double", 1.0 },
                { "pistol.eoka", 1.0 },
                { "grenade.f1", 1.0 },
                { "flamethrower", 1.0 },
                { "grenade.flashbang", 1.0 },
                { "pistol.prototype17", 1.0 },
                { "multiplegrenadelauncher", 1.0 },
                { "mace.baseballbat", 1.0 },
                { "knife.butcher", 1.0 },
                { "pitchfork", 1.0 },
                { "vampire.stake", 1.0 },
                { "hmlmg", 1.0 },
                { "homingmissile.launcher", 1.0 },
                { "knife.combat", 1.0 },
                { "rifle.l96", 1.0 },
                { "rifle.lr300", 1.0 },
                { "lmg.m249", 1.0 },
                { "rifle.m39", 1.0 },
                { "pistol.m92", 1.0 },
                { "mace", 1.0 },
                { "machete", 1.0 },
                { "grenade.molotov", 1.0 },
                { "smg.mp5", 1.0 },
                { "pistol.nailgun", 1.0 },
                { "paddle", 1.0 },
                { "shotgun.waterpipe", 1.0 },
                { "pistol.python", 1.0 },
                { "pistol.revolver", 1.0 },
                { "rocket.launcher", 1.0 },
                { "shotgun.pump", 1.0 },
                { "pistol.semiauto", 1.0 },
                { "rifle.semiauto", 1.0 },
                { "smg.2", 1.0 },
                { "shotgun.spas12", 1.0 },
                { "speargun", 1.0 },
                { "spear.stone", 1.0 },
                { "longsword", 1.0 },
                { "salvaged.sword", 1.0 },
                { "smg.thompson", 1.0 },
                { "spear.wooden", 1.0 }
            };

            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages = new() { NotifyLocked = false };

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Damage Lock Threshold")]
            public float Threshold = 0.2f;

            internal bool IsEnabledWithThreshold => Enabled && Threshold > 0f;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime;

            [JsonProperty(PropertyName = "Loot Lock For X Seconds (0 = Forever)")]
            public int LootLockTime = int.MinValue;

            [JsonProperty(PropertyName = "Minimum Starting Health Requirement")]
            public float Min;

            [JsonProperty(PropertyName = "Lock BossMonster Npcs")]
            public bool BossMonster;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly = true;

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP = 0.0;

            [JsonProperty(PropertyName = "XP Reward")]
            public double XP = 0.0;

            [JsonProperty(PropertyName = "ShoppyStock Reward Value")]
            public double SS;

            [JsonProperty(PropertyName = "ShoppyStock Shop Name")]
            public string ShoppyStockShopName = "";
        }

        private class DistanceMultiplierSettings
        {
            [JsonProperty(PropertyName = "400 meters")]
            public float meters400 = 1f;

            [JsonProperty(PropertyName = "300 meters")]
            public float meters300 = 1f;

            [JsonProperty(PropertyName = "200 meters")]
            public float meters200 = 1f;

            [JsonProperty(PropertyName = "100 meters")]
            public float meters100 = 1f;

            [JsonProperty(PropertyName = "75 meters")]
            public float meters75 = 1f;

            [JsonProperty(PropertyName = "50 meters")]
            public float meters50 = 1f;

            [JsonProperty(PropertyName = "25 meters")]
            public float meters25 = 1f;

            [JsonProperty(PropertyName = "under")]
            public float under = 1f;

            public double GetDistanceMult(float distance) =>
                distance >= 400 ? meters400 :
                distance >= 300 ? meters300 :
                distance >= 200 ? meters200 :
                distance >= 100 ? meters100 :
                distance >= 75 ? meters75 :
                distance >= 50 ? meters50 :
                distance >= 25 ? meters25 :
                under;
        }

        private class SupplyDropSettings
        {
            [JsonProperty(PropertyName = "Allow Locking Signals With These Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> IncludedSkins = new();

            internal bool CanLockSkin(ulong skin) => skin == 0uL ? LockWorldly : IncludedSkins.Contains(skin);

            [JsonProperty(PropertyName = "Lock Supply Drops To Players")]
            public bool Lock = true;

            [JsonProperty(PropertyName = "Lock Supply Drops From Excavator")]
            public bool Excavator = true;

            [JsonProperty(PropertyName = "Lock Supply Drops From Helpful Supply Plugin")]
            public bool HelpfulSupply;

            [JsonProperty(PropertyName = "Lock Supply Drops From Npc Random Raids Plugin")]
            public bool NpcRandomRaids;

            [JsonProperty(PropertyName = "Lock Supply Drops From Everywhere Else")]
            public bool LockWorldly = true;

            [JsonProperty(PropertyName = "Lock To Player For X Seconds (0 = Forever)")]
            public float LockTime;

            [JsonProperty(PropertyName = "Supply Drop Drag")]
            public float Drag = 0.6f;

            [JsonProperty(PropertyName = "Show Grid In Thrown Notification")]
            public bool ThrownAt;

            [JsonProperty(PropertyName = "Show Thrown Notification In Chat")]
            public bool NotifyChat;

            [JsonProperty(PropertyName = "Show Notification In Server Console")]
            public bool NotifyConsole;

            [JsonProperty(PropertyName = "Cooldown Between Notifications For Each Player")]
            public float NotifyCooldown;

            [JsonProperty(PropertyName = "Cargo Plane Speed (Meters Per Second)")]
            public float Speed = 40f;

            [JsonProperty(PropertyName = "Cargo Plane Low Altitude Drop")]
            public bool LowDrop = true;

            [JsonProperty(PropertyName = "Bypass Spawning Cargo Plane")]
            public bool Bypass;

            [JsonProperty(PropertyName = "Smoke Duration")]
            public float Smoke = -1f;

            [JsonProperty(PropertyName = "Maximum Drop Distance From Signal")]
            public float DistanceFromSignal = 20;

            [JsonProperty(PropertyName = "Destroy Drop After X Seconds")]
            public float DestroyTime;
        }

        private class DamageReportSettings
        {
            [JsonProperty(PropertyName = "Hex Color - Single Player")]
            public string SinglePlayer = "#6d88ff";

            [JsonProperty(PropertyName = "Hex Color - Team")]
            public string Team = "#ff804f";

            [JsonProperty(PropertyName = "Hex Color - Ok")]
            public string Ok = "#88ff6d";

            [JsonProperty(PropertyName = "Hex Color - Not Ok")]
            public string NotOk = "#ff5716";
        }

        public class PluginSettingsBaseLockout
        {
            [JsonProperty(PropertyName = "Bypass During F15 Server Wipe Event")]
            public bool F15;

            [JsonProperty(PropertyName = "Command To See Lockout Times")]
            public string Command = "lockouts";

            [JsonProperty(PropertyName = "Time Between Bradley In Minutes")]
            public double Bradley;

            [JsonProperty(PropertyName = "Time Between Heli In Minutes")]
            public double Heli;

            [JsonProperty(PropertyName = "Lockout Entire Team")]
            public bool Team = true;

            [JsonProperty(PropertyName = "Lockout Entire Clan")]
            public bool Clan = true;

            [JsonProperty(PropertyName = "Exclude Members Offline For More Than X Minutes")]
            public float Time = 15f;

            [JsonProperty(PropertyName = "Lockouts Ignored For Entities With Skin ID", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Exceptions = new();
        }

        public class UIBradleyLockoutSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Position")]
            public UiOffsets Position = new(new Vector2(-38.4f, -18.9f), new Vector2(38.4f, 18.9f), new Vector2(0.966f, 0.3425f));

            [JsonProperty(PropertyName = "Bradley Background Color")]
            public string BackgroundColor = "#A52A2A";

            [JsonProperty(PropertyName = "Bradley Text Color")]
            public string TextColor = "#FFFF00";

            [JsonProperty(PropertyName = "Panel Alpha")]
            public float Alpha = 1f;

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize = 18;
        }

        public class UIHeliLockoutSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = true;

            [JsonProperty(PropertyName = "Position")]
            public UiOffsets Position = new(new Vector2(-38.4f, -18.9f), new Vector2(38.4f, 18.9f), new Vector2(0.916f, 0.3425f));

            [JsonProperty(PropertyName = "Heli Background Color")]
            public string BackgroundColor = "#1F51FF";

            [JsonProperty(PropertyName = "Heli Text Color")]
            public string TextColor = "#FFFF00";

            [JsonProperty(PropertyName = "Panel Alpha")]
            public float Alpha = 1f;

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize = 18;
        }

        public class UISettings
        {
            [JsonProperty(PropertyName = "Command To Toggle UI")]
            public string Command = "lockui";

            [JsonProperty(PropertyName = "Bradley")]
            public UIBradleyLockoutSettings Bradley = new();

            [JsonProperty(PropertyName = "Heli")]
            public UIHeliLockoutSettings Heli = new();
        }

        public class DiscordMessagesSettings
        {
            [JsonProperty(PropertyName = "Message - Webhook URL")]
            public string WebhookUrl = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";

            [JsonProperty(PropertyName = "Message - Embed Color (DECIMAL)")]
            public int MessageColor = 3329330;

            [JsonProperty(PropertyName = "Embed_MessageTitle")]
            public string EmbedMessageTitle = "Lockouts";

            [JsonProperty(PropertyName = "Embed_MessagePlayer")]
            public string EmbedMessagePlayer = "Player";

            [JsonProperty(PropertyName = "Embed_MessageMessage")]
            public string EmbedMessageMessage = "Message";

            [JsonProperty(PropertyName = "Embed_MessageServer")]
            public string EmbedMessageServer = "Connect via Steam:";

            [JsonProperty(PropertyName = "Add BattleMetrics Link")]
            public bool BattleMetrics = true;

            [JsonProperty(PropertyName = "Show Notification In Server Console")]
            public bool NotifyConsole;
        }

        private class Configuration
        {
            [JsonProperty(PropertyName = "Bradley Settings")]
            public BradleySettings Bradley = new();

            [JsonProperty(PropertyName = "Helicopter Settings")]
            public HelicopterSettings Helicopter = new();

            [JsonProperty(PropertyName = "Hackable Crate Settings")]
            public HackableSettings Hackable = new();

            [JsonProperty(PropertyName = "Npc Settings")]
            public NpcSettings Npc = new();

            [JsonProperty(PropertyName = "Supply Drop Settings")]
            public SupplyDropSettings SupplyDrop = new();

            [JsonProperty(PropertyName = "Damage Report Settings")]
            public DamageReportSettings Report = new();

            [JsonProperty(PropertyName = "Player Lockouts (0 = ignore)")]
            public PluginSettingsBaseLockout Lockout = new();

            [JsonProperty(PropertyName = "Lockout UI")]
            public UISettings UI = new();

            [JsonProperty(PropertyName = "Discord Messages")]
            public DiscordMessagesSettings DiscordMessages = new();

            [JsonProperty(PropertyName = "Disable CH47 Gibs")]
            public bool CH47Gibs;

            [JsonProperty(PropertyName = "Chat ID")]
            public ulong ChatID;

            [JsonProperty(PropertyName = "Use Clans")]
            public bool Clans = true;

            [JsonProperty(PropertyName = "Clans Hook")]
            public string ClansHook = "IsMemberOrAlly";

            [JsonProperty(PropertyName = "Use Friends")]
            public bool Friends = true;

            [JsonProperty(PropertyName = "Use Teams")]
            public bool Teams = true;

            internal bool UseAlly => Clans || Friends || Teams;

            internal bool BradleyOrHelicopterIsEnabled => Bradley.IsEnabled || Helicopter.IsEnabled;
        }

        private Configuration config;
        private bool canSaveConfig = true;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            canSaveConfig = false;
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
                NormalizeConfig();
                canSaveConfig = true;
                SaveConfig();
            }
            catch (Exception ex)
            {
                Puts(ex.ToString());
                LoadDefaultConfig();
            }
        }

        private void NormalizeConfig()
        {
            if (string.IsNullOrWhiteSpace(config.ClansHook)) // must set it false to disable.
            {
                config.Clans = false;
                config.ClansHook = "IsMemberOrAlly";
            }
            config.Bradley.IncludedSkins ??= new();
            config.Bradley.ReviewableSkins ??= new();
            config.Helicopter.IncludedSkins ??= new();
            config.Helicopter.ReviewableSkins ??= new();
            config.SupplyDrop.IncludedSkins ??= new();
            config.UI ??= new();
            config.UI.Bradley ??= new();
            config.UI.Heli ??= new();
            NormalizeUiPosition(ref config.UI.Bradley.Position, new UiOffsets(new Vector2(-38.4f, -18.9f), new Vector2(38.4f, 18.9f), new Vector2(0.966f, 0.3425f)));
            NormalizeUiPosition(ref config.UI.Heli.Position, new UiOffsets(new Vector2(-38.4f, -18.9f), new Vector2(38.4f, 18.9f), new Vector2(0.916f, 0.3425f)));
            if (config.Bradley.Threshold > 1f) config.Bradley.Threshold /= 100f;
            if (config.Helicopter.Threshold > 1f) config.Helicopter.Threshold /= 100f;
            if (config.Npc.Threshold > 1f) config.Npc.Threshold /= 100f;
            if (!config.Helicopter.LockHarbor.HasValue) config.Helicopter.LockHarbor = config.Bradley.LockHarbor;
            if (!config.Npc.Messages.NotifyLocked.HasValue) config.Npc.Messages.NotifyLocked = false;
            if (config.Helicopter.CratesLockTime == int.MinValue) config.Helicopter.CratesLockTime = config.Helicopter.LockTime; // retroactively apply existing LockTime
            if (config.Bradley.CratesLockTime == int.MinValue) config.Bradley.CratesLockTime = config.Bradley.LockTime;
            if (config.Npc.LootLockTime == int.MinValue) config.Npc.LootLockTime = config.Npc.LockTime;
            config.Bradley.IncludedSkins.Remove(0uL);
            config.Helicopter.IncludedSkins.Remove(0uL);
            config.SupplyDrop.IncludedSkins.Remove(0uL);
        }

        private static void NormalizeUiPosition(ref UiOffsets position, UiOffsets defaults)
        {
            if (position == null || position.Max.x <= position.Min.x || position.Max.y <= position.Min.y)
            {
                position = defaults;
            }
        }

        protected override void SaveConfig()
        {
            if (canSaveConfig)
            {
                Config.WriteObject(config);
            }
        }

        protected override void LoadDefaultConfig() => config = new();

        private bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new()
            {
                ["NoPermission"] = "You do not have permission to use this command!",
                ["DamageReport"] = "Damage report for {0}",
                ["DamageTime"] = "{0} was taken down after {1} seconds",
                ["CannotLoot"] = "You cannot loot this as major damage was not from you.",
                ["CannotLootIt"] = "You cannot loot this supply drop.",
                ["CannotLootCrate"] = "You cannot loot this crate.",
                ["CannotMine"] = "You cannot mine this as major damage was not from you.",
                ["CannotDamageThis"] = "You cannot damage this!",
                ["Locked Heli"] = "{0}: Heli has been locked to <color=#FF0000>{1}</color> and their team",
                ["Locked Bradley"] = "{0}: Bradley has been locked to <color=#FF0000>{1}</color> and their team",
                ["Locked Npc"] = "{0}: {1} has been locked to <color=#FF0000>{2}</color> and their team",
                ["Helicopter"] = "Heli",
                ["BradleyAPC"] = "Bradley",
                ["ThrownSupplySignal"] = "{0} has thrown a supply signal!",
                ["ThrownSupplySignalAt"] = "{0} in {1} has thrown a supply signal!",
                ["Format"] = "<color=#C0C0C0>{0:0.00}</color> (<color=#C3FBFE>{1:0.00}%</color>)",
                ["CannotLeaveBradley"] = "You cannot leave your team until the Bradley is destroyed!",
                ["CannotLeaveHeli"] = "You cannot leave your team until the Heli is destroyed!",
                ["LockedOutBradley"] = "You are locked out from Bradley for {0}",
                ["LockedOutHeli"] = "You are locked out from Heli for {0}",
                ["NoLockouts"] = "You have no lockouts.",
                ["Time"] = "{0}m",
                ["Heli Time"] = "{0}m",
                ["HeliKilled"] = "A heli was killed.",
                ["BradleyKilled"] = "A bradley was killed.",
                ["BradleyUnlocked"] = "The bradley at {0} has been unlocked.",
                ["HeliUnlocked"] = "The heli at {0} has been unlocked.",
                ["FirstLock"] = "First locked to {0} at {1}% threshold",
                ["CrateLocked"] = "A crate has been locked to {0} at {1}",
                ["CrateUnlocked"] = "The crate at {0} is no longer locked to {1}",
                ["NpcUnlocked"] = "{0} at {1} has been unlocked.",
                ["ShoppyStockReward"] = "Added {0} {1} to your account.",
            }, this, "en");

            lang.RegisterMessages(new()
            {
                ["NoPermission"] = "У вас нет разрешения на использование этой команды!",
                ["DamageReport"] = "Нанесенный урон по {0}",
                ["DamageTime"] = "{0} был уничтожен за {1} секунд",
                ["CannotLoot"] = "Это не ваш лут, основная часть урона насена не вами.",
                ["CannotLootIt"] = "Вы не можете открыть этот ящик с припасами.",
                ["CannotLootCrate"] = "Вы не можете разграбить кратэ.",
                ["CannotMine"] = "Вы не можете добывать это, основная часть урона насена не вами.",
                ["CannotDamageThis"] = "Вы не можете повредить это!",
                ["Locked Heli"] = "{0}: Этот патрульный вертолёт принадлежит <color=#FF0000>{1}</color> и участникам команды",
                ["Locked Bradley"] = "{0}: Этот танк принадлежит <color=#FF0000>{1}</color> и участникам команды",
                ["Locked Npc"] = "{0}: {1} заблокирован на <color=#FF0000>{2}</color> и его команду.",
                ["Helicopter"] = "Патрульному вертолету",
                ["BradleyAPC"] = "Танку",
                ["ThrownSupplySignal"] = "{0} запросил сброс припасов!",
                ["ThrownSupplySignalAt"] = "{0} {1} запросил сброс припасов!",
                ["Format"] = "<color=#C0C0C0>{0:0.00}</color> (<color=#C3FBFE>{1:0.00}%</color>)",
                ["CannotLeaveBradley"] = "Вы не можете покинуть команду, пока танк не будет уничтожен!",
                ["CannotLeaveHeli"] = "Вы не можете покинуть свою команду, пока Heli не будет уничтожен!",
                ["LockedOutBradley"] = "Вы заблокированы от танка на {0}",
                ["LockedOutHeli"] = "Вы заблокированы в Heli на {0}",
                ["NoLockouts"] = "У тебя нет замок.",
                ["Time"] = "{0} м",
                ["Heli Time"] = "{0} м",
                ["HeliKilled"] = "Вертолёт был уничтожен.",
                ["BradleyKilled"] = "Танк был уничтожен.",
                ["BradleyUnlocked"] = "Танк на {0} разблокирован.",
                ["HeliUnlocked"] = "Вертолёт на {0} разблокирован.",
                ["FirstLock"] = "Добыча заблокирована на игрока {0}, потому что он нанёс {1}% урона.",
                ["CannotLootCrate"] = "Вы не можете ограбить этот ящик.",
                ["CrateLocked"] = "Ящик в квадрате {1} заблокирован на {0}",
                ["CrateUnlocked"] = "Ящик в квадрате {0} больше не заблокирован на {1}",
                ["NpcUnlocked"] = "{0} в координатах {1} разблокирован.",
                ["ShoppyStockReward"] = "Добавлено {0} {1} в ваш аккаунт.",

            }, this, "ru");
        }

        private string Localize(string key, string id = null, params object[] args)
        {
            string message = id == "server_console" || id == null ? RemoveFormatting(lang.GetMessage(key, this, id)) : lang.GetMessage(key, this, id);

            return args.Length > 0 ? string.Format(message, args) : message;
        }

        public string RemoveFormatting(string source) => source.Contains(">") ? Regex.Replace(source, "<.*?>", string.Empty) : source;

        private void CreateMessage(BasePlayer player, string key, params object[] args)
        {
            if (!HasNetworkConnection(player))
            {
                return;
            }

            string message = Localize(key, player.UserIDString, args);

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Player.Message(player, message, config.ChatID);
        }

        private void Message(BasePlayer player, string message)
        {
            if (HasNetworkConnection(player))
            {
                Player.Message(player, message, config.ChatID);
            }
        }

        #endregion
    }
}