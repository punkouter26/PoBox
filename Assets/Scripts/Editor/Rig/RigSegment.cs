namespace PoBox.Editor
{
    /// <summary>
    /// Rig segments in canonical order. Non-pelvis order defines the joint
    /// list order in Systems_FighterRig, which defines the action mapping —
    /// it must never change once brains are trained.
    /// </summary>
    internal enum RigSegment
    {
        Pelvis,
        Torso,
        Head,
        ThighL,
        ShinL,
        FootL,
        ThighR,
        ShinR,
        FootR,
        UpperArmL,
        ForearmL,
        GloveL,
        UpperArmR,
        ForearmR,
        GloveR
    }
}
