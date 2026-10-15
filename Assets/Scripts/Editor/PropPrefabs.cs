using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoBox.EditorTools
{
    /// <summary>
    /// The corner props (Assets/Models/Props, made in BLENDER/2026-10-14_corner-props): each GLB gets lightmap
    /// UVs and a prefab in Assets/Prefabs/Props, static, outside the MuJoCo world. The stool and the bucket are
    /// only in the ring at the break (ArenaMood.breakProps switches them), so they take their light from the
    /// probes rather than leave a baked shadow behind when they are gone.
    /// </summary>
    static class PropPrefabs
    {
        const string Models = "Assets/Models/Props/", Prefabs = "Assets/Prefabs/Props/";
        static readonly string[] Fixed = { "RingBell", "TurnbucklePad_Red", "TurnbucklePad_Blue", "TurnbucklePad_White" };
        static readonly string[] AtTheBreak = { "CornerStool", "WaterBucket" };

        [MenuItem("PoBox/Props/Build Corner Prop Prefabs")]
        static void Build()
        {
            foreach (string n in Fixed) Make(n, true);
            foreach (string n in AtTheBreak) Make(n, false);
            AssetDatabase.SaveAssets();
        }

        static void Make(string name, bool baked)
        {
            string glb = Models + name + ".glb";
            var importer = AssetImporter.GetAtPath(glb);
            if (importer == null) { Debug.LogError("No " + glb); return; }
            var so = new SerializedObject(importer);
            SerializedProperty uv2 = so.FindProperty("editorImportSettings.generateSecondaryUVSet");
            if (uv2 != null && !uv2.boolValue)
            {
                uv2.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                importer.SaveAndReimport();
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(glb);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                go.name = name;
                var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
                if (baked) flags |= StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic;
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                {
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
                    if (t.TryGetComponent(out MeshRenderer r))
                        r.receiveGI = baked ? ReceiveGI.Lightmaps : ReceiveGI.LightProbes;
                }
                PrefabUtility.SaveAsPrefabAsset(go, Prefabs + name + ".prefab");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
