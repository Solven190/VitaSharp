#!/usr/bin/env bash
#
# Lists the HLE functions whose argument registers differ from their VitaSDK prototype (vita-headers include/psp2):
# argument order, count, 64-bit pairs, stack arguments, floats/doubles in VFP registers (VitaSharp.Desktop hle-abi-check).
# A report, not a gate: the headers can be wrong too, so check a mismatch against the firmware module before changing
# the HLE. Exit 1 when a MISMATCH is listed.
# Usage: build/hle-abi-check.sh [vita-headers folder] [--fewer]   (same as build/hle-abi-check.ps1)
#   vita-headers folder: a checkout of github.com/vitasdk/vita-headers; default: a shallow clone into a temp folder.
#   --fewer: also list bindings that read only a prefix of the prototype's arguments.
#   env: EXE (a built VitaSharp.Desktop to use), OUT (build folder when EXE is not set; default a temp folder).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly HEADERS_URL="https://github.com/vitasdk/vita-headers.git"

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,11s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local headers=""
  local -a extra=()
  local arg
  for arg in "$@"; do
    if [[ "${arg}" == "--fewer" ]]; then
      extra+=("--fewer")
    else
      headers="${arg}"
    fi
  done
  if [[ -z "${headers}" ]]; then
    headers="${TMPDIR:-/tmp}/vitasharp-vita-headers"
    if [[ ! -d "${headers}/include/psp2" ]]; then
      git clone --depth 1 "${HEADERS_URL}" "${headers}"
    fi
  fi
  local exe="${EXE:-}"
  if [[ -z "${exe}" ]]; then
    local out="${OUT:-${TMPDIR:-/tmp}/vitasharp-hle-abi-check}"
    dotnet build "${ROOT}/src/VitaSharp.Desktop" -c Release -m:2 -p:UseSharedCompilation=false -v q -o "${out}"
    exe="${out}/VitaSharp.Desktop"
  fi
  "${exe}" hle-abi-check "--headers=${headers}" "${extra[@]+"${extra[@]}"}"
}

main "$@"
