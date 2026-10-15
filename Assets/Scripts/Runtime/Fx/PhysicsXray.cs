using UnityEngine;

namespace PoBox.Fx
{
    /// <summary>
    /// The debug X-ray: the MuJoCo collision shapes drawn over the boxers, hot where they touch. Off by default;
    /// the debug panel's X-RAY chip sets <see cref="On"/>.
    /// The drawing is XrayShapes (Fx/Xray, its own assembly, as it reads mjData through pointers), which makes
    /// itself on load; this holds only the switch, where the HUD can reach it.
    /// </summary>
    public class PhysicsXray : MonoBehaviour
    {
        public static bool On;
    }
}
