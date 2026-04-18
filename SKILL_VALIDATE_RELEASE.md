# Jellyfin Plugin Release Skill

**Name:** JellyfinPlugin_Validate_Release
**Description:** Validates plugin is ready for release and includes release instructions

## Instructions

When the user asks to validate and release the Jellyfin plugin, follow these steps:

### Validation Checklist

1. **Check csproj version matches manifest**
   - Read Jellyfin.Plugin.Audiobookshelf.csproj
   - Extract Version, AssemblyVersion, FileVersion
   - Read manifest.json latest version entry
   - Verify they match

2. **Build the plugin**
   - Run: `dotnet build Jellyfin.Plugin.Audiobookshelf/Jellyfin.Plugin.Audiobookshelf.csproj -c Release --no-incremental`
   - Verify build succeeds with 0 warnings, 0 errors

3. **Check DLL version**
   - Verify the built DLL has correct version using reflection

4. **Create release package**
   - Create ZIP from the Release DLL
   - Calculate MD5 checksum (not SHA256)

5. **Verify manifest entry**
   - Check latest version entry has correct checksum and sourceUrl

6. **Create GitHub release**
   - Use: `gh release create v{X.X.X.X} --title "v{X.X.X.X}" --notes "{changelog}"`
   - Upload ZIP: `gh release upload v{X.X.X.X} "path/to/zip"`

7. **Push manifest updates**
   - Commit manifest.json changes
   - Git push to remote

### Important Rules

- **ALWAYS ask for user approval** before building, creating releases, or pushing
- Use **MD5 checksums** (Jellyfin uses MD5, not SHA256)
- Ensure version in csproj matches manifest.json
- Release artifacts go in the `dist/` folder
- **Never automatically increment versions** - wait for explicit user request
- Check CLAUDE.md for project-specific release requirements