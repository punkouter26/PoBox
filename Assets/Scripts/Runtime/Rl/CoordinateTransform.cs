using UnityEngine;

namespace PoBox.Rl
{
    /// <summary>
    /// The one place the trainer's frame and Unity's frame meet. As in PoDecath, from which this is taken.
    ///
    /// The trainer (MuJoCo) is right-handed with z up: x forward, y left. Unity is left-handed with y up.
    ///
    ///   position / vector : (x, y, z)        -> (x, z, y)
    ///   quaternion        : (qx, qy, qz, qw) -> (-qx, -qz, -qy, qw)
    ///
    /// Both are their own inverse. Because the mapping is a reflection, a turn of +t about an axis in one
    /// frame is a turn of -t about the mapped axis in the other: joint angles change sign (MjcfRig.sign),
    /// and so does an angular velocity.
    /// </summary>
    public static class CoordinateTransform
    {
        public static Vector3 ExternalToUnity(Vector3 v) => new Vector3(v.x, v.z, v.y);
        public static Vector3 UnityToExternal(Vector3 v) => new Vector3(v.x, v.z, v.y);

        public static Quaternion ExternalToUnity(Quaternion q) => new Quaternion(-q.x, -q.z, -q.y, q.w);

        /// <summary>Writes a Unity-frame vector into a flat buffer in the trainer's (x, y, z) order.</summary>
        public static void Write(float[] dst, int offset, Vector3 unityVector)
        {
            dst[offset + 0] = unityVector.x;
            dst[offset + 1] = unityVector.z;
            dst[offset + 2] = unityVector.y;
        }

        /// <summary>The same for an angular velocity, which flips sign under the reflection.</summary>
        public static void WriteAngular(float[] dst, int offset, Vector3 unityVector)
        {
            dst[offset + 0] = -unityVector.x;
            dst[offset + 1] = -unityVector.z;
            dst[offset + 2] = -unityVector.y;
        }
    }
}
