using UnityEngine;

namespace PoBox.Rl
{
    /// <summary>
    /// Collider pairs that must ignore each other, applied when the scene starts. Physics.IgnoreCollision is
    /// not saved with a scene, so the list is kept here and re-applied every time.
    ///
    /// These are the pairs the model excludes: links joined by a joint, whose shapes overlap by construction
    /// (the thigh starts inside the pelvis). Without this list they push each other apart with everything
    /// the solver has, the shoulders are driven to their stops in a tenth of a second, and the fighter folds.
    /// </summary>
    public class MjcfContactExcludes : MonoBehaviour
    {
        public Collider[] a = new Collider[0];
        public Collider[] b = new Collider[0];

        void Awake() => Apply();
        void OnEnable() => Apply();

        public void Apply()
        {
            for (int i = 0; i < a.Length && i < b.Length; i++)
                if (a[i] != null && b[i] != null) Physics.IgnoreCollision(a[i], b[i], true);
        }
    }
}
