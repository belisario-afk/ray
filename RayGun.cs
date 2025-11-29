using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RayGun", "YourNameHere", "3.0.7")]
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
            [JsonProperty("ImpactFxPrefab")]
            public string ImpactFxPrefab = "assets/prefabs/weapons/eoka pistol/effects/flint_spark.prefab";

            // Beam/Tracer settings - colored ray gun beam line
            [JsonProperty("BeamEnabled")]
            public bool BeamEnabled = true;
            [JsonProperty("BeamColorR")]
            public float BeamColorR = 0f;        // Red component (0-1)
            [JsonProperty("BeamColorG")]
            public float BeamColorG = 1f;        // Green component (0-1)
            [JsonProperty("BeamColorB")]
            public float BeamColorB = 0f;        // Blue component (0-1)
            [JsonProperty("BeamDuration")]
            public float BeamDuration = 0.15f;   // How long the beam is visible (seconds)

            // Projectile ring effect settings - traveling visual projectile
            [JsonProperty("ProjectileEnabled")]
            public bool ProjectileEnabled = true;
            [JsonProperty("ProjectilePrefab")]
            public string ProjectilePrefab = "assets/prefabs/weapons/toolgun/effects/ringeffect_realistic.prefab";
            [JsonProperty("ProjectileSpeed")]
            public float ProjectileSpeed = 150f;  // Speed of the traveling projectile (m/s)

            // Muzzle position offset adjustments (for fine-tuning where effects originate)
            [JsonProperty("MuzzleOffsetX")]
            public float MuzzleOffsetX = 0f;     // Right/Left offset
            [JsonProperty("MuzzleOffsetY")]
            public float MuzzleOffsetY = 0f;     // Up/Down offset
            [JsonProperty("MuzzleOffsetZ")]
            public float MuzzleOffsetZ = 0f;     // Forward/Back offset

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

        // Effect interval for projectile spawning (seconds between effect spawns)
        private const float EffectIntervalSeconds = 0.05f;

        // Offset step size for D-pad style adjustment commands
        private const float OffsetStepSize = 0.05f;

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
            PlayBeamTracer(muzzlePos, hitPoint);
            PlayProjectileEffect(attacker, muzzlePos, hitPoint);
            if (didHit)
                PlayImpactFx(hitPoint, hitNormal);

            if (didDamage)
                PlayHitmarker(attacker);
        }

        /// <summary>
        /// Gets the muzzle position from the player's held weapon for visual effects.
        /// Falls back to a position in front of the player if weapon muzzle can't be found.
        /// Applies configurable offset adjustments for fine-tuning.
        /// </summary>
        private Vector3 GetMuzzlePosition(BasePlayer player, Vector3 eyePos, Vector3 forward)
        {
            Vector3 basePos;
            var heldEntity = player.GetActiveItem()?.GetHeldEntity();
            
            // Try to get the muzzle point from BaseProjectile weapons (includes water pistol)
            // MuzzlePoint may be null for some weapon types or configurations
            if (heldEntity is BaseProjectile projectile && projectile.MuzzlePoint != null)
            {
                basePos = projectile.MuzzlePoint.position;
            }
            else
            {
                // Fallback: position slightly in front and below eye level (approximate gun position)
                // This places effects roughly where a held pistol would be
                Vector3 right = player.eyes != null ? player.eyes.BodyRight() : player.transform.right;
                basePos = eyePos + forward * MuzzleOffsetForward - Vector3.up * MuzzleOffsetDown + right * MuzzleOffsetRight;
            }

            // Apply configurable offset adjustments
            Vector3 right2 = player.eyes != null ? player.eyes.BodyRight() : player.transform.right;
            Vector3 up = Vector3.up;
            
            basePos += right2 * _config.MuzzleOffsetX;
            basePos += up * _config.MuzzleOffsetY;
            basePos += forward * _config.MuzzleOffsetZ;

            return basePos;
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

        /// <summary>
        /// Draws a visible colored beam line from origin to hitPoint using DDraw.
        /// This creates the ray gun laser beam effect visible to all nearby players.
        /// </summary>
        private void PlayBeamTracer(Vector3 origin, Vector3 hitPoint)
        {
            if (!_config.BeamEnabled) return;

            Color beamColor = new Color(
                Mathf.Clamp01(_config.BeamColorR),
                Mathf.Clamp01(_config.BeamColorG),
                Mathf.Clamp01(_config.BeamColorB),
                1f
            );

            float duration = Mathf.Max(0.05f, _config.BeamDuration);

            // Draw the beam line visible to all players
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.net?.connection == null) continue;

                // Only send to players within reasonable distance to optimize network traffic
                float distSqr = (player.transform.position - origin).sqrMagnitude;
                if (distSqr > 22500f) continue; // 150m radius

                player.SendConsoleCommand("ddraw.line", duration, beamColor, origin, hitPoint);
            }
        }

        /// <summary>
        /// Creates a traveling projectile effect using the ring effect prefab.
        /// The effect travels from origin to hitPoint at the configured speed.
        /// </summary>
        private void PlayProjectileEffect(BasePlayer attacker, Vector3 origin, Vector3 hitPoint)
        {
            if (!_config.ProjectileEnabled || string.IsNullOrEmpty(_config.ProjectilePrefab)) return;

            Vector3 direction = (hitPoint - origin);
            float distance = direction.magnitude;
            if (distance < 0.1f) return;

            direction = direction.normalized;
            float speed = Mathf.Max(50f, _config.ProjectileSpeed);
            float travelTime = distance / speed;
            int steps = Mathf.Max(1, Mathf.CeilToInt(travelTime / EffectIntervalSeconds)); // Effect every 50ms

            // Spawn effects along the path with delays to create traveling appearance
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector3 pos = Vector3.Lerp(origin, hitPoint, t);
                float delay = t * travelTime;

                timer.Once(delay, () =>
                {
                    if (attacker == null || attacker.IsDestroyed) return;
                    Effect.server.Run(_config.ProjectilePrefab, pos, direction);
                });
            }
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

            string beamInfo = _config.BeamEnabled 
                ? $"ON (R:{_config.BeamColorR:F1} G:{_config.BeamColorG:F1} B:{_config.BeamColorB:F1}, {_config.BeamDuration:F2}s)"
                : "OFF";

            string projectileInfo = _config.ProjectileEnabled
                ? $"ON (Speed: {_config.ProjectileSpeed:F0})"
                : "OFF";

            string offsetInfo = $"X:{_config.MuzzleOffsetX:F2} Y:{_config.MuzzleOffsetY:F2} Z:{_config.MuzzleOffsetZ:F2}";

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
                $"- Beam: {beamInfo}\n" +
                $"- Projectile: {projectileInfo}\n" +
                $"- MuzzleOffset: {offsetInfo}\n" +
                $"- ImpactFx: '{_config.ImpactFxPrefab}'"
            );
        }

        [ChatCommand("raygun.offset")]
        private void CmdRayGunOffset(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
            {
                player?.ChatMessage("Admin only command.");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage(
                    "RayGun Muzzle Offset Adjustment:\n" +
                    $"Current: X:{_config.MuzzleOffsetX:F2} Y:{_config.MuzzleOffsetY:F2} Z:{_config.MuzzleOffsetZ:F2}\n" +
                    "Commands:\n" +
                    "  /raygun.offset x <value> - Set X offset (right/left)\n" +
                    "  /raygun.offset y <value> - Set Y offset (up/down)\n" +
                    "  /raygun.offset z <value> - Set Z offset (forward/back)\n" +
                    "  /raygun.offset reset - Reset all offsets to 0\n" +
                    "D-Pad Style:\n" +
                    "  /raygun.offset left - Move X -0.05\n" +
                    "  /raygun.offset right - Move X +0.05\n" +
                    "  /raygun.offset up - Move Y +0.05\n" +
                    "  /raygun.offset down - Move Y -0.05\n" +
                    "  /raygun.offset forward - Move Z +0.05\n" +
                    "  /raygun.offset back - Move Z -0.05"
                );
                return;
            }

            string action = args[0].ToLower();
            float step = OffsetStepSize;

            switch (action)
            {
                case "x":
                    if (args.Length >= 2 && float.TryParse(args[1], out float xVal))
                    {
                        _config.MuzzleOffsetX = xVal;
                        SaveConfig();
                        player.ChatMessage($"Muzzle X offset set to {_config.MuzzleOffsetX:F2}");
                    }
                    else
                        player.ChatMessage("Usage: /raygun.offset x <value>");
                    break;

                case "y":
                    if (args.Length >= 2 && float.TryParse(args[1], out float yVal))
                    {
                        _config.MuzzleOffsetY = yVal;
                        SaveConfig();
                        player.ChatMessage($"Muzzle Y offset set to {_config.MuzzleOffsetY:F2}");
                    }
                    else
                        player.ChatMessage("Usage: /raygun.offset y <value>");
                    break;

                case "z":
                    if (args.Length >= 2 && float.TryParse(args[1], out float zVal))
                    {
                        _config.MuzzleOffsetZ = zVal;
                        SaveConfig();
                        player.ChatMessage($"Muzzle Z offset set to {_config.MuzzleOffsetZ:F2}");
                    }
                    else
                        player.ChatMessage("Usage: /raygun.offset z <value>");
                    break;

                case "reset":
                    _config.MuzzleOffsetX = 0f;
                    _config.MuzzleOffsetY = 0f;
                    _config.MuzzleOffsetZ = 0f;
                    SaveConfig();
                    player.ChatMessage("Muzzle offsets reset to 0.");
                    break;

                // D-Pad style adjustments
                case "left":
                    _config.MuzzleOffsetX -= step;
                    SaveConfig();
                    player.ChatMessage($"X: {_config.MuzzleOffsetX:F2} (moved left)");
                    break;

                case "right":
                    _config.MuzzleOffsetX += step;
                    SaveConfig();
                    player.ChatMessage($"X: {_config.MuzzleOffsetX:F2} (moved right)");
                    break;

                case "up":
                    _config.MuzzleOffsetY += step;
                    SaveConfig();
                    player.ChatMessage($"Y: {_config.MuzzleOffsetY:F2} (moved up)");
                    break;

                case "down":
                    _config.MuzzleOffsetY -= step;
                    SaveConfig();
                    player.ChatMessage($"Y: {_config.MuzzleOffsetY:F2} (moved down)");
                    break;

                case "forward":
                    _config.MuzzleOffsetZ += step;
                    SaveConfig();
                    player.ChatMessage($"Z: {_config.MuzzleOffsetZ:F2} (moved forward)");
                    break;

                case "back":
                    _config.MuzzleOffsetZ -= step;
                    SaveConfig();
                    player.ChatMessage($"Z: {_config.MuzzleOffsetZ:F2} (moved back)");
                    break;

                default:
                    player.ChatMessage($"Unknown action '{action}'. Use /raygun.offset for help.");
                    break;
            }
        }

        [ChatCommand("raygun.beam")]
        private void CmdRayGunBeam(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
            {
                player?.ChatMessage("Admin only command.");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage(
                    "RayGun Beam Settings:\n" +
                    $"Enabled: {_config.BeamEnabled}\n" +
                    $"Color: R:{_config.BeamColorR:F1} G:{_config.BeamColorG:F1} B:{_config.BeamColorB:F1}\n" +
                    $"Duration: {_config.BeamDuration:F2}s\n" +
                    "Commands:\n" +
                    "  /raygun.beam toggle - Toggle beam on/off\n" +
                    "  /raygun.beam color <r> <g> <b> - Set RGB color (0-1)\n" +
                    "  /raygun.beam duration <seconds> - Set duration"
                );
                return;
            }

            string action = args[0].ToLower();

            switch (action)
            {
                case "toggle":
                    _config.BeamEnabled = !_config.BeamEnabled;
                    SaveConfig();
                    player.ChatMessage($"Beam is now {(_config.BeamEnabled ? "ENABLED" : "DISABLED")}");
                    break;

                case "color":
                    if (args.Length >= 4 &&
                        float.TryParse(args[1], out float r) &&
                        float.TryParse(args[2], out float g) &&
                        float.TryParse(args[3], out float b))
                    {
                        _config.BeamColorR = Mathf.Clamp01(r);
                        _config.BeamColorG = Mathf.Clamp01(g);
                        _config.BeamColorB = Mathf.Clamp01(b);
                        SaveConfig();
                        player.ChatMessage($"Beam color set to R:{_config.BeamColorR:F1} G:{_config.BeamColorG:F1} B:{_config.BeamColorB:F1}");
                    }
                    else
                        player.ChatMessage("Usage: /raygun.beam color <r> <g> <b> (values 0-1)");
                    break;

                case "duration":
                    if (args.Length >= 2 && float.TryParse(args[1], out float dur))
                    {
                        _config.BeamDuration = Mathf.Max(0.05f, dur);
                        SaveConfig();
                        player.ChatMessage($"Beam duration set to {_config.BeamDuration:F2}s");
                    }
                    else
                        player.ChatMessage("Usage: /raygun.beam duration <seconds>");
                    break;

                default:
                    player.ChatMessage($"Unknown action '{action}'. Use /raygun.beam for help.");
                    break;
            }
        }

        [ChatCommand("raygun.projectile")]
        private void CmdRayGunProjectile(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
            {
                player?.ChatMessage("Admin only command.");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage(
                    "RayGun Projectile Settings:\n" +
                    $"Enabled: {_config.ProjectileEnabled}\n" +
                    $"Speed: {_config.ProjectileSpeed:F0} m/s\n" +
                    $"Prefab: '{_config.ProjectilePrefab}'\n" +
                    "Commands:\n" +
                    "  /raygun.projectile toggle - Toggle projectile effect on/off\n" +
                    "  /raygun.projectile speed <value> - Set projectile speed (m/s)"
                );
                return;
            }

            string action = args[0].ToLower();

            switch (action)
            {
                case "toggle":
                    _config.ProjectileEnabled = !_config.ProjectileEnabled;
                    SaveConfig();
                    player.ChatMessage($"Projectile effect is now {(_config.ProjectileEnabled ? "ENABLED" : "DISABLED")}");
                    break;

                case "speed":
                    if (args.Length >= 2 && float.TryParse(args[1], out float spd))
                    {
                        _config.ProjectileSpeed = Mathf.Max(50f, spd);
                        SaveConfig();
                        player.ChatMessage($"Projectile speed set to {_config.ProjectileSpeed:F0} m/s");
                    }
                    else
                        player.ChatMessage("Usage: /raygun.projectile speed <value>");
                    break;

                default:
                    player.ChatMessage($"Unknown action '{action}'. Use /raygun.projectile for help.");
                    break;
            }
        }

        #endregion
    }
}