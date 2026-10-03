#!/bin/bash
set -Eeuo pipefail
cd "$(dirname "$0")"
ROOT="$PWD"
exec 3>&1
PREFIX="$ROOT/prefix"
export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig:$PREFIX/share/pkgconfig"
export PKG_CONFIG_PATH=""
export CC=x86_64-w64-mingw32-gcc-posix
export CXX=x86_64-w64-mingw32-g++-posix
export AR=x86_64-w64-mingw32-ar
export RANLIB=x86_64-w64-mingw32-ranlib
export CFLAGS='-O2 -D_WIN32_WINNT=0x0A00'
export CXXFLAGS="$CFLAGS"
export LDFLAGS='-static-libgcc -static-libstdc++ -static'
mkdir -p "$PREFIX" logs build stamps
python3 unpack.py
cat > cross.ini <<EOF
[binaries]
c = '$CC'
cpp = '$CXX'
ar = '$AR'
strip = 'x86_64-w64-mingw32-strip'
windres = 'x86_64-w64-mingw32-windres'
dlltool = 'x86_64-w64-mingw32-dlltool'
pkg-config = 'pkg-config'
[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
[properties]
needs_exe_wrapper = true
[built-in options]
c_args = ['-D_WIN32_WINNT=0x0A00']
cpp_args = ['-D_WIN32_WINNT=0x0A00']
c_link_args = ['-static-libgcc', '-static-libstdc++', '-static']
cpp_link_args = ['-static-libgcc', '-static-libstdc++', '-static']
EOF
cat > toolchain.cmake <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_C_COMPILER $CC)
set(CMAKE_CXX_COMPILER $CXX)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
set(CMAKE_FIND_ROOT_PATH "$PREFIX" /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
EOF
meson_build() {
 local name="$1"; shift
 meson setup "build/$name" "src/$name" --cross-file cross.ini --prefix "$PREFIX" --libdir lib --buildtype release --default-library static --wrap-mode nodownload "$@"
 ninja -C "build/$name" -j6
 ninja -C "build/$name" install
}
cmake_build() {
 local name="$1"; shift
 cmake -S "src/$name" -B "build/$name" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$ROOT/toolchain.cmake" -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF "$@"
 cmake --build "build/$name" -j6
 cmake --install "build/$name"
}
step() {
 local name="$1"; shift
 if [[ -e stamps/$name ]]; then return; fi
 echo "Building $name"
 trap 'echo "Build failed: see logs/$name.log" >&3' ERR
 "$@" > "logs/$name.log" 2>&1
 trap - ERR
 touch "stamps/$name"
}
build_headers() {
 make -C src/ffnvcodec PREFIX="$PREFIX" install
 mkdir -p "$PREFIX/include/AMF"
 cp -r src/AMF/amf/public/include/* "$PREFIX/include/AMF/"
}
build_x264() {
 (cd src/x264; ./configure --host=x86_64-w64-mingw32 --cross-prefix=x86_64-w64-mingw32- --prefix="$PREFIX" --enable-static --disable-cli --disable-opencl; make -j6; make install)
}
build_shaderc() {
 local dependency
 for dependency in glslang spirv-headers spirv-tools; do
  cp -a "src/$dependency/." "src/shaderc/third_party/$dependency/"
 done
 cmake_build shaderc -DSHADERC_SKIP_TESTS=ON -DSHADERC_SKIP_EXAMPLES=ON -DSHADERC_SKIP_COPYRIGHT_CHECK=ON -DSPIRV_SKIP_TESTS=ON -DSPIRV_SKIP_EXECUTABLES=ON -DENABLE_GLSLANG_BINARIES=OFF
 # mpv requests shaderc; the combined static library retains its dependencies.
 cp "$PREFIX/lib/pkgconfig/shaderc_combined.pc" "$PREFIX/lib/pkgconfig/shaderc.pc"
}
build_placebo() {
 mkdir -p src/libplacebo/3rdparty/glad src/libplacebo/3rdparty/fast_float
 cp -a src/glad/. src/libplacebo/3rdparty/glad/
 cp -a src/fast_float/. src/libplacebo/3rdparty/fast_float/
 meson_build libplacebo -Dvulkan=disabled -Dopengl=enabled -Dd3d11=enabled -Dshaderc=enabled -Dglslang=disabled -Dlcms=enabled -Ddemos=false -Dtests=false
}
build_spirv_cross() {
 cmake_build spirv-cross -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_STATIC=ON -DSPIRV_CROSS_SHARED=OFF
 cp "$PREFIX/lib/pkgconfig/spirv-cross-c.pc" "$PREFIX/lib/pkgconfig/spirv-cross-c-shared.pc"
}
build_ffmpeg() {
 mkdir -p build/ffmpeg
 (cd build/ffmpeg; "$ROOT/src/ffmpeg/configure" --prefix="$PREFIX" --arch=x86_64 --target-os=mingw32 --cross-prefix=x86_64-w64-mingw32- --cc="$CC" --cxx="$CXX" --enable-cross-compile --pkg-config=pkg-config --pkg-config-flags=--static --enable-gpl --enable-version3 --disable-shared --enable-static --disable-autodetect --enable-libx264 --enable-libdav1d --enable-libvpl --enable-amf --enable-nvenc --enable-nvdec --enable-ffnvcodec --enable-d3d11va --enable-dxva2 --enable-schannel --enable-zlib --disable-doc --disable-ffplay --extra-cflags="-I$PREFIX/include -D_WIN32_WINNT=0x0A00" --extra-ldflags="-L$PREFIX/lib -static -static-libgcc -static-libstdc++" --extra-libs=-lstdc++; make -j6; make install)
}
step headers build_headers
cp -r src/vulkan-headers/include/* "$PREFIX/include/"
step x264 build_x264
step zlib cmake_build zlib -DZLIB_BUILD_TESTING=OFF -DZLIB_BUILD_SHARED=OFF
cp "$PREFIX/lib/libzs.a" "$PREFIX/lib/libz.a"
step VPL cmake_build VPL -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF -DINSTALL_EXAMPLES=OFF
step dav1d meson_build dav1d -Denable_tools=false -Denable_tests=false
step ffmpeg build_ffmpeg
step freetype meson_build freetype -Dharfbuzz=disabled -Dbrotli=disabled -Dbzip2=disabled -Dpng=disabled -Dzlib=system
step fribidi meson_build fribidi -Ddocs=false -Dtests=false -Dbin=false
step harfbuzz meson_build harfbuzz -Dtests=disabled -Ddocs=disabled -Dutilities=disabled -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dicu=disabled -Dfreetype=enabled
step libass meson_build libass -Dfontconfig=disabled -Ddirectwrite=enabled -Dtest=disabled -Dcompare=disabled -Dprofile=disabled
step lcms2 meson_build lcms2 -Dutils=false -Djpeg=disabled -Dtiff=disabled
step spirv-cross build_spirv_cross
cp "$PREFIX/lib/pkgconfig/spirv-cross-c.pc" "$PREFIX/lib/pkgconfig/spirv-cross-c-shared.pc"
echo 'Libs.private: -lspirv-cross-glsl -lspirv-cross-hlsl -lspirv-cross-msl -lspirv-cross-cpp -lspirv-cross-reflect -lspirv-cross-util -lspirv-cross-core -lstdc++' >> "$PREFIX/lib/pkgconfig/spirv-cross-c-shared.pc"
step shaderc build_shaderc
step libplacebo build_placebo
step mpv meson_build mpv --default-library shared --prefer-static -Dlibmpv=true -Dcplayer=true -Dlua=disabled -Djavascript=disabled -Dmanpage-build=disabled -Dwin32-smtc=disabled -Dd3d11=enabled -Dshaderc=enabled -Dspirv-cross=enabled -Dwasapi=enabled -Dgl-win32=enabled -Dbuild-date=false
mkdir -p output
cp "$PREFIX/bin/ffmpeg.exe" "$PREFIX/bin/ffprobe.exe" "$PREFIX/bin/libmpv-2.dll" output/
x86_64-w64-mingw32-strip --strip-unneeded output/ffmpeg.exe output/ffprobe.exe output/libmpv-2.dll
echo 'Native build finished'
