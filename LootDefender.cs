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
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Loot Defender", "Author Egor Blagov, Maintainer nivex", "2.2.4")]
    [Description("Defends loot from other players who dealt less damage than you.")]
    internal class LootDefender : RustPlugin
    {
        [PluginReference]
        Plugin PersonalHeli, Friends, Clans, RustRewards, HeliSignals, BradleyDrops, HelpfulSupply, ShoppyStock, XLevels, XPerience, SkillTree;

        private static LootDefender Instance;
        private static StringBuilder sb;
        private const ulong supplyDropSkinID = 234501;
        private const string bypassLootPerm = "lootdefender.bypass.loot";
        private const string bypassDamagePerm = "lootdefender.bypass.damage";
        private const string bypassLockoutsPerm = "lootdefender.bypass.lockouts";
        private Dictionary<ulong, List<DamageKey>> _apcAttackers = new();
        private Dictionary<ulong, List<DamageKey>> _heliAttackers = new();
        private Dictionary<ulong, ulong> _locked { get; set; } = new();
        private List<ulong> _personal { get; set; } = new();
        private List<ulong> _boss { get; set; } = new();
        private List<string> _sent { get; set; } = new();
        private static StoredData data { get; set; } = new();
        private MonumentInfo launchSite { get; set; }
        private List<MonumentInfo> harbors { get; set; } = new();
        private List<ulong> ownerids = new() { 0, 1337422, 3566257, 123425345634634 };

        public enum DamageEntryType
        {
            Bradley,
            Corpse,
            Heli,
            NPC,
            None
        }

        public class Lockout
        {
            public double Bradley { get; set; }

            public double Heli { get; set; }

            public bool Any() => Bradley > 0 ||  Heli > 0;
        }

        private class StoredData
        {
            public Dictionary<string, Lockout> Lockouts { get; } = new();
            public Dictionary<string, UI.Info> UI { get; set; } = new();
            [JsonProperty(PropertyName = "DamageInfo")]
            public Dictionary<ulong, DamageInfo> Damage { get; set; } = new();
            [JsonProperty(PropertyName = "LockInfo")]
            public Dictionary<ulong, LockInfo> Lock { get; set; } = new();

            public void Sanitize()
            {
                foreach (var (uid, damageInfo) in Damage.ToList())
                {
                    damageInfo._entity = BaseNetworkable.serverEntities.Find(new(uid)) as BaseEntity;

                    if (damageInfo.damageEntryType == DamageEntryType.NPC && !config.Npc.Enabled)
                    {
                        if (damageInfo._entity.IsValid())
                        {
                            damageInfo._entity.OwnerID = 0uL;
                        }

                        Damage.Remove(uid);
                    }
                    else if (damageInfo._entity == null)
                    {
                        Damage.Remove(uid);
                    }
                    else
                    {
                        foreach (var x in damageInfo.damageKeys)
                        {
                            x.attacker = BasePlayer.FindByID(x.userid);
                        }

                        damageInfo.Start();
                    }
                }

                foreach (var (uid, lockInfo) in Lock.ToList())
                {
                    var entity = BaseNetworkable.serverEntities.Find(new(uid)) as BaseEntity;

                    if (lockInfo.damageInfo.damageEntryType == DamageEntryType.NPC && !config.Npc.Enabled)
                    {
                        if (entity.IsValid())
                        {
                            entity.OwnerID = 0uL;
                        }

                        Lock.Remove(uid);
                    }
                    else if (entity == null)
                    {
                        Lock.Remove(uid);
                    }
                }
            }
        }

        private class DamageEntry
        {
            public float DamageDealt { get; set; }
            public DateTime Timestamp { get; set; }
            public string TeamID { get; set; }

            public DamageEntry() { }

            public DamageEntry(ulong teamID)
            {
                Timestamp = DateTime.Now;
                TeamID = teamID.ToString();
            }

            public bool IsOutdated(int timeout) => timeout > 0 && DateTime.Now.Subtract(Timestamp).TotalSeconds >= timeout;
        }

        private class DamageKey
        {
            public ulong userid { get; set; }
            public string name { get; set; }
            public DamageEntry damageEntry { get; set; }
            internal BasePlayer attacker { get; set; }

            public DamageKey() { }

            public DamageKey(BasePlayer attacker)
            {
                this.attacker = attacker;
                userid = attacker.userID;
                name = attacker.displayName;
            }
        }

        private class DamageInfo
        {
            public List<DamageKey> damageKeys { get; set; } = new();
            [JsonIgnore]
            public Dictionary<ulong, BasePlayer> interact { get; set; } = new();
            private List<ulong> participants { get; set; } = new();
            public DamageEntryType damageEntryType { get; set; } = DamageEntryType.None;
            public string NPCName { get; set; }
            public ulong OwnerID { get; set; }
            public ulong SkinID { get; set; }
            public DateTime start { get; set; }
            internal int _lockTime { get; set; }
            [JsonIgnore]
            internal BaseEntity _entity { get; set; }
            internal Vector3 _position { get; set; }
            internal Vector3 lastAttackedPosition { get; set; }
            internal ulong _uid { get; set; }
            [JsonIgnore]
            internal Timer _timer { get; set; }
            internal List<DamageKey> keys { get; set; } = new();

            List<DamageGroup> damageGroups;

            internal float FullDamage
            {
                get
                {
                    return damageKeys.Sum(x => x.damageEntry.DamageDealt);
                }
            }

            public DamageInfo() { }

            public DamageInfo(DamageEntryType damageEntryType, string NPCName, BaseEntity entity, DateTime start)
            {
                SkinID = entity.skinID;
                _entity = entity;
                _uid = entity.net.ID.Value;

                this.damageEntryType = damageEntryType;
                this.NPCName = NPCName;
                this.start = start;

                Start();
            }

            public void Start()
            {
                _lockTime = GetLockTime(damageEntryType);
                _timer = Instance.timer.Every(1f, CheckExpiration);
            }

            public void DestroyTimer()
            {
                _timer?.Destroy();
            }

            private void CheckExpiration()
            {
                damageKeys.ForEach(x =>
                {
                    if (x.damageEntry.IsOutdated(_lockTime))
                    {
                        if (x.userid == OwnerID)
                        {
                            Unlock();
                        }

                        keys.Add(x);
                    }
                });

                keys.ForEach(x => damageKeys.Remove(x));
                keys.Clear();
            }

            public void Unlock()
            {
                OwnerID = 0;

                if (_entity != null)
                {
                    _entity.OwnerID = 0;
                }

                if (!Instance._locked.Remove(_uid))
                {
                    return;
                }

                if (damageEntryType == DamageEntryType.Bradley && config.Bradley.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "BradleyUnlocked", PositionToGrid(_position));
                    }
                }

                if (damageEntryType == DamageEntryType.Heli && config.Helicopter.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "HeliUnlocked", PositionToGrid(_position));
                    }
                }

                if (damageEntryType == DamageEntryType.NPC && config.Npc.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "NpcUnlocked", NPCName, PositionToGrid(_position));
                    }
                }
            }

            private void Lock(BaseEntity entity, ulong id)
            {
                Instance._locked[_uid] = entity.OwnerID = OwnerID = id;
                _position = entity.transform.position;
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

                if (damageEntryType == DamageEntryType.Bradley && !Instance.CanLockBradley(entity))
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

                if (entry.TeamID != "0")
                {
                    foreach (var x in damageKeys)
                    {
                        if (x.damageEntry.TeamID == entry.TeamID)
                        {
                            damage += x.damageEntry.DamageDealt;
                        }
                    }
                }
                else damage = entry.DamageDealt;

                if (config.Helicopter.Threshold > 0f && entity is PatrolHelicopter)
                {
                    if (damage >= entity.MaxHealth() * config.Helicopter.Threshold && !Instance.HasPermission(attacker, "lootdefender.bypasshelilock"))
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
                else if (config.Bradley.Threshold > 0f && entity is BradleyAPC && Instance.CanLockBradley(entity))
                {
                    if (damage >= entity.MaxHealth() * config.Bradley.Threshold && !Instance.HasPermission(attacker, "lootdefender.bypassbradleylock"))
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
                else if (config.Npc.Threshold > 0f && entity is BasePlayer npc && Instance.CanLockNpc(npc))
                {
                    if (!npc.userID.IsSteamId() && damage >= entity.MaxHealth() * config.Npc.Threshold && !Instance.HasPermission(attacker, "lootdefender.bypassnpclock"))
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

            public DamageEntry TryGet(ulong id)
            {
                foreach (var x in damageKeys)
                {
                    if (x.userid == id)
                    {
                        return x.damageEntry;
                    }
                }

                return null;
            }

            public DamageEntry Get(BasePlayer attacker)
            {
                DamageEntry entry = TryGet(attacker.userID);

                if (entry == null)
                {
                    damageKeys.Add(new(attacker)
                    {
                        damageEntry = entry = new(attacker.currentTeam),
                    });
                }

                return entry;
            }

            public bool isKilled;

            public void OnKilled(Vector3 position, HitInfo hitInfo, float distance)
            {
                if (isKilled) return;
                isKilled = true;
                SetCanInteract();
                DisplayDamageReport();
                FindLooters(position, hitInfo, distance);
            }

            private void FindLooters(Vector3 position, HitInfo hitInfo, float distance)
            {
                var weapon = hitInfo?.Weapon?.GetItem()?.info?.shortname ?? hitInfo?.WeaponPrefab?.ShortPrefabName ?? "";
                HashSet<ulong> looters = new();
                HashSet<ulong> users = new();
                
                foreach (var x in damageKeys)
                {
                    if (CanInteract(x.userid, x.attacker))
                    {
                        if (TryGet(x.userid)?.DamageDealt > 0)
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

                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    Instance.LockoutLooters(looters, position, damageEntryType, SkinID);
                }
            }

            public void DisplayDamageReport()
            {
                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        if (CanDisplayReport(target))
                        {
                            Message(target, GetDamageReport(target.userID));
                        }
                    }
                }
                else if (damageEntryType == DamageEntryType.NPC)
                {
                    foreach (var x in damageKeys)
                    {
                        if (CanDisplayReport(x.attacker))
                        {
                            Message(x.attacker, GetDamageReport(x.userid));
                        }
                    }
                }
            }

            private bool CanDisplayReport(BasePlayer target)
            {
                if (target == null || !target.IsConnected || damageEntryType == DamageEntryType.None)
                {
                    return false;
                }

                if (damageEntryType == DamageEntryType.Bradley)
                {
                    if (config.Bradley.Messages.NotifyKiller && IsParticipant(target.userID, target))
                    {
                        return true;
                    }

                    return config.Bradley.Messages.NotifyChat;
                }

                if (damageEntryType == DamageEntryType.Heli)
                {
                    if (config.Helicopter.Messages.NotifyKiller && IsParticipant(target.userID, target))
                    {
                        return true;
                    }

                    return config.Helicopter.Messages.NotifyChat;
                }

                if (damageEntryType == DamageEntryType.NPC)
                {
                    if (config.Npc.Messages.NotifyKiller && IsParticipant(target.userID, target))
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
                var topDamageGroups = GetTopDamageGroups(damageGroups, damageEntryType);
                if (damageGroups.Count > 0)
                {
                    foreach (var damageGroup in damageGroups)
                    {
                        if (topDamageGroups.Contains(damageGroup) || Instance.IsAlly(OwnerID, damageGroup.FirstDamagerDealer.userid))
                        {
                            interact[damageGroup.FirstDamagerDealer.userid] = damageGroup.FirstDamagerDealer.attacker;
                        }
                        else
                        {
                            var damage = TryGet(damageGroup.FirstDamagerDealer.userid)?.DamageDealt ?? 0f;
                            var dmgRatio = damage > 0 && FullDamage > 0 ? damage / FullDamage : 0;
                            float threshold = config.Npc.Threshold;
                            if (damageEntryType == DamageEntryType.Bradley)
                            {
                                threshold = config.Bradley.Threshold;
                            }
                            else if (damageEntryType == DamageEntryType.Heli)
                            {
                                threshold = config.Helicopter.Threshold;
                            }
                            if (OwnerID == 0 && dmgRatio >= threshold)
                            {
                                OwnerID = damageGroup.FirstDamagerDealer.userid;
                                interact[damageGroup.FirstDamagerDealer.userid] = damageGroup.FirstDamagerDealer.attacker;
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

            public string GetDamageReport(ulong targetId)
            {
                var userid = targetId.ToString();
                var nameKey = damageEntryType == DamageEntryType.Bradley ? _("BradleyAPC", userid) : damageEntryType == DamageEntryType.Heli ? _("Helicopter", userid) : NPCName;
                var firstDamageDealer = string.Empty;

                sb.Length = 0;
                sb.AppendLine($"{_("DamageReport", userid, $"<color={config.Report.Ok}>{nameKey}</color>")}:");

                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    var seconds = Math.Ceiling((DateTime.Now - start).TotalSeconds);

                    sb.AppendLine($"{_("DamageTime", userid, nameKey, seconds)}");
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

                        sb.Append($"{damageGroup.ToReport(damageGroup.FirstDamagerDealer, this)}\n");
                    }

                    if (damageEntryType == DamageEntryType.NPC && !string.IsNullOrEmpty(firstDamageDealer) && damageGroups.Count > 1)
                    {
                        sb.Append($" {_("FirstLock", userid, firstDamageDealer, config.Npc.Threshold * 100f)}");
                    }
                }

                return sb.ToString();
            }

            public bool IsParticipant(ulong userid, BasePlayer player)
            {
                return participants.Contains(userid) || CanInteract(userid, player);
            }

            public bool CanInteract(ulong userid, BasePlayer player)
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

                if (Instance.IsAlly(userid, OwnerID))
                {
                    interact.Add(userid, player);
                    return true;
                }

                return false;
            }

            private List<DamageGroup> GetTopDamageGroups(List<DamageGroup> damageGroups, DamageEntryType damageEntryType)
            {
                List<DamageGroup> topDamageGroups = new();

                if (damageGroups.Count == 0)
                {
                    return topDamageGroups;
                }

                var topDamageGroup = damageGroups.OrderByDescending(x => x.TotalDamage).First();

                foreach (var damageGroup in damageGroups)
                {
                    foreach (var playerId in damageGroup.Players)
                    {
                        if (Instance.IsAlly(playerId, OwnerID))
                        {
                            topDamageGroups.Add(damageGroup);
                            break;
                        }
                    }
                }

                return topDamageGroups;
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
            public DamageInfo damageInfo { get; set; }

            private DateTime LockTimestamp { get; set; }

            private int LockTimeout { get; set; }

            internal bool IsLockOutdated => LockTimeout > 0 && DateTime.Now.Subtract(LockTimestamp).TotalSeconds >= LockTimeout;

            public LockInfo() { }

            public LockInfo(DamageInfo damageInfo, int lockTimeout)
            {
                LockTimestamp = DateTime.Now;
                LockTimeout = lockTimeout;
                this.damageInfo = damageInfo;
            }

            public bool CanInteract(ulong userId, BasePlayer target) => damageInfo.CanInteract(userId, target);

            public string GetDamageReport(ulong userId) => damageInfo.GetDamageReport(userId);
        }

        private class DamageGroup
        {
            public float TotalDamage { get; set; }

            public DamageKey FirstDamagerDealer { get; set; }

            private List<ulong> additionalPlayers { get; set; } = new();

            [JsonIgnore]
            public List<ulong> Players
            {
                get
                {
                    List<ulong> players = new()
                    {
                        FirstDamagerDealer.userid
                    };

                    foreach (var targetId in additionalPlayers)
                    {
                        if (!players.Contains(targetId))
                        {
                            players.Add(targetId);
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

                // add clan
            }

            public string ToReport(DamageKey damageKey, DamageInfo damageInfo)
            {
                var damage = damageInfo.TryGet(damageKey.userid)?.DamageDealt ?? 0f;
                var percent = damage > 0 && damageInfo.FullDamage > 0 ? damage / damageInfo.FullDamage * 100 : 0;
                var color = additionalPlayers.Count == 0 ? config.Report.SinglePlayer : config.Report.Team;
                var damageLine = _("Format", damageKey.userid.ToString(), damage, percent);

                return $"<color={color}>{damageKey.name}</color> {damageLine}";
            }
        }

        #region Hooks

        private void OnServerSave()
        {
            timer.Once(15f, SaveData);
        }

        private void Init()
        {
            Unsubscribe(nameof(OnEventTrigger));
            Unsubscribe();
            Instance = this;
            sb = new();
            if (!string.IsNullOrEmpty(config.Lockout.Command)) 
                AddCovalenceCommand(config.Lockout.Command, nameof(CommandLockouts));
            if (!string.IsNullOrEmpty(config.UI.Command)) 
                AddCovalenceCommand(config.UI.Command, nameof(CommandUI));
            AddCovalenceCommand("lo", nameof(CommandLootDefender));
            RegisterPermissions();
            LoadData();
        }

        private void OnServerInitialized(bool serverinit)
        {
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
            }

            if (config.Bradley.Threshold != 0f || config.Helicopter.Threshold != 0f || config.Hackable.Laptop)
            {
                Subscribe(nameof(OnPlayerAttack));
            }

            if (!config.SupplyDrop.Skins.Contains(0uL))
            {
                config.SupplyDrop.Skins.Add(0uL);
            }

            if (config.Lockout.F15)
            {
                Subscribe(nameof(OnEventTrigger));
            }
            
            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnEntityDeath));
            Subscribe(nameof(OnEntityKill));
            Subscribe(nameof(CanLootEntity));
            Subscribe(nameof(CanBradleyTakeDamage));
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
            Instance = null;
            data = null;
            sb = null;
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            UI.DestroyLockoutUI(player);
            UI.ShowLockouts(player);
        }

        private object OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player) => HandleTeam(team, player.userID);

        private object OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong targetId) => HandleTeam(team, targetId);

        private object OnPlayerAttack(BasePlayer attacker, HitInfo hitInfo)
        {
            if (attacker == null || HasPermission(attacker, bypassDamagePerm) || hitInfo == null)
            {
                return null;
            }

            if (config.Hackable.Laptop && hitInfo.HitBone == 242862488 && hitInfo.HitEntity is HackableLockedCrate crate && IsDefended(crate)) // laptopcollision
            {
                hitInfo.HitBone = 0;
                return null;
            }

            if (config.Bradley.Threshold != 0f || config.Helicopter.Threshold != 0f)
            {
                if (hitInfo.HitEntity is ServerGib gibs && gibs.IsValid() && data.Lock.TryGetValue(gibs.net.ID.Value, out var lockInfo))
                {
                    if (gibs.OwnerID != 0 && !lockInfo.IsLockOutdated)
                    {
                        if (!lockInfo.CanInteract(attacker.userID, attacker))
                        {
                            if (CanMessage(attacker))
                            {
                                CreateMessage(attacker, "CannotMine");
                                Message(attacker, lockInfo.GetDamageReport(attacker.userID));
                            }

                            CancelDamage(hitInfo);
                            return false;
                        }
                    }
                    else
                    {
                        data.Lock.Remove(gibs.net.ID.Value);
                        gibs.OwnerID = 0;
                    }
                }
            }

            return null;
        }

        private object OnEntityTakeDamage(PatrolHelicopter heli, HitInfo hitInfo)
        {
            if (config.Helicopter.Threshold == 0f || !heli.IsValid() || _personal.Contains(heli.net.ID.Value) || hitInfo == null || heli.myAI == null || heli.myAI.isDead || CanHeliTakeDamage(heli, hitInfo) != null)
            {
                return null;
            }

            return OnEntityTakeDamageHandler(heli, hitInfo, DamageEntryType.Heli, string.Empty);
        }

        private object OnEntityTakeDamage(BradleyAPC apc, HitInfo hitInfo)
        {
            if (config.Bradley.Threshold == 0f || !apc.IsValid() || hitInfo == null || CanBradleyTakeDamage(apc, hitInfo) != null)
            {
                return null;
            }

            return OnEntityTakeDamageHandler(apc, hitInfo, DamageEntryType.Bradley, string.Empty);
        }

        private object OnEntityTakeDamage(BasePlayer player, HitInfo hitInfo)
        {
            if (!config.Npc.Enabled || !player.IsValid() || player.userID.IsSteamId() || hitInfo == null)
            {
                return null;
            }

            if (config.Npc.Min > 0 && player.startHealth < config.Npc.Min)
            {
                return null;
            }

            return OnEntityTakeDamageHandler(player, hitInfo, DamageEntryType.NPC, player.displayName);
        }

        private object OnEntityTakeDamageHandler(BaseCombatEntity entity, HitInfo hitInfo, DamageEntryType damageEntryType, string npcName)
        {
            if (!config.Bradley.LockConvoy && entity.skinID == 755446 && entity is BradleyAPC)
            {
                return null;
            }

            if (!config.Helicopter.LockConvoy && entity.skinID == 755446 && entity is PatrolHelicopter)
            {
                return null;
            }

            if (!(hitInfo.Initiator is BasePlayer attacker) || !attacker.userID.IsSteamId())
            {
                return null;
            }

            if (_locked.TryGetValue(entity.net.ID.Value, out var ownerId) && !HasPermission(attacker, bypassDamagePerm) && !IsAlly(attacker.userID, ownerId))
            {
                if (!BlockDamage(damageEntryType))
                {
                    return null;
                }

                if (CanMessage(attacker))
                {
                    CreateMessage(attacker, "CannotDamageThis");
                }

                CancelDamage(hitInfo);
                return true;
            }

            if (!data.Damage.TryGetValue(entity.net.ID.Value, out var damageInfo))
            {
                data.Damage[entity.net.ID.Value] = damageInfo = new(damageEntryType, npcName, entity, DateTime.Now);
            }

            DamageEntry entry = damageInfo.Get(attacker);

            float total = hitInfo.damageTypes.Total();

            if (hitInfo.isHeadshot) total *= 2f;

            damageInfo.AddDamage(entity, attacker, entry, total);

            if (damageEntryType == DamageEntryType.Heli)
            {
                float prevHealth = entity.health;

                NextTick(() =>
                {
                    if (entity == null)
                    {
                        return;
                    }

                    damageInfo.AddDamage(entity, attacker, entry, Mathf.Abs(prevHealth - entity.health));
                });
            }

            return null;
        }

        public static bool IsKilled(BaseNetworkable a) => a == null || a.IsDestroyed || !a.isSpawned;

        private bool BlockDamage(DamageEntryType damageEntryType)
        {
            if (damageEntryType == DamageEntryType.NPC && config.Npc.LootingOnly)
            {
                return false;
            }
            else if (damageEntryType == DamageEntryType.Heli && config.Helicopter.LootingOnly)
            {
                return false;
            }
            else if (damageEntryType == DamageEntryType.Bradley && config.Bradley.LootingOnly)
            {
                return false;
            }

            return true;
        }

        private object CanBradleyTakeDamage(BradleyAPC apc, HitInfo hitInfo)
        {
            if (config.Lockout.Bradley <= 0 || !apc.IsValid() || !(hitInfo.Initiator is BasePlayer attacker))
            {
                return null;
            }

            if (HasLockout(attacker, DamageEntryType.Bradley, apc.skinID))
            {
                CancelDamage(hitInfo);
                return false;
            }

            if (!data.Lockouts.ContainsKey(attacker.UserIDString))
            {
                if (!_apcAttackers.TryGetValue(apc.net.ID.Value, out var attackers))
                {
                    _apcAttackers[apc.net.ID.Value] = attackers = new();
                }

                if (!attackers.Exists(x => x.userid == attacker.userID))
                {
                    attackers.Add(new(attacker));
                }
            }

            return null;
        }

        private object CanHeliTakeDamage(PatrolHelicopter heli, HitInfo hitInfo)
        {
            if (config.Lockout.Heli <= 0 || !heli.IsValid() || !(hitInfo.Initiator is BasePlayer attacker))
            {
                return null;
            }

            if (HasLockout(attacker, DamageEntryType.Heli, heli.skinID))
            {
                CancelDamage(hitInfo);
                return false;
            }

            if (!data.Lockouts.ContainsKey(attacker.UserIDString))
            {
                if (!_heliAttackers.TryGetValue(heli.net.ID.Value, out var attackers))
                {
                    _heliAttackers[heli.net.ID.Value] = attackers = new();
                }

                if (!attackers.Exists(x => x.userid == attacker.userID))
                {
                    attackers.Add(new(attacker));
                }
            }

            return null;
        }

        private void OnEntityDeath(PatrolHelicopter heli, HitInfo hitInfo)
        {
            if (!heli.IsValid())
            {
                return;
            }

            _personal.Remove(heli.net.ID.Value);
            _heliAttackers.Remove(heli.net.ID.Value);

            OnEntityDeathHandler(heli, DamageEntryType.Heli, hitInfo);
        }

        private void OnEntityKill(PatrolHelicopter heli) => OnEntityDeath(heli, null);

        private void OnEntityDeath(BradleyAPC apc, HitInfo hitInfo)
        {
            if (!apc.IsValid())
            {
                return;
            }

            _apcAttackers.Remove(apc.net.ID.Value);

            OnEntityDeathHandler(apc, DamageEntryType.Bradley, hitInfo);
        }

        private void OnEntityDeath(BasePlayer player, HitInfo hitInfo)
        {
            if (!config.Npc.Enabled || !player.IsValid() || player.userID.IsSteamId())
            {
                return;
            }

            OnEntityDeathHandler(player, DamageEntryType.NPC, hitInfo);
        }

        private void OnEntityDeath(NPCPlayerCorpse corpse, HitInfo hitInfo)
        {
            if (!config.Npc.Enabled || !corpse.IsValid())
            {
                return;
            }

            OnEntityDeathHandler(corpse, DamageEntryType.Corpse, hitInfo);
        }

        private void OnEntityKill(NPCPlayerCorpse corpse) => OnEntityDeath(corpse, null);

        private bool IsInBounds(MonumentInfo monument, Vector3 target)
        {
            return monument.IsInBounds(target) || new OBB(monument.transform.position, monument.transform.rotation, new Bounds(monument.Bounds.center, new Vector3(300f, 300f, 300f))).Contains(target);
        }

        private bool CanLockBradley(BaseEntity entity)
        {
            if (config.Bradley.Threshold <= 0f || _personal.Contains(entity.net.ID.Value))
            {
                return false;
            }

            if (BradleyDrops && BradleyDrops.CallHook("IsBradleyDrop", entity.skinID) != null)
            {
                return false;
            }

            if (entity.name.Contains($"BradleyApc[{entity.net.ID}]"))
            {
                return config.Bradley.LockBradleyTiers;
            }

            if (entity.skinID != 0)
            {
                if (entity.skinID == 755446)
                {
                    return config.Bradley.LockConvoy;
                }
                if (entity.skinID == 81182151852251420)
                {
                    return config.Bradley.LockHarbor;
                }
                if (entity.skinID == 8675309)
                {
                    return config.Bradley.LockMonument;
                }
                return false;
            }

            if (launchSite != null && IsInBounds(launchSite, entity.ServerPosition))
            {
                return config.Bradley.LockLaunchSite;
            }

            if (harbors.Exists(mi => IsInBounds(mi, entity.ServerPosition)))
            {
                return config.Bradley.LockHarbor;
            }

            return config.Bradley.LockWorldly;
        }

        private bool CanLockHeli(BaseCombatEntity entity)
        {
            if (HeliSignals && HeliSignals.CallHook("IsHeliSignalObject", entity.skinID) != null)
            {
                return false;
            }
            if (entity.skinID != 0)
            {
                if (entity.skinID == 755446)
                {
                    return config.Helicopter.LockConvoy;
                }
                if (entity.skinID == 81182151852251420)
                {
                    return config.Helicopter.LockHarbor == true;
                }
                return false;
            }
            return config.Helicopter.Threshold > 0f;
        }

        private bool CanLockNpc(BaseEntity entity)
        {
            if (entity.OwnerID.ToString().Length == 5)
            {
                return false;
            }
            return config.Npc.Threshold > 0f && !_boss.Contains(entity.net.ID.Value);
        }

        private void OnEntityDeathHandler(BaseCombatEntity entity, DamageEntryType damageEntryType, HitInfo hitInfo)
        {
            if (data.Damage.TryGetValue(entity.net.ID.Value, out var damageInfo) && !damageInfo.isKilled)
            {
                if (damageEntryType == DamageEntryType.Bradley && !CanLockBradley(entity)) return;
                if (damageEntryType == DamageEntryType.Heli && !CanLockHeli(entity)) return;
                if (damageEntryType == DamageEntryType.NPC && !CanLockNpc(entity)) return;

                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    var lockInfo = new LockInfo(damageInfo, damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Bradley.LockTime);
                    var position = entity.transform.position;

                    if (entity is PatrolHelicopter && (position - Vector3.zero).magnitude > World.Size / 1.25f)
                    {
                        return;
                    }
                    
                    damageInfo.OnKilled(position, hitInfo, hitInfo?.ProjectileDistance ?? Vector3.Distance(position, damageInfo.lastAttackedPosition));

                    timer.Once(0.1f, () =>
                    {
                        LockInRadius<LockedByEntCrate>(position, lockInfo, damageEntryType);
                        LockInRadius<HelicopterDebris>(position, lockInfo, damageEntryType);
                        RemoveFireFromCrates(position, damageEntryType);
                    });

                    damageInfo.DestroyTimer();
                }
                else if (damageEntryType == DamageEntryType.NPC && config.Npc.Enabled && entity is BasePlayer npc)
                {
                    var position = entity.transform.position;
                    var npcId = npc.userID;
                    var damageKey = npc.net.ID.Value;

                    damageInfo.OnKilled(position, hitInfo, hitInfo?.ProjectileDistance ?? Vector3.Distance(position, damageInfo.lastAttackedPosition));
                    damageInfo.DestroyTimer();

                    timer.Once(0.1f, () => LockInRadius(position, damageKey, damageInfo, npcId));
                }
                else if (damageEntryType == DamageEntryType.NPC && config.Npc.Enabled && entity is LootableCorpse corpse)
                {
                    var position = entity.transform.position;
                    var npcId = corpse.playerSteamID;
                    var damageKey = corpse.net.ID.Value;

                    damageInfo.OnKilled(position, hitInfo, hitInfo?.ProjectileDistance ?? Vector3.Distance(position, damageInfo.lastAttackedPosition));
                    damageInfo.DestroyTimer();

                    timer.Once(0.1f, () => LockInRadius(position, damageKey, damageInfo, npcId));
                }
            }

            if (data.Lock.Remove(entity.net.ID.Value, out var lockInfo2) && damageEntryType == DamageEntryType.Corpse && config.Npc.Enabled && entity is LootableCorpse corpse2)
            {
                var corpsePos = corpse2.transform.position;
                var corpseId = corpse2.playerSteamID;

                timer.Once(0.1f, () => LockInRadius(corpsePos, lockInfo2, corpseId));
            }
        }

        //void OnEntitySpawned(DroppedItemContainer container)
        //{
        //    if (!config.Npc.Enabled || !container.IsValid() || container.playerSteamID.IsSteamId() || data.Lock.ContainsKey(container.net.ID.Value))
        //    {
        //        return;
        //    }
        //    foreach (var damageInfo in data.Damage.Values)
        //    {
        //        if (damageInfo.damageEntryType == DamageEntryType.NPC && container.Distance(damageInfo._position) <= 3f)
        //        {
        //            if (config.Npc.LockTime > 0f)
        //            {
        //                var uid = container.net.ID.Value;

        //                timer.Once(config.Npc.LockTime, () => data.Lock.Remove(uid));
        //                container.Invoke(() => container.OwnerID = 0, config.Npc.LockTime);
        //            }

        //            ulong ownerid = damageInfo.OwnerID;
        //            container.OwnerID = ownerid;
        //            container.Invoke(() => container.OwnerID = ownerid, 1f);
        //            data.Lock[container.net.ID.Value] = new(damageInfo, config.Npc.LockTime);
        //        }
        //    }
        //}

        void GiveRustReward(BaseEntity entity, DamageInfo damageInfo, ulong userid, string weapon, int total)
        {
            if (RustRewards == null)
            {
                return;
            }

            BasePlayer attacker = BasePlayer.FindByID(userid);

            if (attacker == null || !attacker.userID.IsSteamId())
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

        private static void ApplyWeaponMultiplierReward(DamageInfo damageInfo, string weapon, ref double amount, float distance)
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

        private object OnAutoPickupEntity(BasePlayer player, BaseEntity entity) => CanLootEntityHandler(player, entity);

        private object CanLootEntity(BasePlayer player, DroppedItemContainer container) => CanLootEntityHandler(player, container);

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse) => CanLootEntityHandler(player, corpse);

        private object CanLootEntity(BasePlayer player, StorageContainer container) => CanLootEntityHandler(player, container);

        private object CanLootEntityHandler(BasePlayer player, BaseEntity entity)
        {
            if (!entity.IsValid())
            {
                return null;
            }

            if (HasPermission(player, bypassLootPerm))
            {
                return null;
            }

            if (entity.OwnerID == 0)
            {
                return null;
            }

            if (ownerids.Contains(entity.OwnerID))
            {
                return null;
            }

            if (entity is SupplyDrop && entity.skinID == supplyDropSkinID || config.Hackable.Enabled && entity is HackableLockedCrate crate && IsDefended(crate))
            {
                if (Convert.ToBoolean(Interface.CallHook("OnLootLockedEntity", player, entity)))
                {
                    return null;
                }

                if (!IsAlly(player.userID, entity.OwnerID))
                {
                    if (CanMessage(player))
                    {
                        CreateMessage(player, entity is SupplyDrop ? "CannotLootIt" : "CannotLootCrate");
                    }

                    return true;
                }

                return null;
            }

            if (!data.Lock.TryGetValue(entity.net.ID.Value, out var lockInfo))
            {
                return null;
            }

            if (entity.OwnerID == 0 || lockInfo.IsLockOutdated)
            {
                data.Lock.Remove(entity.net.ID.Value);
                entity.OwnerID = 0;
                return null;
            }

            if (!lockInfo.CanInteract(player.userID, player) && Interface.CallHook("OnLootLockedEntity", player, entity) == null)
            {
                if (CanMessage(player))
                {
                    CreateMessage(player, "CannotLoot");
                    Message(player, lockInfo.GetDamageReport(player.userID));
                }

                return true;
            }

            return null;
        }

        private void OnBossSpawn(ScientistNPC boss)
        {
            if (boss.IsValid())
            {
                _boss.Add(boss.net.ID.Value);
            }
        }

        private void OnBossKilled(ScientistNPC boss, BasePlayer attacker)
        {
            if (boss.IsValid())
            {
                _boss.Remove(boss.net.ID.Value);
            }
        }

        private void OnPersonalHeliSpawned(BasePlayer player, PatrolHelicopter heli)
        {
            if (heli.IsValid())
            {
                _personal.Add(heli.net.ID.Value);
            }
        }

        private void OnPersonalApcSpawned(BasePlayer player, BradleyAPC apc)
        {
            if (apc.IsValid())
            {
                _personal.Add(apc.net.ID.Value);
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
            if (player == null || ss == null || !config.SupplyDrop.Skins.Contains(tw.skinID))
            {
                return;
            }

            if (tw.GetItem() is Item item && !config.SupplyDrop.Skins.Contains(item.skin))
            {
                return;
            }

            ss.OwnerID = player.userID;
            ss.skinID = supplyDropSkinID;

            if (config.SupplyDrop.Bypass)
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
                Puts(_("ThrownSupplySignalAt", null, player.displayName, PositionToGrid(player.transform.position)));
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
                    ss.SetFlag(BaseEntity.Flags.On, true, false, true);
                    ss.SendNetworkUpdateImmediate(false);
                }
                else ss.FinishUp();
            }

            if (GameManager.server.CreateEntity(StringPool.Get(3632568684), position) is SupplyDrop drop)
            {
                drop.OwnerID = userid;
                drop.skinID = supplyDropSkinID;
                drop.Spawn();
                drop.Invoke(() => drop.OwnerID = userid, 1f);

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
                drop.skinID = supplyDropSkinID;
                OnSupplyDropLanded(drop);
            }
        }

        private void OnCargoPlaneSignaled(CargoPlane plane, SupplySignal ss)
        {
            if (ss?.skinID != supplyDropSkinID)
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
            plane.skinID = supplyDropSkinID;
        }

        private void OnSupplyDropDropped(SupplyDrop drop, CargoPlane plane)
        {
            if (plane?.skinID != supplyDropSkinID)
            {
                return;
            }

            if (drop.TryGetComponent(out Rigidbody rb))
            {
                rb.drag = Mathf.Clamp(config.SupplyDrop.Drag, 0.1f, 3f);
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }

            DelayedDestroySupplyDrop(drop);

            drop.OwnerID = plane.OwnerID;
            drop.skinID = supplyDropSkinID;

            Interface.CallHook("OnModifiedSupplyDropDropped", drop, plane);
        }

        private void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (drop?.skinID != supplyDropSkinID)
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
            if (drop.skinID == supplyDropSkinID)
            {
                DelayedDestroySupplyDrop(drop);
            }
            else OnHelpfulSupplyDropped(drop);
        }

        private void OnHelpfulSupplyDropped(SupplyDrop drop)
        {
            if (!config.SupplyDrop.HelpfulSupply || HelpfulSupply == null) return;
            if (drop == null || drop.IsDestroyed) return;
            if (BasePlayer.allPlayerList.Any(x => x.OwnerID == drop.OwnerID)) return;
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
            return Interface.CallHook("OnLootLockedEntity", player, crate) == null;
        }

        private void CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (CanLockHackableCrate(player, crate))
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

            var val = crate.net.ID.Value;
            var userid = owner.userID;
            var username = owner.displayName;
            var grid = PositionToGrid(owner.transform.position);

            _locked[val] = crate.OwnerID;

            if (config.Hackable.LockTime > 0f)
            {
                crate.Invoke(() =>
                {
                    crate.OwnerID = 0;
                    _locked.Remove(val);

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

        private void CancelDamage(HitInfo hitInfo)
        {
            hitInfo.damageTypes = new();
            hitInfo.DoHitEffects = false;
            hitInfo.DidHit = false;
        }

        private bool CanMessage(BasePlayer player)
        {
            if (_sent.Contains(player.UserIDString))
            {
                return false;
            }

            string uid = player.UserIDString;

            _sent.Add(uid);
            timer.Once(10f, () => _sent.Remove(uid));

            return true;
        }

        public bool HasLockout(BasePlayer player, DamageEntryType damageEntryType, ulong skinid)
        {
            if (config.Lockout.Exceptions.Contains(skinid))
            {
                return false;
            }

            if (damageEntryType == DamageEntryType.Bradley && config.Lockout.Bradley <= 0)
            {
                return false;
            }

            if (damageEntryType == DamageEntryType.Heli && config.Lockout.Heli <= 0)
            {
                return false;
            }

            if (!player.IsValid() || IsF15EventActive || HasPermission(player, bypassLockoutsPerm))
            {
                return false;
            }

            if (data.Lockouts.TryGetValue(player.UserIDString, out var lo))
            {
                double time = UI.GetLockoutTime(damageEntryType, lo, player.UserIDString);

                if (time > 0f)
                {
                    if (CanMessage(player))
                    {
                        CreateMessage(player, damageEntryType == DamageEntryType.Bradley ? "LockedOutBradley" : "LockedOutHeli", FormatTime(time));
                    }

                    return true;
                }
            }

            return false;
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

        private void ApplyCooldowns(DamageEntryType damageEntryType)
        {
            foreach (var lo in data.Lockouts.ToList())
            {
                double time = UI.GetLockoutTime(damageEntryType);

                if (time <= 0f)
                {
                    continue;
                }

                bool update = false;

                if (damageEntryType == DamageEntryType.Bradley && lo.Value.Bradley - Epoch.Current > config.Lockout.Bradley)
                {
                    lo.Value.Bradley = Epoch.Current + time;

                    update = true;
                }

                if (damageEntryType == DamageEntryType.Heli && lo.Value.Heli - Epoch.Current > config.Lockout.Heli)
                {
                    lo.Value.Heli = Epoch.Current + time;

                    update = true;
                }

                if (update)
                {
                    var player = BasePlayer.Find(lo.Key);

                    if (player == null) continue;

                    UI.UpdateLockoutUI(player);
                }
            }
        }

        public void TrySetLockout(string userid, BasePlayer player, DamageEntryType damageEntryType, ulong skinID)
        {
            if (IsF15EventActive || config.Lockout.Exceptions.Contains(skinID))
            {
                return;
            }

            if (permission.UserHasPermission(userid, bypassLockoutsPerm))
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

            switch (damageEntryType)
            {
                case DamageEntryType.Bradley:
                    {
                        if (lo.Bradley <= 0)
                        {
                            lo.Bradley = Epoch.Current + time;
                        }
                        break;
                    }
                case DamageEntryType.Heli:
                    {
                        if (lo.Heli <= 0)
                        {
                            lo.Heli = Epoch.Current + time;
                        }
                        break;
                    }
            }

            if (lo.Any())
            {
                UI.UpdateLockoutUI(player);
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

                if (looter) usernames.Add(looter.displayName);

                TrySetLockout(looterId.ToString(), looter, damageEntryType, skinID);
                LockoutTeam(members, looterId, damageEntryType, skinID);
                LockoutClan(members, looterId, damageEntryType, skinID);
            }

            SendDiscordMessage(members, usernames.ToList(), position, damageEntryType);
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

        private void LockoutClan(HashSet<ulong> members, ulong looterId, DamageEntryType damageEntryType, ulong skinID)
        {
            if (!config.Lockout.Clan || Instance?.Clans?.Call("GetClanMembers", looterId) is not List<string> clan)
            {
                return;
            }

            foreach (ulong memberId in clan.Select(ulong.Parse))
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

        private object HandleTeam(RelationshipManager.PlayerTeam team, ulong userid)
        {
            List<(string key, Dictionary<ulong, List<DamageKey>> dict)> attackers = new()
            {
                ("CannotLeaveBradley", _apcAttackers),
                ("CannotLeaveHeli", _heliAttackers)
            };

            foreach (var (key, dict) in attackers)
            {
                foreach (var list in dict.Values)
                {
                    foreach (var info in list)
                    {
                        if (info.userid == userid)
                        {
                            CreateMessage(info.attacker, key);
                            return true;
                        }
                    }
                }
            }

            return null;
        }

        private bool IsDefended(PatrolHelicopter heli) => heli.IsValid() && (data.Lock.ContainsKey(heli.net.ID.Value) || data.Damage.ContainsKey(heli.net.ID.Value));

        private bool IsDefended(BaseCombatEntity victim) => victim.IsValid() && (_locked.ContainsKey(victim.net.ID.Value) || data.Lock.ContainsKey(victim.net.ID.Value));

        private void DoLockoutRemoves()
        {
            foreach (var (userid, lo) in data.Lockouts.ToList())
            {
                if (lo.Bradley - Epoch.Current <= 0)
                {
                    lo.Bradley = 0;
                }

                if (lo.Heli - Epoch.Current <= 0)
                {
                    lo.Heli = 0;
                }

                if (!lo.Any())
                {
                    data.Lockouts.Remove(userid);
                }
            }
        }

        private void Unsubscribe()
        {
            Unsubscribe(nameof(OnBossSpawn));
            Unsubscribe(nameof(OnBossKilled));
            Unsubscribe(nameof(OnGuardedCrateEventEnded));
            Unsubscribe(nameof(CanHackCrate));
            Unsubscribe(nameof(OnPlayerSleepEnded));
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
            Unsubscribe(nameof(CanBradleyTakeDamage));
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

            data.Sanitize();
        }

        private void RegisterPermissions()
        {
            foreach (var entry in config.Hackable.Permissions)
            {
                permission.RegisterPermission(entry.Permission, this);
            }

            permission.RegisterPermission(bypassLootPerm, this);
            permission.RegisterPermission(bypassDamagePerm, this);
            permission.RegisterPermission(bypassLockoutsPerm, this);
            permission.RegisterPermission("lootdefender.bypassnpclock", this);
            permission.RegisterPermission("lootdefender.bypasshelilock", this);
            permission.RegisterPermission("lootdefender.bypassbradleylock", this);

        }

        private bool IsAlly(ulong playerId, ulong targetId)
        {
            if (playerId == targetId)
            {
                return true;
            }

            if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(playerId, out var team) && team.members.Contains(targetId))
            {
                return true;
            }

            if (Clans != null && Convert.ToBoolean(Clans?.Call("IsMemberOrAlly", playerId, targetId)))
            {
                return true;
            }

            if (Friends != null && Convert.ToBoolean(Friends?.Call("AreFriends", playerId.ToString(), targetId.ToString())))
            {
                return true;
            }

            return false;
        }

        private static List<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1) where T : BaseEntity
        {
            List<T> entities = Pool.Get<List<T>>();
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

        private void RemoveFireFromCrates(Vector3 position, DamageEntryType damageEntryType)
        {
            var entities = FindEntitiesOfType<BaseEntity>(position, 25f);
            foreach (var e in entities)
            {
                if (CanRemoveFire(damageEntryType))
                {
                    if (e is LockedByEntCrate crate)
                    {
                        var lockingEnt = crate.lockingEnt;

                        if (lockingEnt == null) continue;

                        var entity = lockingEnt.ToBaseEntity();

                        if (entity != null && !entity.IsDestroyed)
                        {
                            entity.Kill();
                        }
                    }
                    else if (e is FireBall fireball)
                    {
                        fireball.Extinguish();
                    }
                }

                if (e is HelicopterDebris debris)
                {
                    float num = damageEntryType == DamageEntryType.Heli ? config.Helicopter.TooHotUntil : config.Bradley.TooHotUntil;

                    if (num > -1)
                    {
                        debris.tooHotUntil = Time.realtimeSinceStartup + num;
                    }
                }
            }
            Pool.FreeUnmanaged(ref entities);
        }

        private void LockInRadius<T>(Vector3 position, LockInfo lockInfo, DamageEntryType damageEntryType) where T : BaseEntity
        {
            var entities = FindEntitiesOfType<T>(position, damageEntryType == DamageEntryType.Heli ? 50f : 20f);
            foreach (var entity in entities)
            {
                if (data.Lock.ContainsKey(entity.net.ID.Value))
                {
                    continue;
                }

                ulong ownerid = lockInfo.damageInfo.OwnerID;
                entity.OwnerID = ownerid;
                data.Lock[entity.net.ID.Value] = lockInfo;

                float time = GetLockTime(damageEntryType);

                entity.Invoke(() => entity.OwnerID = ownerid, 1f);

                if (time > 0f)
                {
                    entity.Invoke(() => entity.OwnerID = 0, time);
                }
            }
            Pool.FreeUnmanaged(ref entities);
        }

        private void LockInRadius(Vector3 position, ulong damageKey, DamageInfo damageInfo, ulong playerSteamID)
        {
            var corpses = FindEntitiesOfType<LootableCorpse>(position, 3f);
            foreach (var corpse in corpses)
            {
                if (corpse.IsValid() && corpse.playerSteamID == playerSteamID && !data.Lock.ContainsKey(corpse.net.ID.Value))
                {
                    if (config.Npc.LockTime > 0f)
                    {
                        var uid = corpse.net.ID.Value;

                        timer.Once(config.Npc.LockTime, () => data.Lock.Remove(uid));
                        corpse.Invoke(() => corpse.OwnerID = 0, config.Npc.LockTime);
                    }

                    ulong ownerid = damageInfo.OwnerID;
                    corpse.OwnerID = ownerid;
                    corpse.Invoke(() => corpse.OwnerID = ownerid, 1f);
                    data.Lock[corpse.net.ID.Value] = new(damageInfo, config.Npc.LockTime);
                    timer.Once(3f, () => data.Damage.Remove(damageKey));
                }
            }
            Pool.FreeUnmanaged(ref corpses);
        }

        private void LockInRadius(Vector3 position, LockInfo lockInfo, ulong playerSteamID)
        {
            var containers = FindEntitiesOfType<DroppedItemContainer>(position, 3f);
            foreach (var container in containers)
            {
                if (container.IsValid() && container.playerSteamID == playerSteamID && !data.Lock.ContainsKey(container.net.ID.Value))
                {
                    if (config.Npc.LockTime > 0f)
                    {
                        var uid = container.net.ID.Value;

                        timer.Once(config.Npc.LockTime, () => data.Lock.Remove(uid));
                        container.Invoke(() => container.OwnerID = 0, config.Npc.LockTime);
                    }

                    ulong ownerid = lockInfo.damageInfo.OwnerID;
                    container.OwnerID = ownerid;
                    container.Invoke(() => container.OwnerID = ownerid, 1f);
                    data.Lock[container.net.ID.Value] = lockInfo;
                }
            }
            Pool.FreeUnmanaged(ref containers);
        }

        private static int GetLockTime(DamageEntryType damageEntryType)
        {
            int time = damageEntryType == DamageEntryType.Bradley ? config.Bradley.LockTime : damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Npc.LockTime;

            return time > 0 ? time : int.MaxValue;
        }

        #endregion Helpers

        #region UI

        public class UI // Credits: Absolut & k1lly0u
        {
            private static CuiElementContainer CreateElementContainer(string panelName, string color, string aMin, string aMax, bool cursor = false, string parent = "Overlay")
            {
                var NewElement = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            Image =
                            {
                                Color = color
                            },
                            RectTransform =
                            {
                                AnchorMin = aMin,
                                AnchorMax = aMax
                            },
                            CursorEnabled = cursor
                        },
                        new CuiElement().Parent = parent,
                        panelName
                    }
                };
                return NewElement;
            }

            private static void CreateLabel(ref CuiElementContainer container, string panel, string color, string text, int size, string aMin, string aMax, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Color = color,
                        FontSize = size,
                        Align = align,
                        FadeIn = 1.0f,
                        Text = text
                    },
                    RectTransform =
                    {
                        AnchorMin = aMin,
                        AnchorMax = aMax
                    }
                },
                panel);
            }

            private static string Color(string hexColor, float a = 1.0f)
            {
                a = Mathf.Clamp(a, 0f, 1f);
                hexColor = hexColor.TrimStart('#');
                int r = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int g = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int b = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);
                return $"{(double)r / 255} {(double)g / 255} {(double)b / 255} {a}";
            }

            public static void DestroyLockoutUI(BasePlayer player)
            {
                if (player.IsValid() && player.IsConnected && Lockouts.Contains(player))
                {
                    CuiHelper.DestroyUi(player, BradleyPanelName);
                    CuiHelper.DestroyUi(player, HeliPanelName);
                    Lockouts.Remove(player);
                    DestroyLockoutUpdate(player);
                }
            }

            public static void DestroyAllLockoutUI()
            {
                foreach (var player in Lockouts)
                {
                    if (player.IsValid() && player.IsConnected && Lockouts.Contains(player))
                    {
                        CuiHelper.DestroyUi(player, BradleyPanelName);
                        CuiHelper.DestroyUi(player, HeliPanelName);
                        DestroyLockoutUpdate(player);
                    }
                }

                Lockouts.Clear();
            }

            private static void Create(BasePlayer player, string panelName, string text, int fontSize, string color, string panelColor, string aMin, string aMax)
            {
                var element = CreateElementContainer(panelName, panelColor, aMin, aMax, false, "Hud");

                CreateLabel(ref element, panelName, Color(color), text, fontSize, "0 0", "1 1");
                CuiHelper.AddUi(player, element);

                if (!Lockouts.Contains(player))
                {
                    Lockouts.Add(player);
                }
            }

            public static bool ShowTemporaryLockouts(BasePlayer player, string min, string max)
            {
                double bradleyTime = 3600;

                if (bradleyTime <= 0)
                {
                    return false;
                }

                string bradley = Math.Floor(TimeSpan.FromSeconds(bradleyTime).TotalMinutes).ToString();
                string bradleyBackgroundColor = Color(config.UI.Bradley.BackgroundColor, config.UI.Bradley.Alpha);

                Create(player, BradleyPanelName, _("Time", player.UserIDString, bradley), config.UI.Bradley.FontSize, config.UI.Bradley.TextColor, bradleyBackgroundColor, min, max);

                config.UI.Bradley.AnchorMin = min;
                config.UI.Bradley.AnchorMax = max;
                Instance.SaveConfig();

                player.Invoke(() => DestroyLockoutUI(player), 5f);
                return true;
            }

            public static void ShowLockouts(BasePlayer player)
            {
                if (Instance.IsF15EventActive || Instance.HasPermission(player, bypassLockoutsPerm))
                {
                    data.Lockouts.Remove(player.UserIDString);
                    return;
                }

                if (!data.Lockouts.TryGetValue(player.UserIDString, out var lo))
                {
                    data.Lockouts[player.UserIDString] = lo = new();
                }

                if (config.UI.Bradley.Enabled)
                {
                    double bradleyTime = GetLockoutTime(DamageEntryType.Bradley, lo, player.UserIDString);

                    if (bradleyTime > 0f)
                    {
                        string bradley = Math.Floor(TimeSpan.FromSeconds(bradleyTime).TotalMinutes).ToString();
                        string bradleyBackgroundColor = Color(config.UI.Bradley.BackgroundColor, config.UI.Bradley.Alpha);

                        Create(player, BradleyPanelName, _("Time", player.UserIDString, bradley), config.UI.Bradley.FontSize, config.UI.Bradley.TextColor, bradleyBackgroundColor, config.UI.Bradley.AnchorMin, config.UI.Bradley.AnchorMax);
                        SetLockoutUpdate(player);
                    }
                }

                if (config.UI.Heli.Enabled)
                {
                    double heliTime = GetLockoutTime(DamageEntryType.Heli, lo, player.UserIDString);

                    if (heliTime > 0)
                    {
                        string heli = Math.Floor(TimeSpan.FromSeconds(heliTime).TotalMinutes).ToString();
                        string heliBackgroundColor = Color(config.UI.Heli.BackgroundColor, config.UI.Heli.Alpha);

                        Create(player, HeliPanelName, _("Heli Time", player.UserIDString, heli), config.UI.Heli.FontSize, config.UI.Heli.TextColor, heliBackgroundColor, config.UI.Heli.AnchorMin, config.UI.Heli.AnchorMax);
                        SetLockoutUpdate(player);
                    }
                }
            }

            public static double GetLockoutTime(DamageEntryType damageEntryType)
            {
                switch (damageEntryType)
                {
                    case DamageEntryType.Bradley:
                        {
                            return config.Lockout.Bradley * 60;
                        }
                    case DamageEntryType.Heli:
                        {
                            return config.Lockout.Heli * 60;
                        }
                }

                return 0;
            }

            public static double GetLockoutTime(DamageEntryType damageEntryType, Lockout lo, string playerId)
            {
                double time = 0;

                switch (damageEntryType)
                {
                    case DamageEntryType.Bradley:
                        {
                            if ((time = lo.Bradley) <= 0 || (time -= Epoch.Current) <= 0)
                            {
                                lo.Bradley = 0;
                            }

                            break;
                        }
                    case DamageEntryType.Heli:
                        {
                            if ((time = lo.Heli) <= 0 || (time -= Epoch.Current) <= 0)
                            {
                                lo.Heli = 0;
                            }

                            break;
                        }
                }

                if (!lo.Any())
                {
                    data.Lockouts.Remove(playerId);
                }

                return time < 0 ? 0 : time;
            }

            public static void UpdateLockoutUI(BasePlayer player)
            {
                Lockouts.RemoveAll(p => p == null || !p.IsConnected);

                if (player == null || !player.IsConnected)
                {
                    return;
                }

                DestroyLockoutUI(player);

                var uii = GetSettings(player.UserIDString);

                if (!uii.Enabled || !uii.Lockouts)
                {
                    return;
                }

                ShowLockouts(player);
            }

            private static void SetLockoutUpdate(BasePlayer player)
            {
                if (!InvokeTimers.TryGetValue(player.userID, out var timers))
                {
                    InvokeTimers[player.userID] = timers = new();
                }

                if (timers.Lockout == null || timers.Lockout.Destroyed)
                {
                    timers.Lockout = Instance.timer.Once(60f, () => UpdateLockoutUI(player));
                }
                else
                {
                    timers.Lockout.Reset();
                }
            }

            public static void DestroyLockoutUpdate(BasePlayer player)
            {
                if (!InvokeTimers.TryGetValue(player.userID, out var timers))
                {
                    return;
                }

                if (timers.Lockout == null || timers.Lockout.Destroyed)
                {
                    return;
                }

                timers.Lockout.Destroy();
            }

            public static Info GetSettings(string playerId)
            {
                if (!data.UI.TryGetValue(playerId, out var uii))
                {
                    data.UI[playerId] = uii = new();
                }

                return uii;
            }

            private const string BradleyPanelName = "Lockouts_UI_Bradley";
            private const string HeliPanelName = "Lockouts_UI_Heli";

            public static List<BasePlayer> Lockouts { get; set; } = new();
            public static Dictionary<ulong, Timers> InvokeTimers { get; set; } = new();

            public class Timers
            {
                public Timer Lockout;
            }

            public class Info
            {
                public bool Enabled { get; set; } = true;
                public bool Lockouts { get; set; } = true;
            }
        }

        private void CommandUI(IPlayer user, string command, string[] args)
        {
            var player = user.Object as BasePlayer;
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
                if (args.Length == 2)
                {
                    if (args[0] == "setbradleytime")
                    {
                        if (double.TryParse(args[1], out var time))
                        {
                            config.Lockout.Bradley = time;
                            SaveConfig();

                            user.Reply($"Cooldown changed to {time} minutes");
                            ApplyCooldowns(DamageEntryType.Bradley);
                        }
                        else user.Reply($"The specified time '{args[1]}' is not a valid number.");
                    }
                    if (args[0] == "sethelitime")
                    {
                        if (double.TryParse(args[1], out var time))
                        {
                            config.Lockout.Heli = time;
                            SaveConfig();

                            user.Reply($"Cooldown changed to {time} minutes");
                            ApplyCooldowns(DamageEntryType.Heli);
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
                    else if (command == "unlock")
                    {
                        foreach (var pair in data.Lock.ToList())
                        {
                            if (player.Distance(pair.Value.damageInfo._position) < 25f || pair.Value.CanInteract(player.userID, player))
                            {
                                if (pair.Value.damageInfo._entity != null)
                                {
                                    pair.Value.damageInfo._entity.OwnerID = 0uL;
                                    Message(player, $"Unlocked {(pair.Value.damageInfo.damageEntryType == DamageEntryType.Bradley ? "bradley" : pair.Value.damageInfo.damageEntryType == DamageEntryType.NPC ? "npc" : pair.Value.damageInfo.damageEntryType == DamageEntryType.Heli ? "heli" : "corpse")}");
                                }
                                data.Lock.Remove(pair.Key);
                            }
                        }
                    }
                }
            }

            if (player.IsAdmin && args.Length == 3)
            {
                UI.ShowTemporaryLockouts(player, args[1], args[2]);
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

        private void SendDiscordMessage(HashSet<ulong> members, List<string> usernames, Vector3 position, DamageEntryType damageEntryType)
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

            SendDiscordMessage(players, position, damageEntryType == DamageEntryType.Bradley ? _("BradleyKilled") : _("HeliKilled"));
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
            public bool NotifyChat { get; set; } = true;

            [JsonProperty(PropertyName = "Broadcast Kill Notification To Killer")]
            public bool NotifyKiller { get; set; } = true;

            [JsonProperty(PropertyName = "Broadcast Locked Notification To Chat", NullValueHandling = NullValueHandling.Ignore)]
            public bool? NotifyLocked { get; set; } = true;
        }

        private class HackPermission
        {
            [JsonProperty(PropertyName = "Permission")]
            public string Permission { get; set; }

            [JsonProperty(PropertyName = "Hack Time")]
            public float Value { get; set; }
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
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Permissions Enabled To Set Required Hack Seconds")]
            public bool Seconds { get; set; } = true;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime { get; set; } = 900;

            [JsonProperty(PropertyName = "Lock Hackable Crates At Harbor")]
            public bool Harbor { get; set; }

            [JsonProperty(PropertyName = "Block Timer Increase On Damage To Laptop")]
            public bool Laptop { get; set; } = true;

            [JsonProperty(PropertyName = "Broadcast Locked Notification To Chat", NullValueHandling = NullValueHandling.Ignore)]
            public bool NotifyLocked { get; set; }

            [JsonProperty(PropertyName = "Broadcast Unlocked Notification To Chat", NullValueHandling = NullValueHandling.Ignore)]
            public bool NotifyUnlocked { get; set; }

            [JsonProperty(PropertyName = "Cooldown Between Notifications For Each Player")]
            public float NotifyCooldown { get; set; }
        }

        private class BradleySettings
        {
            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages { get; set; } = new();

            [JsonProperty(PropertyName = "Damage Lock Threshold")]
            public float Threshold { get; set; } = 0.2f;

            [JsonProperty(PropertyName = "Harvest Too Hot Until (0 = Never)")]
            public float TooHotUntil { get; set; } = 480f;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime { get; set; } = 900;

            [JsonProperty(PropertyName = "Remove Fire From Crates")]
            public bool RemoveFireFromCrates { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Bradley At Launch Site")]
            public bool LockLaunchSite { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Bradley At Harbor")]
            public bool LockHarbor { get; set; }

            [JsonProperty(PropertyName = "Lock Bradley From Personal Apc Plugin")]
            public bool LockPersonal { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Bradley From Monument Bradley Plugin")]
            public bool LockMonument { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Bradley From Convoy Plugin")]
            public bool LockConvoy { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Bradley From Bradley Tiers Plugin")]
            public bool LockBradleyTiers { get; set; }

            [JsonProperty(PropertyName = "Lock Bradley From Everywhere Else")]
            public bool LockWorldly { get; set; } = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; }

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP { get; set; } = 0.0;

            [JsonProperty(PropertyName = "XP Reward")]
            public double XP { get; set; } = 0.0;

            [JsonProperty(PropertyName = "ShoppyStock Reward Value")]
            public double SS { get; set; }

            [JsonProperty(PropertyName = "ShoppyStock Shop Name")]
            public string ShoppyStockShopName { get; set; } = "";
        }

        private class HelicopterSettings
        {
            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages { get; set; } = new();

            [JsonProperty(PropertyName = "Damage Lock Threshold")]
            public float Threshold { get; set; } = 0.2f;

            [JsonProperty(PropertyName = "Harvest Too Hot Until (0 = Never)")]
            public float TooHotUntil { get; set; } = 480f;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime { get; set; } = 900;

            [JsonProperty(PropertyName = "Remove Fire From Crates")]
            public bool RemoveFireFromCrates { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Heli From Convoy Plugin")]
            public bool LockConvoy { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Heli At Harbor")]
            public bool? LockHarbor { get; set; } = null;

            [JsonProperty(PropertyName = "Lock Heli From Personal Heli Plugin")]
            public bool LockPersonal { get; set; } = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; }

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP { get; set; } = 0.0;

            [JsonProperty(PropertyName = "XP Reward")]
            public double XP { get; set; } = 0.0;

            [JsonProperty(PropertyName = "ShoppyStock Reward Value")]
            public double SS { get; set; }

            [JsonProperty(PropertyName = "ShoppyStock Shop Name")]
            public string ShoppyStockShopName { get; set; } = "";
        }

        private class NpcSettings
        {
            [JsonProperty(PropertyName = "Reward Distance Multiplier")]
            public DistanceMultiplierSettings Distance { get; set; } = new();

            [JsonProperty(PropertyName = "Reward Weapon Multiplier", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, double> WeaponMultipliers { get; set; } = new()
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
            public NotifySettings Messages { get; set; } = new() { NotifyLocked = false };

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Damage Lock Threshold")]
            public float Threshold { get; set; } = 0.2f;

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime { get; set; }

            [JsonProperty(PropertyName = "Minimum Starting Health Requirement")]
            public float Min { get; set; }

            [JsonProperty(PropertyName = "Lock BossMonster Npcs")]
            public bool BossMonster { get; set; }

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; } = true;

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP { get; set; } = 0.0;

            [JsonProperty(PropertyName = "XP Reward")]
            public double XP { get; set; } = 0.0;

            [JsonProperty(PropertyName = "ShoppyStock Reward Value")]
            public double SS { get; set; }

            [JsonProperty(PropertyName = "ShoppyStock Shop Name")]
            public string ShoppyStockShopName { get; set; } = "";
        }

        private class DistanceMultiplierSettings
        {
            [JsonProperty(PropertyName = "400 meters")]
            public float meters400 { get; set; } = 1f;

            [JsonProperty(PropertyName = "300 meters")]
            public float meters300 { get; set; } = 1f;

            [JsonProperty(PropertyName = "200 meters")]
            public float meters200 { get; set; } = 1f;

            [JsonProperty(PropertyName = "100 meters")]
            public float meters100 { get; set; } = 1f;

            [JsonProperty(PropertyName = "75 meters")]
            public float meters75 { get; set; } = 1f;

            [JsonProperty(PropertyName = "50 meters")]
            public float meters50 { get; set; } = 1f;

            [JsonProperty(PropertyName = "25 meters")]
            public float meters25 { get; set; } = 1f;

            [JsonProperty(PropertyName = "under")]
            public float under { get; set; } = 1f;

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
            public List<ulong> Skins { get; set; } = new() { 0 };

            [JsonProperty(PropertyName = "Lock Supply Drops To Players")]
            public bool Lock { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Supply Drops From Excavator")]
            public bool Excavator { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Supply Drops From Helpful Supply Plugin")]
            public bool HelpfulSupply { get; set; }

            [JsonProperty(PropertyName = "Lock Supply Drops From Npc Random Raids Plugin")]
            public bool NpcRandomRaids { get; set; }

            [JsonProperty(PropertyName = "Lock To Player For X Seconds (0 = Forever)")]
            public float LockTime { get; set; }

            [JsonProperty(PropertyName = "Supply Drop Drag")]
            public float Drag { get; set; } = 0.6f;

            [JsonProperty(PropertyName = "Show Grid In Thrown Notification")]
            public bool ThrownAt { get; set; }

            [JsonProperty(PropertyName = "Show Thrown Notification In Chat")]
            public bool NotifyChat { get; set; }

            [JsonProperty(PropertyName = "Show Notification In Server Console")]
            public bool NotifyConsole { get; set; }

            [JsonProperty(PropertyName = "Cooldown Between Notifications For Each Player")]
            public float NotifyCooldown { get; set; }

            [JsonProperty(PropertyName = "Cargo Plane Speed (Meters Per Second)")]
            public float Speed { get; set; } = 40f;

            [JsonProperty(PropertyName = "Cargo Plane Low Altitude Drop")]
            public bool LowDrop { get; set; } = true;

            [JsonProperty(PropertyName = "Bypass Spawning Cargo Plane")]
            public bool Bypass { get; set; }

            [JsonProperty(PropertyName = "Smoke Duration")]
            public float Smoke { get; set; } = -1f;

            [JsonProperty(PropertyName = "Maximum Drop Distance From Signal")]
            public float DistanceFromSignal { get; set; } = 20;

            [JsonProperty(PropertyName = "Destroy Drop After X Seconds")]
            public float DestroyTime { get; set; }
        }

        private class DamageReportSettings
        {
            [JsonProperty(PropertyName = "Hex Color - Single Player")]
            public string SinglePlayer { get; set; } = "#6d88ff";

            [JsonProperty(PropertyName = "Hex Color - Team")]
            public string Team { get; set; } = "#ff804f";

            [JsonProperty(PropertyName = "Hex Color - Ok")]
            public string Ok { get; set; } = "#88ff6d";

            [JsonProperty(PropertyName = "Hex Color - Not Ok")]
            public string NotOk { get; set; } = "#ff5716";
        }

        public class PluginSettingsBaseLockout
        {
            [JsonProperty(PropertyName = "Bypass During F15 Server Wipe Event")]
            public bool F15 { get; set; }

            [JsonProperty(PropertyName = "Command To See Lockout Times")]
            public string Command { get; set; } = "lockouts";

            [JsonProperty(PropertyName = "Time Between Bradley In Minutes")]
            public double Bradley { get; set; }

            [JsonProperty(PropertyName = "Time Between Heli In Minutes")]
            public double Heli { get; set; }

            [JsonProperty(PropertyName = "Lockout Entire Team")]
            public bool Team { get; set; } = true;

            [JsonProperty(PropertyName = "Lockout Entire Clan")]
            public bool Clan { get; set; } = true;

            [JsonProperty(PropertyName = "Exclude Members Offline For More Than X Minutes")]
            public float Time { get; set; } = 15f;

            [JsonProperty(PropertyName = "Lockouts Ignored For Entities With Skin ID", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Exceptions { get; set; } = new();
        }

        public class UIBradleyLockoutSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Bradley Anchor Min")]
            public string AnchorMin { get; set; } = "0.946 0.325";

            [JsonProperty(PropertyName = "Bradley Anchor Max")]
            public string AnchorMax { get; set; } = "0.986 0.360";

            [JsonProperty(PropertyName = "Bradley Background Color")]
            public string BackgroundColor { get; set; } = "#A52A2A";

            [JsonProperty(PropertyName = "Bradley Text Color")]
            public string TextColor { get; set; } = "#FFFF00";

            [JsonProperty(PropertyName = "Panel Alpha")]
            public float Alpha { get; set; } = 1f;

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize { get; set; } = 18;
        }

        public class UIHeliLockoutSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Heli Anchor Min")]
            public string AnchorMin { get; set; } = "0.896 0.325";

            [JsonProperty(PropertyName = "Heli Anchor Max")]
            public string AnchorMax { get; set; } = "0.936 0.360";

            [JsonProperty(PropertyName = "Heli Background Color")]
            public string BackgroundColor { get; set; } = "#1F51FF";

            [JsonProperty(PropertyName = "Heli Text Color")]
            public string TextColor { get; set; } = "#FFFF00";

            [JsonProperty(PropertyName = "Panel Alpha")]
            public float Alpha { get; set; } = 1f;

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize { get; set; } = 18;
        }

        public class UISettings
        {
            [JsonProperty(PropertyName = "Command To Toggle UI")]
            public string Command { get; set; } = "lockui";

            [JsonProperty(PropertyName = "Bradley")]
            public UIBradleyLockoutSettings Bradley { get; set; } = new();

            [JsonProperty(PropertyName = "Heli")]
            public UIHeliLockoutSettings Heli { get; set; } = new();
        }

        public class DiscordMessagesSettings
        {
            [JsonProperty(PropertyName = "Message - Webhook URL")]
            public string WebhookUrl { get; set; } = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";

            [JsonProperty(PropertyName = "Message - Embed Color (DECIMAL)")]
            public int MessageColor { get; set; } = 3329330;

            [JsonProperty(PropertyName = "Embed_MessageTitle")]
            public string EmbedMessageTitle { get; set; } = "Lockouts";

            [JsonProperty(PropertyName = "Embed_MessagePlayer")]
            public string EmbedMessagePlayer { get; set; } = "Player";

            [JsonProperty(PropertyName = "Embed_MessageMessage")]
            public string EmbedMessageMessage { get; set; } = "Message";

            [JsonProperty(PropertyName = "Embed_MessageServer")]
            public string EmbedMessageServer { get; set; } = "Connect via Steam:";

            [JsonProperty(PropertyName = "Add BattleMetrics Link")]
            public bool BattleMetrics { get; set; } = true;

            [JsonProperty(PropertyName = "Show Notification In Server Console")]
            public bool NotifyConsole { get; set; }
        }

        private class Configuration
        {
            [JsonProperty(PropertyName = "Bradley Settings")]
            public BradleySettings Bradley { get; set; } = new();

            [JsonProperty(PropertyName = "Helicopter Settings")]
            public HelicopterSettings Helicopter { get; set; } = new();

            [JsonProperty(PropertyName = "Hackable Crate Settings")]
            public HackableSettings Hackable { get; set; } = new();

            [JsonProperty(PropertyName = "Npc Settings")]
            public NpcSettings Npc { get; set; } = new();

            [JsonProperty(PropertyName = "Supply Drop Settings")]
            public SupplyDropSettings SupplyDrop { get; set; } = new();

            [JsonProperty(PropertyName = "Damage Report Settings")]
            public DamageReportSettings Report { get; set; } = new();

            [JsonProperty(PropertyName = "Player Lockouts (0 = ignore)")]
            public PluginSettingsBaseLockout Lockout { get; set; } = new();

            [JsonProperty(PropertyName = "Lockout UI")]
            public UISettings UI { get; set; } = new();

            [JsonProperty(PropertyName = "Discord Messages")]
            public DiscordMessagesSettings DiscordMessages { get; set; } = new();

            [JsonProperty(PropertyName = "Disable CH47 Gibs")]
            public bool CH47Gibs { get; set; }

            [JsonProperty(PropertyName = "Chat ID")]
            public ulong ChatID { get; set; }
        }

        private static Configuration config;
        private bool configLoaded;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            canSaveConfig = false;
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
                if (config.Bradley.Threshold > 1f) config.Bradley.Threshold /= 100f;
                if (config.Helicopter.Threshold > 1f) config.Helicopter.Threshold /= 100f;
                if (config.Npc.Threshold > 1f) config.Npc.Threshold /= 100f;
                if (!config.Helicopter.LockHarbor.HasValue) config.Helicopter.LockHarbor = config.Bradley.LockHarbor;
                if (!config.Npc.Messages.NotifyLocked.HasValue) config.Npc.Messages.NotifyLocked = false;
                canSaveConfig = true;
                SaveConfig();
            }
            catch (Exception ex)
            {
                Puts(ex.ToString());
                LoadDefaultConfig();
            }
        }

        private bool canSaveConfig = true;

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

        private static string _(string key, string userId = null, params object[] args)
        {
            string message = userId == "server_console" || userId == null ? RemoveFormatting(Instance.lang.GetMessage(key, Instance, userId)) : Instance.lang.GetMessage(key, Instance, userId);

            return args.Length > 0 ? string.Format(message, args) : message;
        }

        public static string RemoveFormatting(string source) => source.Contains(">") ? Regex.Replace(source, "<.*?>", string.Empty) : source;

        private static void CreateMessage(BasePlayer player, string key, params object[] args)
        {
            if (player.IsValid())
            {
                Instance.Player.Message(player, _(key, player.UserIDString, args), config.ChatID);
            }
        }

        private static void Message(BasePlayer player, string message)
        {
            if (player.IsValid())
            {
                Instance.Player.Message(player, message, config.ChatID);
            }
        }

        #endregion
    }
}