using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Lets an outside process run an editor method inside the ALREADY-OPEN
    /// Editor, by dropping a file.
    ///
    /// WHY THIS EXISTS. Unity allows exactly one process per project, so
    /// `Unity.exe -batchmode -executeMethod ...` cannot run while the Editor is
    /// open — it exits 1 the moment it resolves the project path. That makes
    /// every scene tool in this repo unreachable to automation unless somebody
    /// closes the Editor first, which is a poor trade when the Editor is the
    /// thing doing the work anyway.
    ///
    /// HOW IT WORKS. Write a file containing one fully-qualified method name:
    ///
    ///   echo PoBox.Editor.RigTool_MattDemoScene.BuildAll > Temp/agent-command.txt
    ///
    /// The Editor picks it up within a second, deletes it (so a command runs
    /// EXACTLY ONCE — a crash must not leave a file that re-fires on every
    /// reload), invokes the method, and writes stdout-ish results to
    /// Temp/agent-command-result.txt for the caller to read.
    ///
    /// SAFETY. It only invokes public/non-public STATIC methods taking no
    /// arguments, and only inside the PoBox.Editor namespace. That is enough
    /// for the RigTool_* and Build_* entry points and nothing else. Temp/ is
    /// gitignored, so neither the trigger nor the result is committable.
    ///
    /// This is a development convenience, not a shipping feature. It lives in
    /// an Editor-only assembly and cannot reach a player build.
    /// </summary>
    [InitializeOnLoad]
    internal static class Editor_CommandBridge
    {
        private const string CommandPath = "Temp/agent-command.txt";
        private const string ResultPath = "Temp/agent-command-result.txt";
        private const string RequiredNamespace = "PoBox.Editor.";
        private const double PollSeconds = 0.5;

        private static double _nextPoll;

        static Editor_CommandBridge()
        {
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) { return; }
            _nextPoll = EditorApplication.timeSinceStartup + PollSeconds;
            if (!File.Exists(CommandPath)) { return; }

            string request;
            try
            {
                request = File.ReadAllText(CommandPath).Trim();
            }
            catch (IOException)
            {
                // Still being written. Try again on the next poll.
                return;
            }

            // Delete BEFORE invoking. A command that throws, or an Editor that
            // dies mid-build, must not leave a trigger that re-fires forever.
            try { File.Delete(CommandPath); } catch (IOException) { }
            if (string.IsNullOrEmpty(request)) { return; }

            Debug.Log($"Editor_CommandBridge: running {request}");
            string result = Invoke(request);
            try { File.WriteAllText(ResultPath, result); } catch (IOException) { }
            Debug.Log($"Editor_CommandBridge: {result}");
        }

        private static string Invoke(string qualifiedMethod)
        {
            if (!qualifiedMethod.StartsWith(RequiredNamespace, StringComparison.Ordinal))
            {
                return $"REFUSED {qualifiedMethod}: only {RequiredNamespace}* may be invoked.";
            }
            int split = qualifiedMethod.LastIndexOf('.');
            if (split <= 0)
            {
                return $"REFUSED {qualifiedMethod}: expected Namespace.Type.Method.";
            }
            string typeName = qualifiedMethod.Substring(0, split);
            string methodName = qualifiedMethod.Substring(split + 1);

            Type type = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, throwOnError: false);
                if (type != null) { break; }
            }
            if (type == null) { return $"FAILED {qualifiedMethod}: type not found."; }

            MethodInfo method = type.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (method == null)
            {
                return $"FAILED {qualifiedMethod}: no static parameterless method by that name.";
            }

            try
            {
                method.Invoke(null, null);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return $"OK {qualifiedMethod}";
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                return $"THREW {qualifiedMethod}: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}";
            }
        }
    }
}
