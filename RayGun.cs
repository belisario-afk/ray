using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RayGun", "YourNameHere", "3.0.5")]
    [Description("Water pistol RayGun: hitscan damage via Hurt + HitInfo/OnAttacked, with FX and optional hitmarker.")]
    public class RayGun : RustPlugin
    {
        #region Configuration

        private RayGunConfig _config;

        public class RayGunConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            // Which item acts as the RayGun
            [JsonProperty("ItemShortname")]
            public string ItemShortname = "pistol.water";

            // If empty, all skins for that item are allowed.
            [JsonProperty("AllowedSkins")]
            public List<ulong> AllowedSkins = new List<ulong>();

            // RayGun behaviour
            [JsonProperty("MaxRange")]
            public float MaxRange = 100f;      // how far the ray goes
            [JsonProperty("Damage")]
            public float Damage = 25f;         // damage per hit
            [JsonProperty("FireRate")]
            public float FireRate = 8f;        // shots per second per player

            // EMPTY = no permission required (everyone can use)
            [JsonProperty("PermissionName")]
            public string PermissionName = "";

            // FX paths (your requested ones)
            [JsonProperty("MuzzleFxPrefab")]
            public string MuzzleFxPrefab = "assets/content/effects/muzzleflashes/other/muzzle_flash_silencer_oilfilter.prefab";
            [JsonProperty("TracerFxPrefab")]
            public string TracerFxPrefab = "assets/prefabs/weapons/eoka pistol/effects/flint_spark.prefab";
            [JsonProperty("ImpactFxPrefab")]
            public string ImpactFxPrefab = "assets/prefabs/weapons/eoka pistol/effects/flint_spark.prefab";

            // Hitmarker settings
            [JsonProperty("HitmarkerEnabled")]
            public bool HitmarkerEnabled = false;

            // Leave empty by default to avoid StringPool errors.
            // Set to a valid sound prefab path on your build to enable audio hitmarkers.
            [JsonProperty("HitmarkerSound")]
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
        private const Rust.DamageType DamageType = Rust.DamageType.Bullet;

        // Cached StringPool values to avoid repeated lookups
        private uint _fleshMaterialId;
        private uint _spineBoneId;

        // Fallback muzzle offset constants (used when weapon muzzle point is unavailable)
        private const float MuzzleOffsetForward = 0.5f;
        private const float MuzzleOffsetDown = 0.15f;
        private const float MuzzleOffsetRight = 0.1f;

        #endregion

        #region Hooks

        private void Init()
        {
            SetupPermission();

            if (_config.MaxRange <= 0f) _config.MaxRange = 100f;
            if (_config.FireRate <= 0f) _config.FireRate = 5f;
        }

        private void OnServerInitialized()
        {
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
                "Player (Server)"
            );

            // Cache StringPool values after server is initialized
            _fleshMaterialId = StringPool.Get("Flesh");
            _spineBoneId = StringPool.Get("spine1");

            Puts("[RayGun] Loaded. Hold pistol.water and fire – hitscan RayGun using Hurt + HitInfo/OnAttacked.");
        }

        private void Unload()
        {
            _lastShotTime.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
                _lastShotTime.Remove(player.userID);
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

            // Get eye position for raycast origin (accurate aiming)
            Vector3 eyePos = attacker.eyes != null
                ? attacker.eyes.position
                : attacker.transform.position;

            Vector3 forward = attacker.eyes != null
                ? attacker.eyes.BodyForward()
                : attacker.transform.forward;

            // Calculate muzzle position from the held weapon for visual effects
            Vector3 muzzlePos = GetMuzzlePosition(attacker, eyePos, forward);

            RaycastHit hit;
            BaseEntity hitEntity = null;
            Vector3 hitPoint = eyePos + forward * _config.MaxRange;
            Vector3 hitNormal = -forward;

            bool didHit = Physics.Raycast(eyePos, forward, out hit, _config.MaxRange, _rayMask,
                QueryTriggerInteraction.Ignore);

            if (didHit)
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
                hitEntity = hit.GetEntity();
            }

            bool didDamage = false;

            if (didHit && hitEntity != null)
            {
                didDamage = ApplyDamageViaHitInfo(hitEntity, attacker, hitPoint, hitNormal);
            }

            // Use muzzle position for visual effects so they appear at the gun barrel
            PlayMuzzleFx(muzzlePos, forward);
            PlayTracerFx(muzzlePos, hitPoint);
            if (didHit)
                PlayImpactFx(hitPoint, hitNormal);

            if (didDamage)
                PlayHitmarker(attacker);
        }

        /// <summary>
        /// Gets the muzzle position from the player's held weapon for visual effects.
        /// Falls back to a position in front of the player if weapon muzzle can't be found.
        /// </summary>
        private Vector3 GetMuzzlePosition(BasePlayer player, Vector3 eyePos, Vector3 forward)
        {
            var heldEntity = player.GetActiveItem()?.GetHeldEntity();
            
            // Try to get the muzzle point from BaseProjectile weapons (includes water pistol)
            // MuzzlePoint may be null for some weapon types or configurations
            if (heldEntity is BaseProjectile projectile && projectile.MuzzlePoint != null)
            {
                return projectile.MuzzlePoint.position;
            }

            // Fallback: position slightly in front and below eye level (approximate gun position)
            // This places effects roughly where a held pistol would be
            Vector3 right = player.eyes != null ? player.eyes.BodyRight() : player.transform.right;
            return eyePos + forward * MuzzleOffsetForward - Vector3.up * MuzzleOffsetDown + right * MuzzleOffsetRight;
        }

        /// <summary>
        /// Apply damage using BaseCombatEntity.Hurt (like other hitscan weapons),
        /// and also send a HitInfo to OnAttacked so Rust + plugins see a normal hit.
        /// </summary>
        private bool ApplyDamageViaHitInfo(BaseEntity entity, BasePlayer attacker, Vector3 hitPoint, Vector3 hitNormal)
        {
            if (entity == null || entity.IsDestroyed || attacker == null) return false;

            float dmg = Mathf.Max(0f, _config.Damage);
            if (dmg <= 0f) return false;

            try
            {
                var held = attacker.GetActiveItem()?.GetHeldEntity() as AttackEntity;
                var bce = entity as BaseCombatEntity;

                // 1) Direct Hurt (this actually changes HP)
                if (bce != null && !bce.IsDestroyed)
                {
                    bce.Hurt(dmg, DamageType, attacker);
                }

                // 2) Build HitInfo and call OnAttacked
                Vector3 pointStart = attacker.eyes != null ? attacker.eyes.position : attacker.transform.position;
                Vector3 hitDirection = (hitPoint - pointStart);
                float projectileDistance = hitDirection.magnitude;
                var hitInfo = new HitInfo(attacker, entity, DamageType, dmg)
                {
                    Weapon = held,
                    HitMaterial = _fleshMaterialId,
                    DoHitEffects = true,
                    HitPositionWorld = hitPoint,
                    HitNormalWorld = hitNormal,
                    PointStart = pointStart,
                    ProjectileID = 0,
                    ProjectileDistance = projectileDistance,
                    ProjectileVelocity = (projectileDistance > 0.001f ? hitDirection / projectileDistance : Vector3.forward) * 250f
                };

                // Give it a reasonable bone so head/body plugins see it like a normal hit
                if (bce != null)
                {
                    hitInfo.HitBone = _spineBoneId;
                }

                entity.OnAttacked(hitInfo);

                // didDamage only true if we hit something damageable
                return bce != null;
            }
            catch (Exception e)
            {
                PrintError($"[RayGun] Failed to apply damage to {entity.ShortPrefabName}: {e.Message}");
                return false;
            }
        }

        #endregion

        #region FX

        private void PlayMuzzleFx(Vector3 position, Vector3 forward)
        {
            if (string.IsNullOrEmpty(_config.MuzzleFxPrefab)) return;

            Effect.server.Run(_config.MuzzleFxPrefab, position, forward);
        }

        private void PlayTracerFx(Vector3 origin, Vector3 hitPoint)
        {
            if (string.IsNullOrEmpty(_config.TracerFxPrefab)) return;

            Vector3 direction = (hitPoint - origin).normalized;
            Effect.server.Run(_config.TracerFxPrefab, origin, direction);
        }

        private void PlayImpactFx(Vector3 position, Vector3 normal)
        {
            if (string.IsNullOrEmpty(_config.ImpactFxPrefab)) return;

            Vector3 forward = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
            Effect.server.Run(_config.ImpactFxPrefab, position, forward);
        }

        private void PlayHitmarker(BasePlayer attacker)
        {
            if (!_config.HitmarkerEnabled || attacker == null || string.IsNullOrEmpty(_config.HitmarkerSound))
                return;

            var conn = attacker.net?.connection;
            if (conn == null) return;

            Vector3 pos = attacker.eyes != null ? attacker.eyes.position : attacker.transform.position;
            Effect.server.Run(_config.HitmarkerSound, pos, Vector3.up, conn);
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