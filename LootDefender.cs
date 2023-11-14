//#define DEBUG

using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins {
    [Info("Loot Defender", "Egor Blagov", "1.0.3")]
    [Description("Defends loot from other players who not involved into action")]
    class LootDefender : RustPlugin {
        [PluginReference]
        Plugin PersonalHeli;

        private const float KillEntitySpawnRadius = 10.0f;
        private string permUse = "lootdefender.use";
        private string permAdm = "lootdefender.adm";
        private static LootDefender Instance;


        #region Config

        class PluginConfig {
            public int AttackTimeoutSeconds = 300;
            public float RelativeAdvantageMin = 0.05f;
            public bool UseTeams = true;
            public string HexColorSinglePlayer = "#6d88ff";
            public string HexColorTeam = "#ff804f";
            public string HexColorOk = "#88ff6d";
            public string HexColorNotOk = "#ff5716";

            public int LockBradleySeconds = 900;
            public int LockHeliSeconds = 900;
            public int LockNPCSeconds = 300;
            public bool RemoveFireFromCrates = true;
        }

        private PluginConfig config;

        protected override void LoadDefaultConfig() {
            Config.WriteObject(new PluginConfig(), true);
        }

        #endregion

        #region Stored data

        class StoredData {
            public Dictionary<uint, DamageInfo> damageInfos = new Dictionary<uint, DamageInfo>();
            public Dictionary<uint, LockInfo> lockInfos = new Dictionary<uint, LockInfo>();

            public void Santize() {
                HashSet<uint> allNetIds = new HashSet<uint>();
                foreach (var ent in BaseNetworkable.serverEntities) {
                    if (ent?.net?.ID != null) {
                        allNetIds.Add(ent.net.ID);
                    }
                }

                foreach (var id in damageInfos.Keys.ToList()) {
                    if (!allNetIds.Contains(id)) {
                        damageInfos.Remove(id);
                    }
                }

                foreach (var id in lockInfos.Keys.ToList()) {
                    if (!allNetIds.Contains(id)) {
                        lockInfos.Remove(id);
                    }
                }
            }
        }

        private StoredData storedData;

        private Dictionary<uint, LockInfo> lockInfos => this.storedData.lockInfos;

        private Dictionary<uint, DamageInfo> damageInfos => this.storedData.damageInfos;

        private void SaveData() {
            if (storedData != null) {
                Interface.Oxide.DataFileSystem.WriteObject(Name, storedData, true);
            }
        }

        private void LoadData() {
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            if (storedData == null) {
                storedData = new StoredData();
                SaveData();
            }
        }

        #endregion

        #region L10N

        protected override void LoadDefaultMessages() {
            lang.RegisterMessages(new Dictionary<string, string> {
                ["NoPermission"] = "You have no permission to use this command",
                ["DamageReport"] = "Damage report for {0}",
                ["CannotLoot"] = "You cannot loot it, major damage was not from you",
                ["CannotMine"] = "You cannot mine it, major damage was not from you",
                ["Heli"] = "Patrol helicopter",
                ["Bradley"] = "Bradley APC"
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string> {
                ["NoPermission"] = "У Вас нет привилегии на использование этой команды",
                ["DamageReport"] = "Нанесенный урон по {0}",
                ["CannotLoot"] = "Это не Ваш лут, основная часть урона нанесена другими игроками",
                ["CannotMine"] = "Вы не можете добывать это, основная часть урона насена другими игроками",
                ["Heli"] = "Патрульному вертолету",
                ["Bradley"] = "Танку"
            }, this, "ru");
        }

        private static string _(string key, string userId, params object[] args) {
            return string.Format(Instance.lang.GetMessage(key, Instance, userId), args);
        }

        #endregion

        #region Damage and Locks calculation

        class DamageEntry {
            public float DamageDealt = 0.0f;
            public DateTime Timestamp;

            [JsonIgnore]
            public bool IsOutdated => DateTime.Now.Subtract(Timestamp).TotalSeconds > Instance.config.AttackTimeoutSeconds;
            public void AddDamage(float amount) {
                DamageDealt += amount;
                Timestamp = DateTime.Now;
            }
        }

        class DamageInfo {
            public Dictionary<ulong, DamageEntry> damageEntries = new Dictionary<ulong, DamageEntry>();
            public string NameKey;
            [JsonIgnore]
            public float FullDamage => damageEntries.Values.Select(x => x.DamageDealt).Sum();
            public DamageInfo() : this("unknown") {

            }
            public DamageInfo(string namekey) {
                this.NameKey = namekey;
            }

            public void Damage(HitInfo info) {
                if (info.InitiatorPlayer == null || info.InitiatorPlayer.IsNpc) {
                    return;
                }
                if (!damageEntries.ContainsKey(info.InitiatorPlayer.userID)) {
                    damageEntries[info.InitiatorPlayer.userID] = new DamageEntry();
                }
                damageEntries[info.InitiatorPlayer.userID].AddDamage(info.damageTypes.Total());
            }

            public void OnKilled() {
                foreach (var key in damageEntries.Keys.ToList()) {
                    if (damageEntries[key].IsOutdated) {
                        damageEntries.Remove(key);
                    }
                }
                DisplayDamageReport();
            }

            public void DisplayDamageReport() {
                foreach (var damager in this.damageEntries.Keys) {
                    BasePlayer player = RelationshipManager.FindByID(damager);
                    Instance.SendReply(player, this.GetDamageReport(player.UserIDString));
                }
            }

            public string GetDamageReport(string userIDString) {
                var damageGroups = GetDamageGroups();
                var topDamageGroups = GetTopDamageGroups(damageGroups);
                string result = $"{_("DamageReport", userIDString, $"<color={Instance.config.HexColorOk}>{_(NameKey, userIDString)}</color>")}:\n";
                foreach (var dg in damageGroups) {
                    if (topDamageGroups.Contains(dg)) {
                        result += $"<color={Instance.config.HexColorOk}>√</color> ";
                    } else {
                        result += $"<color={Instance.config.HexColorNotOk}>✖</color> ";
                    }
                    result += dg.ToReport(this) + "\n";
                }

                return result;
            }

            public bool CanInteract(ulong playerId) {
                var ableToInteract = getAbleToLoot();
                if (ableToInteract == null || ableToInteract.Count == 0) {
                    return true;
                }

                return ableToInteract.Contains(playerId);
            }

            private List<ulong> getAbleToLoot() {
                var topDamageGroups = GetTopDamageGroups(GetDamageGroups());
                return topDamageGroups.SelectMany(x => x.Players).ToList();
            }

            private List<DamageGroup> GetTopDamageGroups(List<DamageGroup> damageGroups) {
                if (damageGroups.Count == 0) {
                    return new List<DamageGroup>();
                }

                DamageGroup topDamageGroup = damageGroups.OrderByDescending(x => x.TotalDamage).First();
                var topDamageGroups = new List<DamageGroup>();
                foreach (var dg in damageGroups) {
                    if ((topDamageGroup.TotalDamage - dg.TotalDamage) <= Instance.config.RelativeAdvantageMin * this.FullDamage) {
                        topDamageGroups.Add(dg);
                    }
                }
                return topDamageGroups;
            }

            private List<DamageGroup> GetDamageGroups() {
                var result = new List<DamageGroup>();

                foreach (var damage in this.damageEntries) {
                    bool merged = false;
                    foreach (var dT in result) {
                        if (dT.TryMergeDamage(damage.Key, damage.Value.DamageDealt)) {
                            merged = true;
                            break;
                        }
                    }

                    if (!merged) {
                        if (RelationshipManager.FindByID(damage.Key) == null) {
                            Instance.PrintError($"Invalid id, unable to find: {damage.Key}");
                            continue;
                        }
                        result.Add(new DamageGroup(damage.Key, damage.Value.DamageDealt));
                    }
                }

                return result;
            }
        }

        class LockInfo {
            public DamageInfo damageInfo;
            public DateTime LockTimestamp;
            public int LockTimeout;

            [JsonIgnore]
            public bool IsLockOutdated => DateTime.Now.Subtract(LockTimestamp).TotalSeconds >= this.LockTimeout;

            public LockInfo(DamageInfo damageInfo, int lockTimeout) {
                LockTimestamp = DateTime.Now;
                this.LockTimeout = lockTimeout;
                this.damageInfo = damageInfo;
            }

            public bool CanInteract(ulong playerId) => this.damageInfo.CanInteract(playerId);
            public string GetDamageReport(string userIdString) => this.damageInfo.GetDamageReport(userIdString);
        }

        class DamageGroup {
            public float TotalDamage { get; private set; }
            public List<ulong> Players => new List<ulong> { FirstDamagerDealer }.Concat(this.additionalPlayers).ToList();
            public bool IsSingle => additionalPlayers.Count == 0;
            private ulong FirstDamagerDealer;
            private List<ulong> additionalPlayers = new List<ulong>();
            public DamageGroup(ulong playerId, float damage) {
                this.TotalDamage = damage;
                this.FirstDamagerDealer = playerId;
                if (Instance.config.UseTeams) {
                    var firstDDPlayer = RelationshipManager.FindByID(this.FirstDamagerDealer);
                    if (firstDDPlayer.currentTeam != 0) {
                        foreach (var member in RelationshipManager.Instance.teams[firstDDPlayer.currentTeam].members) {
                            if (member != this.FirstDamagerDealer) {
                                additionalPlayers.Add(member);
                            }
                        }
                    }
                }
            }

            public bool TryMergeDamage(ulong playerId, float damageAmount) {
                if (IsPlayerInvolved(playerId)) {
                    this.TotalDamage += damageAmount;
                    return true;
                }

                return false;
            }

            public bool IsPlayerInvolved(ulong playerId) => playerId == this.FirstDamagerDealer || this.additionalPlayers.Contains(playerId);

            public string ToReport(DamageInfo damageInfo) {
                if (IsSingle) {
                    return this.getLineForPlayer(this.FirstDamagerDealer, Instance.config.HexColorSinglePlayer, damageInfo);
                }

                return string.Format("({1}) {0:0}%",
                    this.TotalDamage / damageInfo.FullDamage * 100,
                    string.Join(" ", this.Players.Select(x => this.getLineForPlayer(x, Instance.config.HexColorTeam, damageInfo)))
                 );

            }

            private string getLineForPlayer(ulong playerId, string color, DamageInfo damageInfo) {
                float damage = 0.0f;
                if (damageInfo.damageEntries.ContainsKey(playerId)) {
                    damage = damageInfo.damageEntries[playerId].DamageDealt;
                }
                string damageLine = string.Format("{0:0}%", damage / damageInfo.FullDamage * 100);
                return $"<color={color}>{RelationshipManager.FindByID(playerId).displayName}</color> {damageLine}";
            }
        }

        #endregion

        #region uMod hooks

        private void OnServerSave() {
            SaveData();
        }

        private void Init() {
            Instance = this;

            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permAdm, this);

            config = Config.ReadObject<PluginConfig>();
            Config.WriteObject(config, true);
            LoadData();

            storedData.Santize();
        }

        private void Unload() {
            SaveData();
            Instance = null;
        }

        private void OnEntityTakeDamage (BaseEntity entity, HitInfo hitInfo) {
            if (entity?.net?.ID == null) {
                return;
            }

            if (hitInfo.InitiatorPlayer == null || hitInfo.InitiatorPlayer.IsNpc) {
                return;
            }

            if (!permission.UserHasPermission(hitInfo.InitiatorPlayer.UserIDString, permUse)) {
                return;
            }

            string nameKey = null;
            if (entity is BaseHelicopter)  {
                if (PersonalHeli != null && PersonalHeli.Call<bool>("IsPersonal", entity as BaseHelicopter)) {
                    return;
                }

                nameKey = "Heli";
            }

            if (entity is BradleyAPC) {
                nameKey = "Bradley";
            }

            if (entity is BasePlayer && (entity as BasePlayer).IsNpc) {
                nameKey = (entity as BasePlayer).displayName;
            }
             if (nameKey != null) {
                if (!damageInfos.ContainsKey(entity.net.ID)) {
                    damageInfos[entity.net.ID] = new DamageInfo(nameKey);
                }

                damageInfos[entity.net.ID].Damage(hitInfo);
            }
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info) {
            if (!permission.UserHasPermission(attacker.UserIDString, permUse)) {
                return null;
            }

            if (info.HitEntity is ServerGib && info.WeaponPrefab is BaseMelee) {
                if (lockInfos.ContainsKey(info.HitEntity.net.ID)) {
                    if (lockInfos[info.HitEntity.net.ID].IsLockOutdated) {
                        lockInfos.Remove(info.HitEntity.net.ID);
                        return null;
                    }

                    if (!lockInfos[info.HitEntity.net.ID].CanInteract(attacker.userID)) {
                        SendReply(attacker, _("CannotMine", attacker.UserIDString));
                        SendReply(attacker, lockInfos[info.HitEntity.net.ID].GetDamageReport(attacker.UserIDString));
                        return false;
                    }
                }
            }
            return null;
        }

        private void OnEntityKill(BaseEntity entity) {
            if (entity?.net?.ID == null || (!damageInfos.ContainsKey(entity.net.ID) && !lockInfos.ContainsKey(entity.net.ID))) {
                return;
            }

            if (entity is BaseHelicopter || entity is BradleyAPC) {
                damageInfos[entity.net.ID].OnKilled();
                var lockInfo = new LockInfo(damageInfos[entity.net.ID], entity is BaseHelicopter ? Instance.config.LockHeliSeconds : Instance.config.LockBradleySeconds);
                LockInRadius<LootContainer>(entity.transform.position, lockInfo, KillEntitySpawnRadius);
                LockInRadius<HelicopterDebris>(entity.transform.position, lockInfo, KillEntitySpawnRadius);
            }

            if (entity is BasePlayer) {
                BasePlayer npc = entity as BasePlayer;
                List<NPCPlayerCorpse> corpses = Pool.GetList<NPCPlayerCorpse>();
                Vis.Entities(npc.transform.position, 3.0f, corpses);
                if (corpses.Where(x => x.parentEnt == npc).Count() != 0) {
                    damageInfos[entity.net.ID].OnKilled();
                    lockInfos[corpses.First(x => x.parentEnt == npc).net.ID] = new LockInfo(damageInfos[entity.net.ID], Instance.config.LockNPCSeconds);
                }
                Pool.FreeList(ref corpses);
            }

            if (entity is NPCPlayerCorpse) {
                NPCPlayerCorpse corpse = entity as NPCPlayerCorpse;
                var corpsePos = corpse.transform.position;
                var corpseId = corpse.playerSteamID;
                var lockInfo = lockInfos[corpse.net.ID];
                NextTick(() => {
                    List<DroppedItemContainer> containers = Pool.GetList<DroppedItemContainer>();
                    Vis.Entities(corpsePos, 1.0f, containers);
                    if (containers.Where(x => x.playerSteamID == corpseId).Count() != 0) {
                        lockInfos[containers.First(x => x.playerSteamID == corpseId).net.ID] = lockInfo;
                    }
                    Pool.FreeList(ref containers);
                });
            }

            if (lockInfos.ContainsKey(entity.net.ID)) {
                lockInfos.Remove(entity.net.ID);
            } else {
                damageInfos.Remove(entity.net.ID);
            }
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity) {
            if (entity?.net?.ID == null || !lockInfos.ContainsKey(entity.net.ID)) {
                return null;
            }

            if(!permission.UserHasPermission(player.UserIDString, permUse)) {
                return null;
            }

            if (lockInfos[entity.net.ID].IsLockOutdated) {
                lockInfos.Remove(entity.net.ID);
                return null;
            }

            var canLoot = lockInfos[entity.net.ID].CanInteract(player.userID);
            if (!canLoot) {
                SendReply(player, _("CannotLoot", player.UserIDString));
                SendReply(player, lockInfos[entity.net.ID].GetDamageReport(player.UserIDString));
                return false;
            }

            return null;
        }

        #endregion

        private void LockInRadius<T>(Vector3 position, LockInfo lockInfo, float radius) where T : BaseEntity {
            var entities = Facepunch.Pool.GetList<T>();
            Vis.Entities(position, radius, entities);
            foreach (var ent in entities) {
                lockInfos[ent.net.ID] = lockInfo;
                if (config.RemoveFireFromCrates) {
                    (ent as LockedByEntCrate)?.lockingEnt?.ToBaseEntity()?.Kill();
                }
            }
            Facepunch.Pool.FreeList(ref entities);
        }

        [ConsoleCommand("testlootdef")]
        private void testlootdef(ConsoleSystem.Arg arg) {
            if (arg.Player() == null || permission.UserHasPermission(arg.Player().UserIDString, permAdm)) {
                Puts($"total damage infos: {damageInfos.Count}");
                Puts($"total lock infos: {lockInfos.Count}");
            }
        }

        [ChatCommand("testlootdef")]
        private void testcmd(BasePlayer player) {
            if (permission.UserHasPermission(player.UserIDString, permAdm)) {
                SendReply(player, $"total damage infos: {damageInfos.Count}");
                SendReply(player, $"total lock infos: {lockInfos.Count}");
            }
        }
    }
}
