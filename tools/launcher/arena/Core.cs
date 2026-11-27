// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: what the old Launcher.ps1 did, without the window. Paths of the build, the settings files
// (config\launcher.ini, account.txt, dlc.txt and some keys of graphics.ini and controllers.ini), the checks before a
// match, the controllers the game sees, saves and DLC, and starting the game with the online environment.
// The exe lives in build\ next to work\ and config\ (scripts\setup.py link copies it there); in the portable copy too.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Arena
{
    public sealed class Check
    {
        public bool Ok;
        public string Text;
        public Check(bool ok, string text) { Ok = ok; Text = text; }
    }

    public sealed class Pad
    {
        public string Guid, Name;
        public Pad(string guid, string name) { Guid = guid; Name = name; }
    }

    public static class Core
    {
        /// <summary>the SSBU Arena lobby the game is started with (relay only, never another player's address): the one of the
        /// region chosen in Settings > Online, or Auto's measured pick (Regions.cs)</summary>
        public static string Server => Regions.Server;
        public const string Site = "https://ssbuarena.com/";

        // the exe at the repository's root (the downloaded launcher after its first install) or in its build\ folder (setup.py
        // link); Repo null = not installed yet (Updater.cs installs into the exe's folder)
        static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Repo = FindRepo();
        public static readonly string Build = FindBuild();
        public static readonly string Work = Path.Combine(Build, "work");
        public static readonly string Cfg = Path.Combine(Build, "config");
        public static readonly string Exe = Path.Combine(Work, "ssbu_native.exe");
        public static readonly string RbExe = Path.Combine(Work, "ssbu_native_rb.exe");
        public static readonly string UiExe = Path.Combine(Work, "ssbu_ui_native.exe");
        public static readonly string MainElf = Path.Combine(Build, "binaries", GameVersion(), "main.elf");
        public static readonly string Fonts = Path.Combine(Build, @"sysdata\fonts");
        public static readonly string GameLog = Path.Combine(Build, "game.log");
        static readonly string LauncherIni = Path.Combine(Cfg, "launcher.ini");
        static readonly string OnlineIni = Path.Combine(Cfg, "online.ini");
        static readonly string GfxIni = Path.Combine(Cfg, "graphics.ini");
        internal static readonly string PadIni = Path.Combine(Cfg, "controllers.ini");
        static readonly string DlcFile = Path.Combine(Cfg, "dlc.txt");
        static readonly string CompDir = Path.Combine(Build, @"characters\competitive");

        public static readonly bool Portable = Directory.Exists(Path.Combine(Build, @"..\dump\romfs"));
        public static string RomFS;
        public static bool HasOnline, RbPair, HasCompetitive, LightArc;

        // settings (launcher.ini and the game's ini files)
        public static string Nickname = "", Saves;
        public static bool Online = true, Ping = true, Competitive = true;
        // (replays) which matches the game records (runtime: SSBU_REPLAY_MODES), and whether online ones go to the Arena server
        public static bool RecMatchmaking = true, RecLdn = true, RecArena = true, RecLocal = true;      // (0.2.37) no upload switches: a replay goes to the server only when made public
        public static string ShaderStamp = "";
        public static string Ticket;                // the lobby ticket of this start (Account.LobbyTicket), signed in only      // launcher.ini shaders: what the last preparation was made for (ShaderKey)
        public static int Aspect, WindowMode, Keyboard;
        public static string WindowSize = "1280x720";
        public static int Volume = 100;
        public static bool CloseToTray;                                   // (0.2.26) launcher.ini close_to_tray: the X leaves the launcher in the notification area
        public static bool CompleteSave = true;                           // (complete save) launcher.ini complete_save: everything unlocked and shown in the save (BaseSave.Complete); SSBU_COMPLETE_SAVE=1 / 0 wins
        public static bool CompleteSaveChosen;                            // (0.2.47) on by default: complete_save counts only with complete_save_choice = 1 (0.2.46 wrote "off" for everyone)
        public static int StageBg;                                        // (0.2.20) launcher.ini stage_bg: 0 original, 1 black, 2 custom (SSBU_STAGE_BG)
        public static double DeadZone = 0.10;                             // (0.2.20) controllers.ini [general] deadzone
        public static double OuterZone = 0.98;                            // (0.2.31) controllers.ini [general] outer_zone: full deflection from there
        public static double TriggerThreshold = 0.33;                     // (0.2.31) [general] trigger_threshold: ZL / ZR count as pressed from there
        public static bool Rumble = true;                                 // (0.2.31) [general] rumble
        public static double RumbleStrength = 1.0;                        // (0.2.31) [general] rumble_strength (0..2)
        public static bool[] Invert = new bool[4];                        // (0.2.31) [general] invert.LX / LY / RX / RY (on top of a device's own setting)
        public static int ShaderWait;                                     // (0.2.61) graphics.ini shader_wait: 0 auto (also a missing key), 1 hold, 2 skip (runtime/gfx_options.c)
        public static int ShaderLoad;                                     // (0.2.19) launcher.ini shader_load: 0 automatic, 1 all at the start, 2 per match (graphics.ini shader_load)
        // (shared game data, SharedData.cs) launcher.ini shared_data: "" = not asked yet, "on" = read OpenSmash Ultimate's game files
        // (install.py --shared-data), "off" = Arena's own copy; shared_old_copy = keep: the player kept build\dump after the switch
        public static string SharedData = "", SharedOldCopy = "";
        public static string OpenSmashPath;                               // (0.2.52) launcher.ini opensmash_path: OpenSmash Ultimate's folder or launcher exe, set by hand when the OpenSmash tab does not find it (MainWindow.OpenSmash.cs)
        public const string CommunityDiscord = "https://discord.gg/pkgyYGf3YB";   // (0.2.51) the OpenSmash-Ultimate Discord (their README's invite): Arena's players meet there
        public static bool CrashReports = true;                           // (0.2.18) launcher.ini crash_reports (CrashReport.cs)
        // (0.2.68, SSBU_ARENA_DELAY_AUTO, default on; 0 = fixed 2 by default as 0.2.38..0.2.67) Auto is the default input delay; a
        // launcher.ini that holds 2 (the old default) is moved to Auto once, then input_delay_auto = done keeps a 2 picked later
        public static bool DelayAutoDefault => Environment.GetEnvironmentVariable("SSBU_ARENA_DELAY_AUTO") != "0";
        static bool delayAutoDone;
        public static int InputDelay = DelayAutoDefault ? 0 : 2;          // (0.2.14) launcher.ini input_delay: 1..5 frames (SSBU_RB_NET_DELAY); (0.2.38) no automatic any more, 2 by default; (rb-8ms) 0 = "Auto": the game chooses from the ping and agrees one delay with the other player (ini "automatic", SSBU_RB_NET_DELAY=auto)
        public static bool Fps;                                           // graphics.ini stats_overlay: the FPS counter in the game                                   // graphics.ini volume (0..100), the game's start volume
        // (0.2.8) which controller button presses each game button ([type ...] map.* of controllers.ini, written for the
        // Xbox, PlayStation, Switch and other pads alike; GameCube controllers keep their own); the default is the
        // position of Nintendo's layout: A on the right, B at the bottom, X at the top, Y on the left
        public static readonly List<Tuple<string, string>> GameButtons = new List<Tuple<string, string>> {
            Tuple.Create("A", "A"), Tuple.Create("B", "B"), Tuple.Create("X", "X"), Tuple.Create("Y", "Y"),
            Tuple.Create("L", "L"), Tuple.Create("R", "R"), Tuple.Create("ZL", "ZL"), Tuple.Create("ZR", "ZR"),
            Tuple.Create("PLUS", "+ (pause)"), Tuple.Create("MINUS", "-"), Tuple.Create("STICK_L", "Left stick press"), Tuple.Create("STICK_R", "Right stick press") };
        public static readonly List<Tuple<string, string>> PadSources = new List<Tuple<string, string>> {
            Tuple.Create("east", "Right face button (Xbox B, PS circle)"), Tuple.Create("south", "Bottom face button (Xbox A, PS cross)"),
            Tuple.Create("north", "Top face button (Xbox Y, PS triangle)"), Tuple.Create("west", "Left face button (Xbox X, PS square)"),
            Tuple.Create("leftshoulder", "Left bumper (LB / L1)"), Tuple.Create("rightshoulder", "Right bumper (RB / R1)"),
            Tuple.Create("lefttrigger", "Left trigger (LT / L2)"), Tuple.Create("righttrigger", "Right trigger (RT / R2)"),
            Tuple.Create("start", "Menu / Options / Start"), Tuple.Create("back", "View / Share / Select"),
            Tuple.Create("leftstick", "Left stick press"), Tuple.Create("rightstick", "Right stick press") };
        public static readonly Dictionary<string, string> DefaultButtons = new Dictionary<string, string> {
            { "A", "east" }, { "B", "south" }, { "X", "north" }, { "Y", "west" }, { "L", "leftshoulder" }, { "R", "rightshoulder" },
            { "ZL", "lefttrigger" }, { "ZR", "righttrigger" }, { "PLUS", "start" }, { "MINUS", "back" }, { "STICK_L", "leftstick" }, { "STICK_R", "rightstick" } };
        public static Dictionary<string, string> Buttons = new Dictionary<string, string>(DefaultButtons);
        static readonly string[] PadTypes = { "xbox", "playstation", "switch", "generic" };
        public static string Player1 = "auto", Player2 = "auto";
        // (0.2.78) Settings > Controls (MainWindow.Controls.cs): the game's own controls of the name tags in the save. SSBU_ARENA_CONTROLS_V2=0:
        // the page is hidden (the Controllers page keeps the pad buttons, as 0.2.77) and no name tag is given to the game
        public static bool ControlsV2 => Environment.GetEnvironmentVariable("SSBU_ARENA_CONTROLS_V2") != "0";
        // (0.2.78, v3) one Controls section: the controllers, then "click an action, press a button" (MainWindow.ControlsV3.cs); the
        // Controllers tab's content and the name-tag editor in its Advanced part. SSBU_ARENA_CONTROLS_V3=0: the two tabs of v2
        public static bool ControlsV3 => ControlsV2 && Environment.GetEnvironmentVariable("SSBU_ARENA_CONTROLS_V3") != "0";
        /// <summary>(0.2.78) controllers.ini [type ...] sources added after a button's first one (map.X = north, rightstick): the Controls
        /// page's stick presses; kept as they are by the button list</summary>
        public static Dictionary<string, List<string>> ButtonExtras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>(0.2.78) controllers.ini [keyboard] keys, SwBtn.Ini order</summary>
        public static string[] KeyboardKeys = SwBtn.KeyDefaults.ToArray();
        /// <summary>(0.2.78) [general] gamecube_style = pro: GameCube controllers play as Pro Controllers</summary>
        public static bool GcAsPro;
        // (0.2.78) launcher.ini my_name_tag = <entry>:<name hash, hex> ("" = none): the name tag player 1 plays with without picking it
        // on the character select (the game gets its entry as SSBU_ARENA_MY_TAG, runtime/my_tag.c). The hash (NameTags.cs) ties the
        // setting to the tag's name: a tag deleted or renamed in the game since is not taken (the ini is ASCII: no name in it)
        public static string MyNameTag = "";

        static string FindRepo()
        {
            if (File.Exists(Path.Combine(ExeDir, @"scripts\setup.py"))) return ExeDir;
            string up = Path.GetDirectoryName(ExeDir);
            return up != null && File.Exists(Path.Combine(up, @"scripts\setup.py")) ? up : null;
        }

        static string FindBuild()
        {
            if (Repo != ExeDir) return ExeDir;
            try
            {
                // build_location.txt (scripts/build_location.py): the install location chosen in the installer window
                string f = Path.Combine(Repo, "build_location.txt");
                string line = File.Exists(f) ? File.ReadAllLines(f).FirstOrDefault()?.Trim().Trim('"') : null;
                if (!string.IsNullOrEmpty(line)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(line));
            }
            catch { }
            return Path.Combine(Repo, "build");
        }

        /// <summary>the game build setup.py converted (state.json game_version, as scripts/run.py reads it): 13.0.2, 13.0.4 or 13.0.5</summary>
        /// <summary>(issue #6) the game runs update 13.0.5: the game keeps run-ahead and the online rollback off there
        /// (runtime/hostrt.c game_rb_blocked; since 2026-11-09 only with SSBU_R1_1305=0 in the environment)</summary>
        public static bool RollbackBlocked1305 => GameVersion() == "13.0.5" && Environment.GetEnvironmentVariable("SSBU_R1_1305") == "0";      // (2026-11-09) on by default again: hostrt.c
        public const string Rollback1305Text = "Online matches with rollback are not supported on game update 13.0.5 yet.";

        internal static string GameVersion()
        {
            try
            {
                string st = Path.Combine(Build, "state.json");
                var m = File.Exists(st) ? Regex.Match(File.ReadAllText(st), "\"game_version\"\\s*:\\s*\"(13\\.0\\.[245])\"") : null;
                if (m != null && m.Success) return m.Groups[1].Value;
            }
            catch { }
            return "13.0.4";
        }

        /// <summary>(fix/setup-0278) why the settings folder (Cfg) could not be made, or null. SSBU_ARENA_CFG_GUARD=0: as 0.2.77,
        /// Load throws (a launcher in Downloads crashed at its start: a file named "config" was there, IOException in Core.Load).
        /// Otherwise the launcher starts: before the install (the exe's own folder, Build = the exe's) nothing needs it (the
        /// setup unpacks into a folder of its own, InstallFolder.Problem, and the install's settings are in build\config);
        /// in an install the start says what to rename (MainWindow.CheckUpdates) and Save says the same.</summary>
        public static string CfgProblem;
        static bool CfgGuard => Environment.GetEnvironmentVariable("SSBU_ARENA_CFG_GUARD") != "0";

        static void MakeCfg()
        {
            CfgProblem = null;
            if (!CfgGuard) { Directory.CreateDirectory(Cfg); return; }
            try { Directory.CreateDirectory(Cfg); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                CfgProblem = File.Exists(Cfg)
                    ? "A file named \"config\" (without an extension) is in " + Build + ", where SSBU Arena keeps its settings folder of the same name. " +
                      "Rename that file or move it away, then start SSBU Arena again."
                    : "SSBU Arena's settings folder " + Cfg + " cannot be made (" + e.Message + "). Check that the folder can be written " +
                      "(an antivirus' Controlled folder access may block it), then start SSBU Arena again.";
            }
        }

        public static void Load()
        {
            MakeCfg();
            if (Portable) RomFS = Path.GetFullPath(Path.Combine(Build, @"..\dump\romfs"));
            else
            {
                string st = Path.Combine(Build, "state.json");
                if (File.Exists(st))
                {
                    // "dump": {..., "romfs": "C:\\..."}: the only key the launcher needs, read without a JSON library
                    var m = Regex.Match(File.ReadAllText(st), "\"romfs\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (m.Success) RomFS = Regex.Unescape(m.Groups[1].Value);
                }
            }
            HasOnline = File.Exists(Exe) && File.ReadAllText(Exe, Encoding.ASCII).Contains("SSBU_ONLINE");
            // the rollback build: game DLL and script modules with dirty-page marks (setup.py step rollback); online rollback
            // runs only with it (the normal build's save is not complete enough: the two games drift apart)
            RbPair = File.Exists(Path.Combine(Work, "ssbu_game_r1.dll")) && File.Exists(Path.Combine(Work, @"nro_bin_eh_o2r1\lua2cpp_common.dll"));      // (0.2.8: ssbu_native.exe itself is the rollback build)
            if (!Directory.Exists(CompDir)) MakeCompetitive();
            HasCompetitive = Directory.Exists(CompDir);
            LightArc = RomFS != null && File.Exists(Path.Combine(RomFS, "data.arcp")) && !File.Exists(Path.Combine(RomFS, "data.arc"));
            SharedArc = RomFS != null && !File.Exists(Path.Combine(RomFS, "data.arc")) && !LightArc && File.Exists(Path.Combine(RomFS, "data.arcl"));

            var L = ReadIni(LauncherIni);
            var O = ReadIni(OnlineIni);
            string defSaves = Portable ? Path.Combine(Path.GetDirectoryName(Build), "saves") : Path.Combine(Build, "saves");
            Saves = Get(L, "saves") ?? defSaves;
            Nickname = Get(O, "nickname") ?? Get(L, "nickname") ?? "";
            Online = Get(L, "online_choice") == "1" ? Get(L, "online") != "off" : true;      // (0.2.8) on by default; a choice made since is kept
            // (server region) "server" of 0.2.50 and older is not read any more (it only ever held the Europe lobby): the region is
            // graphics.ini online_region (LoadGfx); online_server = host:port, written by hand, is a server that wins over it
            Regions.IniServer = string.IsNullOrWhiteSpace(Get(L, "online_server")) ? null : Get(L, "online_server").Trim();
            Ping = Get(L, "ping") != "off";
            StageBg = Get(L, "stage_bg") == "off" ? 1 : Get(L, "stage_bg") == "custom" ? 2 : 0;
            CloseToTray = Get(L, "close_to_tray") == "on";
            CompleteSaveChosen = Get(L, "complete_save_choice") == "1";
            CompleteSave = CompleteSaveChosen ? Get(L, "complete_save") != "off" : true;      // (0.2.47) on unless the player turned it off
            ShaderLoad = Get(L, "shader_load") == "start" ? 1 : Get(L, "shader_load") == "loading" ? 2 : 0;
            CrashReports = Get(L, "crash_reports") != "off";
            InputDelay = Get(L, "input_delay") == "automatic" ? 0 : int.TryParse(Get(L, "input_delay"), out var idl) && idl >= 1 && idl <= 5 ? idl : DelayAutoDefault ? 0 : 2;      // ("auto" of 0.2.37 and older: 2; "automatic": the Auto choice)
            delayAutoDone = Get(L, "input_delay_auto") == "done";
            if (DelayAutoDefault && !delayAutoDone && InputDelay == 2) { InputDelay = 0; Updater.Log("input delay: 2 (the old default) -> Auto, once (SSBU_ARENA_DELAY_AUTO=0: keep 2)"); }      // (0.2.68)
            if (DelayAutoDefault) delayAutoDone = true;      // (written by Save: a 2 chosen from now on stays)
            RecMatchmaking = Get(L, "replay_matchmaking") != "off"; RecLdn = Get(L, "replay_ldn") != "off"; RecArena = Get(L, "replay_arena") != "off";
            RecLocal = Get(L, "replay_local") != "off";
            ShaderStamp = Get(L, "shaders") ?? "";
            MyNameTag = Regex.IsMatch(Get(L, "my_name_tag") ?? "", @"^\d{1,2}:[0-9a-f]{8}$") ? Get(L, "my_name_tag") : "";      // (0.2.78)
            OpenSmashPath = string.IsNullOrWhiteSpace(Get(L, "opensmash_path")) ? null : Get(L, "opensmash_path").Trim();
            SharedData = (Get(L, "shared_data") ?? "").Trim().ToLowerInvariant();
            if (SharedData != "on" && SharedData != "off") SharedData = "";
            SharedOldCopy = (Get(L, "shared_old_copy") ?? "").Trim().ToLowerInvariant();
            Competitive = LightArc || HasCompetitive;      // (0.2.8) always: Arena is the competitive game
            if (!HasOnline) Online = false;
            foreach (var old in new[] { "online.ini.off", "online.ini.old" })      // an older launcher's settings: the name
            {
                string p = Path.Combine(Cfg, old);
                if (Nickname != "" || !File.Exists(p)) continue;
                var o2 = ReadIni(p);
                if (Get(o2, "nickname") != null) Nickname = o2["nickname"];
            }

            LoadGfx(false);
            Buttons = new Dictionary<string, string>(DefaultButtons);
            ButtonExtras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ReadIniSection(PadIni, "type xbox"))
            {
                // (0.2.78) "map.X = north, rightstick": the first source is the button list's, the others were added by the Controls page (a stick press)
                if (!kv.Key.StartsWith("map.") || !DefaultButtons.ContainsKey(kv.Key.Substring(4).ToUpperInvariant())) continue;
                PhysMap.Parse(kv.Value, out string first, out var extras);
                if (!PadSources.Any(x => x.Item1 == first)) continue;
                Buttons[kv.Key.Substring(4).ToUpperInvariant()] = first;
                extras = extras.Where(x => x != first && PadSources.Any(s => s.Item1 == x)).ToList();
                if (extras.Count > 0) ButtonExtras[kv.Key.Substring(4).ToUpperInvariant()] = extras;
            }
            KeyboardKeys = KeyNames.FromIni(ReadIniSection(PadIni, "keyboard"));      // (0.2.78)
            GcAsPro = string.Equals((ReadIniSection(PadIni, "general").TryGetValue("gamecube_style", out var gcs) ? gcs : "").Trim(), "pro", StringComparison.OrdinalIgnoreCase);
            var P = ReadIni(PadIni);
            { var gen = ReadIniSection(PadIni, "general"); if (gen.TryGetValue("deadzone", out var dz) && double.TryParse(dz, NumberStyles.Float, CultureInfo.InvariantCulture, out var dzv)) DeadZone = Math.Max(0, Math.Min(0.4, dzv));
              double D(string k, double def, double lo, double hi) => gen.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Max(lo, Math.Min(hi, d)) : def;
              bool B(string k, bool def) { if (!gen.TryGetValue(k, out var v)) return def; v = v.Trim().ToLowerInvariant(); return v == "on" || v == "1" || v == "true" || v == "yes"; }
              OuterZone = D("outer_zone", 0.98, 0.5, 1.0); TriggerThreshold = D("trigger_threshold", 0.33, 0.05, 0.95);
              Rumble = B("rumble", true); RumbleStrength = D("rumble_strength", 1.0, 0, 2.0);
              for (int i = 0; i < 4; i++) Invert[i] = B("invert." + StickAxes[i], false); }
            Player1 = (Get(P, "player1") ?? "auto").ToLowerInvariant();
            Player2 = (Get(P, "player2") ?? "auto").ToLowerInvariant();
            string kb = Get(P, "player");
            Keyboard = kb == "1" ? 1 : kb == "2" ? 2 : kb == "none" ? 3 : 0;
        }

        /// <summary>(0.2.12) the graphics.ini keys as the launcher last read or wrote them: Save writes only the ones the player
        /// changed here since, so the in-game menu's choices (F1: the FPS counter, the window, the volume...) are not put back
        /// by the next PLAY</summary>
        static Dictionary<string, string> gfxSeen = new Dictionary<string, string>();

        /// <summary>(0.2.12) graphics.ini read (Load, and again when the game ends: the in-game menu writes it)</summary>
        public static void LoadGfx(bool afterGame = true)
        {
            var G = ReadIni(GfxIni);
            // (0.2.19) the in-game menu changed it: that choice stays (at the launcher's start the file may only hold the old default)
            if (afterGame && Get(G, "shader_load") != null && Get(G, "shader_load") != ShaderLoadValue()) ShaderLoad = Get(G, "shader_load") == "loading" ? 2 : 1;
            Aspect = Get(G, "aspect") == "stretch" ? 1 : 0;
            WindowMode = Get(G, "window") == "borderless" ? 1 : Get(G, "window") == "fullscreen" ? 2 : 0;
            if (Get(G, "width") != null && Get(G, "height") != null) WindowSize = G["width"] + "x" + G["height"];
            Volume = int.TryParse(Get(G, "volume"), out var vol) ? Math.Max(0, Math.Min(100, vol)) : 100;
            Fps = Get(G, "stats_overlay") == "on";
            // (0.2.61) shader_wait: auto when missing or not known, as the game reads it; the "hold" of the old template (its old
            // comment line is still there) is shown as auto, the value the game moves it to at its next start (wait_migrate): the
            // launcher leaves that file as it is (gfxSeen holds auto, so Save writes the key only when the player changes it here)
            string sw = (Get(G, "shader_wait") ?? "auto").Trim().ToLowerInvariant();
            ShaderWait = sw == "hold" ? 1 : sw == "skip" ? 2 : 0;
            if (ShaderWait == 1 && WaitOldComment() >= 0 && WaitMigrate()) ShaderWait = 0;
            if (Get(G, "ping_overlay") != null) Ping = Get(G, "ping_overlay") != "off";      // (0.2.8) the in-game menu's choice wins: one switch in both places
            Regions.Setting = Regions.ById(Get(G, "online_region"));      // (server region) one key for the launcher and the game; the in-game menu's choice wins
            if (Regions.ById(Get(G, "online_region_last")) >= 0) Regions.Last = Regions.ById(Get(G, "online_region_last"));
            gfxSeen = GfxValues().Where(kv => Get(G, kv.Key) != null).ToDictionary(kv => kv.Key, kv => kv.Value);      // a key not in the file yet is written
            if (Get(G, "shader_load") != null) gfxSeen["shader_load"] = Get(G, "shader_load");      // (0.2.19) as it is in the file: automatic may want another value
        }

        /// <summary>(0.2.61) the comment line of the graphics.ini template from before shader_wait had auto: the game moves that
        /// file's "hold" to auto once (runtime/gfx_options.c wait_migrate). Its line index, -1 = not there.</summary>
        const string WaitOldLine = "; shader_wait = hold | skip";
        static int WaitOldComment()
        {
            if (!File.Exists(GfxIni)) return -1;
            var lines = File.ReadAllLines(GfxIni);
            for (int i = 0; i < lines.Length; i++) if (lines[i].TrimStart(' ', '\t').StartsWith(WaitOldLine, StringComparison.Ordinal)) return i;
            return -1;
        }
        /// <summary>SSBU_GFX_WAIT_MIGRATE=0 (atoi as the game reads it): the game keeps that hold</summary>
        static bool WaitMigrate()
        {
            string e = Environment.GetEnvironmentVariable("SSBU_GFX_WAIT_MIGRATE");
            if (e == null) return true;
            var m = Regex.Match(e, @"^\s*[+-]?(\d+)");
            return m.Success && m.Groups[1].Value.TrimStart('0') != "";
        }
        /// <summary>(0.2.61) the player chose shader_wait here: the old comment line becomes the new one (as wait_migrate does), so
        /// the game takes this hold as a choice and does not move it to auto</summary>
        static void WaitNewComment()
        {
            int i = WaitOldComment();
            if (i < 0) return;
            var lines = File.ReadAllLines(GfxIni).ToList();
            lines.RemoveAt(i);
            lines.InsertRange(i, new[] { "; shader_wait = auto | hold | skip      (Vulkan)",
                ";   auto: skip where the GPU draws with whole pipelines (AMD, or a driver without pipeline libraries: a missing pipeline",
                ";   costs 0.1 to 1 s there), hold where pipeline libraries are in use (a missing one costs well under a frame)." });
            File.WriteAllLines(GfxIni, lines, Encoding.ASCII);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out long kb);

        /// <summary>(0.2.19) automatic: every shader in memory at the start on a PC with more than 16 GB of RAM, else only the match's
        /// (measured 2026-10-30: 2.9 GB less at the peak, the same frame times in the fight, one pause of about 1.3 s as the match loads)</summary>
        static string ShaderLoadValue()
        {
            if (ShaderLoad != 0) return ShaderLoad == 2 ? "loading" : "start";
            try { if (GetPhysicallyInstalledSystemMemory(out long kb)) return kb > 16L * 1024 * 1024 ? "start" : "loading"; } catch { }
            return "start";
        }

        static Dictionary<string, string> GfxValues()
        {
            var gfx = new Dictionary<string, string> {
                { "aspect", Aspect == 1 ? "stretch" : "keep" },
                { "window", new[] { "windowed", "borderless", "fullscreen" }[Math.Max(0, Math.Min(2, WindowMode))] },
                { "latency", "normal" },        // input latency: always normal (the option is gone)
                { "shader_load", ShaderLoadValue() }, { "shader_wait", new[] { "auto", "hold", "skip" }[Math.Max(0, Math.Min(2, ShaderWait))] }, { "volume", Volume.ToString() }, { "stats_overlay", Fps ? "on" : "off" }, { "ping_overlay", Ping ? "on" : "off" },
                { "online_region", Regions.Id(Regions.Setting) } };
            var m = Regex.Match(WindowSize.Trim(), @"^(\d{3,5})\s*[xX*]\s*(\d{3,5})$");
            if (m.Success) { gfx["width"] = m.Groups[1].Value; gfx["height"] = m.Groups[2].Value; }
            return gfx;
        }

        /// <summary>(0.2.20) the game reloads controllers.ini within a second</summary>
        /// <summary>(server region) Auto's measured pick, kept for the next start of the launcher and of the game</summary>
        public static void SaveRegionLast()
        {
            try { if (Regions.Last >= 0 && Directory.Exists(Cfg)) SetIniValues(GfxIni, null, new Dictionary<string, string> { { "online_region_last", Regions.Id(Regions.Last) } }); } catch { }
        }

        public static void SaveDeadZone() { SetIniValues(PadIni, "general", new Dictionary<string, string> { { "deadzone", DeadZone.ToString("0.00", CultureInfo.InvariantCulture) } }); }
        public static readonly string[] StickAxes = { "LX", "LY", "RX", "RY" };
        /// <summary>(0.2.31) the other [general] keys of controllers.ini the Controllers page shows (the game reloads the file within a second)</summary>
        public static void SavePadGeneral()
        {
            var inv = CultureInfo.InvariantCulture;
            var d = new Dictionary<string, string> {
                { "outer_zone", OuterZone.ToString("0.00", inv) }, { "trigger_threshold", TriggerThreshold.ToString("0.00", inv) },
                { "rumble", Rumble ? "on" : "off" }, { "rumble_strength", RumbleStrength.ToString("0.00", inv) } };
            for (int i = 0; i < 4; i++) d["invert." + StickAxes[i]] = Invert[i] ? "on" : "off";
            SetIniValues(PadIni, "general", d);
        }

        public static void Save()
        {
            // (fix/setup-0278) no settings folder (CfgProblem): before the install nothing is lost (build\config is the install's);
            // in an install the reason, not "Could not find a part of the path"
            if (CfgProblem != null) { if (Repo == null) return; throw new IOException(CfgProblem); }
            string n = Nickname.Trim();
            // the console account id the game uses (nnfs.c): from the Arena account when signed in (the same on every PC)
            if (Online && HasOnline && Account.SignedIn) File.WriteAllText(Path.Combine(Cfg, "account.txt"), UidFromName("arena:" + Account.Me.Id) + "\r\n", Encoding.ASCII);
            else if (Online && HasOnline && n != "") File.WriteAllText(Path.Combine(Cfg, "account.txt"), UidFromName(n) + "\r\n", Encoding.ASCII);
            // the old local wireless online (online.ini, a server that is gone) is never used again: kept aside
            if (File.Exists(OnlineIni))
            {
                string old = Path.Combine(Cfg, "online.ini.old");
                if (File.Exists(old)) File.Delete(old);
                File.Move(OnlineIni, old);
            }
            File.WriteAllLines(LauncherIni, new[] {
                "# written by the launcher", "saves = " + Saves, "nickname = " + n,
                "online = " + (Online ? "on" : "off"), "online_choice = 1",
                "server = " + Server.Trim(), Regions.IniServer != null ? "online_server = " + Regions.IniServer : "# online_server = host:port   (a server set by hand: wins over the region)", "ping = " + (Ping ? "on" : "off"), OpenSmashPath != null ? "opensmash_path = " + OpenSmashPath : "# opensmash_path = <folder>   (OpenSmash Ultimate's folder or launcher exe, when the OpenSmash tab does not find it)", "shaders = " + ShaderStamp, "input_delay = " + (InputDelay == 0 ? "automatic" : InputDelay.ToString()), delayAutoDone ? "input_delay_auto = done" : "# input_delay_auto = done   (0.2.68: the old default 2 was moved to Auto once)", "crash_reports = " + (CrashReports ? "on" : "off"), "close_to_tray = " + (CloseToTray ? "on" : "off"), "complete_save = " + (CompleteSave ? "on" : "off"), "complete_save_choice = " + (CompleteSaveChosen ? "1" : "0"), "shader_load = " + new[] { "auto", "start", "loading" }[ShaderLoad], "stage_bg = " + new[] { "original", "off", "custom" }[StageBg],
                "replay_matchmaking = " + (RecMatchmaking ? "on" : "off"), "replay_ldn = " + (RecLdn ? "on" : "off"), "replay_arena = " + (RecArena ? "on" : "off"),
                "replay_local = " + (RecLocal ? "on" : "off"),
                MyNameTag != "" ? "my_name_tag = " + MyNameTag : "# my_name_tag = <entry>:<name hash>   (Settings > Controls: the name tag player 1 plays with)",
                SharedData != "" ? "shared_data = " + SharedData : "# shared_data = on | off   (OpenSmash Ultimate's game files instead of a copy of Arena's own; asked once)",
                SharedOldCopy != "" ? "shared_old_copy = " + SharedOldCopy : "# shared_old_copy = keep   (Arena's old copy in build\\dump is kept: not asked again)" }, Encoding.ASCII);
            var gfx = GfxValues();
            var gfxNew = gfx.Where(kv => kv.Key == "latency" || !gfxSeen.TryGetValue(kv.Key, out var v) || v != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            SetIniValues(GfxIni, null, gfxNew);
            if (gfxNew.ContainsKey("shader_wait")) WaitNewComment();      // (0.2.61)
            gfxSeen = gfx;
            SetIniValues(PadIni, "players", new Dictionary<string, string> { { "player1", Player1 }, { "player2", Player2 } });
            SetIniValues(PadIni, "keyboard", new Dictionary<string, string> { { "player", new[] { "auto", "1", "2", "none" }[Math.Max(0, Math.Min(3, Keyboard))] } });
            var map = Buttons.ToDictionary(kv => "map." + kv.Key, kv => PhysMap.Format(kv.Value, ButtonExtras.TryGetValue(kv.Key, out var ex) ? ex : null));      // (0.2.78) and the stick presses added
            foreach (var t in PadTypes) SetIniValues(PadIni, "type " + t, map);
        }

        /// <summary>(0.2.78) the keyboard's keys ([keyboard] A .. RSTICK_DOWN): written only when the Controls page changed one</summary>
        public static void SaveKeyboardKeys()
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < KeyboardKeys.Length; i++) d[SwBtn.Ini[i]] = KeyboardKeys[i];
            SetIniValues(PadIni, "keyboard", d);
        }

        /// <summary>(0.2.8) build\characters\competitive (6 stages, no items, no Final Smash meter) made from the dump's
        /// data.arc by tools/competitive/make_competitive.py (half a second): no install made it, so the Arena menu and
        /// the competitive rules stayed off (they need it). Here once, when it is missing and the sources are there.</summary>
        /// <summary>(shared game data) the RomFS folder holds data.arc only as data.arcl, a map into the files of an OpenSmash
        /// Ultimate install (scripts/shared_gamedata.py): the tools read it when given the path of data.arc (tools/nro/arc_file.py)</summary>
        public static bool SharedArc;
        /// <summary>data.arc there in a form the tools read: the archive itself or the linked one (data.arcl)</summary>
        public static bool ArcThere(string arc) { return arc != null && (File.Exists(arc) || File.Exists(arc + "l")); }

        static void MakeCompetitive()
        {
            try
            {
                string py = Repo == null ? null : Path.Combine(Repo, @"deps\python\python.exe"), script = Repo == null ? null : Path.Combine(Repo, @"tools\competitive\make_competitive.py");
                string arc = RomFS == null ? null : Path.Combine(RomFS, "data.arc");
                if (py == null || !File.Exists(py) || !File.Exists(script) || !ArcThere(arc)) return;
                var psi = new ProcessStartInfo(py, "\"" + script + "\" --out \"" + Path.Combine(Build, "characters") + "\" --arc \"" + arc + "\"")
                { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(60000);
            }
            catch { }
        }

        public static string StagesDir => Path.Combine(Build, "stages");
        public static bool StagesReady() { return File.Exists(Path.Combine(StagesDir, "done_v3.txt")); }

        /// <summary>(0.2.20) the stage backgrounds from this player's data.arc (tools/stages/extract_backgrounds.py, about 240 MB, game
        /// content: never in the repository or on the server). When a player first chooses a background that needs them.</summary>
        public static bool MakeStages()
        {
            try
            {
                if (StagesReady()) return true;
                if (Repo == null || RomFS == null) return false;
                string py = Path.Combine(Repo, @"deps\python\python.exe"), script = Path.Combine(Repo, @"tools\stages\extract_backgrounds.py"), arc = Path.Combine(RomFS, "data.arc");
                if (!File.Exists(py) || !File.Exists(script) || !ArcThere(arc)) return false;
                var psi = new ProcessStartInfo(py, "\"" + script + "\" --arc \"" + arc + "\" --out \"" + StagesDir + "\"") { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) { p.WaitForExit(300000); if (p.ExitCode != 0) return false; }
                File.WriteAllText(Path.Combine(StagesDir, "done_v3.txt"), "ok");
                return true;
            }
            catch { return false; }
        }

        /// <summary>(0.2.21) what the replay viewer draws of this replay, from the player's data.arc (tools/replay/replay_assets.py): the
        /// list of its files, made if missing; null if it cannot be made.</summary>
        public static string MakeReplayAssets(string replay)
        {
            string dir = Path.Combine(Build, "replay_assets"), list = Path.Combine(dir, ReplayScene.ListFolder, Path.GetFileName(replay) + ".txt");
            if (File.Exists(list) && ReplayAssetsThere(dir, list)) return list;
            if (Repo == null || RomFS == null) return File.Exists(list) ? list : null;
            string py = Path.Combine(Repo, @"deps\python\python.exe"), script = Path.Combine(Repo, @"tools\replay\replay_assets.py"), arc = Path.Combine(RomFS, "data.arc");
            if (!File.Exists(py) || !File.Exists(script) || !ArcThere(arc)) return null;
            var psi = new ProcessStartInfo(py, "\"" + script + "\" --arc \"" + arc + "\" --out \"" + dir + "\" --replay \"" + replay + "\"") { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            // (0.2.46) what the tool said, in build\replay_assets.log: in 0.2.41 .. 0.2.45 it failed on every compact (version 5)
            // replay without a trace, and the viewer showed no models
            var log = new StringBuilder();
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    bool done = p.WaitForExit(300000);
                    if (done) p.WaitForExit();
                    lock (log) log.Insert(0, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + Path.GetFileName(replay) + (done ? " exit " + p.ExitCode : " still running after 300 s") + "\r\n");
                }
            }
            catch (Exception e) { log.Insert(0, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + Path.GetFileName(replay) + ": " + e.Message + "\r\n"); }
            try { lock (log) File.WriteAllText(Path.Combine(Build, "replay_assets.log"), log.ToString()); } catch { }
            return File.Exists(list) ? list : null;
        }

        /// <summary>the folders a replay's list names are all there (a folder removed by hand: made again)</summary>
        static bool ReplayAssetsThere(string dir, string list)
        {
            foreach (var l in File.ReadAllLines(list))
            {
                var p = l.Split(' ');
                string model = p[0] == "stage" && p.Length >= 2 ? p[1] : p[0] == "fighter" && p.Length >= 5 ? p[2] : p[0] == "extra" && p.Length >= 6 ? p[3] : null;
                if (model != null && !File.Exists(Path.Combine(dir, model, "model.rvm"))) return false;
            }
            return true;
        }

        public static string AvatarsDir => Path.Combine(Build, "avatars");

        /// <summary>(0.2.37) the online pass of the running game (runtime/net_relay.c reads it again when it changes); deleted when the game ends</summary>
        public static string TicketFile => Path.Combine(Build, "cache", "online_pass.txt");
        public static bool WriteTicketFile(string ticket)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TicketFile));
                string tmp = TicketFile + ".part";
                File.WriteAllText(tmp, ticket, new UTF8Encoding(false));
                if (File.Exists(TicketFile)) File.Replace(tmp, TicketFile, null); else File.Move(tmp, TicketFile);
                return true;
            }
            catch (Exception e) { Updater.Log("online pass file: " + e.Message); return false; }
        }
        public static void DeleteTicketFile() { try { File.Delete(TicketFile); File.Delete(TicketFile + ".part"); } catch { } }
        /// <summary>when to ask for the next pass: at half the life of this one ("id|name|expiry|issued.HMAC"; without the issue time:
        /// 8 hours), at least 1 minute, and 2 minutes before it expires at the latest</summary>
        public static TimeSpan TicketRenewAfter(string ticket)
        {
            try
            {
                var p = ticket.Substring(0, ticket.LastIndexOf('.')).Split('|');
                long exp = long.Parse(p[2]), iat = p.Length > 3 ? long.Parse(p[3]) : exp - 8 * 3600, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long at = Math.Min(iat + (exp - iat) / 2, exp - 120) - now;
                return TimeSpan.FromSeconds(Math.Max(60, at));
            }
            catch { return TimeSpan.FromMinutes(10); }
        }

        /// <summary>the fighter's number among the game's own profile icons (avatars\icons.txt, ui_profile_icon_db); -1: none</summary>
        public static int ProfileIconOf(string fighter)
        {
            try
            {
                // (0.2.34) any of the game's profile icons: icon_<number> (avatars\all, its picture there)
                if (fighter != null && fighter.StartsWith("icon_", StringComparison.Ordinal) && int.TryParse(fighter.Substring(5), out int any))
                    return any >= 0 && any < 0x10000 && File.Exists(Path.Combine(AvatarsDir, "all", fighter + ".png")) ? any : -1;
                string f = Path.Combine(AvatarsDir, "icons.txt");
                if (string.IsNullOrEmpty(fighter) || !File.Exists(f)) return -1;
                foreach (var l in File.ReadAllLines(f))
                    if (l.StartsWith(fighter + "=", StringComparison.Ordinal) && int.TryParse(l.Substring(fighter.Length + 1), out int n)) return n;
            }
            catch { }
            return -1;
        }

        const long SaveSize = 5982968, SaveProfileIcon = 0x424080;      // 13.0.4's system_data.bin and where the player's profile icon is in it (u16)

        /// <summary>(0.2.20) the picture of the account as the game's own profile icon: the game shows it in its rooms and sends it to the
        /// other players itself. In the save, before the game starts (0.2.37: after the base save, so a first start has it too).</summary>
        public static bool WriteProfileIcon(string fighter)
        {
            try
            {
                int n = ProfileIconOf(fighter);
                string save = Path.Combine(Saves, "save", "save_data", "system_data.bin");
                if (n < 0 || !File.Exists(save)) return false;
                using (var fs = new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (fs.Length != SaveSize) return false;
                    var around = new byte[16];
                    fs.Position = SaveProfileIcon - 8;
                    fs.Read(around, 0, 16);
                    if (around.All(b => b == 0)) return false;
                    fs.Position = SaveProfileIcon;
                    fs.WriteByte((byte)n); fs.WriteByte((byte)(n >> 8));
                }
                return true;
            }
            catch (Exception e) { Updater.Log("profile icon: " + e.Message); return false; }
        }

        /// <summary>(0.2.20) the profile pictures: each fighter's portrait from this player's data.arc (tools/ui/extract_fighter_icons.py,
        /// game content, never in the repository or on the server). In the background, once, when they are missing.</summary>
        public static void MakeAvatars()
        {
            try
            {
                if (Repo == null || RomFS == null) return;
                string py = Path.Combine(Repo, @"deps\python\python.exe"), script = Path.Combine(Repo, @"tools\ui\extract_fighter_icons.py"), arc = Path.Combine(RomFS, "data.arc");
                // (0.2.52) stock: the small stock icons of the Leaderboard, once the sources have the script that makes them
                bool stockDone = File.Exists(Path.Combine(AvatarsDir, "stock", "done.txt")) || !File.Exists(script) || !File.ReadAllText(script).Contains("chara_2");
                if (File.Exists(Path.Combine(AvatarsDir, "icons.txt")) && File.Exists(Path.Combine(AvatarsDir, "more", "done.txt")) && File.Exists(Path.Combine(AvatarsDir, "all", "icons.txt")) && File.Exists(Path.Combine(AvatarsDir, "all", "v2.txt")) && stockDone) return;      // (0.2.34: all, every profile icon; 0.2.37: v2, with the Assist Trophies)
                if (!File.Exists(py) || !File.Exists(script) || !ArcThere(arc)) return;
                var psi = new ProcessStartInfo(py, "\"" + script + "\" --arc \"" + arc + "\" --out \"" + AvatarsDir + "\"") { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(120000);
            }
            catch { }
        }

        /// <summary>(0.2.8) the Kirby loading animation's atlas beside the exe (runtime/loading_anim.c): made on this PC from
        /// Ultimate Frame Data's GIF by tools/ui/loading_inhale.py (game-derived, never in the repository; about 15 s with
        /// the download). No install made it, so the boot stayed black. In the background, once, when it is missing.</summary>
        public static void MakeLoadingArt()
        {
            try
            {
                string dst = Path.Combine(Work, "loading_kirby.png");
                if (File.Exists(dst) || Repo == null) return;
                string py = Path.Combine(Repo, @"deps\python\python.exe"), script = Path.Combine(Repo, @"tools\ui\loading_inhale.py"), art = Path.Combine(Repo, @"media\loading_kirby.png");
                if (!File.Exists(art))
                {
                    if (!File.Exists(py) || !File.Exists(script)) return;
                    var psi = new ProcessStartInfo(py, "\"" + script + "\" --download") { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) p.WaitForExit(180000);
                }
                if (File.Exists(art) && Directory.Exists(Work)) File.Copy(art, dst, true);
            }
            catch { }
        }

        /// <summary>the keys of one [section] of an ini file (lower case), the others ignored</summary>
        internal static Dictionary<string, string> ReadIniSection(string path, string section)
        {
            var h = new Dictionary<string, string>();
            if (!File.Exists(path)) return h;
            bool on = false;
            foreach (var l in File.ReadAllLines(path))
            {
                var s = Regex.Match(l, @"^\s*\[(.+?)\]\s*$");
                if (s.Success) { on = string.Equals(s.Groups[1].Value.Trim(), section, StringComparison.OrdinalIgnoreCase); continue; }
                if (!on || Regex.IsMatch(l, @"^\s*[#;]")) continue;
                var m = Regex.Match(l, @"^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$");
                if (m.Success) h[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
            }
            return h;
        }

        static string Get(Dictionary<string, string> h, string k) { return h.TryGetValue(k, out var v) && v != "" ? v : null; }

        public static Dictionary<string, string> ReadIni(string path)
        {
            var h = new Dictionary<string, string>();
            if (!File.Exists(path)) return h;
            foreach (var l in File.ReadAllLines(path))
            {
                if (Regex.IsMatch(l, @"^\s*[#;]")) continue;
                var m = Regex.Match(l, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$");
                if (m.Success) h[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
            }
            return h;
        }

        // ini files of the game: change some keys, keep everything else (comments, other sections) as it is
        static void SetIniValues(string path, string section, Dictionary<string, string> vals)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path) : new string[0];
            var outl = new List<string>();
            var left = vals.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
            bool inTarget = section == null, seen = section == null;
            foreach (var l in lines)
            {
                var h = Regex.Match(l, @"^\s*\[(.+?)\]\s*$");
                if (section != null && h.Success)
                {
                    if (inTarget) { foreach (var kv in left) outl.Add(kv.Key + " = " + kv.Value); left.Clear(); }
                    inTarget = string.Equals(h.Groups[1].Value.Trim(), section, StringComparison.OrdinalIgnoreCase);
                    if (inTarget) seen = true;
                }
                else if (inTarget && !Regex.IsMatch(l, @"^\s*[#;]"))
                {
                    var k = Regex.Match(l, @"^\s*([A-Za-z0-9_.]+)\s*=");
                    if (k.Success && left.ContainsKey(k.Groups[1].Value.ToLowerInvariant()))
                    {
                        string key = k.Groups[1].Value.ToLowerInvariant();
                        outl.Add(k.Groups[1].Value + " = " + left[key]); left.Remove(key); continue;
                    }
                }
                outl.Add(l);
            }
            if (left.Count > 0)
            {
                if (!seen) { outl.Add(""); outl.Add("[" + section + "]"); }
                foreach (var kv in left) outl.Add(kv.Key + " = " + kv.Value);
            }
            File.WriteAllLines(path, outl, Encoding.ASCII);
        }

        // the account id from the name: the same name always gives the same id (two players must use two names)
        public static string UidFromName(string name)
        {
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes("ssbu-recomp account id|" + name));
                return string.Concat(b.Take(16).Select(x => x.ToString("x2")));
            }
        }

        /// <summary>the short tag shown under the name: four digits of the account id</summary>
        public static string Tag(string name)
        {
            if (name.Trim() == "") return "ARENA#----";
            string u = UidFromName(name.Trim());
            return "ARENA#" + (Convert.ToUInt32(u.Substring(0, 8), 16) % 10000).ToString("0000");
        }

        /// <summary>(0.2.78) the save's entry of the player's own name tag (launcher.ini my_name_tag), -1 = none; why: the reason it is
        /// not used, for the log. The entry must be in use with the same name (hash) as when it was chosen; a tag moved to another
        /// entry in the game is found by its name too.</summary>
        public static int MyTagSlot(out string why)
        {
            why = null;
            if (!ControlsV2) { why = MyNameTag != "" ? "SSBU_ARENA_CONTROLS_V2=0" : null; return -1; }
            if (MyNameTag == "") return -1;
            try
            {
                var parts = MyNameTag.Split(':');
                int slot = int.Parse(parts[0]); uint hash = Convert.ToUInt32(parts[1], 16);
                var tags = NameTagFile.Read(Saves).Named();
                var t = tags.FirstOrDefault(x => x.Slot == slot && NameTagSave.NameHash(x.Name) == hash) ?? tags.FirstOrDefault(x => NameTagSave.NameHash(x.Name) == hash);
                if (t == null) { why = "that name tag is not in the save any more"; return -1; }
                return t.Slot;
            }
            catch (Exception e) { why = e.Message; return -1; }
        }

        /// <summary>(0.2.78) the setting's value for a name tag of the save</summary>
        public static string MyTagValue(NameTag t) => t == null ? "" : t.Slot + ":" + NameTagSave.NameHash(t.Name).ToString("x8");

        public static bool GameRunning()
        {
            return Process.GetProcessesByName("ssbu_native").Length + Process.GetProcessesByName("ssbu_native_rb").Length
                + Process.GetProcessesByName("ssbu_ui_native").Length > 0;
        }

        // ---------------------------------------------------------------- checks
        /// <summary>the game's files and what Windows must have; slow parts (store packages) included</summary>
        /// <summary>(0.2.50) Arena's own menu (characters\arena_menu, made by the setup): the game is started as Arena
        /// (SSBU_COMPETITIVE_UI=1) only with it; without it the game would show the original menu and the full stats overlay</summary>
        public static string ArenaMenuDir => Path.Combine(Build, @"characters\arena_menu");
        /// <summary>(0.2.50) the folder alone was not enough: a player's install (another install location) had the menu step fail
        /// every time, and a folder left half made passed too. Now every file the menu tool makes must be there and not empty
        /// (tools/ui/prepare_menu_classic.py; its marker arena_menu.json names them, the six below without it).</summary>
        public static bool ArenaMenuOk => Competitive && ArenaMenuFiles(out _);
        /// <summary>(0.2.53) the menu tool's marker version this launcher wants (tools/ui/prepare_menu_classic.py MARKER_VERSION)</summary>
        const int ArenaMenuVersion = 6;      // (22/11) 3: the Online select panel's Peak / Mastery Points labels; 4: the rank emblem textures; 5: the "Elo - <tier>" label slot; 6: the tile "Online Ranked" again (was "Online Matchmaking")
        /// <summary>(0.2.53) the menu was made again once for an older marker: from then on its version is not asked (a menu tool older than
        /// this launcher, e.g. sources not updated yet, would else be run at every check and the game started without Arena's menu)</summary>
        static volatile bool menuVersionTried;
        public const string ArenaMenuMissingText = "Arena menu files missing (characters\\arena_menu): the game would start with the original menu";
        static readonly string[] ArenaMenuNames = { @"ui\layout\menu\main_menu\main_menu\layout.arc", @"ui\layout\menu\pallet_menu\pallet_menu\layout.arc",
            @"ui\layout\menu\main_menu_footer\main_menu_footer\layout.arc", @"ui\script\menu\main\main_menu_layer_top.lc", @"ui\script\menu\main\main_menu_common.lc", @"ui\message\msg_menu.msbt" };

        /// <summary>(0.2.50) whether characters\arena_menu is complete; why not (for the log and the game: SSBU_ARENA_MENU_WHY)</summary>
        public static bool ArenaMenuFiles(out string why)
        {
            try
            {
                if (!Directory.Exists(ArenaMenuDir)) { why = "no folder " + ArenaMenuDir; return false; }
                var names = new List<string>(ArenaMenuNames);
                string marker = Path.Combine(ArenaMenuDir, "arena_menu.json");
                if (File.Exists(marker))
                {
                    // {"version": 1, "region": 2, "files": {"ui/layout/...": 1234, ...}}: read without a JSON library
                    string mtext = File.ReadAllText(marker);
                    // (0.2.53) a menu of an older tool (version < 2: no "Arena Elo" texts) is made again once
                    var mv = Regex.Match(mtext, "\"version\"\\s*:\\s*(\\d+)");
                    if (mv.Success && int.Parse(mv.Groups[1].Value) < ArenaMenuVersion && !menuVersionTried && Environment.GetEnvironmentVariable("SSBU_ARENA_MENU_VERSION_CHECK") != "0")
                    { why = "made by an older menu tool (version " + mv.Groups[1].Value + ", now " + ArenaMenuVersion + ")"; return false; }
                    var listed = Regex.Matches(mtext, "\"(ui/[^\"]+)\"\\s*:\\s*(\\d+)");
                    if (listed.Count > 0) names = listed.Cast<Match>().Select(m => m.Groups[1].Value.Replace('/', '\\')).ToList();
                }
                int bad = 0; string first = null;
                foreach (var n in names)
                {
                    var f = new FileInfo(Path.Combine(ArenaMenuDir, n));
                    if (!f.Exists || f.Length == 0) { bad++; if (first == null) first = n; }
                }
                if (bad > 0) { why = bad + " of " + names.Count + " menu files missing or empty (first: " + first + ")"; return false; }
                why = names.Count + " files" + (File.Exists(marker) ? "" : ", no marker (made before 0.2.50)");
                return true;
            }
            catch (Exception e) { why = "unreadable: " + e.Message; return false; }
        }

        /// <summary>(0.2.50) why the game would start without Arena's menu (null: it starts with it)</summary>
        public static string ArenaMenuOffReason()
        {
            if (!Competitive) return "no competitive set-up (" + CompDir + " missing" + (RomFS == null ? ", no dump in state.json" : "") + ")";
            return ArenaMenuFiles(out string why) ? null : why;
        }

        static readonly object menuLock = new object();
        static string menuFailedWhy;            // (fix/launcher-0277) the last failure's reason and time (EnsureArenaMenu's cap)
        static DateTime menuFailedAt;
        /// <summary>(0.2.50) the Arena menu (and the competitive set-up it needs) made here when it is missing or incomplete, from
        /// this install's dump (tools/ui/prepare_menu_classic.py, a few seconds; build\logs\arena_menu.log). The setup did the same,
        /// but failed for every install outside the repository's build folder until 0.2.50, and the launcher only checked the folder.
        /// SSBU_ARENA_MENU_AUTO=0: not made here (0.2.49's way: only the warning at PLAY). Returns whether the menu is complete.</summary>
        public static bool EnsureArenaMenu()
        {
            lock (menuLock)
            {
                if (ArenaMenuOk) return true;
                if (Environment.GetEnvironmentVariable("SSBU_ARENA_MENU_AUTO") == "0") { Updater.Log("arena menu: not complete (" + ArenaMenuOffReason() + "); SSBU_ARENA_MENU_AUTO=0, not made"); return false; }
                if (!Directory.Exists(CompDir)) { MakeCompetitive(); HasCompetitive = Directory.Exists(CompDir); Competitive = LightArc || HasCompetitive; }
                string before = ArenaMenuOffReason();
                if (before == null) return true;
                // (fix/launcher-0277) SSBU_ARENA_MENU_RETRY_CAP=0: as 0.2.76, tried (and logged) at every file check. Otherwise not in a
                // folder without SSBU Arena (nothing to make it with: 54 "FAILED" lines in 9 minutes, 0.2.74, a folder with a game exe
                // and no install), and a failure is not tried (nor logged) again for 60 s while its reason stays the same (Check again
                // pressed fast, every file check)
                bool cap = Environment.GetEnvironmentVariable("SSBU_ARENA_MENU_RETRY_CAP") != "0";
                if (cap && before == menuFailedWhy && (DateTime.UtcNow - menuFailedAt).TotalSeconds < 60) return false;
                if (cap && Repo == null)
                {
                    Updater.Log("arena menu: not complete (" + before + "); no SSBU Arena install here, not made");
                    menuFailedWhy = before; menuFailedAt = DateTime.UtcNow;
                    return false;
                }
                menuVersionTried = true;
                bool ok = Competitive && MakeArenaMenu();
                string after = ArenaMenuOffReason();
                Updater.Log("arena menu: was " + before + "; made again here: " + (after == null ? "OK" : "FAILED (" + after + "), see " + Path.Combine(Build, @"logs\arena_menu.log")));
                if (after != null) { menuFailedWhy = after; menuFailedAt = DateTime.UtcNow; } else menuFailedWhy = null;
                return ok && after == null;
            }
        }

        static bool MakeArenaMenu()
        {
            try
            {
                string py = Repo == null ? null : Path.Combine(Repo, @"deps\python\python.exe"), script = Repo == null ? null : Path.Combine(Repo, @"tools\ui\prepare_menu_classic.py");
                string arc = RomFS == null ? null : Path.Combine(RomFS, "data.arc");
                if (py == null || !File.Exists(py) || !File.Exists(script)) { Updater.Log("arena menu: no python or menu tool in " + Repo); return false; }
                if (!ArcThere(arc)) { Updater.Log("arena menu: no data.arc (" + (arc ?? "no dump in state.json") + ")"); return false; }
                Directory.CreateDirectory(Path.Combine(Build, "logs"));
                var psi = new ProcessStartInfo(py, "-u \"" + script + "\" --out \"" + ArenaMenuDir + "\" --build \"" + Build + "\" --arc \"" + arc + "\"")
                { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var outp = p.StandardOutput.ReadToEndAsync();
                    bool done = p.WaitForExit(180000);
                    if (!done) { try { p.Kill(); } catch { } p.WaitForExit(5000); }
                    File.WriteAllText(Path.Combine(Build, @"logs\arena_menu.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + psi.FileName + " " + psi.Arguments + "\r\n"
                        + outp.Result + err.Result + "\r\nexit " + (done ? p.ExitCode.ToString() : "timeout") + "\r\n");
                    return done && p.ExitCode == 0;
                }
            }
            catch (Exception e) { Updater.Log("arena menu: " + e.Message); return false; }
        }

        public static List<Check> CheckFiles(out List<string> missingCodecs)
        {
            var r = new List<Check>();
            missingCodecs = new List<string>();
            foreach (var x in new[] { new[] { Exe, "Game executable" }, new[] { Path.Combine(Work, "ssbu_game_r1.dll"), "Game code" }, new[] { MainElf, "main.elf" } })
                r.Add(File.Exists(x[0]) ? new Check(true, x[1]) : new Check(false, x[1] + " missing: " + x[0]));
            int mods = Directory.Exists(Path.Combine(Work, "nro_bin_eh_o2r1")) ? Directory.GetFiles(Path.Combine(Work, "nro_bin_eh_o2r1"), "*.dll").Length : 0;
            r.Add(new Check(mods >= 98, "Fighter modules: " + mods + " of 98"));
            bool arc = RomFS != null && (File.Exists(Path.Combine(RomFS, "data.arc")) || File.Exists(Path.Combine(RomFS, "data.arcp")) || File.Exists(Path.Combine(RomFS, "data.arcl")));
            r.Add(new Check(arc, arc ? "Game data (" + (LightArc ? "light archive" : SharedArc ? "shared with OpenSmash Ultimate" : "data.arc") + ")" : "data.arc not found (RomFS of the dump)"));
            // (0.2.50) without Arena's menu the game starts as the original game (original menu, full stats overlay): never silently
            if (!ArenaMenuOk) EnsureArenaMenu();      // (0.2.50) made here (this runs in the background) when missing or incomplete
            r.Add(ArenaMenuOk ? new Check(true, "Arena menu") : new Check(false, ArenaMenuMissingText));
            bool vk = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll"));
            r.Add(new Check(vk, vk ? "Vulkan" : "Vulkan not found: update the graphics driver"));
            // the opening movie needs these two store packages (without them: a black screen)
            var have = AppxPackages(new[] { "Microsoft.WebMediaExtensions", "Microsoft.VP9VideoExtensions" });
            foreach (var c in new[] { new[] { "Microsoft.WebMediaExtensions", "Web Media Extensions", "9N5TDP8VCMHS" }, new[] { "Microsoft.VP9VideoExtensions", "VP9 Video Extensions", "9N4D0MSMP0PT" } })
            {
                if (have == null || have.Contains(c[0])) r.Add(new Check(true, c[1]));
                else { r.Add(new Check(false, c[1] + " missing (needed by the opening movie)")); missingCodecs.Add(c[2] + "|" + c[1]); }
            }
            return r;
        }

        /// <summary>the settings that must be right before going online; empty = fine</summary>
        public static List<string> CheckOnline()
        {
            var bad = new List<string>();
            if (!Online) return bad;
            if (!Account.SignedIn) bad.Add("Online play needs your account (the same for SSBU Arena and OpenSmash): sign in (Settings > Player), or turn online off");
            if (!Regex.IsMatch(Server.Trim(), @"^[^\s:]+:\d+$")) bad.Add("Online: server must be address:port");
            return bad;
        }

        static HashSet<string> AppxPackages(string[] names)
        {
            try
            {
                // Get-AppxPackage takes one name; "ok" at the end tells an empty answer from a failed one
                string list = string.Join(",", names.Select(n => "'" + n + "'"));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"foreach ($n in " + list + ") { Get-AppxPackage $n | ForEach-Object Name }; 'ok'\"")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    var lines = o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                    return lines.Contains("ok") ? new HashSet<string>(lines) : null;
                }
            }
            catch { return null; }      // cannot ask: do not block the game for it
        }

        public static void InstallCodec(string storeId)
        {
            try
            {
                var psi = new ProcessStartInfo("winget", "install --id " + storeId + " --source msstore --accept-package-agreements --accept-source-agreements")
                { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(300000);
            }
            catch { Process.Start("ms-windows-store://pdp/?ProductId=" + storeId); }
        }

        // ---------------------------------------------------------------- controllers
        /// <summary>the game's own view (SDL): ssbu_native.exe --pad-list prints "pad TAB GUID TAB name" per device</summary>
        public static List<Pad> ListPads()
        {
            var pads = new List<Pad>();
            if (!File.Exists(Exe)) return pads;
            try
            {
                var psi = new ProcessStartInfo(Exe, "--pad-list") { WorkingDirectory = Work, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    foreach (var l in text.Split('\n'))
                    {
                        var c = l.TrimEnd('\r').Split('\t');
                        if (c.Length >= 3 && c[0] == "pad") pads.Add(new Pad(c[1].ToLowerInvariant(), c[2]));
                    }
                }
            }
            catch { }
            return pads;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XInputState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState(int user, out XInputState s);

        /// <summary>XInput pads connected now (cheap: polled for the home page; other pads come from --pad-list)</summary>
        public static int XInputCount()
        {
            int n = 0;
            try { for (int i = 0; i < 4; i++) if (XInputGetState(i, out _) == 0) n++; } catch { }
            return n;
        }

        // ---------------------------------------------------------------- saves and DLC
        public static bool HasSave() { return File.Exists(Path.Combine(Saves, @"save\save_data\system_data.bin")); }

        /// <summary>from: a folder holding save_data\ (or the files of save_data themselves); returns a message</summary>
        public static string ImportSave(string from)
        {
            string src = File.Exists(Path.Combine(from, @"save_data\system_data.bin")) ? Path.Combine(from, "save_data")
                       : File.Exists(Path.Combine(from, "system_data.bin")) ? from : null;
            if (src == null) return "There is no Smash save in that folder (system_data.bin is missing).";
            if (GameRunning()) return "Close the game before importing a save.";
            string dst = Path.Combine(Saves, "save"), bak = null;
            if (Directory.Exists(dst))
            {
                bak = Path.Combine(Saves, "save-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.Move(dst, bak);
            }
            CopyDir(src, Path.Combine(dst, "save_data"));
            foreach (var d in Directory.Exists(Saves) ? Directory.GetDirectories(Saves, "save.work.*") : new string[0])
                Directory.Delete(d, true);
            return "Save imported." + (bak != null ? " The previous one is in " + bak : "");
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>the newest Smash save of Ryujinx (its ExtraData0 starts with the title id), or null</summary>
        public static string FindRyujinxSave()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Ryujinx\bis\user\save");
            if (!Directory.Exists(root)) return null;
            string found = null;
            var sig = new byte[] { 0, 224, 22, 0, 128, 106, 0, 1 };
            foreach (var d in Directory.GetDirectories(root))
            {
                string x = Path.Combine(d, "ExtraData0");
                if (!File.Exists(x)) continue;
                var b = File.ReadAllBytes(x);
                if (b.Length < 8 || !b.Take(8).SequenceEqual(sig)) continue;
                var c = new[] { "0", "1" }.Select(s => Path.Combine(d, s)).Where(s => File.Exists(Path.Combine(s, @"save_data\system_data.bin")))
                        .OrderByDescending(s => File.GetLastWriteTime(Path.Combine(s, @"save_data\system_data.bin"))).FirstOrDefault();
                if (c != null) found = c;
            }
            return found;
        }

        public static int DlcCount()
        {
            if (!File.Exists(DlcFile)) return 0;
            return File.ReadAllLines(DlcFile).Count(l => Regex.IsMatch(l, @"^\s*0x[0-9a-fA-F]+|^\s*[0-9]+"));
        }

        /// <summary>the add-on contents owned, from the file names of the player's own DLC dumps; returns how many</summary>
        public static int ActivateDlc(string folder)
        {
            var rows = new SortedDictionary<int, string>();
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                foreach (Match m in Regex.Matches(Path.GetFileName(f), @"\[([0-9A-Fa-f]{16})\]"))
                {
                    long idx = (long)(Convert.ToUInt64(m.Groups[1].Value, 16) - 0x01006A800016E000UL) - 0x1000;
                    if (idx > 0 && idx < 0x1000) rows[(int)idx] = Path.GetFileNameWithoutExtension(f);
                }
            if (rows.Count == 0) return 0;
            var outl = new List<string> { "# add-on contents owned; written by the launcher from the file names of the player's own DLC dumps" };
            foreach (var kv in rows) outl.Add(string.Format("0x{0:x3}   # {1}", kv.Key, kv.Value));
            File.WriteAllLines(DlcFile, outl, Encoding.ASCII);
            return rows.Count;
        }

        public static void ClearDlc() { if (File.Exists(DlcFile)) File.Delete(DlcFile); }

        // ---------------------------------------------------------------- the game
        /// <summary>no preparation marker yet (the game writes one per GPU, driver and translator version): the shaders
        /// are not prepared on this PC. A new driver or game version makes a new marker name: then the game prepares them
        /// itself at its first start, as before.</summary>
        public static bool FirstRun() { return !Directory.Exists(VkCache) || Directory.GetFiles(VkCache, "prepared_*.txt").Length == 0; }

        /// <summary>the game's shader cache folder (runtime vkc_dir: SSBU_VK_CACHE, relative to work\, else build\cache\vk)</summary>
        public static string VkCache
        {
            get { string e = Environment.GetEnvironmentVariable("SSBU_VK_CACHE"); return !string.IsNullOrEmpty(e) ? Path.GetFullPath(Path.Combine(Work, e)) : Path.Combine(Build, @"cache\vk"); }
        }

        /// <summary>what a preparation is valid for: the game's exe (a new version may translate shaders differently: the
        /// marker's name then changes) and the graphics drivers (the marker and the pipeline cache are per driver)</summary>
        public static string ShaderKey()
        {
            string k = "";
            try { var fi = new FileInfo(Exe); k = fi.Length + "-" + fi.LastWriteTimeUtc.Ticks; } catch { }
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT DriverVersion FROM Win32_VideoController"))
                    foreach (var o in q.Get()) k += "-" + o["DriverVersion"];
            }
            catch { }
            return k;
        }

        /// <summary>the preparation should run before a match: never made, or made for another exe or driver (a run with
        /// nothing to do ends in a few seconds with "already")</summary>
        public static bool NeedsPrepare() { return FirstRun() || ShaderStamp != ShaderKey(); }

        static string PrepExe()
        {
            string exe = Environment.GetEnvironmentVariable("SSBU_ARENA_PREP_EXE");
            return !string.IsNullOrEmpty(exe) ? Path.Combine(Work, exe) : Exe;
        }

        static int canPrepare = -1;
        /// <summary>the game's exe knows SSBU_PREPARE_ONLY (an older one would boot hidden and never end): else the game
        /// prepares its shaders itself at its first start, as before</summary>
        public static bool CanPrepare()
        {
            if (canPrepare < 0)
                try { canPrepare = File.Exists(PrepExe()) && File.ReadAllText(PrepExe(), Encoding.ASCII).Contains("SSBU_PREPARE_ONLY") ? 1 : 0; } catch { canPrepare = 0; }
            return canPrepare == 1;
        }

        /// <summary>(0.2.10) every base-game fighter playable: the base save when there is none, else the fighters still
        /// locked unlocked (BaseSave.cs); only while the game is closed (it keeps its save in memory)</summary>
        static void PrepareSave()
        {
            if (GameRunning()) return;
            string done = BaseSave.Ensure(Saves);
            if (done != "") Updater.Log(done);
            string env = Environment.GetEnvironmentVariable("SSBU_COMPLETE_SAVE");
            if (env == "1" || (env != "0" && CompleteSave))
            {
                done = BaseSave.Complete(Saves);      // (complete save) after Ensure: a new base save is completed at once
                if (done != "") Updater.Log(done);
            }
        }

        /// <summary>what every start of the game gets, a match or the shader preparation</summary>
        internal static void GameEnv(System.Collections.Specialized.StringDictionary env)      // (0.2.58: internal, Bench.cs)
        {
            foreach (var v in new[] { "SSBU_ONLINE", "SSBU_ONLINE_CODE", "SSBU_ONLINE_NAME", "SSBU_ONLINE_DIRECT", "SSBU_NICKNAME", "SSBU_RB_NET", "SSBU_R1_GAMEDLL", "SSBU_NRO_BIN", "SSBU_NET_PING", "SSBU_COMPETITIVE_UI",
                                      "SSBU_PREPARE_ONLY", "SSBU_SHOW", "SSBU_ONLINE_REGION_AUTO", "SSBU_ONLINE_TICKET", "SSBU_ONLINE_TICKET_FILE", "SSBU_RB_NET_DELAY", "SSBU_STAGE_BG", "SSBU_MENU", "SSBU_ARENA_MENU_WHY", "SSBU_MASTERY_FILE", "SSBU_ARENA_MY_TAG" })
                env.Remove(v);
            env["SSBU_ROMFS"] = RomFS ?? "";
            env["SSBU_LAUNCHER"] = Updater.Current.ToString(3);      // (0.2.50) the game logs whether the launcher started it (runtime/competitive.c)
            env["SSBU_USERDATA"] = Saves;
            env["SSBU_FONTS"] = Fonts;
            env["SSBU_LOOSE_FILES"] = Competitive ? "1" : "0";      // build\characters\competitive (runtime/loose_files.c)
            env["SSBU_RB_NET_DELAY"] = InputDelay == 0 ? "auto" : Math.Max(1, Math.Min(5, InputDelay)).ToString();      // (0.2.14) the player's choice; (0.2.38) always set, at least 1; (rb-8ms) "auto"
            if (StageBg > 0 && MakeStages()) env["SSBU_STAGE_BG"] = StageBg == 1 ? "off" : "custom";      // (0.2.20) runtime/loose_files.c: local graphics only
            env["SSBU_LATENCY"] = "low";                             // (0.2.39) the pad read as late as the frame can still be on time (display.c lat_release; never in online rollback matches); wins over graphics.ini
            env["SSBU_COMPETITIVE"] = env["SSBU_LOOSE_FILES"];       // no FS meter, no random items (runtime/competitive.c)
            // the matches the game records for the Replays page (fighter_sdk/replay_state.c: local, mm, ldn, arena)
            var modes = new List<string>();
            if (RecLocal) modes.Add("local"); if (RecMatchmaking) modes.Add("mm"); if (RecLdn) modes.Add("ldn"); if (RecArena) modes.Add("arena");
            env["SSBU_REPLAY_MODES"] = string.Join(",", modes);
            env["SSBU_REPLAY_STATE"] = modes.Count > 0 ? "1" : "0";
        }

        /// <summary>the shader preparation without the game's window (runtime/nvn_vk_draw.c, SSBU_PREPARE_ONLY): the game
        /// boots hidden, prepares as on a first start, prints "##prep {json}" lines and ends once the pipeline cache and the
        /// marker are written. Every line also goes to build\prepare.log. SSBU_ARENA_PREP_EXE names another exe (tests).</summary>
        public static Process StartPrepare(Action<string> line, Action<int> exited)
        {
            Directory.CreateDirectory(Path.Combine(Build, "cache"));
            PrepareSave();
            var psi = new ProcessStartInfo(PrepExe(), "\"" + MainElf + "\" 0") { WorkingDirectory = Work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var env = psi.EnvironmentVariables;
            GameEnv(env);
            env["SSBU_ONLINE"] = "off";
            env["SSBU_SHOW"] = "0";
            env["SSBU_PREPARE_ONLY"] = "1";
            var log = new StreamWriter(Path.Combine(Build, "prepare.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            var p = new Process { StartInfo = psi };
            DataReceivedEventHandler h = (s, e) => { if (e.Data == null) return; lock (log) { try { log.WriteLine(e.Data); } catch { } } line(e.Data); };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            System.Threading.Tasks.Task.Run(() =>
            {
                p.WaitForExit();                    // without a timeout: also waits for the last output lines
                int code = -1;
                try { code = p.ExitCode; } catch { }
                lock (log) { try { log.Dispose(); } catch { } }
                exited(code);
            });
            return p;
        }

        /// <summary>starts the game with the environment of the settings; its output goes to game.log through a hidden
        /// cmd.exe (whose exit code is the game's)</summary>
        public static Process Launch()
        {
            Directory.CreateDirectory(Saves);
            Directory.CreateDirectory(Path.Combine(Build, "cache"));
            PrepareSave();
            if (Account.SignedIn) WriteProfileIcon(Account.Me.Avatar);      // (0.2.37) after PrepareSave: on a first start the base save is there now
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { WorkingDirectory = Work, UseShellExecute = false, CreateNoWindow = true };
            var env = psi.EnvironmentVariables;
            GameEnv(env);
            {   int tag = MyTagSlot(out string why);      // (0.2.78) the player's own name tag (runtime/my_tag.c); "" / unset = off
                if (tag > 0) env["SSBU_ARENA_MY_TAG"] = tag.ToString();
                Updater.Log("PLAY: my name tag " + (tag > 0 ? "entry " + tag : "off") + (why != null ? " (" + why + ")" : "")); }
            env["SSBU_SHOW"] = "1";          // the window, whatever the exe is called (display.c shows it by default only for some names)
            string run = Exe;
            // online through the lobby server (runtime/net_relay.c), relay only; rollback (runtime/rb_net.c) in Smash > Versus
            if (Online && HasOnline && Account.SignedIn)
            {
                string n = Account.Me.Username;
                if (string.IsNullOrWhiteSpace(n)) throw new Exception("Your Arena name could not be read. Sign in again from the account page, then press PLAY.");      // never online without the player's own name
                env["SSBU_ONLINE_TICKET"] = Ticket ?? "";              // the lobby takes the player's name from it
                if (Ticket != null && WriteTicketFile(Ticket)) env["SSBU_ONLINE_TICKET_FILE"] = TicketFile;      // (0.2.37) renewed while the game runs
                if (Regions.SettleForPlay()) SaveRegionLast();      // (sa) Auto: a lobby that answered in this run, never an unreachable one
                env["SSBU_ONLINE"] = Server.Trim();                  // (server region) the chosen region's lobby, or Auto's measured pick
                env["SSBU_ONLINE_REGION_AUTO"] = Regions.Auto ? "1" : "0";      // the in-game settings then say "Auto (Europe)"
                Updater.Log("PLAY: online server " + Regions.Text + (Regions.Ms != null ? " (" + Regions.MsText() + ")" : ""));
                env["SSBU_ONLINE_NAME"] = n;
                env["SSBU_NICKNAME"] = n;
                env["SSBU_ONLINE_CODE"] = "public";                  // (0.2.8) no friend codes: one public pool
                env["SSBU_ONLINE_DIRECT"] = "0";                     // never a direct path: no player sees another one's address
                env["SSBU_NET_PING"] = Ping ? "1" : "0";             // each player's ping to the server (nvn_vk_draw.c)
                if (File.Exists(Mastery.File)) env["SSBU_MASTERY_FILE"] = Mastery.File;      // (22/11) the player's mastery points (Mastery.cs, runtime/online_mastery.c)
                if (RbPair)      // (0.2.8) always rollback; only with the marked pair: the normal build's rollback drifts apart (the game's own netcode then)
                {
                    env["SSBU_RB_NET"] = "net";
                    env["SSBU_R1_GAMEDLL"] = "ssbu_game_r1.dll"; env["SSBU_NRO_BIN"] = "nro_bin_eh_o2r1";
                }
            }
            else env["SSBU_ONLINE"] = "off";
            // Arena's native menu uses the same game DLL (including the rollback pair selected above).
            // Keep the original hosts available when the optional menu override has not been prepared.
            string menuOff = ArenaMenuOffReason();      // (0.2.50) one line in the launcher's log; the reason also goes to the game's log
            Updater.Log("PLAY: arena menu " + (menuOff == null ? "ON" : "OFF: " + menuOff));
            if (menuOff == null)
            {
                env["SSBU_COMPETITIVE_UI"] = "1";
                env["SSBU_MENU"] = "1"; // the Settings tile opens the existing PC settings overlay
            }
            else env["SSBU_ARENA_MENU_WHY"] = menuOff;
            psi.Arguments = "/d /c \"\"" + run + "\" \"" + MainElf + "\" 0 > \"" + GameLog + "\" 2>&1\"";
            var p = Process.Start(psi);
            p.EnableRaisingEvents = true;
            return p;
        }

        public static string[] PrepareLogTail(int n) { return LogTail(n, Path.Combine(Build, "prepare.log")); }

        public static string[] LogTail(int n, string file = null)
        {
            try
            {
                using (var fs = new FileStream(file ?? GameLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var all = sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l != "" && !l.StartsWith("##")).ToArray();      // not the launcher's progress lines
                    return all.Skip(Math.Max(0, all.Length - n)).ToArray();
                }
            }
            catch { return new string[0]; }
        }

        public static void Open(string what)
        {
            try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); } catch { }
        }
    }
}
