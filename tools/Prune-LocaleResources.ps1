param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$allowed = @('zh-cn', 'en-us')
$culturePattern = '^[a-z]{2,3}(-[a-z0-9]{2,8})+$'

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    throw "Publish directory does not exist: $Root"
}

Get-ChildItem -LiteralPath $Root -Directory | Where-Object {
    $name = $_.Name.ToLowerInvariant()
    $name -match $culturePattern -and $allowed -notcontains $name
} | Remove-Item -Recurse -Force

Get-ChildItem -LiteralPath $Root -Directory | Where-Object {
    $name = $_.Name.ToLowerInvariant()
    $name -match $culturePattern -and $allowed -notcontains $name
} | ForEach-Object {
    throw "Unsupported language directory remains: $($_.FullName)"
}
