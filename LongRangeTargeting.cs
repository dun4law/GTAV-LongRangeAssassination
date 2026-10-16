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
    int messageDuration = 1800; // kept for ini compatibility; the game feed controls display time
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
    int stageStart, stageAssignedStart, stageCount, missionAssignedBase;
    readonly int[] missionBuckets = new int[3];
    readonly int[][] pathBuckets = { new int[3], new int[3] }; // [0]=clear, [1]=blocked at launch
    int missionMaxFlight, nextPathCheck, missionAfterKill;
    readonly List<int> farFlights = new List<int>();
    bool pathCheck = true, pathBlocked;
    int pathBlockedSamples, lastApproachSector = -2;
    bool approachMarker = true, showTargetLabel = true, showImpactTimer = true;
    readonly Dictionary<uint, int[]> weaponBuckets = new Dictionary<uint, int[]>();

    sealed class RocketIdentity
    {
        public IntPtr Address;
        public int Weapon;
        public Entity AssignedTarget;
        public Projectile Projectile;
        public IntPtr AssignedAddress;
        public int Started;
        public float LastDistance = -1f;
        public int Ended = -1;
        public bool BlockedAtLaunch;
        public float EtaSmooth = -1f, SpeedSmooth = -1f;
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
        RotateLog();
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
        showTargetLabel = config.GetValue<bool>("Display", "ShowTargetLabel", true);
        showImpactTimer = config.GetValue<bool>("Display", "ShowImpactTimer", true);
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
        pathCheck = config.GetValue<bool>("General", "PathCheck", true);
        approachMarker = config.GetValue<bool>("General", "ApproachMarker", true);
        Log("AutoLock=" + autoLock + "; PathCheck=" + pathCheck);
        streaming = config.GetValue<bool>("General", "RemoteStreaming", true);
        preventEscape = config.GetValue<bool>("General", "PreventDistanceEscape", true);
    }
    string lastStatus = "", lastShipStatus = "";
    string logPath;
    readonly string logStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    void Log(string message)
    {
        string entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine;
        // Something else can hold the log exclusively. Use the first location that accepts writes
        // and keep using it: scripts folder, a per-session file beside it, then %TEMP%.
        string[] candidates = {
            Path.Combine(folder, "LongRangeTargeting.log"),
            Path.Combine(folder, "LongRangeTargeting." + logStamp + ".log"),
            Path.Combine(Path.GetTempPath(), "LongRangeTargeting.fallback.log") };
        if (logPath != null)
        {
            try { AppendShared(logPath, entry); return; }
            catch { logPath = null; }
        }
        Exception first = null;
        foreach (string candidate in candidates)
        {
            try
            {
                string text = entry;
                if (first != null) text = "Log moved to " + candidate + ": " + first.Message + Environment.NewLine + entry;
                AppendShared(candidate, text); logPath = candidate; return;
            }
            catch (Exception ex) { if (first == null) first = ex; }
        }
        System.Diagnostics.Trace.TraceError("LRT could not save its log: " + entry);
        if (!logWarningShown)
        {
            logWarningShown = true;
            try { Notify("Log error"); } catch { }
        }
    }
    static void AppendShared(string path, string text)
    {
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
    void RotateLog()
    {
        try
        {
            string path = Path.Combine(folder, "LongRangeTargeting.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                string old = Path.Combine(folder, "LongRangeTargeting.old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { }
    }
    // Posts a boxed message with icon, title and subtitle to the game's own notification feed
    // (top left), in the same style as other mods' news/status messages.
    int Notify(string text)
    {
        if (!showBottomMessages) return -1;
        Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
        return Function.Call<int>(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT, "CHAR_DEFAULT", "CHAR_DEFAULT", false, 1, "LongRangeTargeting", "Targeting System");
    }
    int pathFeed = -1, pathClearSamples, nextPathMessage;
    // The "Path blocked" notice stays up only while the path is blocked.
    void RemovePathMessage()
    {
        if (pathFeed < 0) return;
        try { Function.Call(Hash.THEFEED_REMOVE_ITEM, pathFeed); } catch { }
        pathFeed = -1;
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
            if (!enabled) return;
            TrackOutcomes();
            if (Online()) { Shutdown(); return; }
            if (Game.IsPaused || Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE)) { ClearFocus(); return; }
            if (Game.Player.Character.IsDead) { locked = false; ClearFocus(); DrawStatus("DEAD"); return; }
            Vehicle pilotedPlane;
            bool isPilot = Pilot(out pilotedPlane);
            if (Game.GameTime >= nextScan)
            {
                // Rescan quickly while a mission has no valid target so the next stage locks sooner.
                nextScan = Game.GameTime + (mission && !ValidTarget() ? 100 : 250);
                bool running = Function.Call<int>(Hash.GET_NUMBER_OF_THREADS_RUNNING_THE_SCRIPT_WITH_THIS_HASH, PatchLayout.MissionHash) > 0;
                if (mission && !running)
                {
                    patch.Restore(); RestoreLods(); ClearFocus(); locked = false; target = null;
                    hasFocusPosition = false; routed = -1; applyAttempted = false;
                    LogMissionSummary();
                    pathBlocked = false; pathBlockedSamples = 0; lastApproachSector = -2; ClearApproachBlip(); RemovePathMessage();
                    observed.Clear(); lastStatus = ""; lastShipStatus = "";
                    Log("Mission ended; reset per-mission state.");
                }
                if (running && !mission) { missionAssignedBase = assigned; stageCount = 0; }
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
                    {
                        string ship = "vehicle=" + current.Handle + "; model=" + current.Model.Hash
                            + "; driver=" + (current.Driver == Game.Player.Character) + "; locked=" + locked
                            + "; targetValid=" + ValidTarget();
                        if (ship != lastShipStatus) { lastShipStatus = ship; Log("Ship status: " + ship + "; assigned=" + assigned); }
                    }
                    string state = "mission=" + mission + "; patched=" + patch.Applied + "; route=" + routed + "; target=" + (ValidTarget() ? target.Handle.ToString() : "missing") + "; locked=" + locked;
                    if (state != lastStatus) { lastStatus = state; Log("Status: " + state + "; assigned=" + assigned); }
                }
            }
            if (!mission) { if (ownedBlips.Count > 0) SweepOwnedBlips(); status = "WAITING"; DrawStatus(status); return; }
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
            CheckPath(plane);
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
            stageStart = Game.GameTime; stageAssignedStart = assigned; stageCount++;
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

    // The game reports no killer for these missiles, so estimate: among rockets assigned to this
    // target that ended within 2s (or are still flying), pick the one last seen closest to it.
    void LogMissionSummary()
    {
        var text = new System.Text.StringBuilder();
        foreach (var item in weaponBuckets)
            text.Append(" ").Append(item.Key).Append("=").Append(item.Value[0]).Append("/").Append(item.Value[1]).Append("/").Append(item.Value[2]);
        Log("Mission summary: stages=" + stageCount + "; missilesFired=" + (assigned - missionAssignedBase)
            + "; endedUnder10m=" + missionBuckets[0] + "; ended10to100m=" + missionBuckets[1] + "; ended100mPlus=" + missionBuckets[2] + "; endedAfterTargetDead=" + missionAfterKill
            + "; byPathAtLaunch(under10/10-100/100+): clear=" + pathBuckets[0][0] + "/" + pathBuckets[0][1] + "/" + pathBuckets[0][2]
            + " blocked=" + pathBuckets[1][0] + "/" + pathBuckets[1][1] + "/" + pathBuckets[1][2]
            + "; byWeapon(under10/10-100/100+):" + (text.Length == 0 ? " none" : text.ToString()));
        string cap = "";
        if (farFlights.Count >= 3)
        {
            int longest = 0; foreach (int f in farFlights) longest = Math.Max(longest, f);
            int same = 0; foreach (int f in farFlights) if (Math.Abs(f - longest) <= 150) same++;
            if (same >= 3) cap = "; likelyLifetimeCapMs=" + longest + " (" + same + " missiles ended 100m+ away at the same flight time)";
        }
        farFlights.Clear();
        int measured = missionBuckets[0] + missionBuckets[1] + missionBuckets[2];
        if (measured > 0)
        {
            Log("Missile range check: observedMaxFlightMs=" + missionMaxFlight + "; ended100mPlus=" + missionBuckets[2] + "/" + measured + cap
                + (missionBuckets[2] * 2 > measured
                    ? "; advice=most missiles disappeared 100m+ from the target (likely range or lifetime limit); raise the Vehicle Weapons Range Increase range/lifetime setting"
                    : "; advice=none"));
        }
        Array.Clear(missionBuckets, 0, 3); weaponBuckets.Clear(); missionMaxFlight = 0; missionAfterKill = 0;
        Array.Clear(pathBuckets[0], 0, 3); Array.Clear(pathBuckets[1], 0, 3);
    }

    string LikelyMissile(Entity victim)
    {
        int bestId = 0; float best = float.MaxValue;
        foreach (var item in observed)
        {
            RocketIdentity shot = item.Value;
            if (shot.AssignedTarget == null || shot.AssignedAddress != victim.MemoryAddress) continue;
            if (shot.Ended >= 0 && Game.GameTime - shot.Ended > 2000) continue;
            if (shot.LastDistance >= 0 && shot.LastDistance < best) { best = shot.LastDistance; bestId = item.Key; }
        }
        return bestId == 0 ? "specific_missile_not_confirmed"
            : "likely_missile=" + bestId + "; lastObservedDistance=" + best.ToString("0.0") + "; estimate_only";
    }

    void TrackRockets()
    {
        List<int> expired = null;
        foreach (var item in observed)
            if (item.Value.Ended >= 0 && Game.GameTime - item.Value.Ended > 10000)
                (expired ?? (expired = new List<int>())).Add(item.Key);
        if (expired != null) foreach (int key in expired) observed.Remove(key);
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
                shot.Ended = Game.GameTime;
                bool orphan = !shot.AssignedTarget.Exists() || shot.AssignedTarget.MemoryAddress != shot.AssignedAddress
                    || shot.AssignedTarget.IsDead;
                if (orphan) missionAfterKill++; // target already dead: its final distance says nothing about range
                else if (shot.LastDistance >= 0)
                {
                    int bucket = shot.LastDistance < 10f ? 0 : shot.LastDistance < 100f ? 1 : 2;
                    missionBuckets[bucket]++;
                    pathBuckets[shot.BlockedAtLaunch ? 1 : 0][bucket]++;
                    missionMaxFlight = Math.Max(missionMaxFlight, Game.GameTime - shot.Started);
                    if (bucket == 2) farFlights.Add(Game.GameTime - shot.Started);
                    int[] counts;
                    uint weapon = unchecked((uint)shot.Weapon);
                    if (!weaponBuckets.TryGetValue(weapon, out counts)) weaponBuckets[weapon] = counts = new int[3];
                    counts[bucket]++;
                }
            }
            else if (shot.AssignedTarget.Exists() && shot.AssignedTarget.MemoryAddress == shot.AssignedAddress)
                shot.LastDistance = shot.Projectile.Position.DistanceTo(shot.AssignedTarget.Position);
        }
    }

    void TrackOutcomes()
    {
        TrackRockets();
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
                    string likely = LikelyMissile(outcomeTarget);
                    Log("Target death confirmed: ped=" + outcomeTarget.Handle + "; causeWeapon=" + cause
                        + "; sourceEntity=" + killer + "; " + likely);
                    Log("Stage summary: stage=" + stageCount + "; elapsedMs=" + (Game.GameTime - stageStart)
                        + "; missilesFired=" + (assigned - stageAssignedStart) + "; " + likely);
                }
            }
        }
    }

    // Straight-line check for map geometry (buildings and terrain) between the launcher and the
    // locked target, plus a suggested open side to approach from. It warns only: it cannot change missile collision or the homing route, and
    // far-away collision that is not streamed in may be missed.
    void CheckPath(Vehicle vehicle)
    {
        // Advisory feature: never let it interfere with the mission or the game.
        if (Game.IsPaused) return;
        try { CheckPathCore(vehicle); }
        catch (Exception ex)
        {
            pathCheck = false; ClearApproachBlip(); RemovePathMessage();
            Log("Path check disabled after error: " + ex.Message);
        }
    }
    void CheckPathCore(Vehicle vehicle)
    {
        if (!pathCheck || Game.GameTime < nextPathCheck) return;
        nextPathCheck = Game.GameTime + 1000;
        if (!locked || !ValidTarget() || vehicle == null || !vehicle.Exists()) { if (pathBlocked) { RemovePathMessage(); Log("Path check stopped: no lock or target."); } pathBlocked = false; pathBlockedSamples = 0; lastApproachSector = -2; ClearApproachBlip(); return; }
        Vector3 aim = target.Position + new Vector3(0, 0, 1f);
        RaycastResult hit = World.Raycast(vehicle.Position, aim, IntersectFlags.Map, vehicle);
        // A hit right at the target is its own window/ledge; anything further out (for example the
        // far wall of the building it stands in) blocks the approach. Two consecutive samples
        // are required so a single flicker does not flip the state.
        bool raw = hit.DidHit && hit.HitPosition.DistanceTo(aim) > 5f;
        pathBlockedSamples = raw ? pathBlockedSamples + 1 : 0;
        pathClearSamples = raw ? 0 : pathClearSamples + 1;
        // Enter "blocked" after two blocked samples; leave it only after two clear samples.
        bool blocked = pathBlocked ? pathClearSamples < 2 : pathBlockedSamples >= 2;
        if (blocked)
        {
            int sector = OpenApproachSector(aim, lastApproachSector);
            bool sectorChanged = !pathBlocked || sector != lastApproachSector;
            lastApproachSector = sector;
            bool blipAlive = approachBlipHandle != 0 && Function.Call<bool>(Hash.DOES_BLIP_EXIST, approachBlipHandle);
            // Keep the marker alive for as long as the path is blocked; recreate it if it vanished.
            if (sectorChanged || (approachMarker && sector >= 0 && !blipAlive)) ShowApproachBlip(aim, sector);
            // The game's notification fades after a few seconds, so repeat it while blocked.
            if (sectorChanged || Game.GameTime >= nextPathMessage)
            {
                RemovePathMessage();
                pathFeed = Notify(sector < 0 ? "~r~PATH BLOCKED~s~ - no open side found, no marker"
                    : approachMarker ? "~r~PATH BLOCKED~s~ - fly to the yellow marker" : "~r~PATH BLOCKED");
                nextPathMessage = Game.GameTime + 6000;
            }
            if (sectorChanged)
            {
                string advice = sector < 0 ? "no open side found"
                    : "open approach from bearing " + (sector * 22.5f).ToString("0") + " deg (position the launcher on that side of the target)";
                Log("Path blocked: obstacleAt=" + hit.HitPosition.DistanceTo(vehicle.Position).ToString("0")
                    + "m of " + target.Position.DistanceTo(vehicle.Position).ToString("0") + "m; " + advice);
            }
        }
        else if (pathBlocked)
        {
            lastApproachSector = -2; ClearApproachBlip(); RemovePathMessage();
            Notify("~g~PATH CLEAR~s~ - missile path is open");
            Log("Path clear.");
        }
        pathBlocked = blocked;
    }

    // Tests 16 compass sectors on an 800 m ring around the target; returns the clear sector whose
    // bearing is closest to the launcher's current side, or -1 when every side is obstructed.
    int approachBlipHandle;
    void ShowApproachBlip(Vector3 aim, int sector)
    {
        ClearApproachBlip();
        if (!approachMarker || sector < 0) return;
        double angle = sector * Math.PI / 8.0;
        float x = aim.X + (float)(Math.Sin(angle) * ApproachRing), y = aim.Y + (float)(Math.Cos(angle) * ApproachRing);
        int blip = Function.Call<int>(Hash.ADD_BLIP_FOR_COORD, x, y, aim.Z);
        if (blip == 0) return;
        Function.Call(Hash.SET_BLIP_SPRITE, blip, 1);
        Function.Call(Hash.SET_BLIP_COLOUR, blip, 5);
        Function.Call(Hash.SET_BLIP_SCALE, blip, 1.2f);
        Function.Call(Hash.SET_BLIP_AS_SHORT_RANGE, blip, false);
        Function.Call(Hash.SET_BLIP_FLASHES, blip, true);
        Function.Call(Hash.BEGIN_TEXT_COMMAND_SET_BLIP_NAME, "STRING");
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "LRT open approach");
        Function.Call(Hash.END_TEXT_COMMAND_SET_BLIP_NAME, blip);
        approachBlipHandle = blip; ownedBlips.Add(blip);
        Log("Approach marker placed: x=" + x.ToString("0") + "; y=" + y.ToString("0"));
    }
    // Every marker this script ever created, so none can be left behind on the map.
    readonly List<int> ownedBlips = new List<int>();
    void ClearApproachBlip()
    {
        if (approachBlipHandle != 0) { int blip = approachBlipHandle; approachBlipHandle = 0; RemoveBlipSafely(blip); }
        SweepOwnedBlips();
    }
    void SweepOwnedBlips()
    {
        for (int i = ownedBlips.Count - 1; i >= 0; i--)
            if (RemoveBlipSafely(ownedBlips[i])) ownedBlips.RemoveAt(i);
    }
    // Returns true when the blip no longer exists (or is at least hidden). REMOVE_BLIP takes a
    // pointer; this SHVDN rejects OutputArgument(int), so try several ways and log which one works.
    bool RemoveBlipSafely(int blip)
    {
        try
        {
            if (!Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip)) return true;
            string[] names = { "Blip.Delete", "OutputArgument(long)", "OutputArgument(ulong)" };
            for (int attempt = 0; attempt < names.Length; attempt++)
            {
                try
                {
                    if (attempt == 0) new Blip(blip).Delete();
                    else if (attempt == 1) Function.Call(Hash.REMOVE_BLIP, new OutputArgument((long)blip));
                    else Function.Call(Hash.REMOVE_BLIP, new OutputArgument((ulong)blip));
                }
                catch (Exception ex) { if (!removeBlipNoted) Log("Approach marker removal via " + names[attempt] + " failed: " + ex.Message); continue; }
                if (!Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
                {
                    if (!removeBlipNoted) { removeBlipNoted = true; Log("Approach marker removal works via " + names[attempt] + "."); }
                    return true;
                }
            }
            // Last resort: make it invisible and retry on the next sweep.
            Function.Call(Hash.SET_BLIP_DISPLAY, blip, 0);
            Function.Call(Hash.SET_BLIP_ALPHA, blip, 0);
            if (!removeBlipNoted) { removeBlipNoted = true; Log("Approach marker " + blip + " could not be removed; hidden, will retry."); }
            return false;
        }
        catch (Exception ex) { Log("Approach marker removal error: " + ex.Message); return false; }
    }
    bool removeBlipNoted;
    const double ApproachRing = 800.0;

    int OpenApproachSector(Vector3 aim, int prefer)
    {
        Vector3 from = Game.Player.Character.Position;
        double current = Math.Atan2(from.X - aim.X, from.Y - aim.Y);
        int best = -1; double bestDelta = double.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI / 8.0;
            Vector3 start = new Vector3(aim.X + (float)(Math.Sin(angle) * ApproachRing), aim.Y + (float)(Math.Cos(angle) * ApproachRing), aim.Z + 30f);
            RaycastResult ray = World.Raycast(start, aim, IntersectFlags.Map);
            if (ray.DidHit && ray.HitPosition.DistanceTo(aim) > 5f) continue;
            if (i == prefer) return i; // keep the current suggestion while it is still open (no jumping marker)
            double delta = Math.Abs(Math.Atan2(Math.Sin(angle - current), Math.Cos(angle - current)));
            if (delta < bestDelta) { bestDelta = delta; best = i; }
        }
        return best;
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
            identity.BlockedAtLaunch = pathBlocked;
            identity.LastDistance = projectile.Position.DistanceTo(target.Position);
            assigned++;
            Log("Assigned fired rocket " + projectile.Handle + " weapon=" + projectile.WeaponHash + " to mission ped=" + target.Handle
                + "; path=" + (pathBlocked ? "blocked" : "clear") + "; vehicle=" + vehicle.Handle + "; type=" + (vehicle.Model.IsBoat ? "boat" : "plane"));
        }
        var expired = new List<int>();
        foreach (var item in observed)
        {
            if (live.Contains(item.Key)) continue;
            // Keep recently ended assigned rockets: TrackRockets prunes them after 10 s and the kill
            // attribution needs them for a moment after they disappear.
            if (item.Value.Ended >= 0 && Game.GameTime - item.Value.Ended <= 10000) continue;
            expired.Add(item.Key);
        }
        foreach (int id in expired) observed.Remove(id);
    }

    // Estimated time until the nearest assigned missile reaches the target, from its distance and
    // closing speed. An estimate only: missiles can lose lifetime, turn or hit something first.
    float impactEtaMax;
    void DrawImpactTimer()
    {
        if (!showImpactTimer || !ValidTarget()) return;
        int flying = 0; float best = float.MaxValue;
        foreach (var item in observed)
        {
            RocketIdentity shot = item.Value;
            Projectile rocket = shot.Projectile;
            if (rocket == null || shot.AssignedAddress != targetAddress) continue;
            if (!rocket.Exists() || rocket.MemoryAddress != shot.Address) continue;
            Vector3 toTarget = target.Position - rocket.Position;
            float distance = toTarget.Length();
            if (distance < 1f) continue;
            flying++;
            // Low-pass the closing speed, then let the shown ETA count down with real time and
            // ease toward the estimate, so a missile accelerating or turning does not make it jump.
            float dt = Math.Max(.001f, Math.Min(.1f, Game.LastFrameTime));
            float closing = Vector3.Dot(rocket.Velocity, toTarget / distance);
            if (closing < 5f) continue;
            shot.SpeedSmooth = shot.SpeedSmooth < 0f ? closing : shot.SpeedSmooth + (closing - shot.SpeedSmooth) * Math.Min(1f, dt * 1.2f);
            float raw = distance / Math.Max(5f, shot.SpeedSmooth);
            if (shot.EtaSmooth < 0f) shot.EtaSmooth = raw;
            else
            {
                shot.EtaSmooth = Math.Max(0f, shot.EtaSmooth - dt);
                shot.EtaSmooth += (raw - shot.EtaSmooth) * Math.Min(1f, dt * .6f);
            }
            best = Math.Min(best, shot.EtaSmooth);
        }
        if (flying == 0) { impactEtaMax = 0f; return; }
        bool known = best < 3600f;
        if (known) impactEtaMax = Math.Max(impactEtaMax, best);
        float progress = known && impactEtaMax > 0.5f ? Math.Max(0f, Math.Min(1f, 1f - best / impactEtaMax)) : 0f;
        int r = 255, g = 210, bl = 40;
        if (known && best < 5f) { g = 70; bl = 50; }
        // Dark panel: accent strip, label + count, large countdown, progress bar.
        const float cx = .5f, cy = .085f, pw = .17f, ph = .072f;
        float left = cx - pw / 2f, top = cy - ph / 2f;
        Function.Call(Hash.DRAW_RECT, cx, cy, pw, ph, 0, 0, 0, 150, false);
        Function.Call(Hash.DRAW_RECT, left + .0015f, cy, .003f, ph, r, g, bl, 235, false);
        DrawText("IMPACT", left + .012f, top + .004f, .26f, false, Color.FromArgb(220, 200, 200, 200));
        DrawText(flying + " IN FLIGHT", cx + .012f, top + .004f, .26f, false, Color.FromArgb(220, 200, 200, 200));
        DrawText(known ? Math.Ceiling(best).ToString("0") + " s" : "--", cx, top + .014f, .62f, true, Color.FromArgb(245, r, g, bl));
        float trackWidth = pw - .024f, trackLeft = left + .012f, trackY = top + ph - .009f;
        Function.Call(Hash.DRAW_RECT, trackLeft + trackWidth / 2f, trackY, trackWidth, .004f, 255, 255, 255, 60, false);
        if (progress > 0f)
            Function.Call(Hash.DRAW_RECT, trackLeft + trackWidth * progress / 2f, trackY, trackWidth * progress, .004f, r, g, bl, 235, false);
    }

    void DrawStatus(string message)
    {
        DrawImpactTimer();
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
                    float distance = target.Position.DistanceTo(Game.Player.Character.Position);
                    // Corner brackets that shrink with distance but stay readable.
                    float hx = Math.Max(.008f, Math.Min(.022f, .023f - distance / 280000f));
                    float aspect = Function.Call<float>(Hash.GET_ASPECT_RATIO, false);
                    if (aspect < .5f) aspect = 16f / 9f;
                    float hy = hx * aspect, lx = hx * .55f, ly = hy * .55f, tx = .0012f, ty = .0012f * aspect;
                    int r, g, b;
                    string state;
                    if (locked && pathBlocked) { r = 255; g = 140; b = 0; state = "LOCKED - PATH BLOCKED"; }
                    else if (locked) { r = 255; g = 50; b = 40; state = "LOCKED"; }
                    else { r = 255; g = 210; b = 40; state = "TARGET"; }
                    for (int cx = -1; cx <= 1; cx += 2)
                        for (int cy = -1; cy <= 1; cy += 2)
                        {
                            Function.Call(Hash.DRAW_RECT, x + cx * (hx - lx / 2f), y + cy * hy, lx, ty, r, g, b, 235, false);
                            Function.Call(Hash.DRAW_RECT, x + cx * hx, y + cy * (hy - ly / 2f), tx, ly, r, g, b, 235, false);
                        }
                    if (showTargetLabel)
                    {
                        string dist = distance >= 1000f ? (distance / 1000f).ToString("0.0") + " km" : distance.ToString("0") + " m";
                        DrawText(state + " | " + dist, x, y + hy + .006f, .26f, true, Color.FromArgb(235, r, g, b));
                    }
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
        enabled = false; locked = false; ClearApproachBlip(); RemovePathMessage();
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
