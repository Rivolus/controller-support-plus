# Dev notes

Not shipped: Thunderstore shows README.md, so working notes live here.

## How to publish

1. **Build the release zip.** From this folder, run `.\package.ps1`. It compiles the release DLL without the developer tools, checks it, syncs the version in `thunderstore.toml` with the source, and runs `tcli build`. The zip lands in `..\upload\Rivolus-Controller_Support_Plus-<version>.zip`. It never publishes.
2. **Check the build in game.** Install it into the Gale profile and play it before you publish.
3. **Publish (by hand, only when ready).** Put the token in this PowerShell window only, then publish:

   ```powershell
   $env:TCLI_AUTH_TOKEN = "<your Thunderstore service account token>"
   & "$env:LOCALAPPDATA\tcli\tcli-0.2.4-win-x64\tcli.exe" publish --config-path thunderstore.toml --file ..\upload\Rivolus-Controller_Support_Plus-<version>.zip
   Remove-Item Env:\TCLI_AUTH_TOKEN
   ```

   tcli reads the token from `TCLI_AUTH_TOKEN`. Never write it into a file in this folder.

Package settings (team Rivolus, package Controller_Support_Plus, community valheim, the categories Mods, Client-side, Utility and AI Generated, and the BepInExPack dependency) are in `thunderstore.toml`.

The token is a Thunderstore service account token: Thunderstore > your team > Service Accounts > Add service account.

tcli 0.2.4 is unpacked in `%LOCALAPPDATA%\tcli\tcli-0.2.4-win-x64\` (official GitHub release, installed 2026-11-24). package.ps1 finds it there or on PATH.
