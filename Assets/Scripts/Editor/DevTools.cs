using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using PoBox.Broadcast;
using PoBox.Diag;
using PoBox.Sim;
using PoBox.UI;
using BodyPart = PoBox.Sim.BodyPart;   // UnityEditor has one too (for avatars)

namespace PoBox.EditorTools
{
    /// <summary>
    /// How the game is driven and looked at without anybody touching the editor.
    ///
    /// One menu item, <c>PoBox/Dev/Run Script</c>, reads commands from <c>Temp/pobox-dev.txt</c>, one a
    /// line, and appends what each printed to <c>Temp/pobox-dev-out.txt</c>. The Unity CLI calls it
    /// (<c>unity command menu --path "PoBox/Dev/Run Script"</c>), which works while the editor is unfocused.
    ///
    ///     play | stop                 enter or leave play mode on the Arena scene
    ///     play-menu                   enter play mode on the Menu scene, where the app starts
    ///     play-testbed                enter play mode on the Testbed scene
    ///     portraits                   in play on the arena: writes Assets/UI/Portraits/NAME.png for the two boxers in the ring
    ///     step N                      advance a paused play mode N frames
    ///     timescale X                 game speed, for getting to the end of a bout quickly
    ///     state                       one line: phase, round, clock, health, shot, hits
    ///     shot-begin W H              point the HUD at a W x H texture (then step, so it draws)
    ///     shot-end NAME               render the camera at that size, lay the HUD over it, write Build/Shots/NAME.png
    ///     layout                      the one-screen audit of the live HUD or menu (call between shot-begin and shot-end)
    ///     call TYPE METHOD [args]     call a method on the first live component of that type (ints, floats, bools, strings)
    ///     gameview W H                set the Game view to a fixed portrait size
    ///     down 0|1                    the red (0) or blue (1) fighter's legs go, now: a knockdown to look at
    ///     sound, sound-reset          the finished mix as measured: peak, average, clipped samples, limiter
    ///     gc-begin, gc-report         record the profiler for a while, then list what allocated garbage, by method
    ///     governor on|off             let the render-scale governor run in the editor and ask for a frame rate it cannot reach
    ///     skip                        end the walk-on now
    ///
    /// The picture is rendered into textures of an exact size, so it is the phone's shape whatever shape
    /// the Game view happens to be. The capture technique and the Game view reflection are the ones proven
    /// in PoDecath's UiShots.
    /// </summary>
    public static class DevTools
    {
        const string ScriptPath = "Temp/pobox-dev.txt";
        const string OutPath = "Temp/pobox-dev-out.txt";
        public const string ShotDir = "Build/Shots";

        static RenderTexture s_camRt, s_uiRt;
        static readonly List<(PanelSettings ps, RenderTexture rt, bool clear, Color clearValue)> s_saved =
            new List<(PanelSettings, RenderTexture, bool, Color)>();

        [MenuItem("PoBox/Dev/Run Script")]
        public static void RunScript()
        {
            if (!File.Exists(ScriptPath)) { Out("no " + ScriptPath); return; }
            foreach (string raw in File.ReadAllLines(ScriptPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string result;
                try { result = Run(line); }
                catch (Exception e) { result = "ERROR " + e.GetBaseException(); }
                Out($"> {line}\n{result}");
            }
        }

        static void Out(string text)
        {
            File.AppendAllText(OutPath, text + "\n");
        }

        static string Run(string line)
        {
            string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (a[0])
            {
                case "play": return Play(PoBoxBuilder.ScenePath);
                case "play-menu": return Play(PoBoxBuilder.MenuScenePath);
                case "play-testbed": return Play("Assets/Scenes/Testbed.unity");
                case "portraits": return Portraits();
                case "stop": EditorApplication.isPlaying = false; return "stopping";
                case "step": return "frame " + Step(int.Parse(a[1]));
                case "timescale":
                    if (Bout.Instance != null) Bout.Instance.userTimeScale = F(a[1]);
                    return "userTimeScale " + a[1];
                case "state": return State();
                case "shot-begin": return ShotBegin(int.Parse(a[1]), int.Parse(a[2]));
                case "shot-end": return ShotEnd(a[1]);
                case "layout": return Layout();
                case "call": return Call(a);
                case "gameview": return SetGameViewSize(int.Parse(a[1]), int.Parse(a[2]), "PoBox " + a[1] + "x" + a[2]) ? "ok" : "failed";
                case "down": return Down(int.Parse(a[1]));
                case "sound": return Sound();
                case "sound-reset": if (PoBox.Audio.AudioMeter.Instance != null) PoBox.Audio.AudioMeter.Instance.ResetStats(); return "reset";
                case "gc-begin": return GcBegin();
                case "gc-report": return GcReport();
                case "governor": return Governor(a.Length > 1 && a[1] == "on");
                case "skip": if (Bout.Instance != null) Bout.Instance.walkOnSeconds = 0.01f; return "walk-on cut short";
                default: return "unknown command";
            }
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- knockdowns, sound, garbage, the governor

        static string Down(int corner)
        {
            Bout b = Bout.Instance;
            if (b == null) return "play first";
            Fighter f = corner == 0 ? b.red : b.blue;
            f.ForceDown();
            return f.displayName + " put down";
        }

        static string Sound()
        {
            var meter = PoBox.Audio.AudioMeter.Instance;
            if (meter == null) return "no AudioMeter on the listener";
            var director = UnityEngine.Object.FindAnyObjectByType<PoBox.Audio.AudioDirector>();
            return $"mix now: peak {meter.PeakDb:0.0} dBFS, average {meter.RmsDb:0.0} dBFS | since reset: loudest {meter.MaxPeakDb:0.0} dBFS, " +
                   $"{meter.OverCount} samples over full scale before the limiter, limiter took off at most {meter.MaxReductionDb:0.0} dB" +
                   (director != null ? $" | voices {director.ActiveVoices}, crowd ducked {director.DuckNow:0.0} dB, mixer {(director.mixer != null ? director.mixer.name : "none")}" : "");
        }

        static bool s_profilerWasOn = true;

        static string GcBegin()
        {
            // Left as found afterwards: with the profiler driver switched off the engine's own render
            // counters (set-pass calls, triangles) read zero, and the debug panel shows them.
            s_profilerWasOn = UnityEditorInternal.ProfilerDriver.enabled;
            UnityEditorInternal.ProfilerDriver.ClearAllFrames();
            UnityEditorInternal.ProfilerDriver.enabled = true;
            UnityEngine.Profiling.Profiler.enabled = true;
            return "profiler recording; play for a few seconds, then gc-report";
        }

        /// <summary>
        /// What made garbage, and how much a frame. Every allocation the profiler recorded is charged to the
        /// nearest thing above it in the call tree that has a name of its own (a script's Update, a
        /// rendering step), summed over the recorded frames and divided by their number.
        /// </summary>
        static string GcReport()
        {
            int first = UnityEditorInternal.ProfilerDriver.firstFrameIndex, last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
            if (first < 0 || last <= first) return "nothing recorded; gc-begin first";
            var bytes = new Dictionary<string, float>();
            var children = new List<int>();
            int frames = 0;
            float total = 0f;
            for (int frame = first; frame <= last; frame++)
            for (int thread = 0; thread < 64; thread++)
            {
                using (var view = UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView(frame, thread,
                           UnityEditor.Profiling.HierarchyFrameDataView.ViewModes.Default, UnityEditor.Profiling.HierarchyFrameDataView.columnGcMemory, false))
                {
                    if (view == null || !view.valid) break;
                    if (thread == 0) frames++;
                    string where = thread == 0 ? "" : $" [{view.threadName}]";
                    var stack = new Stack<(int id, string owner)>();
                    stack.Push((view.GetRootItemID(), "(frame)"));
                    while (stack.Count > 0)
                    {
                        (int id, string owner) = stack.Pop();
                        string name = view.GetItemName(id);
                        if (name == "GC.Alloc")
                        {
                            float b = view.GetItemColumnDataAsFloat(id, UnityEditor.Profiling.HierarchyFrameDataView.columnGcMemory);
                            bytes.TryGetValue(owner + where, out float had);
                            bytes[owner + where] = had + b;
                            total += b;
                            continue;
                        }
                        if (view.GetItemColumnDataAsFloat(id, UnityEditor.Profiling.HierarchyFrameDataView.columnGcMemory) <= 0f && id != view.GetRootItemID()) continue;
                        children.Clear();
                        view.GetItemChildren(id, children);
                        foreach (int child in children) stack.Push((child, name));
                    }
                }
            }
            UnityEditorInternal.ProfilerDriver.enabled = s_profilerWasOn;
            if (frames == 0) return "no frames could be read";
            var rows = new List<KeyValuePair<string, float>>(bytes);
            rows.Sort((x, y) => y.Value.CompareTo(x.Value));
            var s = new StringBuilder($"garbage over {frames} frames: {total / frames / 1024f:0.0} kB a frame");
            for (int i = 0; i < rows.Count && i < 14; i++)
                s.Append($"\n  {rows[i].Value / frames / 1024f,7:0.00} kB  {rows[i].Key}");
            return s.ToString();
        }

        static string Governor(bool on)
        {
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf == null) return "play first";
            perf.governInEditor = on;
            // A frame rate the editor cannot reach, so every frame is over budget and the governor has to act.
            perf.SetTargetFps(on ? 240 : 60);
            if (!on) perf.RestoreScale();
            return on ? "governor running in the editor against a 240 FPS target; read `state` in a few seconds" : "governor off, 60 FPS";
        }

        // ---------------------------------------------------------------- play mode

        static string Play(string scenePath)
        {
            if (EditorApplication.isPlaying) return "already playing";
            if (EditorSceneManager.GetActiveScene().path != scenePath)
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
            return "entering play mode on " + Path.GetFileNameWithoutExtension(scenePath);
        }

        /// <summary>Steps a paused play mode. Keep batches small: the pipeline aborts main-thread work past five seconds.</summary>
        public static int Step(int frames)
        {
            if (!EditorApplication.isPlaying) return 0;
            EditorApplication.isPaused = true;
            for (int i = 0; i < frames; i++) EditorApplication.Step();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return Time.frameCount;
        }

        public static string State()
        {
            if (!EditorApplication.isPlaying) return "not playing";
            Bout b = Bout.Instance;
            if (b == null)
            {
                MenuView menu = UnityEngine.Object.FindAnyObjectByType<MenuView>();
                return menu != null ? $"menu: red={menu.boxers[menu.Red].name} blue={menu.boxers[menu.Blue].name} of {menu.boxers.Length}" : "playing, no Bout";
            }
            var s = new StringBuilder();
            s.Append($"frame={Time.frameCount} t={Time.time:0.00} ts={Time.timeScale:0.00} fdt={Time.fixedDeltaTime * 1000f:0.0}ms");
            s.Append($" | bout={b.BoutNumber} phase={b.Phase} round={b.Round} left={b.RoundTimeLeft:0.0} clock={Bout.Clock:0.0} count={b.Count} sim={Bout.SimRunning}");
            foreach (Fighter f in new[] { b.red, b.blue })
            {
                if (f == null) continue;
                Vector3 p = f.pelvis.transform.position;
                s.Append($"\n  {f.displayName}: hp={f.Health:0} stam={f.Stamina:0.00} auth={f.Authority:0.00} down={f.IsDown} pelvis=({p.x:0.00},{p.y:0.00},{p.z:0.00})");
                s.Append($" bal={f.BalanceMargin:0.00} stress={f.PeakStress:0.00} power={f.PowerW:0}W thrown={f.stats.thrown} landed={f.stats.landed} clean={f.stats.clean} peak={f.stats.peakImpulse:0.0}Ns dmg={f.stats.damageDealt:0.0} kd={f.stats.knockdowns} kJ={f.stats.energyJ / 1000f:0.00}");
                s.Append($" gloves=({f.GloveSpeedL:0.0},{f.GloveSpeedR:0.0})m/s daze={f.Daze:0.0} drive={f.DriveScale:0.00} rising={f.IsRising} blocked={f.stats.blocked} downs={f.stats.downs}");
                s.Append($" taken=[{f.taken[0]:0},{f.taken[1]:0},{f.taken[2]:0},{f.taken[3]:0},{f.taken[4]:0}] capture=({f.CaptureOffset.x:0.00},{f.CaptureOffset.y:0.00})");
                var policy = f.GetComponent<MjBrain>();
                if (policy != null) s.Append($" value={(policy.HasValue ? policy.Value.ToString("0.0") : "none")} getup={(f.boxer.getUpPolicy != null ? (policy.GettingUp ? "RUNNING" : "ready") : "none")}");
                Vector3 v = f.CenterOfMassVelocity;
                s.Append($" walk={new Vector2(v.x, v.z).magnitude:0.00}m/s peakJoint={(f.PeakStressPart != null ? f.PeakStressPart.name : "-")}");
            }
            s.Append($"\n  excitement={Excitement.Value:0.00} redShare={Excitement.RedShare:0.00}");
            BroadcastDirector d = BroadcastDirector.Instance;
            if (d != null) s.Append($" shot={d.Current} cuts={d.CutCount}");

            if (b.Phase == BoutPhase.Results) s.Append($"\n  result: {(b.Winner != null ? b.Winner.displayName : "draw")} {b.Method} elo={b.EloDelta:0.0}");
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null) s.Append($"\n  perf: frame={perf.FrameMs:0.0}ms physics={perf.PhysicsMs:0.00}ms setpass={perf.SetPass} tris={perf.Triangles} gc={perf.GcKb:0.0}kB (average {perf.GcAverageKb:0.0}) scale={perf.RenderScale:0.00} (down {perf.StepsDown}, up {perf.StepsUp})");
            s.Append($"\n  cards: {b.CardsText()} (rounds scored {b.judges.RoundsScored}) leans now {b.judges.Lean(0):+0;-0;0} {b.judges.Lean(1):+0;-0;0} {b.judges.Lean(2):+0;-0;0}");
            Excitement ex = Excitement.Instance;
            if (ex != null) s.Append($" | critic edge {ex.CriticEdge:+0.00;-0.00} ({(ex.HasCritic ? "both critics" : "no critic")}) usual hit {ex.UsualImpulse:0.0}Ns");
            var ropes = UnityEngine.Object.FindAnyObjectByType<Fx.RopeFlex>();
            if (ropes != null) s.Append($" | rope bow {ropes.MaxBow * 100f:0.0}cm");
            return s.ToString();
        }

        // ---------------------------------------------------------------- pictures

        static List<PanelSettings> LivePanels()
        {
            var panels = new List<PanelSettings>();
            foreach (UIDocument d in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
                if (d.isActiveAndEnabled && d.panelSettings != null && !panels.Contains(d.panelSettings)) panels.Add(d.panelSettings);
            return panels;
        }

        /// <summary>
        /// Points every live panel at a texture of the wanted size. The panel only draws inside the player
        /// loop, so this is followed by a <c>step</c>, and only then by <see cref="ShotEnd"/>.
        /// </summary>
        public static string ShotBegin(int width, int height)
        {
            ShotRestore();
            s_camRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            s_uiRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            int i = 0;
            foreach (PanelSettings ps in LivePanels())
            {
                s_saved.Add((ps, ps.targetTexture, ps.clearColor, ps.colorClearValue));
                ps.targetTexture = s_uiRt;
                ps.clearColor = i++ == 0;
                ps.colorClearValue = Color.clear;
            }
            return $"panels retargeted to {width}x{height}: {s_saved.Count}";
        }

        public static string ShotEnd(string name)
        {
            if (s_camRt == null) return "no shot-begin";
            Texture2D cam = null, ui = null;
            try
            {
                RenderTexture prevActive = RenderTexture.active;
                RenderTexture.active = s_camRt;
                GL.Clear(true, true, new Color(0.02f, 0.02f, 0.03f, 1f));
                RenderTexture.active = prevActive;

                Camera main = Camera.main;
                if (main != null)
                {
                    RenderTexture prevTarget = main.targetTexture;
                    main.targetTexture = s_camRt;
                    main.Render();
                    main.targetTexture = prevTarget;
                }

                cam = Read(s_camRt);
                ui = Read(s_uiRt);
                Color32[] c = cam.GetPixels32(), u = ui.GetPixels32();
                for (int i = 0; i < c.Length; i++)
                {
                    // The panel writes premultiplied colour over a cleared (0,0,0,0) target.
                    float alpha = u[i].a / 255f;
                    c[i] = new Color32(
                        (byte)Mathf.Min(255, u[i].r + c[i].r * (1f - alpha)),
                        (byte)Mathf.Min(255, u[i].g + c[i].g * (1f - alpha)),
                        (byte)Mathf.Min(255, u[i].b + c[i].b * (1f - alpha)),
                        255);
                }
                cam.SetPixels32(c);

                Directory.CreateDirectory(ShotDir);
                string path = Path.GetFullPath(Path.Combine(ShotDir, name.EndsWith(".png") ? name : name + ".png"));
                File.WriteAllBytes(path, cam.EncodeToPNG());
                return path;
            }
            finally
            {
                if (cam != null) UnityEngine.Object.DestroyImmediate(cam);
                if (ui != null) UnityEngine.Object.DestroyImmediate(ui);
                ShotRestore();
            }
        }

        /// <summary>Puts the shared panel asset back exactly as it was: a target texture left on it would be saved.</summary>
        static void ShotRestore()
        {
            foreach (var s in s_saved)
            {
                if (s.ps == null) continue;
                s.ps.targetTexture = s.rt;
                s.ps.clearColor = s.clear;
                s.ps.colorClearValue = s.clearValue;
            }
            s_saved.Clear();
            if (s_camRt != null) { s_camRt.Release(); UnityEngine.Object.DestroyImmediate(s_camRt); s_camRt = null; }
            if (s_uiRt != null) { s_uiRt.Release(); UnityEngine.Object.DestroyImmediate(s_uiRt); s_uiRt = null; }
        }

        static Texture2D Read(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            return t;
        }

        static string Layout()
        {
            HudView hud = UnityEngine.Object.FindAnyObjectByType<HudView>();
            if (hud != null && hud.Root != null) return LayoutAudit.Check(hud.Root).Summary();
            MenuView menu = UnityEngine.Object.FindAnyObjectByType<MenuView>();
            if (menu != null && menu.Root != null) return LayoutAudit.Check(menu.Root, LayoutAudit.MenuAnchors).Summary();
            return "no HUD";
        }

        // ---------------------------------------------------------------- portraits

        /// <summary>
        /// The pictures on the first screen's cards. Each of the two boxers in the ring is photographed
        /// alone, from in front and a little to one side, head to hips, with its gloves off so the same
        /// picture serves either corner. Run in play mode during the introduction, while both still stand
        /// in their guard. Then rebuild the menu scene so the cards pick the files up.
        /// </summary>
        static string Portraits()
        {
            Bout b = Bout.Instance;
            Camera main = Camera.main;
            if (b == null || main == null || !EditorApplication.isPlaying) return "play the arena first";
            const int width = 640, height = 832;
            var written = new List<string>();
            AssetBakery.EnsureFolder(AssetBakery.PortraitDir);

            Vector3 keptPosition = main.transform.position;
            Quaternion keptRotation = main.transform.rotation;
            float keptFov = main.fieldOfView;
            RenderTexture keptTarget = main.targetTexture;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                foreach (Fighter f in new[] { b.red, b.blue })
                {
                    if (f == null || f.boxer == null) continue;
                    Fighter other = b.Other(f);
                    var skin = f.GetComponent<Fx.FighterSkin>();
                    var otherSkin = other != null && other != f ? other.GetComponent<Fx.FighterSkin>() : null;
                    if (skin == null) continue;

                    // Gloves and trails off; the other boxer out of the picture altogether.
                    var hidden = new List<Renderer>();
                    foreach (Fx.FighterSkin.Part p in skin.parts)
                        foreach (Renderer r in p.renderers)
                            if (r != null && r.enabled) { r.enabled = false; hidden.Add(r); }
                    foreach (TrailRenderer t in skin.trails)
                        if (t != null && t.enabled) { t.enabled = false; hidden.Add(t); }
                    // The gloves are the MuJoCo glove spheres, drawn in the corner's colour: the only plain mesh renderers.
                    foreach (Renderer r in skin.extraRenderers)
                        if (r is MeshRenderer && r.enabled) { r.enabled = false; hidden.Add(r); }
                    if (otherSkin != null) otherSkin.SetVisible(false);

                    Vector3 head = skin.HeadPoint, pelvis = skin.PelvisPoint;
                    Vector3 forward = f.boxer.Forward;
                    forward.y = 0f;
                    forward.Normalize();
                    Vector3 look = Vector3.Lerp(pelvis, head, 0.62f);
                    const float fov = 26f;
                    float span = Mathf.Max(0.9f, (head.y - pelvis.y) * 1.75f);
                    float distance = span * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
                    Vector3 from = Quaternion.AngleAxis(24f, Vector3.up) * forward;
                    main.transform.position = look + from * distance + Vector3.up * 0.12f;
                    main.transform.rotation = Quaternion.LookRotation(look - main.transform.position, Vector3.up);
                    main.fieldOfView = fov;
                    main.targetTexture = rt;
                    main.Render();

                    Texture2D shot = Read(rt);
                    string path = $"{AssetBakery.PortraitDir}/{f.boxer.Cfg.name}.png";
                    File.WriteAllBytes(Path.GetFullPath(path), shot.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(shot);
                    written.Add(path);

                    foreach (Renderer r in hidden) if (r != null) r.enabled = true;
                    if (otherSkin != null) otherSkin.SetVisible(true);
                }
            }
            finally
            {
                main.targetTexture = keptTarget;
                main.fieldOfView = keptFov;
                main.transform.SetPositionAndRotation(keptPosition, keptRotation);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
            return written.Count > 0 ? "wrote " + string.Join(", ", written) : "no trained boxers in the ring";
        }

        // ---------------------------------------------------------------- calls

        /// <summary>call TYPE METHOD [args]: the first live component whose type has that short name.</summary>
        static string Call(string[] a)
        {
            if (a.Length < 3) return "usage: call TYPE METHOD [args]";
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
            {
                if (mb == null || mb.GetType().Name != a[1]) continue;
                foreach (MethodInfo m in mb.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != a[2]) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != a.Length - 3) continue;
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) args[i] = Convert(a[3 + i], ps[i].ParameterType);
                    object r = m.Invoke(m.IsStatic ? null : mb, args);
                    return r?.ToString() ?? "ok";
                }
                return $"{a[1]} has no {a[2]} taking {a.Length - 3} argument(s)";
            }
            return "no live " + a[1];
        }

        static object Convert(string s, Type t)
        {
            if (t == typeof(int)) return int.Parse(s);
            if (t == typeof(float)) return F(s);
            if (t == typeof(bool)) return bool.Parse(s);
            if (t.IsEnum) return Enum.Parse(t, s);
            Type inner = Nullable.GetUnderlyingType(t);
            if (inner != null) return s == "null" ? null : Convert(s, inner);
            return s;
        }

        // ---------------------------------------------------------------- game view

        /// <summary>
        /// Selects (adding it first if need be) a fixed-resolution size in the Game view. GameViewSizes is
        /// internal, so this is reflection; it fails with a warning rather than an exception if the editor
        /// has moved things around.
        /// </summary>
        public static bool SetGameViewSize(int width, int height, string label)
        {
            try
            {
                Assembly asm = typeof(UnityEditor.Editor).Assembly;
                Type sizesType = asm.GetType("UnityEditor.GameViewSizes");
                Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                object sizes = singleton.GetProperty("instance").GetValue(null);
                object groupType = sizesType.GetProperty("currentGroupType").GetValue(sizes);
                object group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { groupType });
                Type groupT = group.GetType();

                int total = (int)groupT.GetMethod("GetTotalCount").Invoke(group, null);
                int index = -1;
                for (int i = 0; i < total; i++)
                {
                    object s = groupT.GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                    Type st = s.GetType();
                    if ((int)st.GetProperty("width").GetValue(s) == width &&
                        (int)st.GetProperty("height").GetValue(s) == height &&
                        st.GetProperty("sizeType").GetValue(s).ToString() == "FixedResolution")
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    Type sizeT = asm.GetType("UnityEditor.GameViewSize");
                    Type kindT = asm.GetType("UnityEditor.GameViewSizeType");
                    object kind = Enum.Parse(kindT, "FixedResolution");
                    object size = Activator.CreateInstance(sizeT, kind, width, height, label);
                    groupT.GetMethod("AddCustomSize").Invoke(group, new[] { size });
                    index = (int)groupT.GetMethod("GetTotalCount").Invoke(group, null) - 1;
                }

                Type gvT = asm.GetType("UnityEditor.GameView");
                EditorWindow gv = EditorWindow.GetWindow(gvT);
                MethodInfo select = gvT.GetMethod("SizeSelectionCallback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (select != null) select.Invoke(gv, new object[] { index, null });
                else gvT.GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(gv, index);
                gv.Repaint();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PoBox] could not set the Game view size: {e.GetBaseException().Message}");
                return false;
            }
        }

        [MenuItem("PoBox/Dev/Game View 1080x1920 (9:16)")]
        public static void GameViewPortrait() => SetGameViewSize(1080, 1920, "PoBox 9:16");

        [MenuItem("PoBox/Dev/Check Layout")]
        public static void CheckLayoutMenu() => Debug.Log("[PoBox] " + Layout());
    }
}
