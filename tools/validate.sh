#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$ROOT"
COMMAND=${1:-}
if [ "$#" -gt 0 ]; then
  shift
fi
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
UNIT_RESULTS="artifacts/coverage/unit/$RUN_ID"
INTEGRATION_RESULTS="artifacts/coverage/integration/$RUN_ID"
TEST_RESULTS="artifacts/test-results/$RUN_ID"
VALIDATOR="tools/PersonalAgent.Validation/PersonalAgent.Validation.csproj"

restore_solution() {
  dotnet restore Jarvis.sln --locked-mode
}

run_test() {
  project=$1
  category=$2
  result_directory=$3
  log_name=$4
  shift 4
  mkdir -p "$result_directory"
  dotnet test "$project" -c Release --no-restore --filter "Category=$category" \
    --logger "trx;LogFileName=$log_name" --results-directory "$result_directory" "$@"
  dotnet run --project "$VALIDATOR" -c Release --no-restore -- verify-discovery "$result_directory/$log_name"
}

install_playwright_browser() {
  if [ "$(uname -s)" = Linux ]; then
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- playwright install --with-deps chromium
  else
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- playwright install chromium
  fi
}

latest_results_directory() {
  directory=$1
  latest=$(find "$directory" -mindepth 1 -maxdepth 1 -type d -name '[0-9]*' | LC_ALL=C sort | tail -n 1)
  if [ -z "$latest" ]; then
    echo "No timestamped test results found under $directory." >&2
    exit 1
  fi
  printf '%s\n' "$latest"
}

case "$COMMAND" in
  restore)
    dotnet restore Jarvis.sln --locked-mode
    ;;
  build)
    restore_solution
    dotnet format Jarvis.sln --verify-no-changes --severity error --no-restore
    dotnet build Jarvis.sln -c Release --no-restore -warnaserror \
      -p:TreatWarningsAsErrors=true -p:Nullable=enable
    ;;
  unit)
    restore_solution
    run_test tests/PersonalAgent.UnitTests/PersonalAgent.UnitTests.csproj Unit "$UNIT_RESULTS" unit.trx \
      --collect "XPlat Code Coverage" --settings tools/coverage.runsettings
    ;;
  integration)
    restore_solution
    run_test tests/PersonalAgent.IntegrationTests/PersonalAgent.IntegrationTests.csproj Integration \
      "$INTEGRATION_RESULTS" integration.trx \
      --collect "XPlat Code Coverage" --settings tools/coverage.runsettings
    ;;
  architecture)
    restore_solution
    run_test tests/PersonalAgent.ArchitectureTests/PersonalAgent.ArchitectureTests.csproj Architecture \
      "$TEST_RESULTS/architecture" architecture.trx
    ;;
  sdk-contracts)
    dotnet restore tools/sdk-validation/SdkValidation.csproj --locked-mode
    dotnet build tools/sdk-validation/SdkValidation.csproj -c Release --no-restore \
      -warnaserror -p:TreatWarningsAsErrors=true
    dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release \
      --no-build --no-restore -- --contracts
    restore_solution
    run_test tests/PersonalAgent.SdkContractTests/PersonalAgent.SdkContractTests.csproj SdkContract \
      "$TEST_RESULTS/sdk-contracts" sdk-contracts.trx
    ;;
  coverage)
    restore_solution
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- gate-self-test
    if [ -z "${GITHUB_BASE_REF:-}" ] && [ -z "${GITHUB_EVENT_NAME:-}" ]; then
      GITHUB_BASE_REF=main
      export GITHUB_BASE_REF
    fi
    if [ -n "${GITHUB_BASE_REF:-}" ] &&
      ! git show-ref --verify --quiet "refs/remotes/origin/$GITHUB_BASE_REF"; then
      echo "Coverage base origin/$GITHUB_BASE_REF is unavailable; fetch it before running the coverage gate." >&2
      exit 1
    fi
    unit_directory=${1:-$(latest_results_directory artifacts/coverage/unit)}
    integration_directory=${2:-$(latest_results_directory artifacts/coverage/integration)}
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- coverage \
      "$unit_directory" "$integration_directory"
    ;;
  aspire-e2e)
    restore_solution
    run_test tests/PersonalAgent.E2ETests/PersonalAgent.E2ETests.csproj AspireE2E \
      "$TEST_RESULTS/aspire-e2e" aspire-e2e.trx
    ;;
  playwright-install)
    restore_solution
    dotnet build "$VALIDATOR" -c Release --no-restore
    install_playwright_browser
    ;;
  browser-e2e)
    restore_solution
    dotnet build tests/PersonalAgent.E2ETests/PersonalAgent.E2ETests.csproj -c Release --no-restore
    install_playwright_browser
    JARVIS_PLAYWRIGHT_ARTIFACTS=artifacts/playwright \
      run_test tests/PersonalAgent.E2ETests/PersonalAgent.E2ETests.csproj BrowserE2E \
        "$TEST_RESULTS/browser-e2e" browser-e2e.trx
    mkdir -p "$TEST_RESULTS/browser-gate-probe"
    set +e
    dotnet test tests/PersonalAgent.E2ETests/PersonalAgent.E2ETests.csproj -c Release --no-build --no-restore \
      --filter Category=GateProbe --logger "trx;LogFileName=browser-gate-probe.trx" \
      --results-directory "$TEST_RESULTS/browser-gate-probe"
    probe_status=$?
    set -e
    if [ "$probe_status" -eq 0 ]; then
      echo "The deliberately failing Playwright gate probe unexpectedly passed." >&2
      exit 1
    fi
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- verify-browser-failure \
      "$TEST_RESULTS/browser-gate-probe/browser-gate-probe.trx"
    ;;
  gate-self-test)
    restore_solution
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- gate-self-test
    ;;
  docs)
    restore_solution
    dotnet run --project "$VALIDATOR" -c Release --no-restore -- docs
    ;;
  *)
    echo "Usage: tools/validate.sh {restore|build|unit|integration|architecture|sdk-contracts|coverage|aspire-e2e|playwright-install|browser-e2e|gate-self-test|docs}" >&2
    exit 2
    ;;
esac
