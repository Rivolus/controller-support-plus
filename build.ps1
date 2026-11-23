# Developer build: compiles ControllerPro.dll WITH the developer tools (DEV: screenshots of the mod's states, the design
# export, the HUD layout dump) and copies it into every Gale profile that has the mod. It makes no zip.
# The shareable zip is made only by package.ps1, only when Rob asks, and never contains the developer tools.
# Uses the C# compiler that ships with Windows, Valheim's own files, and the BepInEx copy Gale already downloaded.
$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
. "$root\compile.ps1"

$out = "$root\bin\dev"
New-Item -ItemType Directory -Force $out | Out-Null
Compile-Mod -Source "$root\src\ControllerPro.cs" -Out "$out\ControllerPro.dll" -Dev $true

# Update the copy in every Gale profile that has the mod, so there is no need to import again after each build.
# The developer build gets the internal icon (icon-dev.png, with the DEV tag); the shareable zip keeps package\icon.png (silver plate).
# A profile gets the mod once, by importing a zip in Gale (Import > Local mod); from then on it is kept current here.
$profiles = "$env:APPDATA\com.kesomannen.gale\valheim\profiles"
# the mod's folder in a profile: found by its DLL, whatever the folder is called
$found = @(); foreach ($pr in Get-ChildItem $profiles -Directory -ErrorAction SilentlyContinue) { $d = Get-ChildItem "$($pr.FullName)\BepInEx\plugins" -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path "$($_.FullName)\ControllerPro.dll" } | Select-Object -First 1; if ($d) { $found += $d } }
if ($found.Count -eq 0) { "No Gale profile has the mod yet: import a zip in Gale once." }
elseif (Get-Process valheim -ErrorAction SilentlyContinue) { "Valheim is running: profile copy NOT updated. Close the game and build again." }
else { foreach ($p in $found) { Copy-Item "$out\ControllerPro.dll" $p.FullName -Force; Copy-Item "$root\icon-dev.png" "$($p.FullName)\icon.png" -Force; "Updated $($p.FullName)" } }
"Built developer DLL $(Get-ModVersion "$root\src\ControllerPro.cs")"
