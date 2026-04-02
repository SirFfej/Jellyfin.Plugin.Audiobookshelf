# Jellyfin.Plugin.Audiobookshelf - Optimization Roadmap

## Overview
Performance improvements and features for the Audiobookshelf Jellyfin plugin.

---

## Implemented ✓

### Performance Optimizations (dev-optimization branch)

| # | Optimization | Status | Notes |
|---|--------------|--------|-------|
| 1.1 | Parallelize user sync | ✓ Done | `InboundSyncTask.cs` - `Task.WhenAll` |
| 1.2 | Levenshtein space opt | ✓ Done | `ItemMatcher.cs` - O(min(n,m)) space |
| 2.1 | Disable AutoFlush | ✓ Done | `AbsFileLoggerProvider.cs` - buffered writes |
| 2.2 | API retry logic | ✓ Done | `AbsApiClient.cs` - 3 retries with backoff |
| 2.3 | Compile regex | ✓ Done | `AbsBookMetadataProvider.cs` - static patterns |

### Auto User Mapping (dev-optimization branch)

| # | Feature | Status | Files |
|---|---------|--------|-------|
| U1 | TokenVault with keyring | ✓ Done | `Services/TokenVault.cs` |
| U2 | UserMappingService | ✓ Done | `Services/UserMappingService.cs` |
| U3 | REST API controller | ✓ Done | `Api/UserDiscoveryController.cs` |
| U4 | GetAllUsersAsync | ✓ Done | `Api/AbsApiClient.cs` |
| U5 | Config page UI | ✓ Done | `Configuration/configPage.html` |

---

## Planned

### Phase 3: Low Impact (Not Started)

| # | Optimization | Effort | Impact |
|---|--------------|--------|--------|
| 3.1 | String allocations | Low | Low |
| 3.2 | Early exit matching | Low | Low |
| 3.3 | Dictionary lookups | Medium | Low |

---

## Auto User Mapping - Implementation Details

### Storage Strategy
1. **Primary:** System keyring via KeySharp (Linux libsecret, macOS Keychain, Windows Credential Manager)
2. **Fallback:** Plugin config (requires explicit user approval after warning)

### API Endpoints
```
GET  /Audiobookshelf/UserDiscovery/KeyringStatus
POST /Audiobookshelf/UserDiscovery/ApproveFallback
GET  /Audiobookshelf/UserDiscovery/Discover?adminToken=&serverUrl=
POST /Audiobookshelf/UserDiscovery/SaveMappings
```

### User Matching
- Exact username match (case-insensitive)
- Manual selection for unmatched users
- ABS tokens stored securely per-user

### Linux Requirement
```bash
sudo apt-get install libsecret-1-dev  # Debian/Ubuntu
sudo yum install libsecret-devel       # Red Hat/CentOS
sudo pacman -Sy libsecret             # Arch
```

---

## Testing

After deploying optimizations:
1. Profile sync time with multiple users
2. Monitor memory allocations during bulk operations
3. Verify log file I/O doesn't block main thread
4. Test user discovery with multiple ABS users
5. Verify keyring storage on each platform

---

## Branch Status

| Branch | Status | Commit |
|--------|--------|--------|
| `main` | Production | (last stable) |
| `dev-optimization` | Ready for testing | `352fcee` |

---

## Not Planned (Out of Scope)

- Database layer changes (none exists)
- Major architectural refactoring
- Caching layer for library items (requires ABS API changes)
