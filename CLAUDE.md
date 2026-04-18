# Claude Code Guidance — Jellyfin.Plugin.Audiobookshelf

## CRITICAL: Interactive Approval Required for All Releases

**This workspace requires explicit user approval BEFORE any release-related actions.**
Do NOT proceed automatically — ALWAYS stop and ask.

### Actions Requiring Approval (stop and ask before executing):

| Command / Action | Reason |
|-----------------|--------|
| `dotnet build -c Release` | Creates release DLL |
| `dotnet publish` | Creates output with extra assemblies |
| `Compress-Archive` / zip creation | Produces release artifact |
| `git push` | Pushes to remote |
| `gh release create` | Creates GitHub release |
| Any command that modifies `manifest.json` | Updates release metadata |
| Any command that updates `.csproj` version | Version bump |
| Any CI/CD or workflow triggers | Automated releases |

### What To Do Instead:
1. Complete the code changes
2. Stop and explicitly ask: "Should I build and create the release?"
3. Wait for user confirmation
4. Only then proceed with build/release commands

### Examples of What NOT To Do:
- **DO NOT** automatically increment version numbers
- **DO NOT** update manifest.json checksums
- **DO NOT** create .zip files after successful builds
- **DO NOT** push tags or commits that include version bumps
- **DO NOT** assume the user wants a release just because code was written

## Release Artifact Location

All `.zip` release artifacts must be placed in:
```
C:\Users\SirFfej\CodeEtAl\Mine-Modified\Jellyfin.Plugin.Audiobookshelf\dist\
```
**Never** place artifacts in the repo root or any other location.

## Build Command

Use this exact command for release builds:
```bash
dotnet build Jellyfin.Plugin.Audiobookshelf/Jellyfin.Plugin.Audiobookshelf.csproj -c Release
```

**DO NOT use `dotnet publish`** — it copies Microsoft.Extensions.* assemblies that cause TypeLoadException.

## No Automatic Releases

**All releases must be explicitly requested by the user.** This includes:
- Version number changes
- Manifest.json updates
- Git tags
- GitHub releases
- CI/CD triggered builds

## Version Update Checklist

When the user requests a release, the following must be updated together:
1. `Jellyfin.Plugin.Audiobookshelf.csproj` — Version, AssemblyVersion, FileVersion
2. `manifest.json` — new version entry with changelog, checksum, sourceUrl, timestamp

Calculate checksum after creating the zip:
```bash
Get-FileHash dist/Jellyfin.Plugin.Audiobookshelf_X.X.X.X.zip -Algorithm MD5
```
