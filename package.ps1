# Shareable build: makes the zip to share or upload to Thunderstore, in ..\upload (only that zip is kept there).
# Run only when Rob asks for the mod to be packaged. The developer tools (screenshots of the mod's states, the design
# export, the HUD layout dump) are left out completely: compiled without DEV, with every call to them taken out of the
# source first. The finished DLL is then scanned, and the zip is refused if any trace of them is left.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. "$root\compile.ps1"
$version = Get-ModVersion "$root\src\ControllerPro.cs"

# 1. the source without any call to the developer tools
$src = [IO.File]::ReadAllText("$root\src\ControllerPro.cs", [Text.Encoding]::UTF8)
$src = [regex]::Replace($src, '(Shots\.(Want|Tick)|HudDump\.Maybe|Export\.Maybe)\([^;]*\);', '')
$obj = "$root\obj"; New-Item -ItemType Directory -Force $obj | Out-Null
[IO.File]::WriteAllText("$obj\ControllerPro.share.cs", $src, (New-Object Text.UTF8Encoding $true))

# 2. compiled without DEV
$pkg = "$root\package"
New-Item -ItemType Directory -Force "$pkg\plugins" | Out-Null
Compile-Mod -Source "$obj\ControllerPro.share.cs" -Out "$pkg\plugins\ControllerPro.dll" -Dev $false
Remove-Item "$obj\ControllerPro.share.cs" -Force

# 3. no trace left: refused if any name, text or reference of the developer tools is in the DLL
$bytes = [IO.File]::ReadAllBytes("$pkg\plugins\ControllerPro.dll")
$text = [Text.Encoding]::ASCII.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes)
$bad = [regex]::Matches($text, '(?i)ScreenCapture|CaptureScreenshot|Shots|HudDump|EncodeToPNG|Design captures|Design export|screenshot|captures|spec\.json|LayoutDump') | ForEach-Object { $_.Value } | Select-Object -Unique
if ($bad) { Remove-Item "$pkg\plugins\ControllerPro.dll" -Force; throw "Package refused: developer tool traces left in the DLL: $($bad -join ', ')" }

# 4. the package files and the zip
$manifest = [ordered]@{
  name           = 'Controller_Support_Plus'
  version_number = $version
  website_url    = 'https://github.com/Rivolus/controller-support-plus'
  description    = 'Valheim, the way it should feel on a controller. Plug and play: a controller hotbar, a pad-friendly inventory, HUD presets and a PRO button layout. Client-side, works on any server.'
  dependencies   = @("denikson-BepInExPack_Valheim-$(Get-BepInExVersion)")
}
[IO.File]::WriteAllText("$pkg\manifest.json", ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Copy-Item "$root\README.md" "$pkg\README.md" -Force

$upload = Join-Path (Split-Path -Parent $root) 'upload'
New-Item -ItemType Directory -Force $upload | Out-Null
Get-ChildItem $upload -Filter *.zip -ErrorAction SilentlyContinue | Remove-Item -Force
$zip = "$upload\RobVilladiego-Controller_Support_Plus-$version.zip"
# Compress-Archive writes backslashes into entry names, which other tools misread; add entries by hand with forward slashes.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem $pkg -Recurse -File) {
  $name = $f.FullName.Substring($pkg.Length + 1).Replace('\', '/')
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name) | Out-Null
}
$archive.Dispose()
"Packaged $zip (checked: no developer tools)"
