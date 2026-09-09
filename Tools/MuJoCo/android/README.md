# libmujoco.so for Android, built at 3.12.0

`Assets/Plugins/Android/libmujoco.so` is MuJoCo **3.12.0** compiled for
arm64-v8a. It has to be 3.12.0 and not something close: the C# bindings in
`Packages/org.mujoco` are generated per release and pin
`mjVERSION_HEADER = 3012000`, and they marshal `mjModel`/`mjData` by explicit
layout. A different minor version is not a degraded experience, it is a
segfault -- see the measurement below.

## Why not the prebuilt binaries

`https://github.com/joanllobera/mujoco-bin/` ships an arm64-v8a `libmujoco.so`
and it does load cleanly on device. It is **MuJoCo 3.3.7** -- read out of the
binary itself, and matching neither the 3.5.0 its README claims nor the 3.3.0
in its `package.json`. Against these 3.12.0 bindings the app dies about a
second after the library loads:

```
signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0 (write)
tid: ..., name: Unity Main Thre  >>> com.punkoutersoftware.pobox <<<
#00-#04  libil2cpp.so     (managed code)
```

Nine minor versions of struct-layout drift. Do not install it.

## Building it

Needs only what Unity already ships: the NDK bundled with the Android player
module (r27c here) and the CMake in the Android SDK. Keep the source in a
SHORT path -- `FetchContent` builds nested stamp directories that blow past
Windows' 260-character limit from anywhere deep, which is a confusing failure
because it looks like a network error.

```bash
git clone --depth 1 --branch 3.12.0 https://github.com/google-deepmind/mujoco.git /c/Users/<you>/mj
cd /c/Users/<you>/mj
git apply <repo>/Tools/MuJoCo/android/mujoco-3.12.0-android.patch

NDK="C:/Program Files/Unity/Hub/Editor/6000.5.6f1/Editor/Data/PlaybackEngines/AndroidPlayer/NDK"
SDKCMAKE="C:/Program Files/Unity/Hub/Editor/6000.5.6f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/cmake/3.22.1/bin"

cmake -S . -B b -G Ninja \
  -DCMAKE_MAKE_PROGRAM="$SDKCMAKE/ninja.exe" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 \
  -DCMAKE_BUILD_TYPE=Release \
  -DMUJOCO_ENABLE_AVX=OFF -DMUJOCO_ENABLE_AVX_INTRINSICS=OFF \
  -DMUJOCO_BUILD_EXAMPLES=OFF -DMUJOCO_BUILD_SIMULATE=OFF \
  -DMUJOCO_BUILD_TESTS=OFF -DMUJOCO_TEST_PYTHON_UTIL=OFF -DBUILD_TESTING=OFF

"$SDKCMAKE/ninja.exe" -C b mujoco
"$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-strip.exe" --strip-unneeded b/lib/libmujoco.so
```

`-DMUJOCO_ENABLE_AVX*=OFF` is not optional on ARM. Stripping takes it from
42 MB to 5.5 MB, which is exactly what Unity's own build produces from the
unstripped input -- so the committed file is byte-for-byte what ships.

## The two patches

Both are upstream portability gaps, not project hacks
(`mujoco-3.12.0-android.patch`, 34 lines, `src/engine/engine_util_errmem.c`):

- **`aligned_alloc`** is only declared by bionic from API 28, and this project
  ships `minSdk 26`. Uses `posix_memalign` on `__ANDROID__` instead, which has
  been there since API 16 and matches for the 64-byte alignment MuJoCo asks
  for. Still freed with plain `free()`, as the existing non-Windows path does.
- **`localtime_r`** exists on bionic, but Android does not define
  `_POSIX_C_SOURCE` by default, so the guard fell through to a hard
  `#error "Thread-safe version of localtime is not present"`. Adds
  `defined(__ANDROID__)` to the branch that was already correct.

## Measured on device, Pixel 9 Pro (Android 17), 2026-09-09

| | before | after |
|---|---|---|
| `Unable to load DLL 'mujoco'` | 2 per run | **0** |
| `Failed to create Mujoco runtime` (`MjScene.StepScene`, every tick) | 1,055-2,133 per round | **0** |
| SIGSEGV | 0 (nothing to crash) / 1 with the 3.3.7 binary | **0** |
| Nick | T-posed statue, never fell, won every round by default | simulates, and can fall |

**The cost is frame rate: 60 fps drops to about 20 during a contest.** Nick is
a 30-hinge MuJoCo body stepped at 0.02 s on a phone alongside five PhysX
ragdolls, and that is what it costs today. Worth profiling before this ships:
the likely levers are `MjScene`'s solver iteration count, Nick's decimation,
and whether the other five need to be PhysX at all.
