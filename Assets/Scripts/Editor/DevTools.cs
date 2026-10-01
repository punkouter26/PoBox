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
    ///     step N                      advance a paused play mode N frames
    ///     timescale X                 game speed, for getting to the end of a bout quickly
    ///     state                       one line: phase, round, clock, health, shot, hits
    ///     shot-begin W H              point the HUD at a W x H texture (then step, so it draws)
    ///     shot-end NAME               render the camera at that size, lay the HUD over it, write Build/Shots/NAME.png
    ///     layout                      the one-screen audit of the live HUD (call between shot-begin and shot-end)
    ///     call TYPE METHOD [args]     call a method on the first live component of that type (ints, floats, bools, strings)
    ///     probe, probe-read           drive single joints, then report which way they went (joint sign check)
    ///     gameview W H                set the Game view to a fixed portrait size
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
                case "play": return Play();
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
                case "probe": return Probe();
                case "probe-read": ProbeRead(); return "read";
                case "gameview": return SetGameViewSize(int.Parse(a[1]), int.Parse(a[2]), "PoBox " + a[1] + "x" + a[2]) ? "ok" : "failed";
                default: return "unknown command";
            }
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- play mode

        static string Play()
        {
            if (EditorApplication.isPlaying) return "already playing";
            if (EditorSceneManager.GetActiveScene().path != PoBoxBuilder.ScenePath)
                EditorSceneManager.OpenScene(PoBoxBuilder.ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
            return "entering play mode";
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
            if (b == null) return "playing, no Bout";
            var s = new StringBuilder();
            s.Append($"frame={Time.frameCount} t={Time.time:0.00} ts={Time.timeScale:0.00} fdt={Time.fixedDeltaTime * 1000f:0.0}ms");
            s.Append($" | bout={b.BoutNumber} phase={b.Phase} round={b.Round} left={b.RoundTimeLeft:0.0} clock={Bout.Clock:0.0} count={b.Count} sim={Bout.SimRunning}");
            foreach (Fighter f in new[] { b.red, b.blue })
            {
                if (f == null) continue;
                Vector3 p = f.pelvis.transform.position;
                s.Append($"\n  {f.displayName}: hp={f.Health:0} stam={f.Stamina:0.00} auth={f.Authority:0.00} down={f.IsDown} pelvis=({p.x:0.00},{p.y:0.00},{p.z:0.00})");
                s.Append($" bal={f.BalanceMargin:0.00} stress={f.PeakStress:0.00} power={f.PowerW:0}W thrown={f.stats.thrown} landed={f.stats.landed} clean={f.stats.clean} peak={f.stats.peakImpulse:0.0}Ns dmg={f.stats.damageDealt:0.0} kd={f.stats.knockdowns} kJ={f.stats.energyJ / 1000f:0.00}");
                s.Append($" gloves=({f.GloveSpeedL:0.0},{f.GloveSpeedR:0.0})m/s");
                var brain = f.GetComponent<ScriptedBoxer>();
                Vector3 v = f.pelvis.body.linearVelocity;
                if (brain != null) s.Append($" walk={new Vector2(v.x, v.z).magnitude:0.00}/{brain.WantVelocity.magnitude:0.00}m/s peakJoint={(f.PeakStressPart != null ? f.PeakStressPart.name : "-")}");
            }
            s.Append($"\n  excitement={Excitement.Value:0.00} redShare={Excitement.RedShare:0.00}");
            BroadcastDirector d = BroadcastDirector.Instance;
            if (d != null) s.Append($" shot={d.Current} cuts={d.CutCount}");
            ReplaySystem r = ReplaySystem.Instance;
            if (r != null) s.Append($" replay={(r.Playing ? r.ClipIndex + 1 + "/" + r.ClipCount : "off")} clips={r.Highlights.Count}");
            if (b.Phase == BoutPhase.Results) s.Append($"\n  result: {(b.Winner != null ? b.Winner.displayName : "draw")} {b.Method} elo={b.EloDelta:0.0}");
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null) s.Append($"\n  perf: frame={perf.FrameMs:0.0}ms physics={perf.PhysicsMs:0.00}ms x{PhysicsStepper.StepsLastFrame} setpass={perf.SetPass} tris={perf.Triangles} gc={perf.GcKb:0.0}kB");
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
            if (hud == null || hud.Root == null) return "no HUD";
            return LayoutAudit.Check(hud.Root).Summary();
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

        // ---------------------------------------------------------------- joint signs

        /// <summary>
        /// Which way does a positive drive target turn a link? Pins the red fighter's pelvis in the air,
        /// switches its brain off, asks one joint at a time for +30 degrees and reads the link's local Euler
        /// angle back after it settles. If the angle that comes back has the opposite sign, RigSigns for
        /// that axis is -1. Run in play mode; restart the bout afterwards.
        /// </summary>
        static string Probe()
        {
            Bout b = Bout.Instance;
            if (b == null || !EditorApplication.isPlaying) return "play first";
            Fighter f = b.red;
            f.GetComponent<ScriptedBoxer>().enabled = false;
            b.blue.GetComponent<ScriptedBoxer>().enabled = false;
            f.pelvis.body.immovable = true;

            var s = new StringBuilder("drive +30 on one axis, then read the link's local Euler angles after `step 90`:\n");
            Set(f.upperArmR, 30f, 0f, 0f);     // X on a spherical joint
            Set(f.thighL, 0f, 0f, 30f);        // Z on a spherical joint
            Set(f.torso, 0f, 30f, 0f);         // Y on a spherical joint
            f.shinR.body.SetDriveTarget(ArticulationDriveAxis.X, 30f);   // revolute
            s.Append("targets set: upperArmR X+30, thighL Z+30, torso Y+30, shinR X+30. Now `step 90`, then `probe-read`.");
            return s.ToString();
        }

        static void Set(BodyPart p, float x, float y, float z)
        {
            p.body.SetDriveTarget(ArticulationDriveAxis.X, x);
            p.body.SetDriveTarget(ArticulationDriveAxis.Y, y);
            p.body.SetDriveTarget(ArticulationDriveAxis.Z, z);
        }

        [MenuItem("PoBox/Dev/Probe Read")]
        public static void ProbeRead()
        {
            Bout b = Bout.Instance;
            if (b == null) { Out("probe-read: not playing"); return; }
            Fighter f = b.red;
            string E(BodyPart p)
            {
                Vector3 e = p.transform.localEulerAngles;
                float N(float v) => v > 180f ? v - 360f : v;
                ArticulationReducedSpace jp = p.body.jointPosition;
                var j = new StringBuilder();
                for (int i = 0; i < p.body.dofCount; i++) j.Append($"{jp[i] * Mathf.Rad2Deg:0.0} ");
                return $"euler=({N(e.x):0.0},{N(e.y):0.0},{N(e.z):0.0}) jointPos=[{j.ToString().Trim()}] dof={p.body.dofCount}";
            }
            Out("probe-read:\n  upperArmR (X+30): " + E(f.upperArmR) + "\n  thighL (Z+30): " + E(f.thighL) + "\n  torso (Y+30): " + E(f.torso) + "\n  shinR (X+30): " + E(f.shinR));
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
