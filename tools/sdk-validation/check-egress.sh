#!/bin/bash
set -euo pipefail

# Run only synthetic contracts. Trace destination metadata, never request payloads.
strace -f -e trace=connect -o /tmp/jarvis-connect.trace \
  dotnet /validation/tools/sdk-validation/bin/Release/net10.0/SdkValidation.dll --contracts

if grep -E 'AF_INET6?|sin6?_addr' /tmp/jarvis-connect.trace \
  | grep -vE 'inet_addr\("127\.0\.0\.1"\)|inet_pton\(AF_INET6, "(::1|::ffff:127\.0\.0\.1)"' \
  > /tmp/jarvis-nonloopback.trace; then
  echo "FAIL non-loopback connection attempts observed:"
  cat /tmp/jarvis-nonloopback.trace
  exit 1
fi
echo "PASS no non-loopback IPv4/IPv6 connect attempts observed during synthetic runtime contracts."
echo "Connection destination metadata:"
grep -E 'AF_INET6?|sin6?_addr' /tmp/jarvis-connect.trace | sort -u
