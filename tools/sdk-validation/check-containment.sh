#!/bin/bash
set -euo pipefail

for flags_file in /sys/class/net/*/flags; do
  interface=$(basename "$(dirname "$flags_file")")
  flags=$(cat "$flags_file")
  if [ "$interface" != "lo" ] && (( (flags & 1) != 0 )); then
    echo "FAIL non-loopback interface is up; use Docker --network none."
    exit 1
  fi
done
dotnet /validation/tools/sdk-validation/bin/Release/net10.0/SdkValidation.dll --diagnose-egress-block
strace -f -e trace=connect -o /tmp/jarvis-connect.trace \
  dotnet /validation/tools/sdk-validation/bin/Release/net10.0/SdkValidation.dll --contracts

grep -E 'AF_INET6?|sin6?_addr' /tmp/jarvis-connect.trace \
  | grep -vE 'inet_addr\("127\.0\.0\.1"\)|inet_pton\(AF_INET6, "(::1|::ffff:127\.0\.0\.1)"' \
  > /tmp/jarvis-external.trace || test ! -s /tmp/jarvis-external.trace
if grep -v ' = -1 ENETUNREACH ' /tmp/jarvis-external.trace > /tmp/jarvis-unexpected.trace; then
  echo "FAIL external connection was not rejected as unreachable."
  cat /tmp/jarvis-unexpected.trace
  exit 1
fi
echo "PASS synthetic runtime contracts completed in a loopback-only network namespace."
echo "Observed blocked external connect attempts: $(wc -l < /tmp/jarvis-external.trace)"
echo "Blocked destination metadata (not proof of no attempted egress):"
cat /tmp/jarvis-external.trace | sort -u
