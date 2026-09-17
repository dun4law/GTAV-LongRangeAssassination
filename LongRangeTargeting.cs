using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LongRangeSupport;

[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.1")]

// Additional lock channel for the vanilla Multi Target Assassination.
// The pilot fires normally; only their newly launched rocket projectiles are assigned.
public sealed class LongRangeTargeting : Script
{
    readonly string folder;
    bool logWarningShown;
    int nextShipDiagnostic;
    Keys toggleKey = Keys.Control | Keys.F10, lockKey = Keys.Control | Keys.L, streamKey = Keys.Control | Keys.F8;
    readonly HashSet<Keys> pressedKeys = new HashSet<Keys>();
    DateTime uiSettingsModified = DateTime.MinValue, nextSettingsCheck = DateTime.MinValue;
    bool showStatusText, showDistance, showTargetBox = true, showBottomMessages = true;
    Color textColor = Color.FromArgb(235, 255, 255, 255);
    Color bottomColor = Color.FromArgb(235, 255, 220, 80);
    int messageDuration = 1800, messageUntil;
    string bottomMessage = "";
    readonly MissionPatch patch;
    readonly Dictionary<int, RocketIdentity> observed = new Dictionary<int, RocketIdentity>();
    readonly List<LodSnapshot> lods = new List<LodSnapshot>();
    readonly int[] models = { unchecked((int)StringHash.AtStringHash("u_m_y_babyd")), unchecked((int)StringHash.AtStringHash("a_m_m_beach_01")), unchecked((int)StringHash.AtStringHash("s_m_y_winclean_01")), unchecked((int)StringHash.AtStringHash("u_m_y_sbike")) };
    readonly Vector3[] markers = {
        new Vector3(-1183.12f, -1523.178f, 3.364f), new Vector3(-2172.901f, -395.7628f, 12.349f),
        new Vector3(-662.1583f, 255.0944f, 80.40974f), new Vector3(-1408.17f, 742.0872f, 182.0377f) };
    readonly Vector3[] spawnAreas = {
        new Vector3(-1205.392f, -1548.022f, 3.323f), new Vector3(-2211.742f, -587.6183f, 0f),
        new Vector3(-642.6985f, 305.823f, 111.5572f), new Vector3(-1406.767f, 738.4614f, 182.4823f) };
    bool enabled, mission, locked, ownsFocus, streaming = true, preventEscape = true;
    bool applyAttempted;
    bool ownsScene, sceneReady;
    int sceneStarted;
    int nextScan, nextLog, focusReadyAt, routed = -1, assigned;
    float range = 5000f, lockRange = 5000f;
    bool autoLock;
    bool playerPriority = true, remoteReleased;
    int focusStage = -2, remoteDeadline;
    Ped target;
    IntPtr targetAddress;
    Vector3 focusPosition;
    bool hasFocusPosition;
    string status = "OFF", lastTarget = "";
    Ped outcomeTarget;
    IntPtr outcomeAddress;
    int outcomeHealth;
    bool outcomeDead;

    sealed class RocketIdentity
    {
        public IntPtr Address;
        public int Weapon;
        public Entity AssignedTarget;
        public Projectile Projectile;
        public IntPtr AssignedAddress;
        public int Started;
        public float LastDistance = -1f;
    }
    sealed class LodSnapshot
    {
        public Entity Entity;
        public IntPtr Address;
        public int Original;
    }

    public LongRangeTargeting()
    {
        folder = BaseDirectory;
        patch = new MissionPatch(Log);
        Tick += OnTick; KeyDown += OnKeyDown;
        KeyUp += delegate(object sender, KeyEventArgs e) { pressedKeys.Remove(e.KeyCode); };
        Aborted += delegate { Shutdown(); };
        Interval = 0;
        RefreshUiSettings(true);
        Log("Loaded v0.1. Ship diagnostics and bilingual documentation. Game=" + Game.FileVersion + "; scripts=" + folder);
    }

    void RefreshUiSettings(bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now < nextSettingsCheck) return;
        nextSettingsCheck = now.AddSeconds(1);
        string filename = Path.Combine(folder, "LongRangeTargeting.ini");
        DateTime modified = File.GetLastWriteTimeUtc(filename);
        if (!force && modified == uiSettingsModified) return;
        ScriptSettings config = ScriptSettings.Load(filename);
        Keys toggle = ReadHotkey(config, "Toggle", Keys.Control | Keys.F10);
        Keys targetLock = ReadHotkey(config, "Lock", Keys.Control | Keys.L);
        Keys stream = ReadHotkey(config, "Streaming", Keys.Control | Keys.F8);
        if (toggle == targetLock || toggle == stream || targetLock == stream)
            Log("Duplicate hotkeys: retaining previous bindings.");
        else { toggleKey = toggle; lockKey = targetLock; streamKey = stream; }
        showStatusText = config.GetValue<bool>("Display", "ShowStatusText", false);
        showDistance = config.GetValue<bool>("Display", "ShowDistance", false);
        showTargetBox = config.GetValue<bool>("Display", "ShowTargetBox", true);
        showBottomMessages = config.GetValue<bool>("Display", "ShowBottomMessages", true);
        messageDuration = Math.Max(300, Math.Min(10000, config.GetValue<int>("Display", "MessageDurationMs", 1800)));
        textColor = Color.FromArgb(ReadChannel(config, "TextAlpha", 235), ReadChannel(config, "TextRed", 255), ReadChannel(config, "TextGreen", 255), ReadChannel(config, "TextBlue", 255));
        string bottomPreset = config.GetValue<string>("Display", "BottomTextColor", "Yellow").Trim();
        int bottomAlpha = ReadChannel(config, "BottomTextAlpha", 235);
        if (string.Equals(bottomPreset, "Red", StringComparison.OrdinalIgnoreCase)) bottomColor = Color.FromArgb(bottomAlpha, 255, 60, 60);
        else if (string.Equals(bottomPreset, "Custom", StringComparison.OrdinalIgnoreCase))
            bottomColor = Color.FromArgb(bottomAlpha, ReadChannel(config, "BottomTextRed", 255), ReadChannel(config, "BottomTextGreen", 220), ReadChannel(config, "BottomTextBlue", 80));
        else bottomColor = Color.FromArgb(bottomAlpha, 255, 220, 80);
        uiSettingsModified = modified;
        Log("UI settings loaded: toggle=" + toggleKey + "; lock=" + lockKey + "; streaming=" + streamKey + "; status=" + showStatusText + "; distance=" + showDistance);
    }
    int ReadChannel(ScriptSettings config, string name, int fallback)
    {
        return Math.Max(0, Math.Min(255, config.GetValue<int>("Display", name, fallback)));
    }
    Keys ReadHotkey(ScriptSettings config, string name, Keys fallback)
    {
        string text = config.GetValue<string>("Hotkeys", name, "");
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        Keys binding;
        if (TryParseHotkey(text, out binding)) return binding;
        Log("Invalid hotkey " + name + "='" + text + "'; using " + fallback);
        return fallback;
    }
    public static bool TryParseHotkey(string text, out Keys binding)
    {
        binding = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool hasKey = false;
        foreach (string part in text.Split('+'))
        {
            string token = part.Trim();
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase)) binding |= Keys.Control;
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase)) binding |= Keys.Alt;
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase)) binding |= Keys.Shift;
            else
            {
                Keys key;
                if (hasKey || !Enum.TryParse<Keys>(token, true, out key) || !Enum.IsDefined(typeof(Keys), key)
                    || key == Keys.None || (key & Keys.Modifiers) != 0
                    || key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu
                    || key == Keys.LControlKey || key == Keys.RControlKey || key == Keys.LShiftKey || key == Keys.RShiftKey
                    || key == Keys.LMenu || key == Keys.RMenu) return false;
                binding |= key; hasKey = true;
            }
        }
        return hasKey;
    }

    void ReadSettings()
    {
        ScriptSettings config = ScriptSettings.Load(Path.Combine(folder, "LongRangeTargeting.ini"));
        range = config.GetValue<float>("General", "RangeMetres", 5000f);
        if (float.IsNaN(range) || float.IsInfinity(range)) range = 5000f;
        range = Math.Max(500f, Math.Min(10000f, range));
        lockRange = config.GetValue<float>("General", "LockRangeMetres", range);
        if (float.IsNaN(lockRange) || float.IsInfinity(lockRange)) lockRange = range;
        lockRange = Math.Max(500f, Math.Min(10000f, lockRange));
        autoLock = config.GetValue<bool>("General", "AutoLock", false);
        playerPriority = config.GetValue<bool>("General", "PlayerPriorityStreaming", true);
        Log("AutoLock=" + autoLock);
        streaming = config.GetValue<bool>("General", "RemoteStreaming", true);
        preventEscape = config.GetValue<bool>("General", "PreventDistanceEscape", true);
    }
    void Log(string message)
    {
        string entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine;
        try { File.AppendAllText(Path.Combine(folder, "LongRangeTargeting.log"), entry); }
        catch (Exception ex)
        {
            string fallback = Path.Combine(Path.GetTempPath(), "LongRangeTargeting.fallback.log");
            try { File.AppendAllText(fallback, "Primary log failed: " + ex.Message + Environment.NewLine + entry); }
            catch { System.Diagnostics.Trace.TraceError("LRT could not save its log: " + entry); }
            if (!logWarningShown)
            {
                logWarningShown = true;
                try { Notify("Log error"); } catch { }
            }
        }
    }
    void Notify(string text) { bottomMessage = text; messageUntil = Game.GameTime + messageDuration; }
    void DrawNotification()
    {
        if (showBottomMessages && Game.GameTime < messageUntil)
            DrawText(bottomMessage, .5f, .90f, .38f, true, bottomColor);
    }
    bool Online() { return Function.Call<bool>(Hash.NETWORK_IS_SESSION_STARTED); }
    bool ValidTarget()
    {
        return target != null && target.Exists() && target.MemoryAddress == targetAddress && !target.IsDead;
    }
    bool Pilot(out Vehicle vehicle)
    {
        Ped player = Game.Player.Character;
        vehicle = player.CurrentVehicle;
        return vehicle != null && vehicle.Exists() && vehicle.Driver == player
            && (vehicle.Model.IsPlane || vehicle.Model.IsBoat) && !player.IsDead;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!pressedKeys.Add(e.KeyCode)) return;
        try
        {
            RefreshUiSettings();
            if (e.KeyData == toggleKey)
            {
                if (enabled) { Shutdown(); Notify("OFF."); return; }
                if (Online()) { Notify("Story mode only"); return; }
                ReadSettings();
                enabled = true; nextScan = 0; nextLog = 0; applyAttempted = false; assigned = 0;
                target = null; locked = false; observed.Clear();
                foreach (Projectile p in World.GetAllProjectiles())
                    observed[p.Handle] = new RocketIdentity { Address = p.MemoryAddress, Weapon = (int)p.WeaponHash };
                status = "WAITING";
                Notify("ON");
                Log("Enabled; spawnRange=" + range + "; lockRange=" + lockRange + "; streaming=" + streaming + "; preventEscape=" + preventEscape);
            }
            else if (e.KeyData == lockKey && enabled)
            {
                if (autoLock) { Notify("Auto lock ON"); return; }
                if (locked) { locked = false; Notify("UNLOCKED"); return; }
                Vehicle v;
                if (!Pilot(out v)) { Notify("Plane/boat driver required"); return; }
                ScanTarget();
                if (!ValidTarget()) { Notify("No target"); return; }
                if (target.Position.DistanceTo(v.Position) > lockRange)
                {
                    Log("Lock rejected: distance=" + target.Position.DistanceTo(v.Position).ToString("0") + "; lockRange=" + lockRange);
                    Notify("Out of range"); return;
                }
                locked = true;
                Log("Locked actual mission ped handle=" + target.Handle + "; distance=" + target.Position.DistanceTo(v.Position).ToString("0")
                    + "; vehicle=" + v.Handle + "; type=" + (v.Model.IsBoat ? "boat" : "plane"));
                Notify("LOCKED");
            }
            else if (e.KeyData == streamKey && enabled)
            {
                streaming = !streaming;
                if (!streaming) ClearFocus();
                Notify("Streaming " + (streaming ? "ON" : "OFF"));
                Log("Remote streaming toggled: " + streaming);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    void OnTick(object sender, EventArgs e)
    {
        try
        {
            RefreshUiSettings();
            if (!Game.IsPaused && !Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE)) DrawNotification();
            if (!enabled) return;
            TrackOutcomes();
            if (Online()) { Shutdown(); return; }
            if (Game.IsPaused || Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE)) { ClearFocus(); return; }
            if (Game.Player.Character.IsDead) { locked = false; ClearFocus(); DrawStatus("DEAD"); return; }
            Vehicle pilotedPlane;
            bool isPilot = Pilot(out pilotedPlane);
            if (Game.GameTime >= nextScan)
            {
                nextScan = Game.GameTime + 250;
                bool running = Function.Call<int>(Hash.GET_NUMBER_OF_THREADS_RUNNING_THE_SCRIPT_WITH_THIS_HASH, PatchLayout.MissionHash) > 0;
                if (mission && !running)
                {
                    patch.Restore(); RestoreLods(); ClearFocus(); locked = false; target = null;
                    hasFocusPosition = false; routed = -1; applyAttempted = false;
                    Log("Mission ended; reset per-mission state.");
                }
                mission = running;
                if (mission)
                {
                    ScanTarget();
                    if (isPilot) { ResolveFocus(); UpdateStreamingPriority(); PrepareScene(); }
                    // Request the target scene before widening its spawn gate.
                    if (isPilot && (!patch.Applied || patch.AppliedStage != routed) && !applyAttempted && hasFocusPosition && routed >= 0 && (!streaming || (sceneReady && Game.GameTime >= focusReadyAt)))
                    {
                        applyAttempted = true;
                        if (!patch.Apply(range, preventEscape, routed)) applyAttempted = false;
                    }
                    if (streaming && hasFocusPosition && !remoteReleased)
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, focusPosition.X, focusPosition.Y, focusPosition.Z);
                    Vector3 playerPosition = Game.Player.Character.Position;
                    if (playerPriority)
                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, playerPosition.X, playerPosition.Y, playerPosition.Z);
                }
                if (Game.GameTime >= nextLog)
                {
                    nextLog = Game.GameTime + 10000;
                    Vehicle current = Game.Player.Character.CurrentVehicle;
                    if (current != null && current.Exists() && current.Model.IsBoat)
                        Log("Ship status: vehicle=" + current.Handle + "; model=" + current.Model.Hash
                            + "; driver=" + (current.Driver == Game.Player.Character) + "; locked=" + locked
                            + "; targetValid=" + ValidTarget() + "; assigned=" + assigned);
                    Log("Status: mission=" + mission + "; patched=" + patch.Applied + "; route=" + routed + "; target=" + (ValidTarget() ? target.Handle.ToString() : "missing") + "; locked=" + locked + "; assigned=" + assigned);
                }
            }
            if (!mission) { status = "WAITING"; DrawStatus(status); return; }
            if (!isPilot) { locked = false; ClearFocus(); DrawStatus("PLANE/BOAT DRIVER"); return; }
            if (streaming && hasFocusPosition && !remoteReleased)
            {
                Function.Call(Hash.SET_FOCUS_POS_AND_VEL, focusPosition.X, focusPosition.Y, focusPosition.Z, 0f, 0f, 0f);
                ownsFocus = true;
            }
            if (!ValidTarget()) locked = false;
            Vehicle plane;
            if (!Pilot(out plane)) locked = false;
            if (locked && target.Position.DistanceTo(plane.Position) > lockRange)
            {
                locked = false;
                Log("Lock lost: distance=" + target.Position.DistanceTo(plane.Position).ToString("0") + "; lockRange=" + lockRange);
                Notify("Out of range");
            }
            if (autoLock && !locked && ValidTarget() && Pilot(out plane)
                && target.Position.DistanceTo(plane.Position) <= lockRange)
            {
                locked = true;
                Notify("LOCKED");
                Log("Auto-locked mission ped=" + target.Handle + "; distance=" + target.Position.DistanceTo(plane.Position).ToString("0")
                    + "; vehicle=" + plane.Handle + "; type=" + (plane.Model.IsBoat ? "boat" : "plane"));
            }
            ProcessRockets(plane);
            status = !patch.Applied ? "LOADING" : !ValidTarget() ? "NO TARGET" : locked ? "LOCKED" : "READY";
            DrawStatus(status);
        }
        catch (Exception ex) { Fail(ex); }
    }

    void ScanTarget()
    {
        if (!mission) return;
        if (ValidTarget()) return;
        locked = false; target = null;
        foreach (Ped ped in World.GetAllPeds())
        {
            if (!ped.Exists() || ped.IsDead || Array.IndexOf(models, ped.Model.Hash) < 0) continue;
            if (!Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, ped.Handle)) continue;
            using (var scriptHash = new OutputArgument())
            {
                string owner = Function.Call<string>(Hash.GET_ENTITY_SCRIPT, ped.Handle, scriptHash);
                if (!string.Equals(owner, "assassin_multi", StringComparison.OrdinalIgnoreCase)) continue;
            }
            target = ped; targetAddress = ped.MemoryAddress;
            outcomeTarget = ped; outcomeAddress = ped.MemoryAddress;
            outcomeHealth = ped.Health; outcomeDead = false;
            RaiseLod(ped);
            Vehicle ride = ped.CurrentVehicle;
            if (ride != null && ride.Exists()) RaiseLod(ride);
            string identity = ped.Handle + ":" + ped.Model.Hash;
            if (identity != lastTarget) { Log("Found original mission target " + identity); lastTarget = identity; }
            break;
        }
    }

    void ResolveFocus()
    {
        if (ValidTarget())
        {
            int stage = Array.IndexOf(models, target.Model.Hash);
            if (routed != stage)
            {
                StopScene(); sceneReady = false; routed = stage;
                applyAttempted = false; focusReadyAt = Game.GameTime + 2500;
            }
            focusPosition = target.Position; hasFocusPosition = true;
            return;
        }
        // Match the mission's coordinate route marker to its known destination, not arbitrary waypoints.
        int candidate = -1; float best = 75f;
        foreach (Blip blip in World.GetAllBlips())
        {
            if (!blip.Exists() || Function.Call<int>(Hash.GET_BLIP_COLOUR, blip.Handle) != 1) continue;
            if (Function.Call<int>(Hash.GET_BLIP_INFO_ID_TYPE, blip.Handle) != 4) continue;
            for (int i = 0; i < markers.Length; i++)
            {
                float distance = blip.Position.DistanceTo(markers[i]);
                if (distance < best) { best = distance; candidate = i; }
            }
        }
        if (candidate >= 0)
        {
            if (candidate != routed)
            {
                StopScene(); sceneReady = false;
                routed = candidate; focusReadyAt = Game.GameTime + 2500; applyAttempted = false;
                Log("Remote scene selected for target " + (candidate + 1));
            }
            focusPosition = spawnAreas[candidate]; hasFocusPosition = true;
        }
    }

    void UpdateStreamingPriority()
    {
        if (!streaming || !hasFocusPosition) return;
        if (focusStage != routed)
        {
            focusStage = routed; remoteReleased = false;
            remoteDeadline = Game.GameTime + 8000;
            Log("Streaming stage=" + routed + "; playerPriority=" + playerPriority);
        }
        if (!playerPriority || remoteReleased) return;
        bool ready = ValidTarget();
        if (!ready && Game.GameTime < remoteDeadline) return;
        StopScene();
        if (ownsFocus) { Function.Call(Hash.CLEAR_FOCUS); ownsFocus = false; }
        remoteReleased = true;
        if (ready) sceneReady = true;
        Log("Player streaming restored: reason=" + (ready ? "target_ready" : "8s_budget") + "; stage=" + routed);
        if (!ready) Notify("Loading paused - toggle to retry");
    }

    void PrepareScene()
    {
        if (!streaming || !hasFocusPosition || sceneReady || remoteReleased) return;
        if (!ownsScene)
        {
            // The scene loader is global; wait if another script currently owns it.
            if (Function.Call<bool>(Hash.IS_NEW_LOAD_SCENE_ACTIVE)) return;
            ownsScene = Function.Call<bool>(Hash.NEW_LOAD_SCENE_START_SPHERE, focusPosition.X, focusPosition.Y, focusPosition.Z, 350f, 0);
            if (ownsScene) sceneStarted = Game.GameTime;
        }
        if (ownsScene && Function.Call<bool>(Hash.IS_NEW_LOAD_SCENE_LOADED))
        {
            StopScene(); sceneReady = true; Log("Target scene finished streaming.");
        }
        else if (ownsScene && Game.GameTime - sceneStarted > 20000)
            throw new IOException("Remote scene loading timed out; target was not force-spawned.");
    }
    void StopScene()
    {
        if (!ownsScene) return;
        Function.Call(Hash.NEW_LOAD_SCENE_STOP); ownsScene = false;
    }

    void RaiseLod(Entity entity)
    {
        foreach (var s in lods) if (s.Entity.Handle == entity.Handle && s.Address == entity.MemoryAddress) return;
        lods.Add(new LodSnapshot { Entity = entity, Address = entity.MemoryAddress, Original = entity.LodDistance });
        entity.LodDistance = (int)range;
    }
    void RestoreLods()
    {
        foreach (var s in lods)
            if (s.Entity.Exists() && s.Entity.MemoryAddress == s.Address) s.Entity.LodDistance = s.Original;
        lods.Clear();
    }

    void ShipDiagnostic(Vehicle vehicle, Projectile projectile, string reason)
    {
        if (vehicle == null || !vehicle.Exists() || !vehicle.Model.IsBoat || Game.GameTime < nextShipDiagnostic) return;
        nextShipDiagnostic = Game.GameTime + 2000;
        Entity owner = projectile.OwnerEntity;
        Log("Ship projectile skipped: reason=" + reason + "; projectile=" + projectile.Handle
            + "; weapon=" + projectile.WeaponHash + "; owner=" + (owner == null ? "none" : owner.Handle.ToString())
            + "; vehicle=" + vehicle.Handle + "; distance=" + projectile.Position.DistanceTo(vehicle.Position).ToString("0"));
    }

    void TrackOutcomes()
    {
        if (outcomeTarget != null)
        {
            if (!outcomeTarget.Exists() || outcomeTarget.MemoryAddress != outcomeAddress)
            {
                if (!outcomeDead) Log("Target unavailable: ped=" + outcomeTarget.Handle + "; death_not_confirmed");
                outcomeTarget = null;
            }
            else
            {
                int health = outcomeTarget.Health;
                if (health < outcomeHealth)
                    Log("Target damaged: ped=" + outcomeTarget.Handle + "; health=" + outcomeHealth + "->" + health
                        + "; missile_hit_not_confirmed");
                outcomeHealth = health;
                if (!outcomeDead && outcomeTarget.IsDead)
                {
                    outcomeDead = true;
                    uint cause = Function.Call<uint>(Hash.GET_PED_CAUSE_OF_DEATH, outcomeTarget.Handle);
                    int killer = Function.Call<int>(Hash.GET_PED_SOURCE_OF_DEATH, outcomeTarget.Handle);
                    Log("Target death confirmed: ped=" + outcomeTarget.Handle + "; causeWeapon=" + cause
                        + "; sourceEntity=" + killer + "; specific_missile_not_confirmed");
                }
            }
        }
        foreach (var item in observed)
        {
            RocketIdentity shot = item.Value;
            if (shot.Projectile == null) continue;
            if (!shot.Projectile.Exists() || shot.Projectile.MemoryAddress != shot.Address)
            {
                Log("Assigned rocket ended: projectile=" + item.Key + "; weapon=" + unchecked((uint)shot.Weapon)
                    + "; target=" + shot.AssignedTarget.Handle + "; elapsedMs=" + (Game.GameTime - shot.Started)
                    + "; lastObservedDistance=" + shot.LastDistance.ToString("0.0") + "; impact_not_confirmed");
                shot.Projectile = null;
            }
            else if (shot.AssignedTarget.Exists() && shot.AssignedTarget.MemoryAddress == shot.AssignedAddress)
                shot.LastDistance = shot.Projectile.Position.DistanceTo(shot.AssignedTarget.Position);
        }
    }

    void ProcessRockets(Vehicle vehicle)
    {
        var live = new HashSet<int>();
        foreach (Projectile projectile in World.GetAllProjectiles())
        {
            if (!projectile.Exists()) continue;
            live.Add(projectile.Handle);
            RocketIdentity identity;
            bool known = observed.TryGetValue(projectile.Handle, out identity)
                && identity.Address == projectile.MemoryAddress && identity.Weapon == (int)projectile.WeaponHash;
            if (known) continue; // Existing missiles are never retargeted when the player changes lock.
            identity = new RocketIdentity { Address = projectile.MemoryAddress, Weapon = (int)projectile.WeaponHash };
            observed[projectile.Handle] = identity;
            if (vehicle == null || !vehicle.Exists()) continue;
            if (!locked || !ValidTarget()) { ShipDiagnostic(vehicle, projectile, "not_locked_or_no_target"); continue; }
            Entity owner = projectile.OwnerEntity;
            if (owner == null || (owner.Handle != Game.Player.Character.Handle && owner.Handle != vehicle.Handle))
            { ShipDiagnostic(vehicle, projectile, "owner_mismatch"); continue; }
            // Large ship launchers may be far from the vehicle origin. Ownership is still mandatory.
            float launchRadius = vehicle.Model.IsBoat ? 500f : 150f;
            if (projectile.Position.DistanceTo(vehicle.Position) > launchRadius)
            { ShipDiagnostic(vehicle, projectile, "outside_launch_radius"); continue; }
            ProjectileRocket rocket = ProjectileRocket.FromHandle(projectile.Handle);
            if (rocket == null) { ShipDiagnostic(vehicle, projectile, "not_standard_rocket"); continue; }
            rocket.Target = target;
            rocket.IsAccurate = true;
            rocket.WasHoming = true;
            rocket.StopHoming = false;
            rocket.TimeBeforeHoming = 0f;
            rocket.TimeBeforeHomingAngleBreak = 60f;
            Entity confirmed = rocket.Target;
            if (confirmed == null || confirmed.Handle != target.Handle)
                throw new InvalidOperationException("SHVDN did not accept the rocket homing target. Check its runtime version.");
            identity.AssignedTarget = target;
            identity.AssignedAddress = targetAddress;
            identity.Projectile = projectile;
            identity.Started = Game.GameTime;
            identity.LastDistance = projectile.Position.DistanceTo(target.Position);
            assigned++;
            Log("Assigned fired rocket " + projectile.Handle + " weapon=" + projectile.WeaponHash + " to mission ped=" + target.Handle
                + "; vehicle=" + vehicle.Handle + "; type=" + (vehicle.Model.IsBoat ? "boat" : "plane"));
        }
        var expired = new List<int>();
        foreach (var item in observed) if (!live.Contains(item.Key)) expired.Add(item.Key);
        foreach (int id in expired) observed.Remove(id);
    }

    void DrawStatus(string message)
    {
        bool valid = ValidTarget();
        if (showStatusText)
        {
            string detail = "LRT | " + message;
            if (showDistance && valid) detail += " | " + target.Position.DistanceTo(Game.Player.Character.Position).ToString("0") + " m";
            DrawText(detail, .02f, .15f, .32f, false, textColor);
        }
        if (showTargetBox && valid)
        {
            Vector3 p = target.Position + new Vector3(0, 0, 1.3f);
            using (var sx = new OutputArgument()) using (var sy = new OutputArgument())
            {
                if (Function.Call<bool>(Hash.GET_SCREEN_COORD_FROM_WORLD_COORD, p.X, p.Y, p.Z, sx, sy))
                {
                    float x = sx.GetResult<float>(), y = sy.GetResult<float>();
                    int r = 255, g = locked ? 50 : 210;
                    Function.Call(Hash.DRAW_RECT, x, y - .012f, .02f, .002f, r, g, 40, 230, false);
                    Function.Call(Hash.DRAW_RECT, x, y + .012f, .02f, .002f, r, g, 40, 230, false);
                    Function.Call(Hash.DRAW_RECT, x - .01f, y, .001f, .026f, r, g, 40, 230, false);
                    Function.Call(Hash.DRAW_RECT, x + .01f, y, .001f, .026f, r, g, 40, 230, false);
                }
            }
        }
    }
    void DrawText(string text, float x, float y, float scale, bool centered, Color color)
    {
        Function.Call(Hash.SET_TEXT_FONT, 0);
        Function.Call(Hash.SET_TEXT_SCALE, 0f, scale);
        Function.Call(Hash.SET_TEXT_COLOUR, color.R, color.G, color.B, color.A);
        Function.Call(Hash.SET_TEXT_CENTRE, centered);
        Function.Call(Hash.SET_TEXT_OUTLINE);
        Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
        Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
    }
    void ClearFocus()
    {
        StopScene(); sceneReady = false;
        focusStage = -2; remoteReleased = false;
        if (!ownsFocus) return;
        Function.Call(Hash.CLEAR_FOCUS); ownsFocus = false;
    }
    void Shutdown()
    {
        outcomeTarget = null;
        enabled = false; locked = false;
        try { patch.Restore(); } catch (Exception ex) { Log("Restore warning: " + ex.Message + "; restarting GTA clears runtime-only patches."); }
        try { RestoreLods(); } catch (Exception ex) { Log("LOD restore warning: " + ex.Message); }
        try { ClearFocus(); } catch { }
        target = null; mission = false; routed = -1; hasFocusPosition = false; observed.Clear();
        status = "OFF";
    }
    void Fail(Exception ex)
    {
        Log("ERROR: " + ex); Shutdown();
        Notify("Error - see LRT log");
    }
}
