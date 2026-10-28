#!/bin/bash
# Build libFNA3D.so.0 (FNA3D 24.04 + ASTC and R8 surface formats) for aarch64/SDL2.
# The patches are described in ../BUILD.md.

set -e
export DEBIAN_FRONTEND=noninteractive

apt-get update -qq >/dev/null
apt-get install -y -qq --no-install-recommends \
    build-essential cmake ninja-build git ca-certificates pkg-config libsdl2-dev >/dev/null

cd /tmp
git clone --recursive --depth 1 -b 24.04 https://github.com/FNA-XNA/FNA3D.git
cd FNA3D

git apply /host/fna3d-astc-r8.patch

(cd MojoShader && git apply -p2 /host/mojoshader-glsles3-highp.patch)
grep -A2 '"#define texture2DRectLod textureLod"' MojoShader/profiles/mojoshader_profile_glsl.c \
    | grep -q 'precision highp float' || { echo "highp patch did not apply"; exit 1; }

# Drop the Vulkan driver: the launcher forces OpenGL, and FNA3D_Driver_Vulkan.c
# is fully guarded by #if FNA3D_DRIVER_VULKAN
sed -i '/-DFNA3D_DRIVER_VULKAN/d' CMakeLists.txt

cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build build --parallel

cp -v build/libFNA3D.so.0.* /host/
readelf -d build/libFNA3D.so.0.* | grep -E "NEEDED.*SDL"
