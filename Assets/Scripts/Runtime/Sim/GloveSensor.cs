using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// Sits on the two forearms only. A punch hands its momentum over across several physics steps, so the
    /// impulse of a hit is the sum over the touch, and that needs the stay callback as well as the enter.
    /// It is a separate component because declaring OnCollisionStay on every body part would have the
    /// engine call it for both feet on the canvas every step for nothing.
    /// </summary>
    [RequireComponent(typeof(BodyPart))]
    public class GloveSensor : MonoBehaviour
    {
        BodyPart _part;

        void Awake() => _part = GetComponent<BodyPart>();

        void OnCollisionStay(Collision c)
        {
            if (_part != null && _part.owner != null) _part.owner.OnPartTouched(_part, c, false);
        }
    }
}
