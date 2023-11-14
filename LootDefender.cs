using Facepunch;
using Facepunch.Math;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Loot Defender", "Author Egor Blagov, Maintainer nivex", "2.1.4")]
    [Description("Defends loot from other players who dealt less damage than you.")]
    internal class LootDefender : RustPlugin
    {
        [PluginReference]
        Plugin PersonalHeli, Friends, Clans, RustRewards, HeliSignals, BradleyDrops;

        private static LootDefender Instance;
        private static StringBuilder sb;
        private const ulong supplyDropSkinID = 234501;
        private const string bypassLootPerm = "lootdefender.bypass.loot";
        private const string bypassDamagePerm = "lootdefender.bypass.damage";
        private const string bypassLockoutsPerm = "lootdefender.bypass.lockouts";
        private Dictionary<BradleyAPC, List<DamageKey>> _apcAttackers = new Dictionary<BradleyAPC, List<DamageKey>>();
        private Dictionary<NetworkableId, ulong> _locked { get; set; } = new Dictionary<NetworkableId, ulong>();
        private List<NetworkableId> _personal { get; set; } = new List<NetworkableId>();
        private List<NetworkableId> _boss { get; set; } = new List<NetworkableId>();
        private List<string> _sent { get; set; } = new List<string>();
        private static StoredData data { get; set; } = new StoredData();
        private MonumentInfo launchSite { get; set; }
        private List<MonumentInfo> harbors { get; set; } = new List<MonumentInfo>();
        private List<ulong> ownerids = new List<ulong> { 0, 1337422, 3566257, 123425345634634 };

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

            public bool Any() => Bradley > 0;
        }

        private class StoredData
        {
            public Dictionary<string, Lockout> Lockouts { get; } = new Dictionary<string, Lockout>();
            public Dictionary<string, UI.Info> UI { get; set; } = new Dictionary<string, UI.Info>();
            public Dictionary<NetworkableId, DamageInfo> Damage { get; set; } = new Dictionary<NetworkableId, DamageInfo>();
            public Dictionary<NetworkableId, LockInfo> Lock { get; set; } = new Dictionary<NetworkableId, LockInfo>();

            public void Sanitize()
            {
                foreach (var damageInfo in Damage.ToList())
                {
                    damageInfo.Value._entity = BaseNetworkable.serverEntities.Find(damageInfo.Key) as BaseEntity;

                    if (damageInfo.Value.damageEntryType == DamageEntryType.NPC && !config.Npc.Enabled)
                    {
                        if (damageInfo.Value._entity.IsValid())
                        {
                            damageInfo.Value._entity.OwnerID = 0uL;
                        }

                        Damage.Remove(damageInfo.Key);
                    }
                    else if (damageInfo.Value._entity == null)
                    {
                        Damage.Remove(damageInfo.Key);
                    }
                    else
                    {
                        foreach (var x in damageInfo.Value.damageKeys)
                        {
                            x.attacker = BasePlayer.FindByID(x.id);
                        }

                        damageInfo.Value.Start();
                    }
                }

                foreach (var lockInfo in Lock.ToList())
                {
                    var entity = BaseNetworkable.serverEntities.Find(lockInfo.Key) as BaseEntity;

                    if (lockInfo.Value.damageInfo.damageEntryType == DamageEntryType.NPC && !config.Npc.Enabled)
                    {
                        if (entity.IsValid())
                        {
                            entity.OwnerID = 0uL;
                        }

                        Lock.Remove(lockInfo.Key);
                    }
                    else if (entity == null)
                    {
                        Lock.Remove(lockInfo.Key);
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
            public ulong id { get; set; }
            public string name { get; set; }
            public DamageEntry damageEntry { get; set; }
            internal BasePlayer attacker { get; set; }

            public DamageKey() { }

            public DamageKey(BasePlayer attacker)
            {
                this.attacker = attacker;
                id = attacker.userID;
                name = attacker.displayName;
            }
        }

        private class DamageInfo
        {
            public List<DamageKey> damageKeys { get; set; } = new List<DamageKey>();
            private List<ulong> interact { get; set; } = new List<ulong>();
            private List<ulong> participants { get; set; } = new List<ulong>();
            public DamageEntryType damageEntryType { get; set; } = DamageEntryType.None;
            public string NPCName { get; set; }
            public ulong OwnerID { get; set; }
            public DateTime start { get; set; }
            internal int _lockTime { get; set; }
            [JsonIgnore]
            internal BaseEntity _entity { get; set; }
            internal Vector3 _position { get; set; }
            internal NetworkableId _uid { get; set; }
            [JsonIgnore]
            internal Timer _timer { get; set; }
            internal List<DamageKey> keys { get; set; } = new List<DamageKey>();

            List<DamageGroup> damageGroups;

            internal float FullDamage
            {
                get
                {
                    float sum = 0f;

                    foreach (var x in damageKeys)
                    {
                        sum += x.damageEntry.DamageDealt;
                    }

                    return sum;
                }
            }

            public DamageInfo() { }

            public DamageInfo(DamageEntryType damageEntryType, string NPCName, BaseEntity entity, DateTime start)
            {
                _entity = entity;
                _uid = entity.net.ID;

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

            public void DestroyMe()
            {
                _timer?.Destroy();
            }

            private void CheckExpiration()
            {
                damageKeys.ForEach(x =>
                {
                    if (x.damageEntry.IsOutdated(_lockTime))
                    {
                        if (x.id == OwnerID)
                        {
                            Unlock();
                        }

                        keys.Add(x);
                    }
                });

                keys.ForEach(x => damageKeys.Remove(x));
                keys.Clear();
            }

            private void Unlock()
            {
                OwnerID = 0;

                if (_entity.IsValid() && !_entity.IsDestroyed)
                {
                    _entity.OwnerID = 0;
                }

                if (!Instance._locked.Remove(_uid)) return;

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

                if (!Update(entity))
                {
                    return;
                }

                float damage = 0f;
                var grid = PhoneController.PositionToGridCoord(entity.transform.position);

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

                if (config.Helicopter.Threshold > 0f && entity is BaseHelicopter)
                {
                    if (damage >= entity.MaxHealth() * config.Helicopter.Threshold)
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
                    if (damage >= entity.MaxHealth() * config.Bradley.Threshold)
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
                else if (config.Npc.Threshold > 0f && entity is BasePlayer && Instance.CanLockNpc(entity))
                {
                    var npc = entity as BasePlayer;

                    if (!npc.userID.IsSteamId() && damage >= entity.MaxHealth() * config.Npc.Threshold)
                    {
                        Lock(entity, attacker.userID);
                    }
                }
            }

            private bool Update(BaseEntity entity)
            {
                if (damageEntryType == DamageEntryType.NPC && !Instance.CanLockNpc(entity))
                {
                    return false;
                }

                if (damageEntryType == DamageEntryType.Bradley && !Instance.CanLockBradley(entity))
                {
                    return false;
                }

                if (entity.OwnerID.IsSteamId())
                {
                    OwnerID = entity.OwnerID;
                }

                return OwnerID == 0uL;
            }

            public DamageEntry TryGet(ulong id)
            {
                foreach (var x in damageKeys)
                {
                    if (x.id == id)
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
                    damageKeys.Add(new DamageKey(attacker)
                    {
                        damageEntry = entry = new DamageEntry(attacker.currentTeam),
                    });
                }

                return entry;
            }

            public void OnKilled(Vector3 position)
            {
                SetCanInteract();
                DisplayDamageReport();
                LockoutBradleyLooters(position);
            }

            private void LockoutBradleyLooters(Vector3 position)
            {
                if (damageEntryType == DamageEntryType.Bradley)
                {
                    List<ulong> looters = new List<ulong>();

                    foreach (var x in damageKeys)
                    {
                        if (CanInteract(x.id))
                        {
                            looters.Add(x.id);
                        }
                    }

                    Instance.LockoutBradleyLooters(looters, position);
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
                            Message(x.attacker, GetDamageReport(x.id));
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
                    if (config.Bradley.Messages.NotifyKiller && IsParticipant(target.userID))
                    {
                        return true;
                    }

                    return config.Bradley.Messages.NotifyChat;
                }

                if (damageEntryType == DamageEntryType.Heli)
                {
                    if (config.Helicopter.Messages.NotifyKiller && IsParticipant(target.userID))
                    {
                        return true;
                    }

                    return config.Helicopter.Messages.NotifyChat;
                }

                if (damageEntryType == DamageEntryType.NPC)
                {
                    if (config.Npc.Messages.NotifyKiller && IsParticipant(target.userID))
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
                        if (topDamageGroups.Contains(damageGroup) || Instance.IsAlly(OwnerID, damageGroup.FirstDamagerDealer.id))
                        {
                            interact.Add(damageGroup.FirstDamagerDealer.id);
                        }
                        else
                        {
                            participants.Add(damageGroup.FirstDamagerDealer.id);
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
                        if (interact.Contains(damageGroup.FirstDamagerDealer.id))
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

                    if (damageEntryType == DamageEntryType.NPC && !string.IsNullOrEmpty(firstDamageDealer))
                    {
                        sb.Append($" {_("FirstLock", userid, firstDamageDealer, config.Npc.Threshold * 100f)}");
                    }
                }

                return sb.ToString();
            }

            public bool IsParticipant(ulong userid)
            {
                return participants.Contains(userid) || CanInteract(userid);
            }

            public bool CanInteract(ulong userid)
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

                if (interact.Count == 0 || interact.Contains(userid))
                {
                    return true;
                }

                if (Instance.IsAlly(userid, OwnerID))
                {
                    interact.Add(userid);
                    return true;
                }

                return false;
            }

            private List<DamageGroup> GetTopDamageGroups(List<DamageGroup> damageGroups, DamageEntryType damageEntryType)
            {
                var topDamageGroups = new List<DamageGroup>();

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
                var damageGroups = new List<DamageGroup>();

                foreach (var x in damageKeys)
                {
                    damageGroups.Add(new DamageGroup(x));
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

            public bool CanInteract(ulong userId) => damageInfo.CanInteract(userId);

            public string GetDamageReport(ulong userId) => damageInfo.GetDamageReport(userId);
        }

        private class DamageGroup
        {
            public float TotalDamage { get; set; }

            public DamageKey FirstDamagerDealer { get; set; }

            private List<ulong> additionalPlayers { get; set; } = new List<ulong>();

            [JsonIgnore]
            public List<ulong> Players
            {
                get
                {
                    var list = new List<ulong>
                    {
                        FirstDamagerDealer.id
                    };

                    foreach (var targetId in additionalPlayers)
                    {
                        if (!list.Contains(targetId))
                        {
                            list.Add(targetId);
                        }
                    }

                    return list;
                }
            }

            public DamageGroup() { }

            public DamageGroup(DamageKey x)
            {
                TotalDamage = x.damageEntry.DamageDealt;
                FirstDamagerDealer = x;

                RelationshipManager.PlayerTeam team;
                if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(x.id, out team))
                {
                    for (int i = 0; i < team.members.Count; i++)
                    {
                        ulong member = team.members[i];

                        if (member == x.id || additionalPlayers.Contains(member))
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
                var damage = damageInfo.TryGet(damageKey.id)?.DamageDealt ?? 0f;
                var percent = damage > 0 && damageInfo.FullDamage > 0 ? damage / damageInfo.FullDamage * 100 : 0;
                var color = additionalPlayers.Count == 0 ? config.Report.SinglePlayer : config.Report.Team;
                var damageLine = _("Format", damageKey.id.ToString(), damage, percent);

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
            Unsubscribe();
            Instance = this;
            sb = new StringBuilder();
            AddCovalenceCommand("testlootdef", nameof(CommandTest));
            AddCovalenceCommand("lo", nameof(CommandUI));
            RegisterPermissions();
            LoadData();
        }

        private void OnServerInitialized()
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
                    Subscribe(nameof(OnSupplyDropLanded));
                }

                if (config.SupplyDrop.Excavator)
                {
                    Subscribe(nameof(OnExcavatorSuppliesRequested));
                }

                Subscribe(nameof(OnExplosiveDropped));
                Subscribe(nameof(OnExplosiveThrown));
                Subscribe(nameof(OnSupplyDropDropped));
                Subscribe(nameof(OnCargoPlaneSignaled));
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

            if (config.UI.Bradley.Enabled)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    UI.ShowLockouts(player);
                }

                Subscribe(nameof(OnPlayerSleepEnded));
            }

            if (config.Bradley.Threshold != 0f || config.Helicopter.Threshold != 0f)
            {
                Subscribe(nameof(OnPlayerAttack));
            }

            if (!config.SupplyDrop.Skins.Contains(0uL))
            {
                config.SupplyDrop.Skins.Add(0uL);
            }

            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnEntityDeath));
            Subscribe(nameof(OnEntityKill));
            Subscribe(nameof(CanLootEntity));
            Subscribe(nameof(CanBradleyTakeDamage));
            SetupLaunchSite();
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

            if (config.Hackable.Laptop && hitInfo.HitBone == 242862488) // laptopcollision
            {
                hitInfo.HitBone = 0;
                return null;
            }

            if (!(hitInfo.HitEntity is ServerGib))
            {
                return null;
            }

            LockInfo lockInfo;
            if (data.Lock.TryGetValue(hitInfo.HitEntity.net.ID, out lockInfo))
            {
                if (hitInfo.HitEntity.OwnerID != 0 && !lockInfo.IsLockOutdated)
                {
                    if (!lockInfo.CanInteract(attacker.userID))
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
                    data.Lock.Remove(hitInfo.HitEntity.net.ID);
                    hitInfo.HitEntity.OwnerID = 0;
                }
            }

            return null;
        }

        private object OnEntityTakeDamage(PatrolHelicopter heli, HitInfo hitInfo)
        {
            if (config.Helicopter.Threshold == 0f || !heli.IsValid() || _personal.Contains(heli.net.ID) || hitInfo == null || heli.myAI == null || heli.myAI.isDead)
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

            if (!config.Helicopter.LockConvoy && entity.skinID == 755446 && entity is BaseHelicopter)
            {
                return null;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (!attacker.IsValid() || !attacker.userID.IsSteamId())
            {
                return null;
            }

            ulong ownerId;
            if (_locked.TryGetValue(entity.net.ID, out ownerId) && !HasPermission(attacker, bypassDamagePerm) && !IsAlly(attacker.userID, ownerId))
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

            DamageInfo damageInfo;
            if (!data.Damage.TryGetValue(entity.net.ID, out damageInfo))
            {
                data.Damage[entity.net.ID] = damageInfo = new DamageInfo(damageEntryType, npcName, entity, DateTime.Now);
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
            if (config.Lockout.Bradley <= 0 || !apc.IsValid() || !(hitInfo.Initiator is BasePlayer))
            {
                return null;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (HasLockout(attacker, DamageEntryType.Bradley))
            {
                CancelDamage(hitInfo);
                return false;
            }

            if (!data.Lockouts.ContainsKey(attacker.UserIDString))
            {
                List<DamageKey> attackers;
                if (!_apcAttackers.TryGetValue(apc, out attackers))
                {
                    _apcAttackers[apc] = attackers = new List<DamageKey>();
                }

                if (!attackers.Exists(x => x.id == attacker.userID))
                {
                    attackers.Add(new DamageKey(attacker));
                }
            }

            return null;
        }

        private void OnEntityDeath(BaseHelicopter heli, HitInfo hitInfo)
        {
            if (!heli.IsValid())
            {
                return;
            }

            _personal.Remove(heli.net.ID);

            OnEntityDeathHandler(heli, DamageEntryType.Heli, hitInfo);
        }

        private void OnEntityKill(BaseHelicopter heli) => OnEntityDeath(heli, null);

        private void OnEntityDeath(BradleyAPC apc, HitInfo hitInfo)
        {
            if (!apc.IsValid())
            {
                return;
            }

            _apcAttackers.Remove(apc);

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
            if (config.Bradley.Threshold == 0f || _personal.Contains(entity.net.ID))
            {
                return false;
            }

            if (entity.skinID != 0)
            {
                if (entity.skinID == 755446)
                {
                    return config.Bradley.LockConvoy;
                }
                return false;
            }

            if (entity.name.Contains($"BradleyApc[{entity.net.ID}]"))
            {
                return config.Bradley.LockBradleyTiers;
            }

            if (launchSite != null && IsInBounds(launchSite, entity.ServerPosition))
            {
                return config.Bradley.LockLaunchSite;
            }

            if (harbors.Exists(mi => IsInBounds(mi, entity.ServerPosition)))
            {
                return config.Bradley.LockHarbor;
            }

            if (BradleyDrops && BradleyDrops.CallHook("IsBradleyDrop", entity.skinID) != null)
            {
                return false;
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
                return false;
            }
            return config.Helicopter.Threshold > 0f;
        }

        private bool CanLockNpc(BaseEntity entity)
        {
            return config.Npc.Threshold > 0f && !_boss.Contains(entity.net.ID);
        }

        private void OnEntityDeathHandler(BaseCombatEntity entity, DamageEntryType damageEntryType, HitInfo hitInfo)
        {
            DamageInfo damageInfo;
            if (data.Damage.TryGetValue(entity.net.ID, out damageInfo))
            {
                if (damageEntryType == DamageEntryType.Bradley && !CanLockBradley(entity)) return;
                if (damageEntryType == DamageEntryType.Heli && !CanLockHeli(entity)) return;
                if (damageEntryType == DamageEntryType.NPC && !CanLockNpc(entity)) return;

                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    var lockInfo = new LockInfo(damageInfo, damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Bradley.LockTime);
                    var position = entity.transform.position;

                    damageInfo.OnKilled(position);

                    timer.Once(0.1f, () =>
                    {
                        LockInRadius<LockedByEntCrate>(position, lockInfo, damageEntryType);
                        LockInRadius<HelicopterDebris>(position, lockInfo, damageEntryType);
                        RemoveFireFromCrates(position, damageEntryType);
                    });

                    GiveRustReward(entity, hitInfo, damageInfo);
                }
                else if (damageEntryType == DamageEntryType.NPC && config.Npc.Enabled)
                {
                    var position = entity.transform.position;
                    var npc = entity as BasePlayer;
                    var npcId = npc.userID;

                    damageInfo.OnKilled(position);

                    timer.Once(0.1f, () => LockInRadius(position, damageInfo, npcId));

                    GiveRustReward(entity, hitInfo, damageInfo);
                }

                damageInfo.DestroyMe();
                data.Damage.Remove(entity.net.ID);
            }

            LockInfo lockInfo2;
            if (data.Lock.TryGetValue(entity.net.ID, out lockInfo2))
            {
                if (damageEntryType == DamageEntryType.Corpse && config.Npc.Enabled)
                {
                    var corpse = entity as NPCPlayerCorpse;
                    var corpsePos = corpse.transform.position;
                    var corpseId = corpse.playerSteamID;

                    timer.Once(0.1f, () => LockInRadius(corpsePos, lockInfo2, corpseId));
                }

                data.Lock.Remove(entity.net.ID);
            }
        }

        void GiveRustReward(BaseEntity entity, HitInfo hitInfo, DamageInfo damageInfo)
        {
            if (RustRewards == null || hitInfo == null)
            {
                return;
            }

            BasePlayer attacker = BasePlayer.FindByID(damageInfo.OwnerID) ?? hitInfo.Initiator as BasePlayer;

            if (attacker == null)
            {
                return;
            }

            var amount = damageInfo.damageEntryType == DamageEntryType.Bradley ? config.Bradley.RRP : damageInfo.damageEntryType == DamageEntryType.Heli ? config.Helicopter.RRP : config.Npc.RRP;

            if (amount <= 0.0)
            {
                return;
            }

            var weapon = hitInfo?.Weapon?.GetItem()?.info?.shortname ?? hitInfo?.WeaponPrefab?.ShortPrefabName ?? "";
            var distance = Vector3.Distance(attacker.transform.position, entity.transform.position);

            RustRewards?.Call("GiveRustReward", attacker, 0, amount, entity, weapon, distance, entity.ShortPrefabName);
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

            if (entity is SupplyDrop && entity.skinID == supplyDropSkinID || config.Hackable.Enabled && entity is HackableLockedCrate)
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

            LockInfo lockInfo;
            if (!data.Lock.TryGetValue(entity.net.ID, out lockInfo))
            {
                return null;
            }

            if (entity.OwnerID == 0 || lockInfo.IsLockOutdated)
            {
                data.Lock.Remove(entity.net.ID);
                entity.OwnerID = 0;
                return null;
            }

            if (!lockInfo.CanInteract(player.userID) && Interface.CallHook("OnLootLockedEntity", player, entity) == null)
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
                _boss.Add(boss.net.ID);
            }
        }

        private void OnBossKilled(ScientistNPC boss, BasePlayer attacker)
        {
            if (boss.IsValid())
            {
                _boss.Remove(boss.net.ID);
            }
        }

        private void OnPersonalHeliSpawned(BasePlayer player, BaseHelicopter heli)
        {
            if (heli.IsValid())
            {
                _personal.Add(heli.net.ID);
            }
        }

        private void OnPersonalApcSpawned(BasePlayer player, BradleyAPC apc)
        {
            if (apc.IsValid())
            {
                _personal.Add(apc.net.ID);
            }
        }

        #region SupplyDrops

        private void OnExplosiveDropped(BasePlayer player, SupplySignal ss, ThrownWeapon tw) => OnExplosiveThrown(player, ss, tw);

        private void OnExplosiveThrown(BasePlayer player, SupplySignal ss, ThrownWeapon tw)
        {
            if (player == null || ss == null || !config.SupplyDrop.Skins.Contains(tw.skinID))
            {
                return;
            }

            var item = tw.GetItem();

            if (item != null && !config.SupplyDrop.Skins.Contains(item.skin))
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
                        CreateMessage(target, "ThrownSupplySignalAt", player.displayName, PhoneController.PositionToGridCoord(player.transform.position));
                    }
                    else CreateMessage(target, "ThrownSupplySignal", player.displayName);
                }
            }

            if (config.SupplyDrop.NotifyConsole)
            {
                Puts(_("ThrownSupplySignalAt", null, player.displayName, PhoneController.PositionToGridCoord(player.transform.position)));
            }

            Interface.CallHook("OnModifiedSupplySignal", player, ss, tw);
        }

        private List<ulong> thrown = new List<ulong>();

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

            var drop = GameManager.server.CreateEntity(StringPool.Get(3632568684), position) as SupplyDrop;

            drop.OwnerID = userid;
            drop.skinID = supplyDropSkinID;
            drop.Spawn();
            drop.Invoke(() => drop.OwnerID = userid, 1f);

            if (config.SupplyDrop.LockTime > 0)
            {
                OnSupplyDropLanded(drop);
            }
        }

        private void OnExcavatorSuppliesRequested(ExcavatorSignalComputer computer, BasePlayer player, CargoPlane plane)
        {
            float y = plane.transform.position.y / Core.Random.Range(2, 4); // Change Y, fast drop

            plane.transform.position.Set(plane.transform.position.x, y, plane.transform.position.z);
            plane.startPos.Set(plane.startPos.x, y, plane.startPos.z);
            plane.endPos.Set(plane.endPos.x, y, plane.endPos.z);
            plane.secondsToTake = Vector3.Distance(plane.startPos, plane.endPos) / Mathf.Clamp(config.SupplyDrop.Speed, 40f, World.Size);
            plane.OwnerID = player.userID;
            plane.skinID = supplyDropSkinID;
        }

        private void OnCargoPlaneSignaled(CargoPlane plane, SupplySignal ss)
        {
            if (ss?.skinID != supplyDropSkinID)
            {
                return;
            }

            float y = plane.transform.position.y / Core.Random.Range(2, 4); // Change Y, fast drop

            plane.transform.position.Set(plane.transform.position.x, y, plane.transform.position.z);
            plane.startPos.Set(plane.startPos.x, y, plane.startPos.z);
            plane.endPos.Set(plane.endPos.x, y, plane.endPos.z);
            plane.secondsToTake = Vector3.Distance(plane.startPos, plane.endPos) / Mathf.Clamp(config.SupplyDrop.Speed, 40f, World.Size);
            plane.OwnerID = ss.OwnerID;
            plane.skinID = supplyDropSkinID;

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

            Interface.CallHook("OnModifiedCargoPlaneSignaled", plane, ss);
        }

        private void OnSupplyDropDropped(SupplyDrop drop, CargoPlane plane)
        {
            if (plane?.skinID != supplyDropSkinID)
            {
                return;
            }

            Rigidbody rb;
            if (drop.TryGetComponent(out rb))
            {
                rb.drag = Mathf.Clamp(config.SupplyDrop.Drag, 0.1f, 3f);
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }

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

            drop.Invoke(() => drop.OwnerID = 0, config.SupplyDrop.LockTime);

            Interface.CallHook("OnModifiedSupplyDropLanded", drop);
        }

        #endregion SupplyDrops

        private void OnGuardedCrateEventEnded(BasePlayer player, HackableLockedCrate crate)
        {
            NextTick(() =>
            {
                if (crate != null && crate.OwnerID == 0)
                {
                    crate.OwnerID = player.userID;

                    SetupHackableCrate(player, crate);
                }
            });
        }

        private void CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            crate.OwnerID = player.userID;

            SetupHackableCrate(player, crate);
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

            if (config.Hackable.LockTime > 0f)
            {
                crate.Invoke(() =>
                {
                    crate.OwnerID = 0;
                }, config.Hackable.LockTime + (HackableLockedCrate.requiredHackSeconds - hackSeconds));
            }
        }

        private void CancelDamage(HitInfo hitInfo)
        {
            hitInfo.damageTypes = new DamageTypeList();
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

        public bool HasLockout(BasePlayer player, DamageEntryType damageEntryType)
        {
            if (damageEntryType == DamageEntryType.Bradley && config.Lockout.Bradley <= 0)
            {
                return false;
            }

            if (!player.IsValid() || HasPermission(player, bypassLockoutsPerm))
            {
                return false;
            }

            Lockout lo;
            if (data.Lockouts.TryGetValue(player.UserIDString, out lo))
            {
                double time = UI.GetLockoutTime(damageEntryType, lo, player.UserIDString);

                if (time > 0f)
                {
                    if (CanMessage(player))
                    {
                        CreateMessage(player, "LockedOutBradley", FormatTime(time));
                    }

                    return true;
                }

                data.Lockouts.Remove(player.UserIDString);
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
                if (lo.Value.Bradley - Epoch.Current > config.Lockout.Bradley)
                {
                    double time = UI.GetLockoutTime(damageEntryType);

                    lo.Value.Bradley = Epoch.Current + time;

                    var player = BasePlayer.Find(lo.Key);

                    if (player == null) continue;

                    UI.UpdateLockoutUI(player);
                }
            }
        }

        public void TrySetLockout(string playerId, BasePlayer player, DamageEntryType damageEntryType)
        {
            if (permission.UserHasPermission(playerId, bypassLockoutsPerm))
            {
                return;
            }

            double time = UI.GetLockoutTime(damageEntryType);

            if (time <= 0)
            {
                return;
            }

            Lockout lo;
            if (!data.Lockouts.TryGetValue(playerId, out lo))
            {
                data.Lockouts[playerId] = lo = new Lockout();
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
            }

            if (lo.Any())
            {
                UI.UpdateLockoutUI(player);
            }
        }

        private void LockoutBradleyLooters(List<ulong> looters, Vector3 position)
        {
            if (looters.Count == 0)
            {
                return;
            }

            var members = new List<ulong>(looters);

            foreach (ulong looterId in looters)
            {
                var looter = RelationshipManager.FindByID(looterId);

                TrySetLockout(looterId.ToString(), looter, DamageEntryType.Bradley);
                LockoutTeam(members, looterId, DamageEntryType.Bradley);
                LockoutClan(members, looterId, DamageEntryType.Bradley);
            }

            SendDiscordMessage(members, position, DamageEntryType.Bradley);
        }

        private void LockoutTeam(List<ulong> members, ulong looterId, DamageEntryType damageEntryType)
        {
            if (!config.Lockout.Team)
            {
                return;
            }

            RelationshipManager.PlayerTeam team;
            if (!RelationshipManager.ServerInstance.playerToTeam.TryGetValue(looterId, out team))
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

                if (config.Lockout.Time > 0 && member?.secondsSleeping > config.Lockout.Time * 60f)
                {
                    continue;
                }

                TrySetLockout(memberId.ToString(), member, damageEntryType);

                members.Add(memberId);
            }
        }

        private void LockoutClan(List<ulong> members, ulong looterId, DamageEntryType damageEntryType)
        {
            if (!config.Lockout.Clan)
            {
                return;
            }

            var tag = Clans?.Call("GetClanOf", looterId) as string;

            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            var clan = Clans?.Call("GetClan", tag) as JObject;

            if (clan == null)
            {
                return;
            }

            JToken jToken;
            if (!clan.TryGetValue("members", out jToken))
            {
                return;
            }

            foreach (ulong memberId in jToken)
            {
                if (members.Contains(memberId))
                {
                    continue;
                }

                var member = RelationshipManager.FindByID(memberId);

                if (config.Lockout.Time > 0 && member?.secondsSleeping > config.Lockout.Time * 60f)
                {
                    continue;
                }

                TrySetLockout(memberId.ToString(), member, damageEntryType);

                members.Add(memberId);
            }
        }

        private object HandleTeam(RelationshipManager.PlayerTeam team, ulong targetId)
        {
            foreach (var entry in _apcAttackers)
            {
                foreach (var info in entry.Value)
                {
                    if (info.id == targetId)
                    {
                        CreateMessage(info.attacker, "CannotLeaveBradley");
                        return true;
                    }
                }
            }

            return null;
        }

        private bool IsDefended(BaseHelicopter heli) => heli.IsValid() && (data.Lock.ContainsKey(heli.net.ID) || data.Damage.ContainsKey(heli.net.ID));

        private bool IsDefended(BaseCombatEntity victim) => victim.IsValid() && (_locked.ContainsKey(victim.net.ID) || data.Lock.ContainsKey(victim.net.ID));

        private void DoLockoutRemoves()
        {
            var keys = new List<string>();

            foreach (var lockout in data.Lockouts)
            {
                if (lockout.Value.Bradley - Epoch.Current <= 0)
                {
                    lockout.Value.Bradley = 0;
                }

                if (!lockout.Value.Any())
                {
                    keys.Add(lockout.Key);
                }
            }

            foreach (string key in keys)
            {
                data.Lockouts.Remove(key);
            }
        }

        private void Unsubscribe()
        {
            Unsubscribe(nameof(OnBossSpawn));
            Unsubscribe(nameof(OnBossKilled));
            Unsubscribe(nameof(OnGuardedCrateEventEnded));
            Unsubscribe(nameof(CanHackCrate));
            Unsubscribe(nameof(OnPlayerSleepEnded));
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
        }

        private void SaveData()
        {
            DoLockoutRemoves();
            Interface.Oxide.DataFileSystem.WriteObject(Name, data, true);
        }

        private void LoadData()
        {
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch { }

            if (data == null)
            {
                data = new StoredData();
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
        }

        private bool IsAlly(ulong playerId, ulong targetId)
        {
            if (playerId == targetId)
            {
                return true;
            }

            RelationshipManager.PlayerTeam team;
            if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(playerId, out team) && team.members.Contains(targetId))
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

        private static List<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1) where T : BaseNetworkable
        {
            int hits = Physics.OverlapSphereNonAlloc(a, n, Vis.colBuffer, m, QueryTriggerInteraction.Collide);
            List<T> entities = new List<T>();
            for (int i = 0; i < hits; i++)
            {
                var entity = Vis.colBuffer[i]?.ToBaseEntity();
                if (entity is T) entities.Add(entity as T);
                Vis.colBuffer[i] = null;
            }
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
            foreach (var e in FindEntitiesOfType<BaseEntity>(position, 25f).ToList())
            {
                if (CanRemoveFire(damageEntryType))
                {
                    if (e is LockedByEntCrate)
                    {
                        var crate = e as LockedByEntCrate;

                        if (crate == null) continue;

                        var lockingEnt = crate.lockingEnt;

                        if (lockingEnt == null) continue;

                        var entity = lockingEnt.ToBaseEntity();

                        if (entity != null && !entity.IsDestroyed)
                        {
                            entity.Kill();
                        }
                    }
                    else if (e is FireBall)
                    {
                        var fireball = e as FireBall;

                        fireball.Extinguish();
                    }
                }

                if (e is HelicopterDebris)
                {
                    float num = damageEntryType == DamageEntryType.Heli ? config.Helicopter.TooHotUntil : config.Bradley.TooHotUntil;
                    var debris = e as HelicopterDebris;

                    debris.tooHotUntil = Time.realtimeSinceStartup + num;
                }
            }
        }

        private void LockInRadius<T>(Vector3 position, LockInfo lockInfo, DamageEntryType damageEntryType) where T : BaseEntity
        {
            foreach (var entity in FindEntitiesOfType<T>(position, damageEntryType == DamageEntryType.Heli ? 50f : 20f))
            {
                ulong ownerid = lockInfo.damageInfo.OwnerID;
                entity.OwnerID = ownerid;
                data.Lock[entity.net.ID] = lockInfo;

                float time = GetLockTime(damageEntryType);

                entity.Invoke(() => entity.OwnerID = ownerid, 1f);

                if (time > 0)
                {
                    entity.Invoke(() => entity.OwnerID = 0, time);
                }
            }
        }

        private void LockInRadius(Vector3 position, DamageInfo damageInfo, ulong playerSteamID)
        {
            foreach (var corpse in FindEntitiesOfType<NPCPlayerCorpse>(position, 3f))
            {
                if (corpse.IsValid() && corpse.playerSteamID == playerSteamID && !data.Lock.ContainsKey(corpse.net.ID))
                {
                    if (config.Npc.LockTime > 0f)
                    {
                        var uid = corpse.net.ID;

                        timer.Once(config.Npc.LockTime, () => data.Lock.Remove(uid));
                        corpse.Invoke(() => corpse.OwnerID = 0, config.Npc.LockTime);
                    }

                    ulong ownerid = damageInfo.OwnerID;
                    corpse.OwnerID = ownerid;
                    corpse.Invoke(() => corpse.OwnerID = ownerid, 1f);
                    data.Lock[corpse.net.ID] = new LockInfo(damageInfo, config.Npc.LockTime);
                }
            }
        }

        private void LockInRadius(Vector3 position, LockInfo lockInfo, ulong playerSteamID)
        {
            foreach (var container in FindEntitiesOfType<DroppedItemContainer>(position, 1f))
            {
                if (container.IsValid() && container.playerSteamID == playerSteamID && !data.Lock.ContainsKey(container.net.ID))
                {
                    if (config.Npc.LockTime > 0f)
                    {
                        var uid = container.net.ID;

                        timer.Once(config.Npc.LockTime, () => data.Lock.Remove(uid));
                        container.Invoke(() => container.OwnerID = 0, config.Npc.LockTime);
                    }

                    ulong ownerid = lockInfo.damageInfo.OwnerID;
                    container.OwnerID = ownerid;
                    container.Invoke(() => container.OwnerID = ownerid, 1f);
                    data.Lock[container.net.ID] = lockInfo;
                }
            }
        }

        private static int GetLockTime(DamageEntryType damageEntryType)
        {
            int time = damageEntryType == DamageEntryType.Bradley ? config.Bradley.LockTime : damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Npc.LockTime;

            return time > 0 ? time : int.MaxValue;
        }

        private void CommandTest(IPlayer user, string command, string[] args)
        {
            user.Reply($"data.DamageInfos.Count: {data.Damage.Count}");
            user.Reply($"data.LockInfos.Count: {data.Lock.Count}");
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
                        DestroyLockoutUpdate(player);
                    }
                }

                Lockouts.Clear();
            }

            private static void Create(BasePlayer player, string panelName, string text, string color, string panelColor, string aMin, string aMax)
            {
                var element = CreateElementContainer(panelName, panelColor, aMin, aMax, false, "Hud");

                CreateLabel(ref element, panelName, Color(color), text, config.UI.Bradley.FontSize, "0 0", "1 1");
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
                string bradleyBackgroundColor = Color(config.UI.Bradley.BradleyBackgroundColor, config.UI.Bradley.Alpha);

                Create(player, BradleyPanelName, _("Time", player.UserIDString, bradley), config.UI.Bradley.BradleyTextColor, bradleyBackgroundColor, min, max);

                config.UI.Bradley.BradleyMin = min;
                config.UI.Bradley.BradleyMax = max;
                Instance.SaveConfig();

                player.Invoke(() => DestroyLockoutUI(player), 5f);
                return true;
            }

            public static bool ShowLockouts(BasePlayer player)
            {
                if (!config.UI.Bradley.Enabled)
                {
                    return false;
                }

                if (Instance.HasPermission(player, bypassLockoutsPerm))
                {
                    data.Lockouts.Remove(player.UserIDString);
                    return false;
                }

                Lockout lo;
                if (!data.Lockouts.TryGetValue(player.UserIDString, out lo))
                {
                    data.Lockouts[player.UserIDString] = lo = new Lockout();
                }

                double bradleyTime = GetLockoutTime(DamageEntryType.Bradley, lo, player.UserIDString);

                if (bradleyTime <= 0)
                {
                    return false;
                }

                string bradley = Math.Floor(TimeSpan.FromSeconds(bradleyTime).TotalMinutes).ToString();
                string bradleyBackgroundColor = Color(config.UI.Bradley.BradleyBackgroundColor, config.UI.Bradley.Alpha);

                Create(player, BradleyPanelName, _("Time", player.UserIDString, bradley), config.UI.Bradley.BradleyTextColor, bradleyBackgroundColor, config.UI.Bradley.BradleyMin, config.UI.Bradley.BradleyMax);
                SetLockoutUpdate(player);

                return true;
            }

            public static double GetLockoutTime(DamageEntryType damageEntryType)
            {
                switch (damageEntryType)
                {
                    case DamageEntryType.Bradley:
                        {
                            return config.Lockout.Bradley * 60;
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

                if (!config.UI.Bradley.Enabled)
                {
                    return;
                }

                var uii = GetSettings(player.UserIDString);

                if (!uii.Enabled || !uii.Lockouts)
                {
                    return;
                }

                ShowLockouts(player);
            }

            private static void SetLockoutUpdate(BasePlayer player)
            {
                Timers timers;
                if (!InvokeTimers.TryGetValue(player.userID, out timers))
                {
                    InvokeTimers[player.userID] = timers = new Timers();
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
                Timers timers;
                if (!InvokeTimers.TryGetValue(player.userID, out timers))
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
                Info uii;
                if (!data.UI.TryGetValue(playerId, out uii))
                {
                    data.UI[playerId] = uii = new UI.Info();
                }

                return uii;
            }

            private const string BradleyPanelName = "Lockouts_UI_Bradley";

            public static List<BasePlayer> Lockouts { get; set; } = new List<BasePlayer>();
            public static Dictionary<ulong, Timers> InvokeTimers { get; set; } = new Dictionary<ulong, Timers>();

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
            if (user.IsServer || user.IsAdmin)
            {
                if (args.Length == 2)
                {
                    if (args[0] == "setbradleytime")
                    {
                        double time;
                        if (double.TryParse(args[1], out time))
                        {
                            config.Lockout.Bradley = time;
                            SaveConfig();

                            user.Reply($"Cooldown changed to {time} minutes");
                            ApplyCooldowns(DamageEntryType.Bradley);
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
                }

                return;
            }

            var uii = UI.GetSettings(user.Id);
            var player = user.Object as BasePlayer;

            if (player.IsAdmin && args.Length == 3)
            {
                UI.ShowTemporaryLockouts(player, args[1], args[2]);
                return;
            }

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

        private static string PositionToGrid(Vector3 position) => PhoneController.PositionToGridCoord(position);

        private void SendDiscordMessage(List<ulong> members, Vector3 position, DamageEntryType damageEntryType)
        {
            if (!CanSendDiscordMessage())
            {
                return;
            }

            var players = new Dictionary<string, string>();

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
            var log = new StringBuilder();

            foreach (var member in members)
            {
                log.AppendLine($"[{DateTime.Now}] {member.Key} {member.Value} @ {grid}): {text}");
            }

            LogToFile("bradleykills", log.ToString(), this);

            var _fields = new List<object>();

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
                return new List<HackPermission>
                {
                    new HackPermission { Permission = "lootdefender.hackedcrates.regular", Value = 750f },
                    new HackPermission { Permission = "lootdefender.hackedcrates.elite", Value = 500f },
                    new HackPermission { Permission = "lootdefender.hackedcrates.legend", Value = 300f },
                    new HackPermission { Permission = "lootdefender.hackedcrates.vip", Value = 120f },
                };
            }
        }

        private class HackableSettings
        {
            [JsonProperty(PropertyName = "Permissions", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<HackPermission> Permissions = DefaultHackPermissions;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Lock For X Seconds (0 = Forever)")]
            public int LockTime { get; set; } = 900;

            [JsonProperty(PropertyName = "Block Timer Increase On Damage To Laptop")]
            public bool Laptop { get; set; } = true;
        }

        private class BradleySettings
        {
            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages { get; set; } = new NotifySettings();

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

            //[JsonProperty(PropertyName = "Lock Bradley From Raidable Bases Plugin")]
            //public bool LockRaidableBases { get; set; } = true;

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
        }

        private class HelicopterSettings
        {
            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages { get; set; } = new NotifySettings();

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

            [JsonProperty(PropertyName = "Lock Heli From Personal Heli Plugin")]
            public bool LockPersonal { get; set; } = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; }

            [JsonProperty(PropertyName = "Rust Rewards RP")]
            public double RRP { get; set; } = 0.0;
        }

        private class NpcSettings
        {
            [JsonProperty(PropertyName = "Messages")]
            public NotifySettings Messages { get; set; } = new NotifySettings() { NotifyLocked = null };

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
        }

        private class SupplyDropSettings
        {
            [JsonProperty(PropertyName = "Allow Locking Signals With These Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Skins { get; set; } = new List<ulong> { 0 };

            [JsonProperty(PropertyName = "Lock Supply Drops To Players")]
            public bool Lock { get; set; } = true;

            [JsonProperty(PropertyName = "Lock Supply Drops From Excavator")]
            public bool Excavator { get; set; } = true;

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

            [JsonProperty(PropertyName = "Bypass Spawning Cargo Plane")]
            public bool Bypass { get; set; }

            [JsonProperty(PropertyName = "Smoke Duration")]
            public float Smoke { get; set; } = -1f;
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
            [JsonProperty(PropertyName = "Time Between Bradley In Minutes")]
            public double Bradley { get; set; }

            [JsonProperty(PropertyName = "Lockout Entire Team")]
            public bool Team { get; set; } = true;

            [JsonProperty(PropertyName = "Lockout Entire Clan")]
            public bool Clan { get; set; } = true;

            [JsonProperty(PropertyName = "Exclude Members Offline For More Than X Minutes")]
            public float Time { get; set; } = 15f;
        }

        public class UILockoutSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Bradley Anchor Min")]
            public string BradleyMin { get; set; } = "0.946 0.325";

            [JsonProperty(PropertyName = "Bradley Anchor Max")]
            public string BradleyMax { get; set; } = "0.986 0.360";

            [JsonProperty(PropertyName = "Bradley Background Color")]
            public string BradleyBackgroundColor { get; set; } = "#A52A2A";

            [JsonProperty(PropertyName = "Bradley Text Color")]
            public string BradleyTextColor { get; set; } = "#FFFF00";

            [JsonProperty(PropertyName = "Panel Alpha")]
            public float Alpha { get; set; } = 1f;

            [JsonProperty(PropertyName = "Font Size")]
            public int FontSize { get; set; } = 18;
        }

        public class UISettings
        {
            [JsonProperty(PropertyName = "Bradley")]
            public UILockoutSettings Bradley { get; set; } = new UILockoutSettings();
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
        }

        private class Configuration
        {
            [JsonProperty(PropertyName = "Bradley Settings")]
            public BradleySettings Bradley { get; set; } = new BradleySettings();

            [JsonProperty(PropertyName = "Helicopter Settings")]
            public HelicopterSettings Helicopter { get; set; } = new HelicopterSettings();

            [JsonProperty(PropertyName = "Hackable Crate Settings")]
            public HackableSettings Hackable { get; set; } = new HackableSettings();

            [JsonProperty(PropertyName = "Npc Settings")]
            public NpcSettings Npc { get; set; } = new NpcSettings();

            [JsonProperty(PropertyName = "Supply Drop Settings")]
            public SupplyDropSettings SupplyDrop { get; set; } = new SupplyDropSettings();

            [JsonProperty(PropertyName = "Damage Report Settings")]
            public DamageReportSettings Report { get; set; } = new DamageReportSettings();

            [JsonProperty(PropertyName = "Player Lockouts (0 = ignore)")]
            public PluginSettingsBaseLockout Lockout { get; set; } = new PluginSettingsBaseLockout();

            [JsonProperty(PropertyName = "Lockout UI")]
            public UISettings UI { get; set; } = new UISettings();

            [JsonProperty(PropertyName = "Discord Messages")]
            public DiscordMessagesSettings DiscordMessages { get; set; } = new DiscordMessagesSettings();

            [JsonProperty(PropertyName = "Chat ID")]
            public ulong ChatID { get; set; }
        }

        private static Configuration config;
        private bool configLoaded;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
                if (config.Bradley.Threshold > 1f) config.Bradley.Threshold /= 100f;
                if (config.Helicopter.Threshold > 1f) config.Helicopter.Threshold /= 100f;
                if (config.Npc.Threshold > 1f) config.Npc.Threshold /= 100f;
                SaveConfig();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        protected override void LoadDefaultConfig() => config = new Configuration();

        private bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
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
                ["Helicopter"] = "Heli",
                ["BradleyAPC"] = "Bradley",
                ["ThrownSupplySignal"] = "{0} has thrown a supply signal!",
                ["ThrownSupplySignalAt"] = "{0} in {1} has thrown a supply signal!",
                ["Format"] = "<color=#C0C0C0>{0:0.00}</color> (<color=#C3FBFE>{1:0.00}%</color>)",
                ["CannotLeaveBradley"] = "You cannot leave your team until the Bradley is destroyed!",
                ["LockedOutBradley"] = "You are locked out from Bradley for {0}",
                ["Time"] = "{0}m",
                ["BradleyKilled"] = "A bradley was killed.",
                ["BradleyUnlocked"] = "The bradley at {0} has been unlocked.",
                ["HeliUnlocked"] = "The heli at {0} has been unlocked.",
                ["FirstLock"] = "First locked to {0} at {1}% threshold"
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "У вас нет разрешения на использование этой команды!",
                ["DamageReport"] = "Нанесенный урон по {0}",
                ["DamageTime"] = "{0} was taken down after {1} seconds",
                ["CannotLoot"] = "Это не ваш лут, основная часть урона насена не вами.",
                ["CannotLootIt"] = "Вы не можете открыть этот ящик с припасами.",
                ["CannotMine"] = "Вы не можете добывать это, основная часть урона насена не вами.",
                ["CannotDamageThis"] = "Вы не можете повредить это!",
                ["Locked Heli"] = "{0}: Этот патрульный вертолёт принадлежит <color=#FF0000>{1}</color> и участникам команды",
                ["Locked Bradley"] = "{0}: Этот танк принадлежит <color=#FF0000>{1}</color> и участникам команды",
                ["Helicopter"] = "Патрульному вертолету",
                ["BradleyAPC"] = "Танку",
                ["ThrownSupplySignal"] = "{0} запросил сброс припасов!",
                ["ThrownSupplySignalAt"] = "{0} {1} запросил сброс припасов!",
                ["Format"] = "<color=#C0C0C0>{0:0.00}</color> (<color=#C3FBFE>{1:0.00}%</color>)",
                ["CannotLeaveBradley"] = "Вы не можете покинуть команду, пока танк не будет уничтожен!",
                ["LockedOutBradley"] = "Вы заблокированы от танка на {0}",
                ["Time"] = "{0} м",
                ["BradleyKilled"] = "Танк был уничтожен.",
                ["BradleyUnlocked"] = "Танк на {0} разблокирован.",
                ["HeliUnlocked"] = "Вертолёт на {0} разблокирован.",
                ["FirstLock"] = "First locked to {0} at {1}% threshold"
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