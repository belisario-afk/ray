using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RayGun", "YourNameHere", "3.0.3")]
    [Description("Water pistol RayGun: hitscan damage via Hurt + HitInfo/OnAttacked, with FX and optional hitmarker.")]
    public class RayGun : RustPlugin
    {
        #region Configuration

        private RayGunConfig _config;

        public class RayGunConfig
        {
            public bool Enabled = true;

            // Which item acts as the RayGun
            public string ItemShortname = "pistol.water";

            // If empty, all skins for that item are allowed.
            public List<ulong> AllowedSkins = new List<ulong>();

            // RayGun behaviour
            public float MaxRange = 100f;      // how far the ray goes
            public float Damage = 25f;         // damage per hit
            public float FireRate = 8f;        // shots per second per player

            // EMPTY = no permission required (everyone can use)
            public string PermissionName = "";

            // FX paths (your requested ones)
            public string MuzzleFxPrefab = "assets/content/effects/muzzleflashes/other/muzzle_flash_silencer_oilfilter.prefab";
            public string TracerFxPrefab = "assets/prefabs/weapons/eoka pistol/effects/flint_spark.prefab";
            public string ImpactFxPrefab = "assets/prefabs/weapons/eoka pistol/effects/flint_spark.prefab";

            // Hitmarker settings
            public bool HitmarkerEnabled = false;

            // Leave empty by default to avoid StringPool errors.
            // Set to a valid sound prefab path on your build to enable audio hitmarkers.
            public string HitmarkerSound = "";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new RayGunConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<RayGunConfig>();
                if (_config == null)
                    throw new Exception("Config file is null");
            }
            catch (Exception e)
            {
                PrintError($"[RayGun] Error loading config: {e.Message}. Using default config.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Fields

        private readonly Dictionary<ulong, float> _lastShotTime = new Dictionary<ulong, float>();
        private int _rayMask;

        // Default damage type – this will be put into HitInfo and go through standard pipeline.
        private Rust.DamageType _damageType = Rust.DamageType.Bullet;

        #endregion

        #region Hooks

        private void Init()
        {
            LoadConfig();
            SetupPermission();

            // Updated ray mask: includes players + NPCs + usual world stuff
            _rayMask = LayerMask.GetMask(
                "Default",
                "Deployed",
                "Construction",
                "Construction Trigger",
                "Terrain",
                "World",
                "Tree",
                "Water",
                "AI",
                "Player (Server)",
                "NPC"
            );

            if (_config.MaxRange <= 0f) _config.MaxRange = 100f;
            if (_config.FireRate <= 0f) _config.FireRate = 5f;

            PrintWarning("[RayGun] Loaded. Hold pistol.water and fire – hitscan RayGun using Hurt + HitInfo/OnAttacked.");
        }

        private void SetupPermission()
        {
            if (string.IsNullOrEmpty(_config.PermissionName))
                return;

            if (!permission.PermissionExists(_config.PermissionName))
                permission.RegisterPermission(_config.PermissionName, this);
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null || !_config.Enabled) return;

            var item = newItem;
            if (item == null) return;

            if (string.Equals(item.info?.shortname, _config.ItemShortname, StringComparison.Ordinal))
            {
                PrintWarning($"[RayGun] {player.displayName} equipped {item.info.shortname} (skin {item.skin}).");
            }
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!_config.Enabled || player == null || !player.IsAlive() || input == null)
                return;

            if (!IsHoldingRayGunItem(player))
                return;

            if (!CanUseRayGun(player))
                return;

            if (!input.IsDown(BUTTON.FIRE_PRIMARY))
                return;

            if (!CanFireNow(player))
                return;

            FireRayGun(player);
        }

        #endregion

        #region Core Logic

        private bool CanUseRayGun(BasePlayer player)
        {
            if (player == null) return false;

            if (string.IsNullOrEmpty(_config.PermissionName))
                return true;

            return permission.UserHasPermission(player.UserIDString, _config.PermissionName);
        }

        private bool IsHoldingRayGunItem(BasePlayer player)
        {
            var item = player.GetActiveItem();
            if (item == null) return false;

            if (!string.Equals(item.info?.shortname, _config.ItemShortname, StringComparison.Ordinal))
                return false;

            if (_config.AllowedSkins != null && _config.AllowedSkins.Count > 0)
            {
                if (!_config.AllowedSkins.Contains(item.skin))
                    return false;
            }

            return true;
        }

        private bool CanFireNow(BasePlayer player)
        {
            float now = Time.time;
            float cooldown = 1f / Mathf.Max(_config.FireRate, 0.0001f);

            if (_lastShotTime.TryGetValue(player.userID, out float last))
            {
                if (now - last < cooldown)
                    return false;
            }

            _lastShotTime[player.userID] = now;
            return true;
        }

        private void FireRayGun(BasePlayer attacker)
        {
            if (attacker == null) return;

            // Simplified, more typical origin/forward
            Vector3 origin = attacker.eyes != null
                ? attacker.eyes.position
                : attacker.transform.position;

            Vector3 forward = attacker.eyes != null
                ? attacker.eyes.BodyForward()
                : attacker.transform.forward;

            RaycastHit hit;
            BaseEntity hitEntity = null;
            Vector3 hitPoint = origin + forward * _config.MaxRange;
            Vector3 hitNormal = -forward;

            bool didHit = Physics.Raycast(origin, forward, out hit, _config.MaxRange, _rayMask,
                QueryTriggerInteraction.Ignore);

            if (didHit)
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
                hitEntity = hit.GetEntity();

                if (hitEntity != null)
                {
                    PrintWarning($"[RayGun] Raycast hit entity: {hitEntity.ShortPrefabName} ({hitEntity.GetType().Name}) at {hitPoint}");
                }
                else
                {
                    PrintWarning("[RayGun] Raycast hit something with no BaseEntity");
                }
            }
            else
            {
                PrintWarning("[RayGun] Raycast did not hit anything");
            }

            bool didDamage = false;

            if (didHit && hitEntity != null)
            {
                didDamage = ApplyDamageViaHitInfo(hitEntity, attacker, hitPoint, hitNormal);
            }

            PlayMuzzleFx(origin, forward);
            PlayTracerFx(origin, hitPoint);
            if (didHit)
                PlayImpactFx(hitPoint, hitNormal);

            if (didDamage)
                PlayHitmarker(attacker);
        }

        /// <summary>
        /// Apply damage using BaseCombatEntity.Hurt (like other hitscan weapons),
        /// and also send a HitInfo to OnAttacked so Rust + plugins see a normal hit.
        /// </summary>
        private bool ApplyDamageViaHitInfo(BaseEntity entity, BasePlayer attacker, Vector3 hitPoint, Vector3 hitNormal)
        {
            if (entity == null || attacker == null) return false;

            float dmg = Mathf.Max(0f, _config.Damage);
            if (dmg <= 0f) return false;

            try
            {
                var held = attacker.GetActiveItem()?.GetHeldEntity() as AttackEntity;
                var bce = entity as BaseCombatEntity;

                PrintWarning($"[RayGun] ApplyDamageViaHitInfo -> target={entity.ShortPrefabName}, type={entity.GetType().Name}, dmg={dmg}");

                // 1) Direct Hurt (this actually changes HP)
                if (bce != null && !bce.IsDestroyed)
                {
                    float before = bce.Health();
                    bce.Hurt(dmg, _damageType, attacker);
                    float after = bce.Health();
                    PrintWarning($"[RayGun] Hurt: {bce.ShortPrefabName} HP {before} -> {after}");
                }
                else
                {
                    PrintWarning($"[RayGun] Target is not BaseCombatEntity: {entity.ShortPrefabName} ({entity.GetType().Name})");
                }

                // 2) Build HitInfo and call OnAttacked
                var hitInfo = new HitInfo(attacker, entity, _damageType, dmg)
                {
                    Weapon = held,
                    HitMaterial = StringPool.Get("Flesh"), // general default; Rust doesn’t enforce this hard
                    DoHitEffects = true
                };

                hitInfo.HitPositionWorld = hitPoint;
                hitInfo.HitNormalWorld = hitNormal;
                hitInfo.PointStart = attacker.eyes != null ? attacker.eyes.position : attacker.transform.position;
                hitInfo.ProjectileID = 0;
                hitInfo.ProjectileDistance = Vector3.Distance(hitInfo.PointStart, hitPoint);
                hitInfo.ProjectileVelocity = (hitPoint - hitInfo.PointStart).normalized * 250f;

                // Give it a reasonable bone/area so head/body plugins see it like a normal hit
                if (bce != null)
                {
                    hitInfo.boneArea = HitArea.Torso;
                    hitInfo.HitBone = StringPool.Get("spine1");
                }

                entity.OnAttacked(hitInfo);

                if (bce != null)
                {
                    PrintWarning($"[RayGun] Hurt+OnAttacked: {bce.ShortPrefabName}, health now {bce.Health()}");
                }
                else
                {
                    PrintWarning($"[RayGun] Hurt+OnAttacked: non-combat {entity.ShortPrefabName}");
                }

                // didDamage only true if we hit something damageable
                return bce != null;
            }
            catch (Exception e)
            {
                PrintWarning($"[RayGun] Failed to apply damage to {entity.ShortPrefabName}: {e.Message}");
                return false;
            }
        }

        #endregion

        #region FX

        private void PlayMuzzleFx(Vector3 position, Vector3 forward)
        {
            if (string.IsNullOrEmpty(_config.MuzzleFxPrefab)) return;

            try
            {
                Effect.server.Run(_config.MuzzleFxPrefab, position, forward);
            }
            catch (Exception e)
            {
                PrintWarning($"[RayGun] Failed to play Muzzle FX '{_config.MuzzleFxPrefab}': {e.Message}");
            }
        }

        private void PlayTracerFx(Vector3 origin, Vector3 hitPoint)
        {
            if (string.IsNullOrEmpty(_config.TracerFxPrefab)) return;

            try
            {
                Vector3 direction = (hitPoint - origin).normalized;
                Effect.server.Run(_config.TracerFxPrefab, origin, direction);
            }
            catch (Exception e)
            {
                PrintWarning($"[RayGun] Failed to play Tracer FX '{_config.TracerFxPrefab}': {e.Message}");
            }
        }

        private void PlayImpactFx(Vector3 position, Vector3 normal)
        {
            if (string.IsNullOrEmpty(_config.ImpactFxPrefab)) return;

            try
            {
                Vector3 forward = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
                Effect.server.Run(_config.ImpactFxPrefab, position, forward);
            }
            catch (Exception e)
            {
                PrintWarning($"[RayGun] Failed to play Impact FX '{_config.ImpactFxPrefab}': {e.Message}");
            }
        }

        private void PlayHitmarker(BasePlayer attacker)
        {
            if (!_config.HitmarkerEnabled) return;
            if (attacker == null) return;
            if (string.IsNullOrEmpty(_config.HitmarkerSound)) return;

            try
            {
                var conn = attacker.net?.connection;
                if (conn == null) return;

                Vector3 pos = attacker.eyes != null ? attacker.eyes.position : attacker.transform.position;
                Effect.server.Run(_config.HitmarkerSound, pos, Vector3.up, conn);
            }
            catch (Exception e)
            {
                PrintWarning($"[RayGun] Failed to play hitmarker sound '{_config.HitmarkerSound}': {e.Message}");
            }
        }

        #endregion

        #region Commands

        [ChatCommand("raygun.toggle")]
        private void CmdRayGunToggle(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
                return;

            _config.Enabled = !_config.Enabled;
            SaveConfig();

            player.ChatMessage($"RayGun is now {(_config.Enabled ? "ENABLED" : "DISABLED")}.");
        }

        [ChatCommand("raygun.info")]
        private void CmdRayGunInfo(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            string permission = string.IsNullOrEmpty(_config.PermissionName)
                ? "NONE (everyone)"
                : _config.PermissionName;

            string hitmarkerState = _config.HitmarkerEnabled ? "ON" : "OFF";
            string hitmarkerSound = string.IsNullOrEmpty(_config.HitmarkerSound)
                ? "(none / disabled)"
                : $"'{_config.HitmarkerSound}'";

            player.ChatMessage(
                "RayGun Info:\n" +
                $"- Enabled: {(_config.Enabled ? "YES" : "NO")}\n" +
                $"- Item: '{_config.ItemShortname}'\n" +
                $"- Range: {_config.MaxRange}\n" +
                $"- Damage: {_config.Damage}\n" +
                $"- FireRate: {_config.FireRate} shots/sec\n" +
                $"- Permission: {permission}\n" +
                $"- Hitmarker: {hitmarkerState} (sound: {hitmarkerSound})\n" +
                $"- MuzzleFx: '{_config.MuzzleFxPrefab}'\n" +
                $"- TracerFx: '{_config.TracerFxPrefab}'\n" +
                $"- ImpactFx: '{_config.ImpactFxPrefab}'"
            );
        }

        #endregion
    }
}