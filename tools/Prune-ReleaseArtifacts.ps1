param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    throw "Publish directory does not exist: $Root"
}

# These files support debugging and crash-dump inspection, but are not loaded
# by the application during normal execution. Keep the cleanup scoped to the
# publish root so runtime and resource files below it are never touched.
$diagnosticPattern = '^(createdump\.exe|mscordaccore.*\.dll|mscordbi\.dll|Microsoft\.DiaSymReader\.Native.*\.dll|.*\.pdb)$'
$candidates = Get-ChildItem -LiteralPath $Root -File | Where-Object {
    $_.Name -match $diagnosticPattern
}

foreach ($file in $candidates) {
    Remove-Item -LiteralPath $file.FullName -Force
}

$remaining = Get-ChildItem -LiteralPath $Root -File | Where-Object {
    $_.Name -match $diagnosticPattern
}
if ($remaining) {
    $names = ($remaining | Select-Object -ExpandProperty Name) -join ', '
    throw "Diagnostic artifacts remain after cleanup: $names"
}

Write-Output ("Removed {0} diagnostic artifact(s) from {1}" -f $candidates.Count, $Root)
