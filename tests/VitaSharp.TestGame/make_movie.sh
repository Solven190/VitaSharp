#!/usr/bin/env bash
#
# Makes assets/vstest.mp4, the movie of the "video" area (src/test_video.c): 4 s, 480x272, 30 fps, H.264 Main with
# B frames (a reordering stream, like the games' movies) and AAC 48 kHz stereo. The file is committed; run this only
# to change it.
#
# Usage: ./make_movie.sh   (make_movie.ps1 on Windows)
#
# Requirements: ffmpeg with libx264 and the native AAC encoder on PATH.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR

#######################################
# Encodes the test movie.
# Arguments:
#   None (or -h/--help).
#######################################
main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,9s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  if ! command -v ffmpeg >/dev/null 2>&1; then
    echo "make_movie.sh: ffmpeg not found on PATH" >&2
    return 1
  fi
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc2=size=480x272:rate=30:duration=4" \
    -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=4" \
    -c:v libx264 -profile:v main -bf 2 -g 30 -pix_fmt yuv420p \
    -c:a aac -ac 2 -b:a 96k -movflags +faststart -map_metadata -1 -fflags +bitexact \
    "${SCRIPT_DIR}/assets/vstest.mp4"
  echo "wrote ${SCRIPT_DIR}/assets/vstest.mp4"
}

main "$@"
