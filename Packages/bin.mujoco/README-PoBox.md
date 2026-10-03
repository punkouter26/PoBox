# bin.mujoco, embedded

From https://github.com/joanllobera/mujoco-bin at tag 3.5.0, with one change: that tag's
`Runtime/Plugins/Android/libmujoco.so` is MuJoCo **3.3.7** (its version string says so), while the Windows
library and the plugin are 3.5.0. The two differ in how mjModel and mjData are laid out, so a phone would read
the wrong numbers. `Runtime/Plugins/Android/libmujoco.so` here is MuJoCo 3.5.0 built for arm64-v8a, Android 31:

    git clone --depth 1 --branch 3.5.0 https://github.com/google-deepmind/mujoco.git
    # src/engine/engine_util_errmem.c: add `|| defined(__ANDROID__)` to the localtime_r line (Bionic has it)
    cmake -S mujoco -B build-android -G Ninja       -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake -DANDROID_ABI=arm64-v8a       -DANDROID_PLATFORM=android-31 -DCMAKE_BUILD_TYPE=Release -DMUJOCO_BUILD_EXAMPLES=OFF       -DMUJOCO_BUILD_SIMULATE=OFF -DMUJOCO_BUILD_TESTS=OFF -DMUJOCO_TEST_PYTHON_UTIL=OFF
    cmake --build build-android --target mujoco
    llvm-strip --strip-debug -o libmujoco.so build-android/lib/libmujoco.so

NDK 27.2.12479018 (r27c). It needs only libc, libm and libdl.
