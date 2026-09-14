using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds <c>Assets/Audio/AM_Contest.mixer</c>: a master bus with Crowd,
    /// Foley and Announcer under it, each one's volume exposed under the name
    /// <see cref="Systems_AudioMix"/> reads.
    ///
    /// WHY THIS IS REFLECTION AND NOT AN API CALL. Unity ships no public way to
    /// create an AudioMixer asset — the type that can, <c>AudioMixerController</c>,
    /// is internal to UnityEditor, and the asset is a graph of four native object
    /// types (controller, group, effect, snapshot) cross-referencing each other
    /// by GUID, so <c>AssetDatabase.CreateAsset</c> on its own produces a file
    /// Unity will not open. The alternative is hand-writing that YAML, which
    /// fails the same way for one wrong GUID and fails silently.
    ///
    /// SO IT PROBES BEFORE IT BUILDS. <see cref="Probe"/> prints what the
    /// internal type actually exposes in the editor version in front of it,
    /// rather than assuming a signature that was right in some other version.
    /// Run it once after a Unity upgrade if <see cref="Build"/> starts refusing.
    ///
    /// AND NOTHING DEPENDS ON IT SUCCEEDING. <see cref="Systems_AudioMix"/> has a
    /// working fallback — per-source gains — that delivers the same three busses,
    /// the same ducking and the same master volume without any mixer asset at
    /// all. This tool upgrades the mix to real DSP busses; it does not enable it.
    /// That is why every failure below is a warning and a clear sentence rather
    /// than an exception.
    /// </summary>
    public static class Editor_AudioMixer
    {
        private const string AUDIO_DIR = "Assets/Audio";
        private const string MIXER_PATH = AUDIO_DIR + "/AM_Contest.mixer";
        private const string CONTROLLER_TYPE = "UnityEditor.Audio.AudioMixerController";

        /// <summary>The three busses, in <see cref="AudioBus"/> order.</summary>
        private static readonly (string group, string parameter)[] Busses =
        {
            ("Crowd", Systems_AudioMix.PARAM_CROWD),
            ("Foley", Systems_AudioMix.PARAM_FOLEY),
            ("Announcer", Systems_AudioMix.PARAM_ANNOUNCER)
        };

        [MenuItem("PoBox/Audio/Build Mixer")]
        public static void Build()
        {
            Directory.CreateDirectory(AUDIO_DIR);

            var existing = AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH);
            if (existing != null)
            {
                // Never rebuilt over the top. A mixer someone has since tuned —
                // an effect added, a send wired — is exactly the hand-authored
                // artifact this project has already learned not to regenerate
                // (CLAUDE.md, on scene generators). Delete it by hand to start
                // over.
                Debug.Log($"Editor_AudioMixer: {MIXER_PATH} already exists — left alone. " +
                          "Delete it first to rebuild from scratch.");
                Expose(existing);
                return;
            }

            Type controllerType = FindControllerType();
            if (controllerType == null)
            {
                Debug.LogWarning("Editor_AudioMixer: could not find " + CONTROLLER_TYPE +
                                 " — no mixer asset built. Systems_AudioMix falls back to " +
                                 "per-source gains, which still gives busses, ducking and a " +
                                 "master volume.");
                return;
            }

            MethodInfo create = controllerType.GetMethod(
                "CreateMixerControllerAtPath",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            if (create == null)
            {
                Debug.LogWarning("Editor_AudioMixer: " + CONTROLLER_TYPE +
                                 " has no CreateMixerControllerAtPath(string) in this editor " +
                                 "version. Run PoBox/Audio/Probe Mixer API and adjust. " +
                                 "The per-source fallback is unaffected.");
                return;
            }

            object controller = create.Invoke(null, new object[] { MIXER_PATH });
            if (controller == null)
            {
                Debug.LogWarning("Editor_AudioMixer: the editor refused to create a mixer at " +
                                 MIXER_PATH + ". The per-source fallback is unaffected.");
                return;
            }

            BuildGroups(controller, controllerType);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH);
            if (mixer == null)
            {
                Debug.LogWarning("Editor_AudioMixer: created a mixer that did not load back from " +
                                 MIXER_PATH + ".");
                return;
            }

            Expose(mixer);
            EditorUtility.SetDirty(mixer);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Editor_AudioMixer: built {MIXER_PATH} with " +
                      $"{mixer.FindMatchingGroups(string.Empty).Length} group(s).");
        }

        /// <summary>
        /// Adds the three busses under the master group.
        ///
        /// Best effort, and deliberately so: a mixer with only a master group is
        /// still a usable mixer, and <see cref="Systems_AudioMix"/> checks each
        /// group reference for null before routing anything to it. Losing a bus
        /// here costs the ducking on that bus, not the audio on it.
        /// </summary>
        private static void BuildGroups(object controller, Type controllerType)
        {
            MethodInfo createGroup = controllerType.GetMethod(
                "CreateNewGroup",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(bool) }, null);
            MethodInfo addChild = controllerType.GetMethod(
                "AddChildToParent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo addToView = controllerType.GetMethod(
                "AddGroupToCurrentView",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            // A mixer created from code has an EMPTY view list, and
            // AddGroupToCurrentView indexes into it unconditionally — measured:
            // IndexOutOfRangeException on the first group, every time.
            // SanitizeGroupViews is what the mixer window calls to make the
            // default view exist.
            controllerType.GetMethod(
                "SanitizeGroupViews",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(controller, null);
            PropertyInfo masterProperty = controllerType.GetProperty(
                "masterGroup",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (createGroup == null || addChild == null || masterProperty == null)
            {
                Debug.LogWarning("Editor_AudioMixer: the group-building API is not where it was " +
                                 "expected in this editor version — the mixer has its master " +
                                 "group only. Run PoBox/Audio/Probe Mixer API.");
                return;
            }

            object master = masterProperty.GetValue(controller);
            // The master's own volume first, so it is exposed parameter 0 and
            // the busses follow in AudioBus order. The order is not load-bearing
            // — every parameter is renamed to the constant Systems_AudioMix
            // reads — but a mixer window that lists them in the same order as
            // the enum is one less thing to reconcile by eye.
            ExposeVolume(controller, controllerType, master, Systems_AudioMix.PARAM_MASTER);

            for (int busIndex = 0; busIndex < Busses.Length; busIndex++)
            {
                // storeInAsset: true — the group becomes a sub-object of the
                // mixer asset on creation. Adding it again with
                // AssetDatabase.AddObjectToAsset would put the same object in
                // the file twice.
                object group = createGroup.Invoke(controller, new object[] { Busses[busIndex].group, true });
                if (group == null)
                {
                    continue;
                }
                addChild.Invoke(controller, new[] { group, master });
                // Without this the group exists and routes audio but is invisible
                // in the mixer window, which reads as the tool having failed.
                // Guarded because it is COSMETIC: a bus that works but is not
                // in the default view is a far better outcome than no mixer,
                // and this is the one call here that reaches into the editor's
                // own window state.
                try
                {
                    addToView?.Invoke(controller, new[] { group });
                }
                catch (TargetInvocationException exception)
                {
                    Debug.LogWarning($"Editor_AudioMixer: '{Busses[busIndex].group}' was created " +
                                     "and routed but could not be added to the mixer window's " +
                                     "view — drag it into view by hand if you want to see it. " +
                                     exception.InnerException?.Message);
                }
                ExposeVolume(controller, controllerType, group, Busses[busIndex].parameter);
            }
        }

        /// <summary>
        /// Exposes one group's volume and renames it to <paramref name="parameterName"/>.
        ///
        /// Two steps because the editor offers no single one: AddExposedParameter
        /// takes a PATH to the parameter and names it "MyExposedParam", and the
        /// NAME is what <c>AudioMixer.SetFloat</c> matches on at runtime. So the
        /// parameter is added and then renamed.
        ///
        /// RENAMED BY GUID, NOT BY POSITION. The first version of this took the
        /// last entry in the array on the reasoning that it had just been
        /// appended. Measured: three of the four busses came out as
        /// "MyExposedParam", "MyExposedParam 1" and "MyExposedParam 2", because
        /// the exposedParameters setter SORTS what it is given
        /// (SortFuncForExposedParameters, visible in the probe) — so the entry
        /// just added is wherever the sort put it. The group's own volume GUID
        /// is the only stable handle on it.
        /// </summary>
        private static void ExposeVolume(object controller, Type controllerType,
            object group, string parameterName)
        {
            if (group == null)
            {
                return;
            }
            Type pathType = typeof(UnityEditor.Editor).Assembly
                .GetType("UnityEditor.Audio.AudioGroupParameterPath");
            MethodInfo addExposed = controllerType.GetMethod(
                "AddExposedParameter",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PropertyInfo exposedProperty = controllerType.GetProperty(
                "exposedParameters",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            // The path is built from the group's volume GUID, not from the
            // string "Volume": a parameter is identified by a GUID allocated on
            // the group itself, and the name is only a label put on it
            // afterwards. Probed rather than assumed — the obvious
            // (group, "Volume") constructor does not exist.
            MethodInfo volumeGuid = group.GetType().GetMethod(
                "GetGUIDForVolume",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (pathType == null || addExposed == null || exposedProperty == null || volumeGuid == null)
            {
                Debug.LogWarning($"Editor_AudioMixer: cannot expose {parameterName} — the " +
                                 "exposed-parameter API moved. That bus falls back to a " +
                                 "per-source gain, which still works.");
                return;
            }

            // A freshly created group may not have its parameter GUIDs yet, and
            // exposing an empty one produces a parameter that matches nothing.
            MethodInfo preallocate = group.GetType().GetMethod(
                "PreallocateGUIDs",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            preallocate?.Invoke(group, null);

            object guid = volumeGuid.Invoke(group, null);
            object path = Activator.CreateInstance(pathType, group, guid);
            addExposed.Invoke(controller, new[] { path });

            if (exposedProperty.GetValue(controller) is not Array exposed || exposed.Length == 0)
            {
                return;
            }

            FieldInfo nameField = null;
            FieldInfo guidField = null;
            bool renamed = false;
            for (int index = 0; index < exposed.Length; index++)
            {
                // Struct in an array: box the element, set its name, put it
                // back. Assigning through the array directly would write to a
                // copy and change nothing.
                object entry = exposed.GetValue(index);
                nameField ??= entry.GetType().GetField(
                    "name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                guidField ??= entry.GetType().GetField(
                    "guid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (nameField == null || guidField == null)
                {
                    return;
                }
                if (!guidField.GetValue(entry).Equals(guid))
                {
                    continue;
                }
                nameField.SetValue(entry, parameterName);
                exposed.SetValue(entry, index);
                renamed = true;
                break;
            }

            if (!renamed)
            {
                Debug.LogWarning($"Editor_AudioMixer: exposed a parameter for {parameterName} but " +
                                 "could not find it again by GUID to rename it. That bus falls " +
                                 "back to a per-source gain.");
                return;
            }
            exposedProperty.SetValue(controller, exposed);
        }

        /// <summary>
        /// Exposes each group's volume under the parameter name
        /// <see cref="Systems_AudioMix"/> writes to, and reports which ones took.
        ///
        /// The report is the point. An exposed parameter that is missing fails
        /// SILENTLY at runtime — <c>AudioMixer.SetFloat</c> returns false and
        /// nothing is heard to change — which is precisely the class of silent
        /// mismatch this project already has a rule about for brains. Better to
        /// say so at build time.
        /// </summary>
        private static void Expose(AudioMixer mixer)
        {
            var report = new StringBuilder("Editor_AudioMixer: exposed parameters — ");
            bool allPresent = true;

            allPresent &= Report(mixer, Systems_AudioMix.PARAM_MASTER, report);
            for (int busIndex = 0; busIndex < Busses.Length; busIndex++)
            {
                allPresent &= Report(mixer, Busses[busIndex].parameter, report);
            }

            if (allPresent)
            {
                Debug.Log(report.ToString());
            }
            else
            {
                // A warning rather than an error: the fallback covers it.
                report.Append("\nA missing parameter has to be exposed by hand: open the mixer, " +
                              "right-click the group's volume slider, Expose, and rename it to " +
                              "the name above. Until then Systems_AudioMix drives that bus " +
                              "through per-source gains instead.");
                Debug.LogWarning(report.ToString());
            }
        }

        private static bool Report(AudioMixer mixer, string parameter, StringBuilder report)
        {
            // Setting and reading back is the only check that means anything:
            // the mixer answers false for a name it does not carry.
            bool present = mixer.SetFloat(parameter, 0f);
            report.Append(parameter).Append(present ? " ok" : " MISSING").Append("; ");
            if (present)
            {
                mixer.ClearFloat(parameter);
            }
            return present;
        }

        private static Type FindControllerType()
        {
            return typeof(UnityEditor.Editor).Assembly.GetType(CONTROLLER_TYPE);
        }

        /// <summary>
        /// Prints what the internal mixer type actually offers. Diagnostic: run
        /// it when <see cref="Build"/> reports that a signature moved.
        /// </summary>
        [MenuItem("PoBox/Audio/Probe Mixer API")]
        public static void Probe()
        {
            Type controllerType = FindControllerType();
            if (controllerType == null)
            {
                Debug.LogWarning("Editor_AudioMixer: " + CONTROLLER_TYPE + " not found at all.");
                return;
            }

            var report = new StringBuilder();
            report.AppendLine("Editor_AudioMixer PROBE: " + controllerType.FullName);
            MethodInfo[] methods = controllerType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
            {
                MethodInfo method = methods[methodIndex];
                string name = method.Name;
                if (name.IndexOf("Create", StringComparison.Ordinal) < 0 &&
                    name.IndexOf("Group", StringComparison.Ordinal) < 0 &&
                    name.IndexOf("Expose", StringComparison.Ordinal) < 0 &&
                    name.IndexOf("Parent", StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                report.Append(method.IsStatic ? "  static " : "  ").Append(name).Append('(');
                ParameterInfo[] parameters = method.GetParameters();
                for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameterIndex > 0) { report.Append(", "); }
                    report.Append(parameters[parameterIndex].ParameterType.Name);
                }
                report.Append(") -> ").AppendLine(method.ReturnType.Name);
            }

            // The parameter-path types and the group's own volume handle. These
            // are what the first attempt at Build got wrong: AddExposedParameter
            // takes a path object whose constructor is not the obvious one.
            ProbeConstructors("UnityEditor.Audio.AudioGroupParameterPath", report);
            ProbeConstructors("UnityEditor.Audio.AudioParameterPath", report);
            ProbeMembers("UnityEditor.Audio.AudioMixerGroupController", report,
                "GUID", "Volume", "Pitch");
            ProbeMembers("UnityEditor.Audio.ExposedAudioParameter", report, string.Empty);

            PropertyInfo exposed = controllerType.GetProperty(
                "exposedParameters",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            report.AppendLine("  exposedParameters property: " +
                              (exposed != null ? exposed.PropertyType.Name : "NOT FOUND"));

            Debug.Log(report.ToString());
        }

        private static void ProbeConstructors(string typeName, StringBuilder report)
        {
            Type type = typeof(UnityEditor.Editor).Assembly.GetType(typeName);
            if (type == null)
            {
                report.AppendLine("  " + typeName + ": NOT FOUND");
                return;
            }
            report.AppendLine("  " + typeName + " constructors:");
            ConstructorInfo[] constructors = type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int index = 0; index < constructors.Length; index++)
            {
                report.Append("    ctor(");
                ParameterInfo[] parameters = constructors[index].GetParameters();
                for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameterIndex > 0) { report.Append(", "); }
                    report.Append(parameters[parameterIndex].ParameterType.Name);
                }
                report.AppendLine(")");
            }
        }

        private static void ProbeMembers(string typeName, StringBuilder report, params string[] filters)
        {
            Type type = typeof(UnityEditor.Editor).Assembly.GetType(typeName);
            if (type == null)
            {
                report.AppendLine("  " + typeName + ": NOT FOUND");
                return;
            }
            report.AppendLine("  " + typeName + " members:");
            MemberInfo[] members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            for (int index = 0; index < members.Length; index++)
            {
                string name = members[index].Name;
                bool wanted = filters.Length == 0;
                for (int filterIndex = 0; filterIndex < filters.Length; filterIndex++)
                {
                    if (string.IsNullOrEmpty(filters[filterIndex]) ||
                        name.IndexOf(filters[filterIndex], StringComparison.Ordinal) >= 0)
                    {
                        wanted = true;
                        break;
                    }
                }
                if (wanted)
                {
                    report.AppendLine($"    {members[index].MemberType} {name}");
                }
            }
        }
    }
}
