## Bug Fixes

- **Settings persistence**: Fixed URL and API key not persisting between sessions by using `pageshow` event instead of unreliable `viewshow`
- **Popup alerts**: Added `Dashboard.alert()` popups for Test Connection, User Discovery, and Save Mappings
- **XSS vulnerability**: Fixed user matching UI to properly escape usernames before inserting into HTML
- **Race condition**: Fixed `renderUserMatches()` to bind events before async operations

## API Alignment with Audiobookshelf

- **Episode support**: Added `episodeId` parameter to progress API calls for podcast episodes
- **Complete progress payloads**: Updated progress sync to include `hideFromContinueListening`, `lastUpdate`, `markAsFinishedTimeRemaining`
- **Session sync**: Fixed session sync/close payloads to match ABS API
- **Model updates**: Expanded `AbsUser` and `AbsMediaProgress` models with complete field sets

## Additional Fixes

- Added overflow protection to `TimeHelper.SecondsToTicks()`
- Fixed `AbsApiClientFactory` cache invalidation logic
- Added `BatchUpdateProgressAsync` and `SyncLocalSessionAsync` methods for future features
