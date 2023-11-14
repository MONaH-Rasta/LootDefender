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
    [Info("Loot Defender", "Author Egor Blagov, Maintainer nivex", "2.0.4")]
    [Description("Defends loot from other players who dealt less damage than you.")]
    class LootDefender : RustPlugin
    {
        [PluginReference]
        Plugin PersonalHeli, BlackVenom, Friends, Clans, FancyDrop;

        private static LootDefender Instance;
        private static StringBuilder sb;
        private const ulong supplyDropSkinID = 2345;
        private const string bypassLootPerm = "lootdefender.bypass.loot";
        private const string bypassDamagePerm = "lootdefender.bypass.damage";
        private const string bypassLockoutsPerm = "lootdefender.bypass.lockouts";
        private Dictionary<BradleyAPC, List<AttackerInfo>> _apcAttackers = new Dictionary<BradleyAPC, List<AttackerInfo>>();
        private Dictionary<uint, bool> _blackVenoms { get; set; } = new Dictionary<uint, bool>();
        private Dictionary<uint, ulong> _locked { get; set; } = new Dictionary<uint, ulong>();
        private List<uint> _personal { get; set; } = new List<uint>();
        private List<string> _sent { get; set; } = new List<string>();
        private static StoredData data { get; set; } = new StoredData();

        public enum DamageEntryType
        {
            Bradley,
            Corpse,
            Heli,
            NPC,
            None
        }

        public class AttackerInfo
        {
            public BasePlayer attacker;
            public string attackerId;
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
            public Dictionary<uint, DamageInfo> DamageInfos { get; set; } = new Dictionary<uint, DamageInfo>();
            public Dictionary<uint, LockInfo> LockInfos { get; set; } = new Dictionary<uint, LockInfo>();

            public void Sanitize()
            {
                foreach (var damageInfo in DamageInfos.ToList())
                {
                    damageInfo.Value._entity = BaseNetworkable.serverEntities.Find(damageInfo.Key) as BaseEntity;

                    if (damageInfo.Value._entity == null)
                    {
                        DamageInfos.Remove(damageInfo.Key);
                    }
                    else damageInfo.Value.Start();
                }

                foreach (var lockInfo in LockInfos.ToList())
                {
                    if (BaseNetworkable.serverEntities.Find(lockInfo.Key) == null)
                    {
                        LockInfos.Remove(lockInfo.Key);
                    }
                }
            }
        }

        private class DamageEntry
        {
            public float DamageDealt { get; set; }
            public DateTime Timestamp { get; set; }

            public string teamID { get; set; }

            [JsonIgnore]
            internal ulong _teamId { get; set; }

            [JsonIgnore]
            public ulong TeamID
            {
                get
                {
                    if (_teamId == 0 && !string.IsNullOrEmpty(teamID))
                    {
                        ulong id;
                        if (ulong.TryParse(teamID, out id))
                        {
                            _teamId = id;
                        }
                    }

                    return _teamId;
                }
                set
                {
                    _teamId = value;
                    teamID = value.ToString();
                }
            }

            public DamageEntry(ulong teamID)
            {
                Timestamp = DateTime.Now;
                TeamID = teamID;
            }

            public bool IsOutdated(int timeout) => timeout > 0 && DateTime.Now.Subtract(Timestamp).TotalSeconds >= timeout;
        }

        private class DamageInfo
        {
            public Dictionary<ulong, DamageEntry> damageEntries { get; set; } = new Dictionary<ulong, DamageEntry>();

            private Dictionary<ulong, bool> canInteract { get; set; } = new Dictionary<ulong, bool>();

            private DamageEntryType damageEntryType { get; set; }

            private string NPCName { get; set; }

            public ulong OwnerID { get; set; }

            private bool Locked { get; set; }

            [JsonIgnore]
            private int _lockTime { get; set; }

            [JsonIgnore]
            public BaseEntity _entity { get; set; }

            [JsonIgnore]
            private List<ulong> _remove { get; set; } = new List<ulong>();

            [JsonIgnore]
            private Timer _timer { get; set; }

            [JsonIgnore]
            private bool _unlocked { get; set; }

            public float FullDamage
            {
                get
                {
                    float sum = 0f;

                    foreach (var x in damageEntries.Values)
                    {
                        sum += x.DamageDealt;
                    }

                    return sum;
                }
            }

            public DamageInfo(DamageEntryType damageEntryType, string NPCName)
            {
                this.damageEntryType = damageEntryType;
                this.NPCName = NPCName;

                Start();
            }

            public void Start()
            {
                _lockTime = GetLockTime(damageEntryType);
                _timer = Instance.timer.Every(1f, CheckExpiration);
            }

            public void DestroyMe()
            {
                if (_timer == null)
                {
                    return;
                }

                _timer.Destroy();
            }

            private void CheckExpiration()
            {
                foreach (var damageEntry in damageEntries)
                {
                    if (!damageEntry.Value.IsOutdated(_lockTime))
                    {
                        continue;
                    }

                    if (damageEntry.Key == OwnerID)
                    {
                        Unlock();
                    }

                    _remove.Add(damageEntry.Key);
                }

                if (_remove.Count == 0)
                {
                    return;
                }

                foreach (var key in _remove)
                {
                    damageEntries.Remove(key);
                }

                _remove.Clear();
            }

            private void Unlock()
            {
                _unlocked = true;
                Locked = false;
                OwnerID = 0;

                if (_entity == null)
                {
                    return;
                }

                _entity.OwnerID = 0;
                Instance._locked.Remove(_entity.net.ID);

                if (damageEntryType == DamageEntryType.Bradley && config.Bradley.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "BradleyUnlocked", PositionToGrid(_entity.transform.position));
                    }
                }

                if (damageEntryType == DamageEntryType.Heli && config.Helicopter.Messages.NotifyChat)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        CreateMessage(target, "HeliUnlocked", PositionToGrid(_entity.transform.position));
                    }
                }
            }

            private void Lock(BaseEntity entity, ulong userID)
            {
                if (userID == 0) return;
                Instance._locked[entity.net.ID] = userID;
                if (entity.IsNpc) OwnerID = userID;
                else entity.OwnerID = OwnerID = userID;
                Locked = true;
                _entity = entity;
            }

            public void AddDamage(BaseEntity entity, BasePlayer attacker, DamageEntry entry, float amount)
            {
                entry.DamageDealt += amount;
                entry.Timestamp = DateTime.Now;

                _entity = entity;

                if (Locked)
                {
                    return;
                }

                float damage = 0f;

                if (config.Report.UseTeams && entry.TeamID != 0)
                {
                    foreach (var x in damageEntries.Values)
                    {
                        if (x.TeamID == entry.TeamID)
                        {
                            damage += x.DamageDealt;
                        }
                    }
                }
                else damage = entry.DamageDealt;

                if (config.Helicopter.Threshold > 0f && entity is BaseHelicopter)
                {
                    if (_unlocked || damage >= entity.MaxHealth() * config.Helicopter.Threshold)
                    {
                        if (config.Helicopter.Messages.NotifyLocked == true)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                CreateMessage(target, "LockedHeli", attacker.displayName);
                            }
                        }                        

                        Lock(entity, attacker.userID);
                    }
                }
                else if (config.Bradley.Threshold > 0f && entity is BradleyAPC)
                {
                    if (_unlocked || damage >= entity.MaxHealth() * config.Bradley.Threshold)
                    {
                        if (config.Bradley.Messages.NotifyLocked == true)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                CreateMessage(target, "LockedBradley", attacker.displayName);
                            }
                        }

                        Lock(entity, attacker.userID);
                    }
                }
                else if (config.Npc.Threshold > 0f && entity.IsNpc)
                {
                    if (_unlocked || damage >= entity.MaxHealth() * config.Npc.Threshold)
                    {
                        var targetId = GetLockOwner(entity.MaxHealth(), config.Npc.Threshold);

                        Lock(entity, targetId);
                    }
                }
            }

            private ulong GetLockOwner(float maxhealth, float threshold)
            {
                foreach (var entry in damageEntries)
                {
                    if (entry.Value.DamageDealt >= maxhealth * threshold)
                    {
                        return entry.Key;
                    }
                }

                return 0;
            }

            public void OnKilled(Vector3 position)
            {
                if (damageEntryType == DamageEntryType.Bradley)
                {
                    List<ulong> looters = new List<ulong>();

                    foreach (var x in damageEntries)
                    {
                        if (CanInteract(x.Key))
                        {
                            looters.Add(x.Key);
                        }
                    }

                    Instance.LockoutBradleyLooters(looters, position);
                }

                DisplayDamageReport();
            }

            public void DisplayDamageReport()
            {
                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        if (!CanDisplayReport(target))
                        {
                            continue;
                        }

                        Message(target, GetDamageReport(target.UserIDString));
                    }
                }
                else if (damageEntryType == DamageEntryType.NPC)
                {
                    foreach (ulong key in damageEntries.Keys)
                    {
                        var target = BasePlayer.FindByID(key);

                        if (!CanDisplayReport(target))
                        {
                            continue;
                        }

                        Message(target, GetDamageReport(target.UserIDString));
                    }
                }
            }

            private bool CanDisplayReport(BasePlayer target)
            {
                if (target == null)
                {
                    return false;
                }

                if (damageEntryType == DamageEntryType.Bradley)
                {
                    if (config.Bradley.Messages.NotifyChat)
                    {
                        return true;
                    }

                    return config.Bradley.Messages.NotifyKiller && CanInteract(target.userID);
                }

                if (damageEntryType == DamageEntryType.Heli)
                {
                    if (config.Helicopter.Messages.NotifyChat)
                    {
                        return true;
                    }

                    return config.Helicopter.Messages.NotifyKiller && CanInteract(target.userID);
                }

                if (damageEntryType == DamageEntryType.NPC)
                {
                    if (config.Npc.Messages.NotifyChat)
                    {
                        return true;
                    }

                    return config.Npc.Messages.NotifyKiller && CanInteract(target.userID);
                }

                return false;
            }

            public string GetDamageReport(string targetId)
            {
                var damageGroups = GetDamageGroups();
                var topDamageGroups = GetTopDamageGroups(damageGroups, damageEntryType);
                var nameKey = damageEntryType == DamageEntryType.Bradley ? _("BradleyAPC", targetId) : damageEntryType == DamageEntryType.Heli ? _("Helicopter", targetId) : NPCName;

                sb.Length = 0;
                sb.AppendLine($"{_("DamageReport", targetId, $"<color={config.Report.Ok}>{nameKey}</color>")}:");

                if (damageGroups.Count > 0)
                {
                    foreach (var damageGroup in damageGroups)
                    {
                        if (topDamageGroups.Contains(damageGroup))
                        {
                            sb.Append($"<color={config.Report.Ok}>√</color> ");
                        }
                        else
                        {
                            sb.Append($"<color={config.Report.NotOk}>X</color> ");
                        }

                        sb.Append($"{damageGroup.ToReport(damageGroup.FirstDamagerDealer, this)}\n");
                    }
                }

                return sb.ToString();
            }

            public bool CanInteract(ulong playerId)
            {
                if (canInteract.ContainsKey(playerId))
                {
                    return true;
                }

                if (Instance.IsAlly(playerId, OwnerID))
                {
                    canInteract.Add(playerId, true);
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

                foreach (var damageEntry in damageEntries)
                {
                    damageGroups.Add(new DamageGroup(damageEntry.Key, damageEntry.Value.DamageDealt));
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

            [JsonIgnore]
            public bool IsLockOutdated => LockTimeout > 0 && DateTime.Now.Subtract(LockTimestamp).TotalSeconds >= LockTimeout;

            public LockInfo(DamageInfo damageInfo, int lockTimeout)
            {
                LockTimestamp = DateTime.Now;
                LockTimeout = lockTimeout;
                this.damageInfo = damageInfo;
            }

            public bool CanInteract(ulong playerId) => damageInfo.CanInteract(playerId);

            public string GetDamageReport(string userId) => damageInfo.GetDamageReport(userId);
        }

        private class DamageGroup
        {
            public float TotalDamage { get; private set; }

            public ulong FirstDamagerDealer { get; set; }

            private List<ulong> additionalPlayers { get; } = new List<ulong>();

            public List<ulong> Players
            {
                get
                {
                    var list = new List<ulong>
                    {
                        FirstDamagerDealer
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

            public DamageGroup(ulong playerId, float damage)
            {
                TotalDamage = damage;
                FirstDamagerDealer = playerId;

                if (!config.Report.UseTeams)
                {
                    return;
                }

                RelationshipManager.PlayerTeam team;
                if (!RelationshipManager.ServerInstance.playerToTeam.TryGetValue(playerId, out team))
                {
                    return;
                }

                for (int i = 0; i < team.members.Count; i++)
                {
                    ulong member = team.members[i];

                    if (member == playerId || additionalPlayers.Contains(member))
                    {
                        continue;
                    }

                    additionalPlayers.Add(member);
                }
            }

            public string ToReport(ulong playerId, DamageInfo damageInfo)
            {
                var displayName = RelationshipManager.FindByID(playerId)?.displayName ?? Instance.covalence.Players.FindPlayerById(playerId.ToString())?.Name ?? playerId.ToString();
                var damage = damageInfo.damageEntries.ContainsKey(playerId) ? damageInfo.damageEntries[playerId].DamageDealt : 0f;
                var percent = damage > 0 && damageInfo.FullDamage > 0 ? damage / damageInfo.FullDamage * 100 : 0;
                var color = additionalPlayers.Count == 0 ? config.Report.SinglePlayer : config.Report.Team;
                var damageLine = _("Format", playerId.ToString(), damage, percent);

                return $"<color={color}>{displayName}</color> {damageLine}";
            }
        }

        public class AirdropController : FacepunchBehaviour
        {
            public SupplyDrop drop;
            public Rigidbody body;
            public float y;

            private void Awake()
            {
                drop = GetComponent<SupplyDrop>();
                body = GetComponent<Rigidbody>();

                body.drag = 0f;
            }

            public void FixedUpdate()
            {
                if ((drop.transform.position.y - y) < 50f && body.drag < 3f)
                {
                    body.drag += 0.25f;
                }

                if (body.drag >= 3f)
                {
                    Destroy(this);
                }
            }

            private void OnCollisionEnter(Collision collision)
            {
                if ((1 << collision.collider.gameObject.layer & 1084293393) > 0)
                {
                    Destroy(this);
                }
            }
        }

        #region Hooks

        private void OnServerSave()
        {
            SaveData();
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
            }

            if (config.SupplyDrop.Lock)
            {
                if (config.SupplyDrop.LockTime > 0)
                {
                    Subscribe(nameof(OnSupplyDropLanded));
                }

                Subscribe(nameof(OnExplosiveDropped));
                Subscribe(nameof(OnExplosiveThrown));
                Subscribe(nameof(OnSupplyDropDropped));
                Subscribe(nameof(OnAirdrop));
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

            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnEntityDeath));
            Subscribe(nameof(OnEntityKill));
            Subscribe(nameof(CanLootEntity));
            Subscribe(nameof(OnPlayerAttack));
            Subscribe(nameof(CanBradleyTakeDamage));
        }

        private void Unload()
        {
            UI.DestroyAllLockoutUI();
            SaveData();
            Instance = null;
            data = null;
            sb = null;

            var objects = UnityEngine.Object.FindObjectsOfType(typeof(AirdropController));

            if (objects != null)
            {
                foreach (var gameObj in objects)
                {
                    UnityEngine.Object.Destroy(gameObj);
                }
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            UI.DestroyLockoutUI(player);
            UI.ShowLockouts(player);
        }

        private object OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player) => HandleTeam(team, player.UserIDString);

        private object OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong targetId) => HandleTeam(team, targetId.ToString());

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
            if (data.LockInfos.TryGetValue(hitInfo.HitEntity.net.ID, out lockInfo))
            {
                if (!lockInfo.IsLockOutdated)
                {
                    if (!lockInfo.CanInteract(attacker.userID))
                    {
                        if (CanMessage(attacker))
                        {
                            CreateMessage(attacker, "CannotMine");
                            Message(attacker, lockInfo.GetDamageReport(attacker.UserIDString));
                        }

                        CancelDamage(hitInfo);
                        return false;
                    }
                }
                else
                {
                    data.LockInfos.Remove(hitInfo.HitEntity.net.ID);
                    hitInfo.HitEntity.OwnerID = 0;
                }
            }

            return null;
        }

        private object OnEntityTakeDamage(BaseHelicopter heli, HitInfo hitInfo)
        {
            if (!heli.IsValid() || _personal.Contains(heli.net.ID) || hitInfo == null || heli.myAI == null || heli.myAI.isDead)
            {
                return null;
            }

            return OnEntityTakeDamageHandler(heli, hitInfo, DamageEntryType.Heli, string.Empty);
        }

        private object OnEntityTakeDamage(BradleyAPC apc, HitInfo hitInfo)
        {
            if (!apc.IsValid() || hitInfo == null || CanBradleyTakeDamage(apc, hitInfo) != null)
            {
                return null;
            }

            return OnEntityTakeDamageHandler(apc, hitInfo, DamageEntryType.Bradley, string.Empty);
        }

        private object OnEntityTakeDamage(BasePlayer player, HitInfo hitInfo)
        {
            if (!config.Npc.Enabled || !player.IsValid() || !player.IsNpc || hitInfo == null)
            {
                return null;
            }

            if (config.Npc.Min > 0 && player.startHealth < config.Npc.Min)
            {
                return null;
            }

            return OnEntityTakeDamageHandler(player, hitInfo, DamageEntryType.NPC, player.displayName);
        }

        private object OnEntityTakeDamageHandler(BaseEntity entity, HitInfo hitInfo, DamageEntryType damageEntryType, string npcName)
        {
            var attacker = hitInfo.Initiator as BasePlayer;

            if (!attacker.IsValid() || attacker.IsNpc)
            {
                return null;
            }

            DamageInfo damageInfo;
            if (!data.DamageInfos.TryGetValue(entity.net.ID, out damageInfo))
            {
                data.DamageInfos[entity.net.ID] = damageInfo = new DamageInfo(damageEntryType, npcName);
            }

            ulong ownerId = _locked.ContainsKey(entity.net.ID) ? _locked[entity.net.ID] : 0uL;

            if (ownerId != 0uL && !HasPermission(attacker, bypassDamagePerm) && !IsAlly(attacker.userID, ownerId))
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

            float health = entity.Health();

            NextTick(() =>
            {
                if (entity == null || entity.IsDestroyed)
                {
                    return;
                }

                float damage = health - entity.Health();

                if (damage > 0f)
                {
                    DamageEntry entry;
                    if (!damageInfo.damageEntries.TryGetValue(attacker.userID, out entry))
                    {
                        ulong team = config.Report.UseTeams ? attacker.currentTeam : 0uL;

                        damageInfo.damageEntries[attacker.userID] = entry = new DamageEntry(team);
                    }

                    damageInfo.AddDamage(entity, attacker, entry, damage);
                }
            });

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
                List<AttackerInfo> attackers;
                if (!_apcAttackers.TryGetValue(apc, out attackers))
                {
                    _apcAttackers[apc] = attackers = new List<AttackerInfo>();
                }

                if (!attackers.Any(x => x.attackerId == attacker.UserIDString))
                {
                    attackers.Add(new AttackerInfo
                    {
                        attacker = attacker,
                        attackerId = attacker.UserIDString
                    });
                }
            }

            return null;
        }

        private void OnEntityDeath(BaseHelicopter heli, HitInfo hitInfo) => OnEntityKill(heli);

        private void OnEntityKill(BaseHelicopter heli)
        {
            if (!heli.IsValid())
            {
                return;
            }

            _personal.Remove(heli.net.ID);
            _blackVenoms.Remove(heli.net.ID);

            OnEntityDeathHandler(heli, DamageEntryType.Heli);
        }

        private void OnEntityDeath(BradleyAPC apc, HitInfo hitInfo) => OnEntityKill(apc);

        private void OnEntityKill(BradleyAPC apc)
        {
            if (!apc.IsValid())
            {
                return;
            }

            _apcAttackers.Remove(apc);

            OnEntityDeathHandler(apc, DamageEntryType.Bradley);
        }

        private void OnEntityDeath(BasePlayer player, HitInfo hitInfo) => OnEntityKill(player);

        private void OnEntityKill(BasePlayer player)
        {
            if (!config.Npc.Enabled || !player.IsValid() || !player.IsNpc)
            {
                return;
            }

            OnEntityDeathHandler(player, DamageEntryType.NPC);
        }

        private void OnEntityDeath(NPCPlayerCorpse corpse, HitInfo hitInfo) => OnEntityKill(corpse);

        private void OnEntityKill(NPCPlayerCorpse corpse)
        {
            if (!config.Npc.Enabled || !corpse.IsValid())
            {
                return;
            }

            OnEntityDeathHandler(corpse, DamageEntryType.Corpse);
        }

        private void OnEntityDeathHandler(BaseCombatEntity entity, DamageEntryType damageEntryType)
        {
            DamageInfo damageInfo;
            if (data.DamageInfos.TryGetValue(entity.net.ID, out damageInfo))
            {
                if (damageEntryType == DamageEntryType.Bradley || damageEntryType == DamageEntryType.Heli)
                {
                    var lockInfo = new LockInfo(damageInfo, damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Bradley.LockTime);
                    var position = entity.transform.position;

                    damageInfo.OnKilled(position);

                    NextTick(() =>
                    {
                        LockInRadius<LockedByEntCrate>(position, lockInfo, damageEntryType);
                        LockInRadius<HelicopterDebris>(position, lockInfo, damageEntryType);
                        RemoveFireFromCrates(position, damageEntryType == DamageEntryType.Heli);
                    });
                }
                else if (damageEntryType == DamageEntryType.NPC)
                {
                    var position = entity.transform.position;
                    var npc = entity as BasePlayer;
                    var npcId = npc.userID;

                    damageInfo.OnKilled(position);

                    NextTick(() => LockInRadius(position, damageInfo, npcId));
                }

                damageInfo.DestroyMe();
                data.DamageInfos.Remove(entity.net.ID);
            }

            LockInfo lockInfo2;
            if (data.LockInfos.TryGetValue(entity.net.ID, out lockInfo2))
            {
                if (damageEntryType == DamageEntryType.Corpse)
                {
                    var corpse = entity as NPCPlayerCorpse;
                    var corpsePos = corpse.transform.position;
                    var corpseId = corpse.playerSteamID;

                    NextTick(() => LockInRadius(corpsePos, lockInfo2, corpseId));
                }

                data.LockInfos.Remove(entity.net.ID);
            }
        }

        private object CanLootEntity(BasePlayer player, DroppedItemContainer container) => CanLootEntityHandler(player, container);

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse) => CanLootEntityHandler(player, corpse);

        private object CanLootEntity(BasePlayer player, StorageContainer container) => CanLootEntityHandler(player, container);

        private object CanLootEntityHandler(BasePlayer player, BaseEntity entity)
        {
            if (!entity.IsValid() || HasPermission(player, bypassLootPerm))
            {
                return null;
            }

            if (entity is SupplyDrop && entity.skinID == supplyDropSkinID || config.Hackable.Enabled && entity is HackableLockedCrate)
            {
                if (!IsAlly(player.userID, entity.OwnerID))
                {
                    if (CanMessage(player))
                    {
                        CreateMessage(player, "CannotLootIt");
                    }

                    return false;
                }

                return null;
            }

            LockInfo lockInfo;
            if (!data.LockInfos.TryGetValue(entity.net.ID, out lockInfo))
            {
                return null;
            }

            if (lockInfo.IsLockOutdated)
            {
                data.LockInfos.Remove(entity.net.ID);
                entity.OwnerID = 0;
                return null;
            }

            if (!lockInfo.CanInteract(player.userID))
            {
                if (CanMessage(player))
                {
                    CreateMessage(player, "CannotLoot");
                    Message(player, lockInfo.GetDamageReport(player.UserIDString));
                }

                return false;
            }

            return null;
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

        private void OnExplosiveDropped(BasePlayer player, SupplySignal ss, ThrownWeapon tw) => OnExplosiveThrown(player, ss, tw);

        private void OnExplosiveThrown(BasePlayer player, SupplySignal ss, ThrownWeapon tw)
        {
            if (player == null || ss == null || tw.skinID > 0 || FancyDrop != null) // Only native SupplySignal
            {
                return;
            }

            var ownerID = player.userID;
            var position = ss.transform.position;
            var resourcePath = ss.EntityToCreate.resourcePath;

            ss.skinID = supplyDropSkinID;
            ss.CancelInvoke(ss.Explode);
            ss.Invoke(() => Explode(ss, ownerID, position, resourcePath), 3f);

            if (config.SupplyDrop.NotifyChat)
            {
                foreach (var target in BasePlayer.activePlayerList)
                {
                    CreateMessage(target, "ThrownSupplySignal", player.displayName);
                }
            }

            if (config.SupplyDrop.NotifyConsole)
            {
                Puts(_("ThrownSupplySignal", null, player.displayName));
            }
        }

        private void Explode(SupplySignal ss, ulong ownerID, Vector3 position, string resourcePath)
        {
            if (!ss.IsDestroyed)
            {
                position = ss.transform.position;
            }

            if (config.SupplyDrop.Bypass)
            {
                var drop = GameManager.server.CreateEntity(StringPool.Get(3632568684), position) as SupplyDrop;

                drop.OwnerID = ownerID;
                drop.skinID = supplyDropSkinID;
                drop.Spawn();
                drop.Invoke(() => drop.OwnerID = ownerID, 1f);

                if (config.SupplyDrop.LockTime > 0)
                {
                    OnSupplyDropLanded(drop);
                }
            }
            else
            {
                var plane = GameManager.server.CreateEntity(resourcePath) as CargoPlane;

                plane.InitDropPosition(position);
                plane.OwnerID = ownerID;
                plane.skinID = supplyDropSkinID;
                plane.Spawn();
                plane.Invoke(() => plane.OwnerID = ownerID, 1f);
                plane._name = position.ToString(); // Save SupplySignal position
            }

            if (!ss.IsDestroyed)
            {
                ss.Invoke(ss.FinishUp, config.SupplyDrop.Bypass ? 4.5f : (config.SupplyDrop.Fast ? 10f : 210f));
                ss.SetFlag(BaseEntity.Flags.On, true, false, true);
                ss.SendNetworkUpdateImmediate(false);
            }
        }

        private void OnAirdrop(CargoPlane plane, Vector3 newDropPosition)
        {
            if (plane.skinID > 0 || !plane.OwnerID.IsSteamId() || plane.skinID != supplyDropSkinID)
                return;

            float y = plane.transform.position.y / Core.Random.Range(2, 4); // Change Y, fast drop

            plane.transform.position.Set(plane.transform.position.x, y, plane.transform.position.z);
            plane.startPos.Set(plane.startPos.x, y, plane.startPos.z);
            plane.endPos.Set(plane.endPos.x, y, plane.endPos.z);
            plane.secondsToTake = Vector3.Distance(plane.startPos, plane.endPos) / Mathf.Clamp(config.SupplyDrop.Speed, 40f, World.Size);
        }

        private void OnSupplyDropDropped(SupplyDrop drop, CargoPlane plane)
        {
            if (drop == null || plane == null || !plane.OwnerID.IsSteamId() || plane.skinID != supplyDropSkinID)
            {
                return; // Only native CargoPlane and OwnerID
            }

            drop.OwnerID = plane.OwnerID;

            Vector3 position;

            try
            {
                position = plane._name.ToVector3(); // Using position
            }
            catch
            {
                return;
            }

            if (position == Vector3.zero)
            {
                return;
            }

            drop.transform.position = new Vector3(position.x, drop.transform.position.y, position.z);

            if (!config.SupplyDrop.Fast)
            {
                return;
            }

            AirdropController controller = drop.gameObject.AddComponent<AirdropController>();

            if (controller == null)
            {
                return;
            }

            controller.y = position.y;
        }

        private void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (drop.IsValid() && drop.OwnerID.IsSteamId() && drop.skinID == supplyDropSkinID)
            {
                drop.Invoke(() => drop.OwnerID = 0, config.SupplyDrop.LockTime);
            }
        }

        private void CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            crate.OwnerID = player.userID;

            SetupHackableCrate(player, crate);
        }

        #endregion Hooks

        #region Helpers

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
            hitInfo.HitEntity = null;
            hitInfo.DoHitEffects = false;
            hitInfo.HitMaterial = 0;
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

        private object HandleTeam(RelationshipManager.PlayerTeam team, string targetId)
        {
            foreach (var entry in _apcAttackers)
            {
                foreach (var info in entry.Value)
                {
                    if (info.attackerId == targetId)
                    {
                        CreateMessage(info.attacker, "CannotLeaveBradley");
                        return true;
                    }
                }
            }

            return null;
        }

        private bool IsDefended(BaseHelicopter heli) => heli.IsValid() && (data.LockInfos.ContainsKey(heli.net.ID) || data.DamageInfos.ContainsKey(heli.net.ID));

        private bool IsDefended(BaseCombatEntity victim) => victim.IsValid() && _locked.ContainsKey(victim.net.ID);

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
            Unsubscribe(nameof(CanHackCrate));
            Unsubscribe(nameof(OnPlayerSleepEnded));
            Unsubscribe(nameof(OnSupplyDropLanded));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnSupplyDropDropped));
            Unsubscribe(nameof(OnAirdrop));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnPlayerAttack));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(OnExplosiveDropped));
            Unsubscribe(nameof(OnExplosiveThrown));
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

        private void RemoveFireFromCrates(Vector3 position, bool isHeli)
        {
            if (!isHeli && !config.Bradley.RemoveFireFromCrates)
            {
                return;
            }

            if (isHeli && !config.Helicopter.RemoveFireFromCrates)
            {
                return;
            }

            var entities = new List<BaseEntity>();
            Vis.Entities(position, 15f, entities);

            foreach (var e in entities)
            {
                if (e is LockedByEntCrate)
                {
                    var crate = e as LockedByEntCrate;

                    if (crate == null) continue;

                    var lockingEnt = crate.lockingEnt;

                    if (lockingEnt == null || lockingEnt.transform == null) continue;

                    var entity = lockingEnt.ToBaseEntity();

                    if (entity.IsValid())
                    {
                        entity.Kill();
                    }

                }
                else if (e is FireBall)
                {
                    var fireball = e as FireBall;

                    fireball.Extinguish();
                }
                else if (e is HelicopterDebris)
                {
                    float num = isHeli ? config.Helicopter.TooHotUntil : config.Bradley.TooHotUntil;
                    var debris = e as HelicopterDebris;

                    debris.tooHotUntil = Time.realtimeSinceStartup + num;
                }
            }
        }

        private void LockInRadius<T>(Vector3 position, LockInfo lockInfo, DamageEntryType damageEntryType) where T : BaseEntity
        {
            var entities = Pool.GetList<T>();
            Vis.Entities(position, 15f, entities);

            foreach (var entity in entities)
            {
                entity.OwnerID = lockInfo.damageInfo.OwnerID;
                data.LockInfos[entity.net.ID] = lockInfo;
                
                //Puts("Locking {0} {1}", entity.ShortPrefabName, entity.OwnerID);

                float time = damageEntryType == DamageEntryType.Bradley ? config.Bradley.LockTime : config.Helicopter.LockTime;

                if (time <= 0)
                {
                    continue;
                }

                entity.Invoke(() => entity.OwnerID = 0, time);
            }

            Pool.FreeList(ref entities);
        }

        private void LockInRadius(Vector3 position, DamageInfo damageInfo, ulong npcId)
        {
            var corpses = Pool.GetList<NPCPlayerCorpse>();
            Vis.Entities(position, 3f, corpses);

            foreach (var corpse in corpses)
            {
                if (corpse.playerSteamID == npcId)
                {
                    //corpse.OwnerID = damageInfo.OwnerID;
                    data.LockInfos[corpse.net.ID] = new LockInfo(damageInfo, config.Npc.LockTime);

                    //if (config.Npc.LockTime <= 0) continue;

                    //corpse.Invoke(() => corpse.OwnerID = 0, config.Npc.LockTime);
                }
            }

            Pool.FreeList(ref corpses);
        }

        private void LockInRadius(Vector3 position, LockInfo lockInfo, ulong corpseId)
        {
            var containers = Pool.GetList<DroppedItemContainer>();
            Vis.Entities(position, 1f, containers);

            foreach (var container in containers)
            {
                if (container.IsValid() && container.playerSteamID == corpseId)
                {
                    if (config.Npc.LockTime > 0)
                    {
                        var uid = container.net.ID;
                        //container.Invoke(() => container.OwnerID = 0, config.Npc.LockTime);
                        timer.Once(config.Npc.LockTime, () => data.LockInfos.Remove(uid));
                    }

                    data.LockInfos[container.net.ID] = lockInfo;
                    break;
                }
            }
                
            Pool.FreeList(ref containers);
        }

        private bool IsBlackVenom(BaseEntity entity, BasePlayer attacker)
        {
            if (_blackVenoms.ContainsKey(entity.net.ID))
            {
                return _blackVenoms[entity.net.ID];
            }

            if (entity.OwnerID.IsSteamId() && BlackVenom != null && BlackVenom.IsLoaded)
            {
                var success = BlackVenom?.Call("IsBlackVenom", entity, attacker);

                if (success != null && success is bool && !(bool)success)
                {
                    _blackVenoms[entity.net.ID] = true;
                    return true;
                }
            }

            _blackVenoms[entity.net.ID] = false;
            return false;
        }

        private static int GetLockTime(DamageEntryType damageEntryType)
        {
            int time = damageEntryType == DamageEntryType.Bradley ? config.Bradley.LockTime : damageEntryType == DamageEntryType.Heli ? config.Helicopter.LockTime : config.Npc.LockTime;

            return time > 0 ? time : int.MaxValue;
        }

        private void CommandTest(IPlayer p, string command, string[] args)
        {
            p.Reply($"data.DamageInfos.Count: {data.DamageInfos.Count}");
            p.Reply($"data.LockInfos.Count: {data.LockInfos.Count}");
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

        private void CommandUI(IPlayer p, string command, string[] args)
        {
            if (p.IsServer || p.IsAdmin)
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

                            p.Reply($"Cooldown changed to {time} minutes");
                            ApplyCooldowns(DamageEntryType.Bradley);
                        }
                        else p.Reply($"The specified time '{args[1]}' is not a valid number.");
                    }
                    else if (args[0] == "reset")
                    {
                        var value = args[1];

                        if (data.Lockouts.Remove(value))
                        {
                            UI.DestroyLockoutUI(RustCore.FindPlayerByIdString(value));
                            p.Reply($"Removed lockout for {value}");
                        }
                        else if (!value.IsSteamId())
                        {
                            p.Reply("You must specify a steam ID");
                        }
                        else p.Reply("Target not found");
                    }
                }

                return;
            }

            var uii = UI.GetSettings(p.Id);
            var player = p.Object as BasePlayer;

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

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                config = Config.ReadObject<Configuration>();
            }
            catch { }

            if (config == null)
            {
                config = new Configuration();
            }

            SaveConfig();
        }

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

            [JsonProperty(PropertyName = "Lock Bradley From Personal Apc Plugin")]
            public bool LockPersonal { get; set; } = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; }
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

            [JsonProperty(PropertyName = "Lock Heli From Personal Heli Plugin")]
            public bool LockPersonal { get; set; } = true;

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; }
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

            [JsonProperty(PropertyName = "Block Looting Only")]
            public bool LootingOnly { get; set; } = true;
        }

        private class SupplyDropSettings
        {
            [JsonProperty(PropertyName = "Lock Supply Drops To Players")]
            public bool Lock { get; set; } = true;

            [JsonProperty(PropertyName = "Lock To Player For X Seconds (0 = Forever)")]
            public float LockTime { get; set; }

            [JsonProperty(PropertyName = "Supply Drop Fast")]
            public bool Fast { get; set; } = true;

            [JsonProperty(PropertyName = "Show Thrown Notification In Chat")]
            public bool NotifyChat { get; set; }

            [JsonProperty(PropertyName = "Show Notification In Server Console")]
            public bool NotifyConsole { get; set; }

            [JsonProperty(PropertyName = "Cargo Plane Speed (Meters Per Second)")]
            public float Speed { get; set; } = 40f;

            [JsonProperty(PropertyName = "Bypass Spawning Cargo Plane")]
            public bool Bypass { get; set; }
        }

        private class DamageReportSettings
        {
            [JsonProperty(PropertyName = "Use Teams")]
            public bool UseTeams { get; set; } = true;

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

        protected override void SaveConfig() => Config.WriteObject(config);

        protected override void LoadDefaultConfig() => config = new Configuration();

        private bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to use this command!",
                ["DamageReport"] = "Damage report for {0}",
                ["CannotLoot"] = "You cannot loot this as major damage was not from you.",
                ["CannotLootIt"] = "You cannot loot this supply drop.",
                ["CannotMine"] = "You cannot mine this as major damage was not from you.",
                ["CannotDamageThis"] = "You cannot damage this!",
                ["LockedHeli"] = "Heli has been locked to <color=#FF0000>{0}</color> and their team",
                ["LockedBradley"] = "Bradley has been locked to <color=#FF0000>{0}</color> and their team",
                ["Helicopter"] = "Heli",
                ["BradleyAPC"] = "Bradley",
                ["ThrownSupplySignal"] = "{0} has thrown a supply signal!",
                ["Format"] = "<color=#C0C0C0>{0:0.00}</color> (<color=#C3FBFE>{1:0.00}%</color>)",
                ["CannotLeaveBradley"] = "You cannot leave your team until the Bradley is destroyed!",
                ["LockedOutBradley"] = "You are locked out from Bradley for {0}",
                ["Time"] = "{0}m",
                ["BradleyKilled"] = "A bradley was killed.",
                ["BradleyUnlocked"] = "The bradley at {0} has been unlocked.",
                ["HeliUnlocked"] = "The heli at {0} has been unlocked.",
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "У вас нет разрешения на использование этой команды!",
                ["DamageReport"] = "Нанесенный урон по {0}",
                ["CannotLoot"] = "Это не ваш лут, основная часть урона насена не вами.",
                ["CannotLootIt"] = "Вы не можете открыть этот ящик с припасами.",
                ["CannotMine"] = "Вы не можете добывать это, основная часть урона насена не вами.",
                ["CannotDamageThis"] = "Вы не можете повредить это!",
                ["LockedHeli"] = "Этот патрульный вертолёт принадлежит <color=#FF0000>{0}</color> и участникам команды",
                ["LockedBradley"] = "Этот танк принадлежит <color=#FF0000>{0}</color> и участникам команды",
                ["Helicopter"] = "Патрульному вертолету",
                ["BradleyAPC"] = "Танку",
                ["ThrownSupplySignal"] = "{0} запросил сброс припасов!",
                ["Format"] = "<color=#C0C0C0>{0:0.00}</color> (<color=#C3FBFE>{1:0.00}%</color>)",
                ["CannotLeaveBradley"] = "Вы не можете покинуть команду, пока танк не будет уничтожен!",
                ["LockedOutBradley"] = "Вы заблокированы от танка на {0}",
                ["Time"] = "{0} м",
                ["BradleyKilled"] = "Танк был уничтожен.",
                ["BradleyUnlocked"] = "Танк на {0} разблокирован.",
                ["HeliUnlocked"] = "Вертолёт на {0} разблокирован.",
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