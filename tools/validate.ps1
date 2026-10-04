param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Command,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root "tools/validate.sh"
& $script $Command @Arguments
exit $LASTEXITCODE
