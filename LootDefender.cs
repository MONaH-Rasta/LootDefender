using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

// TODO: Unsubscribe when lockInfos and damageInfos is empty

/*
Fixed remove fire from crates
Fixed bradley apc not locking
Fixed heli not locking
Fixed missing hook
Added `Lock Npcs` (true)
Added `Supply Drop Settings`
Added damage per player to damage report
Removed bad permission checks
*/

namespace Oxide.Plugins
{
    [Info("Loot Defender", "Author Egor Blagov, Maintainer nivex", "1.0.6")]
    [Description("Defends loot from other players who dealt less damage than you.")]
    class LootDefender : RustPlugin
    {
        [PluginReference]
        Plugin PersonalHeli, BlackVenom, Clans, Friends;

        private Dictionary<string, List<string>> _clans { get; set; } = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _friends { get; set; } = new Dictionary<string, List<string>>();
        private List<string> _sent { get; set; } = new List<string>();
        private List<BaseEntity> _cargo { get; set; } = new List<BaseEntity>();

        private const float KillEntitySpawnRadius = 15.0f;
        private const string permUse = "lootdefender.use";
        private const string permAdm = "lootdefender.adm";
        private static LootDefender Instance;

        #region Stored data

        class StoredData
        {
            public Dictionary<uint, DamageInfo> damageInfos = new Dictionary<uint, DamageInfo>();
            public Dictionary<uint, LockInfo> lockInfos = new Dictionary<uint, LockInfo>();

            public void Sanitize()
            {
                foreach (var uid in new List<uint>(damageInfos.Keys))
                {
                    if (BaseNetworkable.serverEntities.Find(uid) == null)
                    {
                        damageInfos.Remove(uid);
                    }
                }

                foreach (var uid in new List<uint>(lockInfos.Keys))
                {
                    if (BaseNetworkable.serverEntities.Find(uid) == null)
                    {
                        lockInfos.Remove(uid);
                    }
                }
            }
        }

        private StoredData storedData = new StoredData();

        private Dictionary<uint, LockInfo> lockInfos => storedData.lockInfos;

        private Dictionary<uint, DamageInfo> damageInfos => storedData.damageInfos;

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData, true);
        }

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch { }

            if (storedData == null)
            {
                storedData = new StoredData();
                SaveData();
            }
        }

        #endregion

        #region Damage and Locks calculation

        class DamageEntry
        {
            public float DamageDealt;
            public DateTime Timestamp;

            [JsonIgnore]
            public bool IsOutdated => DateTime.Now.Subtract(Timestamp).TotalSeconds > Instance.config.AttackTimeoutSeconds;
            public void AddDamage(float amount)
            {
                DamageDealt += amount;
                Timestamp = DateTime.Now;
            }
        }

        class DamageInfo
        {
            private readonly StringBuilder sb = new StringBuilder();
            public Dictionary<ulong, DamageEntry> damageEntries = new Dictionary<ulong, DamageEntry>();
            public string NameKey;
            [JsonIgnore]
            public float FullDamage => damageEntries.Values.Select(x => x.DamageDealt).Sum();
            public DamageInfo() : this("unknown")
            {

            }
            public DamageInfo(string nameKey)
            {
                NameKey = nameKey;
            }

            public void AddDamage(HitInfo info)
            {
                var player = info.Initiator as BasePlayer;

                if (!player.IsValid())
                {
                    return;
                }

                DamageEntry entry;
                if (!damageEntries.TryGetValue(player.userID, out entry))
                {
                    damageEntries[player.userID] = entry = new DamageEntry();
                }

                entry.AddDamage(info.damageTypes.Total());
            }

            public void OnKilled()
            {
                foreach (var key in new List<ulong>(damageEntries.Keys))
                {
                    if (damageEntries[key].IsOutdated)
                    {
                        damageEntries.Remove(key);
                    }
                }

                DisplayDamageReport();
            }

            public void DisplayDamageReport()
            {
                foreach (var damager in damageEntries.Keys)
                {
                    var player = RelationshipManager.FindByID(damager);

                    if (!player.IsValid())
                    {
                        continue;
                    }

                    Instance.SendReply(player, GetDamageReport(player.UserIDString));
                }
            }

            public string GetDamageReport(string playerId)
            {
                var damageGroups = GetDamageGroups();
                var topDamageGroups = GetTopDamageGroups(damageGroups);

                sb.Length = 0;
                sb.AppendLine($"{_("DamageReport", playerId, $"<color={Instance.config.HexColorOk}>{_(NameKey, playerId)}</color>")}:");

                foreach (var dg in damageGroups)
                {
                    if (topDamageGroups.Contains(dg))
                    {
                        sb.Append($"<color={Instance.config.HexColorOk}>√</color> ");
                    }
                    else
                    {
                        sb.Append($"<color={Instance.config.HexColorNotOk}>✖</color> ");
                    }

                    sb.Append($"{dg.ToReport(this)}\n");
                }

                string result = sb.ToString();
                sb.Length = 0;

                return result;
            }

            public bool CanInteract(ulong playerId)
            {
                var topDamageGroups = GetTopDamageGroups(GetDamageGroups());
                var ableToInteract = topDamageGroups.SelectMany(x => x.Players).ToList();

                if (ableToInteract == null || ableToInteract.Count == 0)
                {
                    return true;
                }

                return ableToInteract.Contains(playerId);
            }

            private List<DamageGroup> GetTopDamageGroups(List<DamageGroup> damageGroups)
            {
                var topDamageGroups = new List<DamageGroup>();

                if (damageGroups.Count == 0)
                {
                    return topDamageGroups;
                }

                var topDamageGroup = damageGroups.OrderByDescending(x => x.TotalDamage).First();

                foreach (var dg in damageGroups)
                {
                    if ((topDamageGroup.TotalDamage - dg.TotalDamage) <= Instance.config.RelativeAdvantageMin * FullDamage)
                    {
                        topDamageGroups.Add(dg);
                    }
                }

                return topDamageGroups;
            }

            private List<DamageGroup> GetDamageGroups()
            {
                var result = new List<DamageGroup>();

                foreach (var damage in damageEntries)
                {
                    bool merged = false;

                    foreach (var dT in result)
                    {
                        if (dT.TryMergeDamage(damage.Key, damage.Value.DamageDealt))
                        {
                            merged = true;
                            break;
                        }
                    }

                    if (!merged)
                    {
                        if (RelationshipManager.FindByID(damage.Key) == null)
                        {
                            Instance.PrintError($"Invalid id, unable to find: {damage.Key}");
                            continue;
                        }

                        result.Add(new DamageGroup(damage.Key, damage.Value.DamageDealt));
                    }
                }

                return result;
            }
        }

        class LockInfo
        {
            public DamageInfo damageInfo;
            public DateTime LockTimestamp;
            public int LockTimeout;

            [JsonIgnore]
            public bool IsLockOutdated => DateTime.Now.Subtract(LockTimestamp).TotalSeconds >= LockTimeout;

            public LockInfo(DamageInfo damageInfo, int lockTimeout)
            {
                LockTimestamp = DateTime.Now;
                LockTimeout = lockTimeout;
                this.damageInfo = damageInfo;
            }

            public bool CanInteract(ulong playerId) => damageInfo.CanInteract(playerId);
            public string GetDamageReport(string userIdString) => damageInfo.GetDamageReport(userIdString);
        }

        class DamageGroup
        {
            public float TotalDamage { get; private set; }
            public List<ulong> Players => new List<ulong> { FirstDamagerDealer }.Concat(additionalPlayers).ToList();
            public bool IsSingle => additionalPlayers.Count == 0;
            private ulong FirstDamagerDealer { get; set; }
            private List<ulong> additionalPlayers { get; } = new List<ulong>();

            public DamageGroup(ulong playerId, float damage)
            {
                TotalDamage = damage;
                FirstDamagerDealer = playerId;

                if (!Instance.config.UseTeams)
                {
                    return;
                }

                var target = RelationshipManager.FindByID(playerId);

                if (!target.IsValid())
                {
                    return;
                }

                RelationshipManager.PlayerTeam team;
                if (!RelationshipManager.Instance.playerToTeam.TryGetValue(playerId, out team))
                {
                    return;
                }

                for (int i = 0; i < team.members.Count; i++)
                {
                    ulong member = team.members[i];

                    if (member == playerId)
                    {
                        continue;
                    }

                    additionalPlayers.Add(member);
                }
            }

            public bool TryMergeDamage(ulong playerId, float damageAmount)
            {
                if (IsPlayerInvolved(playerId))
                {
                    TotalDamage += damageAmount;
                    return true;
                }

                return false;
            }

            public bool IsPlayerInvolved(ulong playerId) => playerId == FirstDamagerDealer || additionalPlayers.Contains(playerId);

            public string ToReport(DamageInfo damageInfo)
            {
                if (IsSingle)
                {
                    return getLineForPlayer(FirstDamagerDealer, Instance.config.HexColorSinglePlayer, damageInfo);
                }

                return string.Format("({1}) {0:0}%",
                    TotalDamage / damageInfo.FullDamage * 100,
                    string.Join(" ", Players.Select(x => getLineForPlayer(x, Instance.config.HexColorTeam, damageInfo)))
                );
            }

            private string getLineForPlayer(ulong playerId, string color, DamageInfo damageInfo)
            {
                var displayName = RelationshipManager.FindByID(playerId)?.displayName ?? playerId.ToString();
                float damage = 0.0f;
                if (damageInfo.damageEntries.ContainsKey(playerId))
                {
                    damage = damageInfo.damageEntries[playerId].DamageDealt;
                }
                string damageLine = string.Format("{0} {1:0}%", damage, damage / damageInfo.FullDamage * 100);
                return $"<color={color}>{displayName}</color> {damageLine}";
            }
        }

        #endregion

        #region uMod hooks

        private void OnServerSave()
        {
            SaveData();
        }

        private void Init()
        {
            Unsubscribe(nameof(OnSupplyDropLanded));
            Unsubscribe(nameof(OnExplosiveDropped));
            Unsubscribe(nameof(OnExplosiveThrown));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnClanDestroy));
            Unsubscribe(nameof(OnClanUpdate));
            Unsubscribe(nameof(OnFriendAdded));
            Unsubscribe(nameof(OnFriendRemoved));

            Instance = this;
            AddCovalenceCommand("testlootdef", nameof(CommandTest), permAdm);
            permission.RegisterPermission(permUse, this);

            try
            {
                config = Config.ReadObject<PluginConfig>();
            }
            catch { }

            if (config == null)
            {
                config = new PluginConfig();
            }

            Config.WriteObject(config, true);
            LoadData();

            storedData.Sanitize();
        }

        private void OnServerInitialized(bool isStartup)
        {
            if (config.SupplyDrop.Lock)
            {
                if (config.SupplyDrop.LockTime > 0)
                {
                    Subscribe(nameof(OnSupplyDropLanded));
                }

                Subscribe(nameof(OnExplosiveDropped));
                Subscribe(nameof(OnExplosiveThrown));
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(OnClanDestroy));
                Subscribe(nameof(OnClanUpdate));
                Subscribe(nameof(OnFriendAdded));
                Subscribe(nameof(OnFriendRemoved));
            }
        }

        private void Unload()
        {
            SaveData();
            Instance = null;
        }

        private void OnEntityTakeDamage(BaseEntity entity, HitInfo hitInfo)
        {
            if (hitInfo == null || !entity.IsValid())
            {
                return;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (!attacker.IsValid() || attacker.IsNpc || !permission.UserHasPermission(attacker.UserIDString, permUse))
            {
                return;
            }

            string nameKey = null;

            if (entity is BaseHelicopter)
            {
                var heli = entity as BaseHelicopter;

                if (heli.IsValid())
                {
                    if (PersonalHeli != null && PersonalHeli.IsLoaded)
                    {
                        var success = PersonalHeli?.Call("IsPersonal", heli);

                        if (success is bool && (bool)success)
                        {
                            return;
                        }
                    }

                    if (BlackVenom != null && BlackVenom.IsLoaded && entity.OwnerID.IsSteamId())
                    {
                        var success = BlackVenom?.Call("IsBlackVenom", heli, attacker);

                        if (success is bool && !(bool)success)
                        {
                            return;
                        }
                    }

                    nameKey = "Heli";
                }
            }
            else if (entity is BradleyAPC)
            {
                nameKey = "Bradley";
            }
            else if (config.LockNPCs && entity is BasePlayer)
            {
                var player = entity as BasePlayer;

                if (player.IsValid() && player.IsNpc)
                {
                    nameKey = player.displayName;
                }
            }

            if (string.IsNullOrEmpty(nameKey))
            {
                return;
            }

            DamageInfo damageInfo;
            if (!damageInfos.TryGetValue(entity.net.ID, out damageInfo))
            {
                damageInfos[entity.net.ID] = damageInfo = new DamageInfo(nameKey);
            }

            damageInfo.AddDamage(hitInfo);
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (info.HitEntity is ServerGib && info.WeaponPrefab is BaseMelee)
            {
                LockInfo lockInfo;
                if (lockInfos.TryGetValue(info.HitEntity.net.ID, out lockInfo))
                {
                    if (lockInfo.IsLockOutdated)
                    {
                        lockInfos.Remove(info.HitEntity.net.ID);
                        return null;
                    }

                    if (!lockInfo.CanInteract(attacker.userID))
                    {
                        SendReply(attacker, _("CannotMine", attacker.UserIDString));
                        SendReply(attacker, lockInfo.GetDamageReport(attacker.UserIDString));
                        return false;
                    }
                }
            }

            return null;
        }

        private void OnEntityDeath(BaseEntity entity, HitInfo hitInfo) => OnEntityKill(entity);

        private void OnEntityKill(BaseEntity entity)
        {
            if (!entity.IsValid() || (!damageInfos.ContainsKey(entity.net.ID) && !lockInfos.ContainsKey(entity.net.ID)))
            {
                return;
            }

            if (entity is BaseHelicopter || entity is BradleyAPC)
            {
                damageInfos[entity.net.ID].OnKilled();

                var position = entity.transform.position;
                var lockInfo = new LockInfo(damageInfos[entity.net.ID], entity is BaseHelicopter ? Instance.config.LockHeliSeconds : Instance.config.LockBradleySeconds);
                
                NextTick(() => LockInRadius(position, lockInfo, KillEntitySpawnRadius));
            }

            if (config.LockNPCs)
            {
                if (entity is BasePlayer)
                {
                    var npc = entity as BasePlayer;
                    var corpses = Pool.GetList<NPCPlayerCorpse>();
                    Vis.Entities(npc.transform.position, 3.0f, corpses);
                    var corpse = corpses.FirstOrDefault(x => x.parentEnt == npc);
                    if (corpse.IsValid())
                    {
                        damageInfos[entity.net.ID].OnKilled();
                        lockInfos[corpse.net.ID] = new LockInfo(damageInfos[entity.net.ID], Instance.config.LockNPCSeconds);
                    }
                    Pool.FreeList(ref corpses);
                }
                else if (entity is NPCPlayerCorpse)
                {
                    var corpse = entity as NPCPlayerCorpse;
                    var corpsePos = corpse.transform.position;
                    var corpseId = corpse.playerSteamID;
                    var lockInfo = lockInfos[corpse.net.ID];
                    NextTick(() =>
                    {
                        var containers = Pool.GetList<DroppedItemContainer>();
                        Vis.Entities(corpsePos, 1.0f, containers);
                        var container = containers.FirstOrDefault(x => x.playerSteamID == corpseId);
                        if (container.IsValid())
                        {
                            lockInfos[container.net.ID] = lockInfo;
                        }
                        Pool.FreeList(ref containers);
                    });
                }
            }

            if (lockInfos.ContainsKey(entity.net.ID))
            {
                lockInfos.Remove(entity.net.ID);
            }
            else damageInfos.Remove(entity.net.ID);
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (!entity.IsValid())
            {
                return null;
            }

            if (entity is SupplyDrop)
            {
                if (!IsAlly(player.userID, entity.OwnerID))
                {
                    if (CanMessage(player))
                    {
                        SendReply(player, _("CannotLootIt", player.UserIDString));
                    }

                    return false;
                }

                return null;
            }

            LockInfo lockInfo;
            if (!lockInfos.TryGetValue(entity.net.ID, out lockInfo))
            {
                return null;
            }

            if (lockInfo.IsLockOutdated)
            {
                lockInfos.Remove(entity.net.ID);
                return null;
            }

            if (!lockInfo.CanInteract(player.userID))
            {
                SendReply(player, _("CannotLoot", player.UserIDString));
                SendReply(player, lockInfo.GetDamageReport(player.UserIDString));
                return false;
            }

            return null;
        }

        #endregion

        #region Supply Drops

        private void OnClanUpdate(string tag)
        {
            _clans.Remove(tag);
        }

        private void OnClanDestroy(string tag)
        {
            _clans.Remove(tag);
        }

        private void OnFriendAdded(string playerId, string targetId)
        {
            List<string> playerList;
            if (!_friends.TryGetValue(playerId, out playerList))
            {
                _friends[playerId] = playerList = new List<string>();
            }

            playerList.Add(targetId);
        }

        private void OnFriendRemoved(string playerId, string targetId)
        {
            List<string> playerList;
            if (_friends.TryGetValue(playerId, out playerList))
            {
                playerList.Remove(targetId);
            }
        }

        private void OnExplosiveDropped(BasePlayer player, SupplySignal ss, ThrownWeapon tw) => OnExplosiveThrown(player, ss, tw);

        private void OnExplosiveThrown(BasePlayer player, SupplySignal ss, ThrownWeapon tw)
        {
            if (player == null || ss == null)
            {
                return;
            }

            ulong ownerId = player.userID;

            ss.CancelInvoke(ss.Explode);
            ss.Invoke(() => Explode(ss, ownerId), 3f);

            if (config.SupplyDrop.Notification)
            {
                PrintToChat(_("ThrownSupplySignal", player.UserIDString, player.displayName));
            }

            Puts(_("ThrownSupplySignal", null, player.displayName));
        }

        private void Explode(SupplySignal ss, ulong ownerId)
        {
            if (ss == null || ss.IsDestroyed)
            {
                return;
            }

            var plane = GameManager.server.CreateEntity(ss.EntityToCreate.resourcePath) as CargoPlane;

            _cargo.Add(plane);

            plane.SendMessage("InitDropPosition", ss.transform.position, SendMessageOptions.DontRequireReceiver);
            plane.OwnerID = ownerId;
            plane.Spawn();

            plane.secondsToTake = Vector3.Distance(plane.endPos, plane.startPos) / Mathf.Clamp(config.SupplyDrop.Speed, 40f, World.Size);
            plane.Invoke(() => plane.OwnerID = ownerId, 1f);

            ss.Invoke(ss.FinishUp, 210f);
            ss.SetFlag(BaseEntity.Flags.On, true, false, true);
            ss.SendNetworkUpdateImmediate(false);
        }

        private void OnEntitySpawned(SupplyDrop drop)
        {
            _cargo.RemoveAll(x => x == null || x.IsDestroyed || x.transform == null);

            if (drop == null || drop.IsDestroyed || drop.transform == null || drop.OwnerID != 0)
            {
                return;
            }

            CargoPlane plane = null;

            foreach (var x in _cargo)
            {
                if (x.IsValid() && (x.transform.position - drop.transform.position).magnitude < 15f)
                {
                    plane = x as CargoPlane;
                    break;
                }
            }

            if (plane == null)
            {
                return;
            }

            drop.OwnerID = plane.OwnerID;

            var rb = drop.GetComponent<Rigidbody>();

            if (rb)
            {
                rb.drag = Mathf.Clamp(config.SupplyDrop.Drag, 0.1f, 1f);
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
        }

        private void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (drop.IsValid() && drop.OwnerID.IsSteamId())
            {
                drop.Invoke(() => drop.OwnerID = 0, config.SupplyDrop.LockTime);
            }
        }

        public bool IsAlly(ulong playerId, ulong ownerId)
        {
            return playerId == ownerId || !ownerId.IsSteamId() || IsOnSameTeam(playerId, ownerId) || IsInSameClan(playerId.ToString(), ownerId.ToString()) || AreFriends(playerId.ToString(), ownerId.ToString());
        }

        public bool IsInSameClan(string playerId, string targetId)
        {
            if (playerId == targetId || Clans == null || !Clans.IsLoaded)
            {
                return false;
            }

            var clan = new List<string>();

            foreach (var x in _clans.Values)
            {
                if (x.Contains(playerId))
                {
                    if (x.Contains(targetId))
                    {
                        return true;
                    }

                    clan = x;
                    break;
                }
            }

            string playerClan = Clans?.Call("GetClanOf", playerId) as string;

            if (string.IsNullOrEmpty(playerClan))
            {
                return false;
            }

            string targetClan = Clans?.Call("GetClanOf", targetId) as string;

            if (string.IsNullOrEmpty(targetClan))
            {
                return false;
            }

            if (playerClan == targetClan)
            {
                if (!_clans.ContainsKey(playerClan))
                {
                    _clans[playerClan] = clan;
                }

                if (!clan.Contains(playerId)) clan.Add(playerId);
                if (!clan.Contains(targetId)) clan.Add(targetId);
                return true;
            }

            return false;
        }


        public bool AreFriends(string playerId, string targetId)
        {
            if (playerId == targetId || Friends == null || !Friends.IsLoaded)
            {
                return false;
            }

            List<string> targetList;
            if (!_friends.TryGetValue(targetId, out targetList))
            {
                _friends[targetId] = targetList = new List<string>();
            }

            if (targetList.Contains(playerId))
            {
                return true;
            }

            var success = Friends?.Call("AreFriends", playerId, targetId);

            if (success is bool && (bool)success)
            {
                targetList.Add(playerId);
                return true;
            }

            return false;
        }

        public bool IsOnSameTeam(ulong playerId, ulong targetId)
        {
            RelationshipManager.PlayerTeam team1;
            if (!RelationshipManager.Instance.playerToTeam.TryGetValue(playerId, out team1))
            {
                return false;
            }

            RelationshipManager.PlayerTeam team2;
            if (!RelationshipManager.Instance.playerToTeam.TryGetValue(targetId, out team2))
            {
                return false;
            }

            return team1.teamID == team2.teamID;
        }

        #endregion Supply Drops

        private void LockInRadius(Vector3 position, LockInfo lockInfo, float radius)
        {
            var entities = Pool.GetList<BaseEntity>();
            Vis.Entities(position, radius, entities);

            foreach (var e in entities)
            {
                if (e is HelicopterDebris || e is LockedByEntCrate)
                {
                    lockInfos[e.net.ID] = lockInfo;
                }
                
                if (config.RemoveFireFromCrates)
                {
                    if (e is LockedByEntCrate)
                    {
                        var crate = e as LockedByEntCrate;

                        if (crate == null) continue;

                        var lockingEnt = crate.lockingEnt;

                        if (lockingEnt == null || lockingEnt.transform == null) continue;

                        var entity = lockingEnt.ToBaseEntity();

                        if (entity.IsValid() && !entity.IsDestroyed)
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
            }

            Pool.FreeList(ref entities);
        }

        private void CommandTest(IPlayer p, string command, string[] args)
        {
            p.Reply($"total damage infos: {damageInfos.Count}");
            p.Reply($"total lock infos: {lockInfos.Count}");
        }

        #region Config

        private class SupplyDropSettings
        {
            [JsonProperty(PropertyName = "Lock Supply Drops To Players")]
            public bool Lock { get; set; }

            [JsonProperty(PropertyName = "Lock To Player For X Seconds (0 = Forever)")]
            public float LockTime { get; set; }

            [JsonProperty(PropertyName = "Supply Drop Drag")]
            public float Drag { get; set; } = 1f;

            [JsonProperty(PropertyName = "Show Thrown Supply Drop Notification In Chat")]
            public bool Notification { get; set; }

            [JsonProperty(PropertyName = "Cargo Plane Speed (Meters Per Second)")]
            public float Speed { get; set; } = 40f;
        }

        class PluginConfig
        {
            [JsonProperty(PropertyName = "Supply Drop Settings")]
            public SupplyDropSettings SupplyDrop { get; set; } = new SupplyDropSettings();

            [JsonProperty(PropertyName = "Lock Npcs")]
            public bool LockNPCs { get; set; } = true;

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

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            Config.WriteObject(config, true);
        }

        #endregion

        #region L10N

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You have no permission to use this command!",
                ["DamageReport"] = "Damage report for {0}",
                ["CannotLoot"] = "You cannot loot this as major damage was not from you.",
                ["CannotLootIt"] = "You cannot loot this.",
                ["CannotMine"] = "You cannot mine this as major damage was not from you.",
                ["CannotDamageThis"] = "You cannot damage this!",
                ["Heli"] = "Patrol helicopter",
                ["HeliLocked"] = "{0} has been locked to <color=#FF0000>{1}</color> and their team",
                ["Bradley"] = "Bradley APC",
                ["BradleyLocked"] = "{0} has been locked to <color=#FF0000>{1}</color> and their team",
                ["ThrownSupplySignal"] = "{0} has thrown a supply signal!",
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "У вас нет разрешения на использование этой команды!",
                ["DamageReport"] = "Отчет о повреждениях для {0}",
                ["CannotLoot"] = "Вы не можете разграбить это. \nТак как больше урона нанесли не Вы.",
                ["CannotLootIt"] = "Вы не можете добыть это.",
                ["CannotMine"] = "Вы не можете добыть это. \nТак как больше урона было нанесено не от Вас.",
                ["CannotDamageThis"] = "Вы не можете повредить это!",
                ["Heli"] = "Патрульный вертолет",
                ["HeliLocked"] = "{0} заблокировал от кражи лута игрок <color=#1eff00>{1}</color> \n<color=#FF0000>На 10 минут.</color>",
                ["Bradley"] = "Брэдли",
                ["BradleyLocked"] = "{0} заблокировал от кражи лута игрок <color=#1eff00>{1}</color> \n<color=#FF0000>На 10 минут.</color>",
                ["ThrownSupplySignal"] = "Игрок <color=#1eff00>{0}</color> заблокировал АИРДРОП от кражи лута \n<color=#FF0000>На 10 минут.</color>",
            }, this, "ru");
        }

        private static string _(string key, string userId, params object[] args)
        {
            string message = Instance.lang.GetMessage(key, Instance, userId);
            return args.Length > 0 ? string.Format(message, args) : message;
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

        #endregion
    }
}
