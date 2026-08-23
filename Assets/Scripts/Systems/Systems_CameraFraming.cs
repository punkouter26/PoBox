using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// The one piece of camera maths both contest cameras need, in one place.
    ///
    /// Framing is specified as the world width and height a shot must CONTAIN,
    /// never as a camera distance, because the distance that achieves it
    /// depends on the aspect ratio — and this game is portrait 9:16, where a
    /// 60 degree vertical FOV is only about 34 degrees horizontal. Every
    /// hand-tuned distance in this project's history was tuned on a landscape
    /// editor window and then framed a third of what it promised on a phone.
    /// </summary>
    internal static class Systems_CameraFraming
    {
        /// <summary>
        /// Distance at which a <paramref name="widthMeters"/> x
        /// <paramref name="heightMeters"/> slab exactly fits
        /// <paramref name="camera"/>'s frustum at <paramref name="fovDegrees"/>,
        /// taking whichever of the two axes binds. On a portrait window the
        /// width is almost always what binds — the opposite of the landscape
        /// intuition.
        /// </summary>
        public static float DistanceToFrame(Camera camera, float widthMeters, float heightMeters,
            float fovDegrees, float minDistance)
        {
            float halfVertical = Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);
            float halfHorizontal = halfVertical * Mathf.Max(0.01f, camera.aspect);
            float forWidth = widthMeters * 0.5f / Mathf.Max(0.01f, halfHorizontal);
            float forHeight = heightMeters * 0.5f / Mathf.Max(0.01f, halfVertical);
            return Mathf.Max(minDistance, Mathf.Max(forWidth, forHeight));
        }
    }
}
