using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PoBox.Rl
{
    /// <summary>
    /// The handful of MuJoCo's C functions the game calls. The library is Assets/Plugins/x86_64/mujoco.dll,
    /// taken from the same Python package the trainer uses, so it is the same version of the same engine.
    /// </summary>
    internal static class MuJoCoNative
    {
        const string Lib = "mujoco";

        // mjtState bits: which parts of the simulation state a get or set is about.
        public const int StateQPos = 2, StateQVel = 4, StateCtrl = 64;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mj_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern IntPtr mj_parseXMLString([MarshalAs(UnmanagedType.LPStr)] string xml, IntPtr vfs, StringBuilder error, int errorSize);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mj_compile(IntPtr spec, IntPtr vfs);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_deleteSpec(IntPtr spec);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mj_makeData(IntPtr model);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_deleteData(IntPtr data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_deleteModel(IntPtr model);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_resetDataKeyframe(IntPtr model, IntPtr data, int key);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_step(IntPtr model, IntPtr data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_forward(IntPtr model, IntPtr data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mj_stateSize(IntPtr model, int spec);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_getState(IntPtr model, IntPtr data, [Out] double[] state, int spec);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mj_setState(IntPtr model, IntPtr data, [In] double[] state, int spec);
    }
}
