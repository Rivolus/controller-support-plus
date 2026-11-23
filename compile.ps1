# Shared by build.ps1 (developer build) and package.ps1 (shareable build): compiles one source file into the mod's DLL.
$script:managed = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed'
$script:bepinex = Get-ChildItem "$env:APPDATA\com.kesomannen.gale\cache\denikson-BepInExPack_Valheim" -Directory | Sort-Object Name | Select-Object -Last 1
$script:core    = Join-Path $script:bepinex.FullName 'BepInEx\core'
$script:csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Get-ModVersion([string]$src) { (Select-String -Path $src -Pattern 'Version = "([\d.]+)"').Matches[0].Groups[1].Value }
function Get-BepInExVersion { $script:bepinex.Name }

function Compile-Mod([string]$Source, [string]$Out, [bool]$Dev) {
  $mods = @('mscorlib', 'System', 'System.Core', 'netstandard', 'assembly_valheim', 'assembly_utils', 'assembly_guiutils', 'SoftReferenceableAssets',
            'UnityEngine', 'UnityEngine.CoreModule', 'UnityEngine.UI', 'UnityEngine.UIModule', 'UnityEngine.TextRenderingModule',
            'UnityEngine.TextCoreFontEngineModule', 'UnityEngine.TextCoreTextEngineModule', 'UnityEngine.AnimationModule', 'UnityEngine.ImageConversionModule', 'Unity.TextMeshPro')
  # the screen capture module is only referenced by the developer build
  if ($Dev) { $mods += 'UnityEngine.ScreenCaptureModule' }
  $refs = $mods | ForEach-Object { "/r:$script:managed\$_.dll" }
  $refs += "/r:$script:core\BepInEx.dll", "/r:$script:core\0Harmony.dll"
  $define = @(); if ($Dev) { $define = @('/define:DEV') }
  # the mod's picture inside the DLL, for the first-launch setup: the developer build's bronze DEV plate, the shareable
  # build's silver plate (the same picture as the zip's icon.png), whatever folder the DLL ends up in
  $icon = if ($Dev) { Join-Path $PSScriptRoot 'icon-dev.png' } else { Join-Path $PSScriptRoot 'package\icon.png' }
  if (-not (Test-Path $icon)) { throw "Mod picture missing: $icon" }
  & $script:csc /nologo /target:library /optimize+ /nostdlib+ /noconfig /nowarn:1701,1702,0618 $define "/out:$Out" "/resource:$icon,ControllerPro.icon.png" $refs $Source
  if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
