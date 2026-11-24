set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
out=${1:?Pass a build directory}
mkdir -p "$out"
out=$(cd "$out" && pwd)
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir -p "$out/bin" "$out/notices" "$out/source"
fetch() {
  curl --fail --silent --show-error --location --retry 3 "https://codeload.github.com/$1/tar.gz/$2" -o "$out/source/$3.tar.gz"
  mkdir "$stage/$3"
  tar -xf "$out/source/$3.tar.gz" -C "$stage/$3" --strip-components=1
}
fetch quatric/mobipeg 70b28872f306e45dc67a9587f65815f8fd21836c ffmpeg
fetch xiph/opus ddbe48383984d56acd9e1ab6a090c54ca6b735a6 opus
fetch mackron/miniaudio f40cf03f80cdb7e741d43e53b7e706e8c1394bcf miniaudio
jobs=$(getconf _NPROCESSORS_ONLN)
cd "$stage/ffmpeg"
./configure --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug --disable-x86asm \
  --enable-gpl --enable-version3 --disable-ffplay --disable-ffprobe --enable-ffmpeg \
  --enable-protocol=file,pipe --enable-demuxer=bink,wav --enable-decoder=bink,bink2,binkaudio_dct,binkaudio_rdft,xma1,xma2,pcm_s16le,pcm_s24le,pcm_s32le,pcm_u8,pcm_f32le \
  --enable-filter=scale,format,aresample,aformat,trim,atrim,null,anull --enable-encoder=rawvideo,pcm_s16le --enable-muxer=rawvideo,wav --enable-swscale --enable-swresample > "$stage/config.log" || { cat ffbuild/config.log | tail -50; exit 1; }
make -j"$jobs" ffmpeg > "$stage/ffmpeg.log"
install -m 755 ffmpeg "$out/bin/ffmpeg"
cp COPYING.* "$out/notices/"
curl -fsSL --retry 3 https://www.gnu.org/licenses/agpl-3.0.txt -o "$out/notices/AGPL-3.0.txt"
cp "$stage/opus/COPYING" "$out/notices/Opus-COPYING"
cp "$stage/miniaudio/LICENSE" "$out/notices/miniaudio-LICENSE"
cmake -S "$stage/opus" -B "$stage/opus-build" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_DISABLE_INTRINSICS=ON >/dev/null
cmake --build "$stage/opus-build" --parallel "$jobs" >/dev/null
c++ -std=c++17 -O2 -I"$stage/opus/include" "$root/Tools/OpusPreview.cpp" "$stage/opus-build/libopus.a" -o "$out/bin/opus-preview"
libs=(-lpthread -ldl -lm)
if [[ $(uname) == Darwin ]]; then libs=(-framework CoreAudio -framework AudioToolbox -framework CoreFoundation); fi
cc -std=c11 -O2 -I"$stage/miniaudio" "$root/Tools/audio-output.c" "${libs[@]}" -o "$out/bin/audio-output"
"$out/bin/ffmpeg" -hide_banner -decoders 2>/dev/null | grep -E 'bink2video .*Bink' >/dev/null
cp "$root/Tools/build-unix-media.sh" "$root/Tools/audio-output.c" "$root/Tools/OpusPreview.cpp" "$out/source/"
