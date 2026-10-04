#!/bin/bash
set -euo pipefail

# Synthetic, credential-free runs only; never use payload tracing with live modes.
for mode in --diagnose-listener --diagnose-runtime; do
  trace="/tmp/jarvis-${mode#--}.trace"
  strace -f -e trace=execve,clone,clone3,connect,sendmmsg -s 160 -o "$trace" \
    dotnet /validation/tools/sdk-validation/bin/Release/net10.0/SdkValidation.dll "$mode"
  echo "DNS attribution for $mode:"
  echo "Container hostname: $(hostname)"
  grep 'execve.*copilot-runtime' "$trace" || true
  grep 'htons(53)' "$trace" || true
  grep 'sendmmsg' "$trace" || true
  if [ "$mode" = "--diagnose-listener" ]; then
    if grep -q 'execve.*copilot-runtime' "$trace"; then
      echo "FAIL listener baseline unexpectedly launched the runtime."
      exit 1
    fi
  elif grep -q 'htons(53)' "$trace"; then
    echo "FAIL runtime-only startup baseline attempted DNS."
    exit 1
  fi
done
