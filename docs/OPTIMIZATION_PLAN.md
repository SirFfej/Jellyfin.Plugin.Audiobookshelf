# Jellyfin.Plugin.Audiobookshelf - Optimization Roadmap

## Overview
Performance improvements planned for the Audiobookshelf Jellyfin plugin.

---

## Planned Optimizations

### Phase 1: High Impact (Sync Performance)

#### 1. Parallelize User Sync
**File:** `InboundSyncTask.cs:102-117`
**Issue:** Users processed sequentially - O(n) time
**Fix:** Use `Task.WhenAll` to sync users in parallel

```csharp
// Current: Sequential
foreach (var (jellyfinUserId, absToken) in userTokenPairs)
{
    await SyncUserProgressAsync(...);
}

// Proposed: Parallel
var tasks = userTokenPairs.Select(pair => 
    SyncUserProgressAsync(pair.JellyfinUserId, pair.AbsToken, cancellationToken));
await Task.WhenAll(tasks);
```

#### 2. Optimize Levenshtein Algorithm
**File:** `ItemMatcher.cs:124-145`
**Issue:** O(n*m) 2D array allocation per call
**Fix:** Use 2-row space optimization - O(min(n,m)) space

---

### Phase 2: Medium Impact (Reliability & I/O)

#### 3. Disable File Logger Buffering
**File:** `AbsFileLoggerProvider.cs:113-116`
**Issue:** `AutoFlush=true` causes disk syscall per log entry
**Fix:** Use buffered writes with configurable flush interval

#### 4. Add Retry Logic to API Client
**File:** `AbsApiClient.cs`
**Issue:** No retry on transient network failures
**Fix:** Implement exponential backoff retry (3 attempts)

#### 5. Compile Regex Patterns
**File:** `AbsBookMetadataProvider.cs:267,278`
**Issue:** Regex compiled on every call
**Fix:** Static compiled `Regex` instances

---

### Phase 3: Low Impact (Memory & Polish)

#### 6. Reduce String Allocations
**File:** `ProgressSyncService.cs:64,71`
**Fix:** Consider `ValueStringBuilder` or cached formatters

#### 7. Early Exit in Fuzzy Matching
**File:** `ItemMatcher.cs:79-92`
**Fix:** Return immediately on perfect match (score = 1.0)

#### 8. Dictionary Lookup for Item Matching
**File:** `ItemMatcher.cs:36-95`
**Fix:** Build index dictionary for ASIN/ISBN lookups instead of linear search

---

## Priority Matrix

| Phase | Optimization | Effort | Impact |
|-------|--------------|--------|--------|
| 1.1   | Parallelize user sync | Low | High |
| 1.2   | Levenshtein space opt | Medium | High |
| 2.1   | Disable AutoFlush | Low | Medium |
| 2.2   | API retry logic | Medium | Medium |
| 2.3   | Compile regex | Low | Low-Medium |
| 3.1   | String allocations | Low | Low |
| 3.2   | Early exit matching | Low | Low |
| 3.3   | Dictionary lookups | Medium | Low |

---

## Not Planned (Out of Scope)

- Database layer changes (none exists)
- Major architectural refactoring
- Caching layer for library items (requires ABS API changes)

---

## Testing

After implementing optimizations:
1. Profile sync time with multiple users
2. Monitor memory allocations during bulk operations
3. Verify log file I/O doesn't block main thread
