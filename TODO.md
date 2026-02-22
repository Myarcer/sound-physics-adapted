# Sound Physics Adapted - TODO

## Positional Weather Issues

### Cave Exit Detection
- When exiting a cave, rain positions at the cave opening above the player don't spawn positional sources early enough
- Root cause: heightmap sampling classifies columns where `playerY < rainHeight` as "covered" — but at cave exits, the rain impact surface IS above the player while sound CAN reach through the opening
- These columns never become DDA candidates, so no positional sources spawn until the player physically walks out
- Need: detect nearby "covered" columns that have clear DDA paths to the player (cave mouth scenario)
- Affects: `WeatherEnclosureCalculator.cs` Step 1 heightmap sampling

### DDA Range Increase
- Current 15 DDA candidates with SCAN_RADIUS=12 only spawns sources ~3 blocks away
- Works fine for walking through/near openings, but fails when opening is >3 blocks of airspace away
- Example: standing in a room with a window 5 blocks away — no positional source spawns
- Need: either increase DDA budget, extend scan radius, or add distance-based priority so farther exposed columns get checked
- Must not break performance (~0.9ms per 100ms tick budget)
- Affects: `WeatherEnclosureCalculator.cs` SCAN_RADIUS, MAX_DDA_CANDIDATES

### Replace Structural DDA Check (4c) with Block Event Hooks
- Current 4c structural integrity check uses DDA from member positions to stored player position
- False-triggers on existing house walls when rounding corners, requiring a weight-zeroing workaround instead of direct removal
- Replace with VS block placed/broken event hooks (`BlockChanged` or similar) that check if the changed block is AT or ABOVE a tracked opening's member positions
- Block events give exact coordinates — no DDA needed, no false positives from wall geometry
- Remove `CheckStructuralIntegrity()`, `LastVerifiedPlayerPos`, `STRUCTURAL_OCC_THRESHOLD`, and the DDA-to-player path entirely
- Keeps 4b heightmap check as fast broad-phase, block events become precise narrow-phase

### Other Potential Improvements (from revert analysis)
- ExpiryFadeRate: separate faster fade for source eviction (~1s vs 3s)
- Proximity fade scaling: gradual by cluster size instead of binary gate
- Fixed Y position: sound source stays at rain impact Y, not player Y
- Position stability: 1.5 block threshold to prevent micro-jitter
- Competitive eviction: score-based slot eviction for louder sources
- Angular sector clustering: prevent same-direction source waste
