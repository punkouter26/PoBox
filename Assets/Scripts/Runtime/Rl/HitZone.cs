using UnityEngine;

namespace PoBox.Rl
{
    /// <summary>Says which part of a fighter a collider is, where that is finer than the link it sits on:
    /// the head is a ball on the torso link, and a hit on it is a head shot, not a body shot.</summary>
    public class HitZone : MonoBehaviour
    {
        public Sim.PartKind kind;
    }
}
