# Builds a release: one self-contained AudioMixer.exe (no .NET install needed), zipped with the license, notices,
# README and changelog, plus SHA256SUMS.txt. Output in dist\. The version comes from src\Directory.Build.props.
#   pwsh tools/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\src\Directory.Build.props")).Project.PropertyGroup.Version
$name = "AudioMixer-$version-win-x64"
$out = "$root\dist\$name"
Remove-Item $out, "$root\dist\$name.zip" -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish "$root\src\AudioMixer.App" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$extra = Get-ChildItem $out | Where-Object Name -ne 'AudioMixer.exe'
if ($extra) { throw "expected only AudioMixer.exe, also got: $($extra.Name -join ', ')" }
Copy-Item "$root\LICENSE", "$root\THIRD-PARTY-NOTICES.md", "$root\README.md", "$root\CHANGELOG.md" $out

Compress-Archive -Path "$out\*" -DestinationPath "$root\dist\$name.zip"
$hashes = foreach ($f in "$root\dist\$name.zip", "$out\AudioMixer.exe") {
    "$((Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $f -Leaf)"
}
$hashes | Set-Content "$root\dist\SHA256SUMS.txt" -Encoding ascii
"$name.zip: $([math]::Round((Get-Item "$root\dist\$name.zip").Length / 1MB, 1)) MB"
$hashes
