using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds and saves the rigging/calibration staging scene SCN_RIGSTAGE:
    /// max-friction ground, 1 m² semi-transparent scale markers (project
    /// rule), a Staging_Area drop node, and a jointed calibration dummy.
    /// </summary>
    internal static class RigTool_StagingScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_RIGSTAGE.unity";
        private const string MARKER_MATERIAL_FOLDER = "Assets/Art";
        private const string MARKER_MATERIAL_PATH = "Assets/Art/Materials/M_ScaleMarker.mat";
        private const int MARKER_GRID_HALF = 4;

        [MenuItem("Tools/ML Boxing/4. Create Staging Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            BuildGround();
            BuildScaleMarkers();
            new GameObject("Staging_Area").transform.position = new Vector3(-3f, 0f, 0f);
            BuildCalibrationDummy();
            FrameCamera();

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log("RigTool: staging scene saved to " + SCENE_PATH);
        }

        private static void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2f, 1f, 2f); // 20 x 20 m
            var highFriction = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);
            if (highFriction != null)
            {
                ground.GetComponent<Collider>().sharedMaterial = highFriction;
            }
        }

        private static void BuildScaleMarkers()
        {
            Material markerMaterial = GetOrCreateMarkerMaterial();
            var parent = new GameObject("ScaleMarkers");
            for (int gridX = -MARKER_GRID_HALF; gridX < MARKER_GRID_HALF; gridX++)
            {
                for (int gridZ = -MARKER_GRID_HALF; gridZ < MARKER_GRID_HALF; gridZ++)
                {
                    // Checkerboard: only every second cell gets a marker quad.
                    if (((gridX + gridZ) & 1) != 0)
                    {
                        continue;
                    }
                    GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = $"Marker_{gridX}_{gridZ}";
                    Object.DestroyImmediate(quad.GetComponent<Collider>());
                    quad.transform.SetParent(parent.transform, false);
                    quad.transform.SetPositionAndRotation(
                        new Vector3(gridX + 0.5f, 0.001f, gridZ + 0.5f),
                        Quaternion.Euler(90f, 0f, 0f));
                    quad.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;
                }
            }
        }

        private static void BuildCalibrationDummy()
        {
            var anchor = new GameObject("CalibrationDummy_Anchor");
            anchor.transform.position = new Vector3(2f, 2.2f, 0f);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "CalibrationDummy";
            body.transform.position = new Vector3(2f, 1.1f, 0f);
            var dummyBody = body.AddComponent<Rigidbody>();
            dummyBody.mass = 40f;

            var joint = body.AddComponent<ConfigurableJoint>();
            joint.connectedBody = null; // anchored to world at the anchor point
            joint.anchor = new Vector3(0f, 1.1f, 0f);
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -60f };
            joint.highAngularXLimit = new SoftJointLimit { limit = 60f };
            joint.angularYLimit = new SoftJointLimit { limit = 30f };
            joint.angularZLimit = new SoftJointLimit { limit = 60f };
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive { positionSpring = 800f, positionDamper = 40f, maximumForce = 2000f };
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
        }

        private static void FrameCamera()
        {
            Camera camera = Camera.main;
            if (camera != null)
            {
                camera.transform.SetPositionAndRotation(
                    new Vector3(0f, 1.6f, -6f),
                    Quaternion.Euler(8f, 0f, 0f));
            }
        }

        private static Material GetOrCreateMarkerMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MARKER_MATERIAL_PATH);
            if (existing != null)
            {
                return existing;
            }
            if (!AssetDatabase.IsValidFolder(MARKER_MATERIAL_FOLDER))
            {
                AssetDatabase.CreateFolder("Assets", "Art");
            }
            if (!AssetDatabase.IsValidFolder(MARKER_MATERIAL_FOLDER + "/Materials"))
            {
                AssetDatabase.CreateFolder(MARKER_MATERIAL_FOLDER, "Materials");
            }

            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
            {
                color = new Color(1f, 1f, 1f, 0.15f)
            };
            material.SetFloat("_Surface", 1f); // transparent
            material.SetFloat("_Blend", 0f);   // alpha blend
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            AssetDatabase.CreateAsset(material, MARKER_MATERIAL_PATH);
            return material;
        }
    }
}
