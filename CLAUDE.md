# Claude Code Guidance — Jellyfin.Plugin.Audiobookshelf

## Build & Release Approval Required

**Never build, package, or push to the repository without explicit user approval.**

This includes:
- `dotnet build` / `dotnet publish`
- Creating `.zip` release artifacts
- `git push` (any branch)
- Creating or editing GitHub releases
- Updating `manifest.json` checksums or version entries

Always stop and ask before executing any of these steps, even if the work leading up to them is complete.

## No Automatic Releases

**All releases must be explicitly requested by the user.** Never create releases, push tags, or update GitHub releases automatically based on version number changes or any other triggers.

## Release Artifact Location

All `.zip` release artifacts must be placed in `C:\Users\SirFfej\CodeEtAl\Mine-Modified\Jellyfin.Plugin.Audiobookshelf\dist\` — never in the repo root or any other location.

## Build vs Publish

**Always use `dotnet build` (not `dotnet publish`) to produce the release DLL.**

`dotnet publish` copies all `Microsoft.Extensions.*` runtime assemblies into the output folder. When these are zipped and shipped with the plugin, Jellyfin's plugin loader finds a second copy of `Microsoft.Extensions.DependencyInjection.Abstractions.dll` in the plugin directory, loads it into the plugin's AssemblyLoadContext, and `IServiceCollection` resolves to a different assembly instance than the one Jellyfin uses for `IPluginServiceRegistrator`. The interface check for `RegisterServices(IServiceCollection, ...)` then fails with `TypeLoadException: Method does not have an implementation`.

The zip should contain **only** `Jellyfin.Plugin.Audiobookshelf.dll` — Jellyfin provides all `Microsoft.Extensions.*` from the host.
