using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Calculates weather enclosure using two complementary metrics derived from
    /// AAA game audio principles (Wwise obstruction vs occlusion separation):
    ///
    /// 1. Sky Coverage (heightmap sampling, ~50 points, radius 8):
    ///    "What fraction of the sky above me is blocked by structure?"
    ///    Drives VOLUME of ambient weather bed.
    ///    One opening doesn't collapse the entire room's coverage.
    ///
    /// 2. Occlusion Factor (multi-height DDA verification on exposed columns):
    ///    "Can I hear rain falling at ANY visible height in this column?"
    ///    Drives LPF — material between player and falling rain = heavy muffling,
    ///    open air path = crisp sound.
    ///
    ///    For each exposed column, casts DDA rays at multiple heights above
    ///    the rain impact point (ground+1, +2, +3, +4, +8, +12). Takes the MINIMUM
    ///    occlusion — "best audible path wins". This correctly handles:
    ///    - Roof no walls: ground-level ray passes through open air
    ///    - Tunnel end / short walls: higher rays clear over the wall
    ///    - Cave ceiling: all rays hit rock at every height
    ///    - Full building: wall blocks low rays, roof blocks high rays
    ///
    /// Replaces vanilla roomVolumePitchLoss which uses BFS flood fill that
    /// treats any nearby opening as "fully outdoors" (loss → 0).
    ///
    /// Also outputs verified open-air rain positions for Phase 5B positional
    /// source clustering.
    ///
    /// Performance: ~50 heightmap O(1) lookups + ~90 DDA rays ≈ 0.9ms every 100ms.
    /// </summary>
    public class WeatherEnclosureCalculator : IDisposable
    {
        private readonly ICoreClientAPI capi;

        // ── Configuration ──
        private const int SCAN_RADIUS = 12;          // Blocks from player (covers large halls/rooms)
        internal const int MAX_DDA_CANDIDATES = 50;    // DDA verify top N closest exposed
        private const int MAX_Y_DIFF = 12;            // Rain impact within 12 blocks of player Y

        // Step 2C: Horizontal probe rays for cave exit detection.
        // When enclosed (high sky coverage), cast rays outward from player at
        // various angles (15-75 degrees above horizontal) in 8 compass directions.
        // Rays that exit into sky-exposed air become positional rain sources.
        // This detects diagonal cave exits, overhangs, and wall openings.
        private const int PROBE_RAY_DIRECTIONS = 12;    // Compass directions to probe
        private const float PROBE_RAY_MAX_DIST = 24f;   // Max ray distance in blocks
        private const float PROBE_SKY_COVERAGE_MIN = 0.5f; // Only probe when this enclosed

        // Below 0.3 = practically clear air path (only grass/plants touch the ray).
        // Intentionally strict — verified openings must have near-zero material between
        // rain and player. Blocked candidates (0.3+) trigger Step 3 neighbor search
        // to find the actual opening column laterally.
        private const float DDA_OCCLUDED_THRESHOLD = 0.3f;

        // ── Schmitt Trigger Hysteresis ──
        // Separate join/leave thresholds prevent borderline columns from flickering
        // in/out of verified openings each cycle. A column must be clearly open to
        // enter (JOIN), and clearly blocked to leave (LEAVE). The gap between thresholds
        // absorbs DDA noise from player micro-movement.
        private const float HYSTERESIS_JOIN_THRESHOLD = 0.25f;   // Must be this clear to enter
        private const float HYSTERESIS_LEAVE_THRESHOLD = 0.45f;  // Must exceed this to leave

        // ── Temporal Opening Cache ──
        // Columns that passed DDA are cached between cycles. On each cycle, re-verified
        // columns update their data; columns not found increment a fail counter.
        // Only removed after consecutive failures, not instantly.
        private const int MAX_CONSECUTIVE_FAILS = 5;  // 5 cycles × 100ms = 500ms grace period

        // Step 3: Opening neighbor search — when no direct paths exist but nearby
        // blocked candidates suggest a thin wall, search neighboring columns to find
        // the actual opening (door/window). The opening IS the column with occ < 0.3.
        private const float PARTIAL_OCC_THRESHOLD = 3.0f;  // Max occ for neighbor search candidates
        private const int MAX_NEIGHBOR_CANDIDATES = 3;      // Top N partial candidates to search around

        // Multi-height column sampling: heights above rain impact (RainY) to check.
        // Rain falls from sky → ground. We check if the player can hear rain
        // at ANY height in the column. Minimum occlusion wins (best audible path).
        // +1   = just above ground impact (roof-no-walls, open outdoors)
        // +2   = low wall openings, archways, crawl spaces
        // +3   = typical window height, doorway top
        // +4   = clears 2-3 block walls/fences
        // +8   = clears medium structures
        // +12  = matches MAX_Y_DIFF, catches tall structures
        //
        // Dense sampling at +1..+4 is critical for wall openings:
        // Ray angle at the wall depends on distance, so a hole at height 2-3
        // may only be caught by a ray starting at exactly the right height.
        //
        // NOTE: Only used when rainY >= playerY. When rain impacts BELOW player
        // (multi-story / floor hole), eye-level + foot-level rays are cast instead
        // to avoid false occlusion from intervening floors.
        private static readonly float[] COLUMN_SAMPLE_HEIGHTS = { 1.01f, 2f, 3f, 4f, 8f, 12f };

        // Step 3 neighbor search: fewer height samples (skip +2, +3).
        // Neighbors are lateral searches where precise wall-opening angles matter less.
        private static readonly float[] NEIGHBOR_SAMPLE_HEIGHTS = { 1.01f, 4f, 8f, 12f };

        // ── Update interval ──
        private const long UPDATE_INTERVAL_MS = 100;  // Fast updates for responsive enclosure tracking
        private long lastUpdateMs = 0;

        // ── Outputs (read by WeatherAudioManager/RainAudioHandler) ──

        /// <summary>
        /// Fraction of sky overhead that is covered (0 = outdoors, 1 = fully roofed).
        /// Drives ambient weather volume: more coverage = quieter ambient bed.
        /// Distance-weighted: columns closer to player contribute more.
        /// </summary>
        public float SkyCoverage { get; private set; } = 0f;

        /// <summary>
        /// Fraction of nearby rain that is behind material (0 = all in open air, 1 = all occluded).
        /// Drives LPF: high = heavy muffling (rain behind walls), low = crisp (rain through air).
        /// When no exposed samples exist (fully enclosed), this is 1.0.
        /// </summary>
        public float OcclusionFactor { get; private set; } = 0f;

        /// <summary>
        /// Smoothed sky coverage for audio use (avoids pops when moving between blocks).
        /// Initialized to 1.0 (fully enclosed) as a safe default — prevents loud audio
        /// spikes on spawn before the first enclosure calculation completes.
        /// FirstUpdate direct-sets this to the real value on the first successful calc.
        /// </summary>
        public float SmoothedSkyCoverage { get; private set; } = 1f;

        /// <summary>
        /// Smoothed occlusion factor for audio use.
        /// Initialized to 1.0 (fully occluded) as a safe default — prevents
        /// unfiltered audio on spawn before enclosure calculation runs.
        /// </summary>
        public float SmoothedOcclusionFactor { get; private set; } = 1f;

        /// <summary>
        /// Verified open-air rain impact positions (clear DDA path to player).
        /// Temporally stable: cached between cycles with Schmitt trigger hysteresis.
        /// Used by Phase 5B clustering via OpeningClusterer.
        /// </summary>
        public IReadOnlyList<VerifiedRainPosition> VerifiedOpenings => verifiedOpenings;
        private readonly List<VerifiedRainPosition> verifiedOpenings = new();

        // ── Temporal Opening Cache ──
        // Keyed by (columnX, columnZ) — persists openings between scan cycles.
        // Each entry tracks consecutive DDA failures for gradual removal.
        private readonly Dictionary<(int x, int z), CachedOpening> openingCache = new();

        /// <summary>
        /// Invalidate cached opening columns near a block change.
        /// Evicts any column within 1 block XZ of the changed position,
        /// preventing stale cache from re-creating tracked openings that
        /// were just removed by ForceReverify.
        /// </summary>
        public void InvalidateNearbyColumns(BlockPos changedPos)
        {
            var keysToRemove = new List<(int, int)>();
            foreach (var kvp in openingCache)
            {
                int dx = Math.Abs(kvp.Key.x - changedPos.X);
                int dz = Math.Abs(kvp.Key.z - changedPos.Z);
                if (dx <= 2 && dz <= 2)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            for (int i = 0; i < keysToRemove.Count; i++)
            {
                openingCache.Remove(keysToRemove[i]);
            }
        }

        /// <summary>
        /// Cached opening state for temporal persistence and Schmitt trigger hysteresis.
        /// </summary>
        private class CachedOpening
        {
            public VerifiedRainPosition Data;          // Last verified position data
            public int ConsecutiveFailCount;             // Cycles since last DDA pass
            public bool ReVerifiedThisCycle;              // Set during current scan
        }

        // Smoothing factor — converge ~0.5-1s at 100ms interval
        // 0.2 at 100ms: 90% convergence in ~1s (11 ticks), 2x faster than old 0.4 at 500ms
        private const float SMOOTH_FACTOR = 0.2f;

        // First update flag — on first calculation, snap smoothed values to raw values.
        // SmoothedSkyCoverage starts at 1.0 (safe enclosed default) and first update
        // snaps it to the real value so convergence is immediate for indoor spawns
        // and smooth (from 1.0 toward real) for outdoor spawns.
        private bool firstUpdate = true;

        // ── Debug Visualization ──
        private readonly EnclosureDebugVisualizer debugViz;

        // Precomputed sample offsets (diamond pattern, distance-weighted)
        private static SamplePoint[] samplePoints;
        private static bool samplesInitialized = false;

        public WeatherEnclosureCalculator(ICoreClientAPI api)
        {
            capi = api;
            debugViz = new EnclosureDebugVisualizer(api);
            InitializeSamplePoints();
        }

        /// <summary>
        /// Precompute sample offsets in a diamond/circle pattern around origin.
        /// Each sample has a distance weight (closer = more influence).
        /// </summary>
        private static void InitializeSamplePoints()
        {
            if (samplesInitialized) return;

            var points = new List<SamplePoint>();

            for (int dx = -SCAN_RADIUS; dx <= SCAN_RADIUS; dx++)
            {
                int maxDz = SCAN_RADIUS - Math.Abs(dx); // Diamond pattern
                for (int dz = -maxDz; dz <= maxDz; dz++)
                {
                    // Skip center (player's own column — always "covered" by their own body isn't meaningful)
                    if (dx == 0 && dz == 0) continue;

                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    // Inverse distance weight: closer columns matter more
                    // Gaussian-ish falloff: weight = e^(-dist²/2σ²), σ = radius/2
                    float sigma = SCAN_RADIUS * 0.5f;
                    float weight = MathF.Exp(-(dist * dist) / (2f * sigma * sigma));

                    points.Add(new SamplePoint { DX = dx, DZ = dz, Distance = dist, Weight = weight });
                }
            }

            samplePoints = points.ToArray();
            samplesInitialized = true;
        }

        // ══════════════════════════════════════════════════════════════
        //  Scan context — mutable state shared across scan steps
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Per-cycle mutable state passed between scan steps.
        /// Avoids threading dozens of locals through method signatures.
        /// Created fresh each scan cycle in Update().
        /// </summary>
        private class ScanContext
        {
            // Input (immutable during scan)
            public Vec3d PlayerEarPos;
            public int PlayerX, PlayerY, PlayerZ;
            public IBlockAccessor BlockAccessor;
            public bool DebugWeather;

            // Step 1 output → Step 2 input
            public List<ExposedCandidate> ExposedCandidates = new();

            // Accumulated across Steps 2, 3, 2C
            public List<VerifiedRainPosition> FreshVerified = new();
            public int DirectCount;
            public float DirectWeight;
            public float ExposedTotalWeight;
            public int NeighborFinds;
            public int ProbeFinds;

            // Visualization (null when viz disabled — check before use)
            public EnclosureDebugVisualizer.VizData Viz;
        }

        // ══════════════════════════════════════════════════════════════
        //  Main update — orchestrates the scan pipeline
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Main update — call from WeatherAudioManager tick (~100ms).
        /// Internally rate-limited to UPDATE_INTERVAL_MS.
        /// playerEarPos should be at ear/eye level (entity.Pos.XYZ + LocalEyePos).
        /// </summary>
        public void Update(Vec3d playerEarPos, long gameTimeMs)
        {
            // On first update, skip smoothing — initialize directly to raw values.
            // SmoothedSkyCoverage starts at 1.0 (safe enclosed default). FirstUpdate
            // snaps it to the real value so indoor spawns converge instantly and
            // outdoor spawns transition smoothly under spawnFade protection.
            if (!firstUpdate)
            {
                SmoothedSkyCoverage += (SkyCoverage - SmoothedSkyCoverage) * SMOOTH_FACTOR;
                SmoothedOcclusionFactor += (OcclusionFactor - SmoothedOcclusionFactor) * SMOOTH_FACTOR;
            }

            if (gameTimeMs - lastUpdateMs < UPDATE_INTERVAL_MS) return;
            lastUpdateMs = gameTimeMs;

            // Build scan context for this cycle
            var ctx = new ScanContext
            {
                PlayerEarPos = playerEarPos,
                PlayerX = (int)Math.Floor(playerEarPos.X),
                PlayerY = (int)Math.Floor(playerEarPos.Y),
                PlayerZ = (int)Math.Floor(playerEarPos.Z),
                BlockAccessor = capi.World.BlockAccessor,
                DebugWeather = SoundPhysicsAdaptedModSystem.Config?.DebugMode == true
                            && SoundPhysicsAdaptedModSystem.Config?.DebugWeather == true,
                Viz = debugViz.BeginFrame(gameTimeMs)
            };

            // ── Step 1: Heightmap sky coverage ──
            ScanSkyCoverage(ctx);

            // ── Early exit: fully outdoors ──
            if (ctx.ExposedCandidates.Count == 0 && SkyCoverage < 0.3f)
            {
                HandleOutdoors(ctx, gameTimeMs);
                return;
            }

            // Mark all cached openings as not-yet-reverified this cycle
            foreach (var kvp in openingCache)
                kvp.Value.ReVerifiedThisCycle = false;

            // ── Steps 2 & 3: DDA verification + neighbor search ──
            if (ctx.ExposedCandidates.Count > 0)
            {
                VerifyExposedCandidates(ctx);
                SearchNeighborOpenings(ctx);
            }

            // ── Step 2C: Horizontal probe rays for cave exits ──
            ProbeForCaveExits(ctx);

            // ── Cache merge + output calculation ──
            MergeCacheAndComputeOutput(ctx);

            // ── First update: snap smoothed values ──
            if (firstUpdate)
            {
                SmoothedSkyCoverage = SkyCoverage;
                SmoothedOcclusionFactor = OcclusionFactor;
                firstUpdate = false;
                WeatherAudioManager.WeatherDebugLog(
                    $"[FIRST UPDATE] Set smoothed values directly: sky={SkyCoverage:F2} occl={OcclusionFactor:F2}");
            }

            WeatherAudioManager.WeatherDebugLog(
                $"Enclosure calc: sky={SkyCoverage:F2} occl={OcclusionFactor:F2} " +
                $"exposed={ctx.ExposedCandidates.Count} verified={Math.Min(ctx.ExposedCandidates.Count, MAX_DDA_CANDIDATES)} " +
                $"direct={ctx.DirectCount} neighbor={ctx.NeighborFinds} probe={ctx.ProbeFinds} " +
                $"fresh={ctx.FreshVerified.Count} cached={openingCache.Count} openings={verifiedOpenings.Count}");

            // ── Visualization ──
            debugViz.EndFrame(ctx.Viz, gameTimeMs);
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 1: Heightmap sky coverage sampling
        // ══════════════════════════════════════════════════════════════

        private void ScanSkyCoverage(ScanContext ctx)
        {
            float coveredWeight = 0f;
            float totalWeight = 0f;

            for (int i = 0; i < samplePoints.Length; i++)
            {
                ref SamplePoint sp = ref samplePoints[i];
                int sampleX = ctx.PlayerX + sp.DX;
                int sampleZ = ctx.PlayerZ + sp.DZ;

                int rainHeight = ctx.BlockAccessor.GetRainMapHeightAt(sampleX, sampleZ);

                totalWeight += sp.Weight;

                if (ctx.PlayerY < rainHeight)
                {
                    // Player is BELOW the rain-blocking surface at this column -> covered
                    coveredWeight += sp.Weight;
                    ctx.Viz?.AddSample(sampleX, ctx.PlayerY, sampleZ, EnclosureDebugVisualizer.VizColor.Covered);
                }
                else
                {
                    // Player is ABOVE or AT rain level -> rain can fall here
                    int yDiff = Math.Abs(rainHeight - ctx.PlayerY);
                    if (yDiff <= MAX_Y_DIFF)
                    {
                        ctx.ExposedCandidates.Add(new ExposedCandidate
                        {
                            WorldX = sampleX,
                            WorldZ = sampleZ,
                            RainY = rainHeight,
                            HorizontalDist = sp.Distance,
                            Weight = sp.Weight
                        });

                        ctx.Viz?.AddSample(sampleX, ctx.PlayerY, sampleZ, EnclosureDebugVisualizer.VizColor.Exposed);
                    }
                }
            }

            SkyCoverage = totalWeight > 0f ? coveredWeight / totalWeight : 0f;
        }

        /// <summary>
        /// Early exit when player is above everything — open sky, no blocks nearby.
        /// </summary>
        private void HandleOutdoors(ScanContext ctx, long gameTimeMs)
        {
            OcclusionFactor = 0f;
            openingCache.Clear();
            verifiedOpenings.Clear();

            WeatherAudioManager.WeatherDebugLog(
                $"Enclosure calc: sky={SkyCoverage:F2} occl=0.00 (above rain heights) " +
                $"samples={samplePoints.Length}");

            if (firstUpdate)
            {
                SmoothedSkyCoverage = SkyCoverage;
                SmoothedOcclusionFactor = OcclusionFactor;
                firstUpdate = false;
                WeatherAudioManager.WeatherDebugLog(
                    $"[FIRST UPDATE] Set smoothed values directly (early return): sky={SkyCoverage:F2} occl={OcclusionFactor:F2}");
            }

            debugViz.EndFrame(ctx.Viz, gameTimeMs);
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 2: DDA verification on exposed candidates
        // ══════════════════════════════════════════════════════════════

        private void VerifyExposedCandidates(ScanContext ctx)
        {
            // Sort by distance -- verify closest first (most perceptually relevant)
            ctx.ExposedCandidates.Sort((a, b) => a.HorizontalDist.CompareTo(b.HorizontalDist));

            // Adaptive DDA budget: scale with enclosure level.
            // Outdoors (sky~0): 15 candidates (plenty — rain is everywhere).
            // Enclosed (sky~1): 50 candidates (need precision to find small openings).
            float budgetSky = firstUpdate ? SkyCoverage : SmoothedSkyCoverage;
            int adaptiveBudget = 15 + (int)((MAX_DDA_CANDIDATES - 15) * Math.Clamp(budgetSky, 0f, 1f));
            int toVerify = Math.Min(ctx.ExposedCandidates.Count, adaptiveBudget);

            // Mark candidates beyond budget in viz (dim orange = over budget)
            if (ctx.Viz != null && ctx.ExposedCandidates.Count > toVerify)
            {
                for (int d = toVerify; d < ctx.ExposedCandidates.Count; d++)
                {
                    var disc = ctx.ExposedCandidates[d];
                    ctx.Viz.AddDda(disc.WorldX, disc.RainY + 1, disc.WorldZ, EnclosureDebugVisualizer.VizColor.Candidate);
                }
            }

            if (ctx.DebugWeather)
            {
                int playerRainH = ctx.BlockAccessor.GetRainMapHeightAt(ctx.PlayerX, ctx.PlayerZ);
                WeatherAudioManager.WeatherDebugLog(
                    $"ENCL DIAG: playerPos=({ctx.PlayerX},{ctx.PlayerY},{ctx.PlayerZ}) " +
                    $"ear=({ctx.PlayerEarPos.X:F1},{ctx.PlayerEarPos.Y:F1},{ctx.PlayerEarPos.Z:F1}) " +
                    $"playerColumnRainH={playerRainH} exposed={ctx.ExposedCandidates.Count} toVerify={toVerify}");
            }

            // Collect blocked candidates for Step 3 neighbor search
            var partialCandidates = new List<PartialCandidate>();

            for (int i = 0; i < toVerify; i++)
            {
                var candidate = ctx.ExposedCandidates[i];
                ctx.ExposedTotalWeight += candidate.Weight;

                // Schmitt trigger: columns already in the cache use the lenient leave
                // threshold. New columns must beat the stricter join threshold.
                var columnKey = (candidate.WorldX, candidate.WorldZ);
                bool alreadyCached = openingCache.ContainsKey(columnKey);
                float effectiveThreshold = alreadyCached ? HYSTERESIS_LEAVE_THRESHOLD : HYSTERESIS_JOIN_THRESHOLD;

                // Find best audible path through this column (multi-height DDA)
                var result = FindBestPath(ctx, candidate, effectiveThreshold, i);

                if (result.BestOcclusion < effectiveThreshold)
                {
                    // Clear path found — rain is audible from this column
                    ctx.DirectCount++;
                    ctx.DirectWeight += candidate.Weight;

                    if (ctx.DebugWeather)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"  CAND[{i}] ({candidate.WorldX},{candidate.WorldZ}) rainY={candidate.RainY} dist={candidate.HorizontalDist:F1} " +
                            $"DIRECT: bestOccl={result.BestOcclusion:F2} < {effectiveThreshold:F2}{(alreadyCached ? " (hysteresis)" : "")}");
                    }

                    ctx.FreshVerified.Add(new VerifiedRainPosition
                    {
                        WorldPos = result.BestRainPos,
                        EntryPos = result.BestEntryPoint,
                        Occlusion = result.BestOcclusion + result.BestInteractableOcclusion,
                        Distance = (float)result.BestRainPos.DistanceTo(ctx.PlayerEarPos),
                        ColumnX = candidate.WorldX,
                        ColumnZ = candidate.WorldZ
                    });

                    ctx.Viz?.AddDda(candidate.WorldX, candidate.RainY + (int)result.BestHeight, candidate.WorldZ,
                        EnclosureDebugVisualizer.VizColor.Confirmed);
                }
                else
                {
                    if (ctx.DebugWeather)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"  CAND[{i}] ({candidate.WorldX},{candidate.WorldZ}) rainY={candidate.RainY} dist={candidate.HorizontalDist:F1} " +
                            $"OCCLUDED: bestOccl={result.BestOcclusion:F2} (all heights blocked)");
                    }

                    // Collect for Step 3 -- nearby wall with thin occlusion -> opening might be adjacent
                    if (result.BestOcclusion < PARTIAL_OCC_THRESHOLD)
                    {
                        partialCandidates.Add(new PartialCandidate
                        {
                            WorldX = candidate.WorldX,
                            WorldZ = candidate.WorldZ,
                            RainY = candidate.RainY,
                            BestOcclusion = result.BestOcclusion,
                            Weight = candidate.Weight
                        });
                        ctx.Viz?.AddDda(candidate.WorldX, candidate.RainY + 1, candidate.WorldZ,
                            EnclosureDebugVisualizer.VizColor.Partial);
                    }
                    else
                    {
                        ctx.Viz?.AddDda(candidate.WorldX, candidate.RainY + 1, candidate.WorldZ,
                            EnclosureDebugVisualizer.VizColor.Blocked);
                    }
                }
            }

            // Store partial candidates on context for Step 3
            _lastPartialCandidates = partialCandidates;
        }

        // Temporary storage between VerifyExposedCandidates and SearchNeighborOpenings
        // (avoids adding yet another field to ScanContext for a single-use handoff)
        private List<PartialCandidate> _lastPartialCandidates;

        /// <summary>
        /// Result of multi-height DDA path finding for a single column.
        /// </summary>
        private struct PathResult
        {
            public float BestOcclusion;
            public float BestInteractableOcclusion;
            public Vec3d BestRainPos;
            public Vec3d BestEntryPoint;
            public float BestHeight;
        }

        /// <summary>
        /// Find the best audible path from a column to the player by casting DDA rays
        /// at multiple heights. Returns the minimum-occlusion result.
        ///
        /// Handles two cases:
        /// - Rain below player (multi-story): eye-level + foot-level rays
        /// - Normal: multi-height sampling from rainY upward (+1, +2, +3, +4, +8, +12)
        /// </summary>
        private PathResult FindBestPath(ScanContext ctx, ExposedCandidate candidate, float threshold, int candidateIndex)
        {
            // Determine if rain impacts below player (multi-story / floor hole).
            // When rain impact surface (rainY+1) is genuinely below player feet,
            // rainY-relative offsets cast rays upward through intervening floors
            // → false occlusion. Instead, cast near-horizontal rays at player's Y level.
            //
            // IMPORTANT: Only trigger for multi-story gaps (rain 2+ blocks below feet).
            // When rainY+1 is near player feet (rainY = playerY-1), standard sampling
            // from rainY+1.01 gives nearly-horizontal rays that correctly traverse walls.
            // The old threshold (rainY < playerY) caused foot-level rays at Y=3.2 to
            // pass UNDER walls at Y=4 due to DDA diagonal stepping.
            int playerFeetY = (int)Math.Floor(ctx.PlayerEarPos.Y - 1.5);
            bool rainBelowPlayer = (candidate.RainY + 1) < playerFeetY;

            // First ray — player-level when rain is below, ground-level otherwise
            float firstSampleY = rainBelowPlayer
                ? (float)ctx.PlayerEarPos.Y
                : candidate.RainY + COLUMN_SAMPLE_HEIGHTS[0];

            Vec3d firstRainPos = new Vec3d(candidate.WorldX + 0.5, firstSampleY, candidate.WorldZ + 0.5);

            Vec3d firstEntryPoint;
            float firstInteractable;
            float firstOcclusion = OcclusionCalculator.CalculateWeatherPathOcclusionWithEntry(
                firstRainPos, ctx.PlayerEarPos, ctx.BlockAccessor, out firstEntryPoint, out firstInteractable);

            // Early out: first ray already clear
            if (firstOcclusion < threshold)
            {
                return new PathResult
                {
                    BestOcclusion = firstOcclusion,
                    BestInteractableOcclusion = firstInteractable,
                    BestRainPos = firstRainPos,
                    BestEntryPoint = firstEntryPoint,
                    BestHeight = firstSampleY
                };
            }

            float bestOcclusion = firstOcclusion;
            float bestInteractable = firstInteractable;
            Vec3d bestRainPos = firstRainPos;
            Vec3d bestEntryPoint = firstEntryPoint;
            float bestHeight = firstSampleY;

            if (rainBelowPlayer)
            {
                // Rain below player: only check foot level (eye level already failed above)
                float footY = (float)ctx.PlayerEarPos.Y - 1.5f;
                Vec3d footPos = new Vec3d(candidate.WorldX + 0.5, footY, candidate.WorldZ + 0.5);

                Vec3d footEntryPoint;
                float footInteractable;
                float footOcclusion = OcclusionCalculator.CalculateWeatherPathOcclusionWithEntry(
                    footPos, ctx.PlayerEarPos, ctx.BlockAccessor, out footEntryPoint, out footInteractable);

                if (ctx.DebugWeather)
                {
                    WeatherAudioManager.WeatherDebugLog(
                        $"  CAND[{candidateIndex}] ({candidate.WorldX},{candidate.WorldZ}) rainY={candidate.RainY} " +
                        $"BELOW-PLAYER foot-level (y={footY:F1}) occl={footOcclusion:F2}");
                }

                if (footOcclusion < bestOcclusion)
                {
                    bestOcclusion = footOcclusion;
                    bestInteractable = footInteractable;
                    bestRainPos = footPos;
                    bestEntryPoint = footEntryPoint;
                    bestHeight = footY;
                }
            }
            else
            {
                // Normal multi-height sampling from rainY upward
                for (int h = 1; h < COLUMN_SAMPLE_HEIGHTS.Length; h++)
                {
                    Vec3d rainPos = new Vec3d(
                        candidate.WorldX + 0.5,
                        candidate.RainY + COLUMN_SAMPLE_HEIGHTS[h],
                        candidate.WorldZ + 0.5
                    );

                    Vec3d elevatedEntryPoint;
                    float elevatedInteractable;
                    float pathOcclusion = OcclusionCalculator.CalculateWeatherPathOcclusionWithEntry(
                        rainPos, ctx.PlayerEarPos, ctx.BlockAccessor, out elevatedEntryPoint, out elevatedInteractable);

                    if (ctx.DebugWeather)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"  CAND[{candidateIndex}] ({candidate.WorldX},{candidate.WorldZ}) rainY={candidate.RainY} " +
                            $"h=+{COLUMN_SAMPLE_HEIGHTS[h]:F0} (y={candidate.RainY + COLUMN_SAMPLE_HEIGHTS[h]:F1}) occl={pathOcclusion:F2}");
                    }

                    if (pathOcclusion < bestOcclusion)
                    {
                        bestOcclusion = pathOcclusion;
                        bestInteractable = elevatedInteractable;
                        bestRainPos = rainPos;
                        bestEntryPoint = elevatedEntryPoint;
                        bestHeight = COLUMN_SAMPLE_HEIGHTS[h];
                    }

                    // Found a clear elevated path — rain is visible above obstacle
                    if (bestOcclusion < threshold)
                        break;
                }
            }

            return new PathResult
            {
                BestOcclusion = bestOcclusion,
                BestInteractableOcclusion = bestInteractable,
                BestRainPos = bestRainPos,
                BestEntryPoint = bestEntryPoint,
                BestHeight = bestHeight
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 3: Opening neighbor search
        //  When no direct paths exist but partially-occluded candidates
        //  are nearby, the actual opening (door/window) is likely 1-2 blocks
        //  away laterally. Search neighboring columns of the best blocked
        //  candidates to find the column with clear LOS (occ < 0.3).
        //  The source gets placed AT the opening — where rain enters.
        //
        //  Only triggers when verifiedOpenings is empty. Once an opening is
        //  found and tracked by OpeningTracker, it persists via audibility
        //  until the player moves and it becomes occluded again.
        // ══════════════════════════════════════════════════════════════

        // Search pattern: 8 adjacent + 4 cardinal at distance 2
        private static readonly int[] NeighborSearchDX = { -1, 0, 1, -1, 1, -1, 0, 1, -2, 2, 0, 0 };
        private static readonly int[] NeighborSearchDZ = { -1, -1, -1, 0, 0, 1, 1, 1, 0, 0, -2, 2 };

        private void SearchNeighborOpenings(ScanContext ctx)
        {
            var partialCandidates = _lastPartialCandidates;
            if (partialCandidates == null || partialCandidates.Count == 0) return;
            if (verifiedOpenings.Count > 0) return; // Only search when no existing openings

            partialCandidates.Sort((a, b) => a.BestOcclusion.CompareTo(b.BestOcclusion));
            int toSearch = Math.Min(partialCandidates.Count, MAX_NEIGHBOR_CANDIDATES);

            if (ctx.DebugWeather)
            {
                WeatherAudioManager.WeatherDebugLog(
                    $"  Step3: searching neighbors of {toSearch} partial candidates (best occ={partialCandidates[0].BestOcclusion:F2})");
            }

            // Pre-allocate sample Y buffer outside loops (avoid stackalloc in loop)
            Span<float> sampleYs = stackalloc float[6];

            for (int p = 0; p < toSearch; p++)
            {
                var partial = partialCandidates[p];
                bool foundForThis = false;

                for (int n = 0; n < NeighborSearchDX.Length && !foundForThis; n++)
                {
                    int nx = partial.WorldX + NeighborSearchDX[n];
                    int nz = partial.WorldZ + NeighborSearchDZ[n];

                    int neighborRainH = ctx.BlockAccessor.GetRainMapHeightAt(nx, nz);

                    // Must be exposed (rain falls here) and at similar Y
                    if (ctx.PlayerY < neighborRainH) continue;
                    int yDiff = Math.Abs(neighborRainH - ctx.PlayerY);
                    if (yDiff > MAX_Y_DIFF) continue;

                    // Determine sample Y positions: player-level when rain is below feet, height-offset otherwise
                    // Same threshold as main loop: rain impact (rainH+1) must be below player feet
                    int neighborPlayerFeetY = (int)Math.Floor(ctx.PlayerEarPos.Y - 1.5);
                    bool neighborBelowPlayer = (neighborRainH + 1) < neighborPlayerFeetY;
                    int sampleCount;
                    if (neighborBelowPlayer)
                    {
                        // Near-horizontal rays at player eye/foot level
                        sampleYs[0] = (float)ctx.PlayerEarPos.Y;
                        sampleYs[1] = (float)ctx.PlayerEarPos.Y - 1.5f;
                        sampleCount = 2;
                    }
                    else
                    {
                        for (int sh = 0; sh < NEIGHBOR_SAMPLE_HEIGHTS.Length; sh++)
                            sampleYs[sh] = neighborRainH + NEIGHBOR_SAMPLE_HEIGHTS[sh];
                        sampleCount = NEIGHBOR_SAMPLE_HEIGHTS.Length;
                    }

                    float bestNeighborOcc = float.MaxValue;
                    float bestNeighborInteractable = 0f;
                    Vec3d bestNeighborPos = null;
                    Vec3d bestNeighborEntry = null;

                    for (int h = 0; h < sampleCount; h++)
                    {
                        Vec3d rainPos = new Vec3d(nx + 0.5, sampleYs[h], nz + 0.5);

                        Vec3d neighborEntryPoint;
                        float neighborInteractable;
                        float pathOcc = OcclusionCalculator.CalculateWeatherPathOcclusionWithEntry(
                            rainPos, ctx.PlayerEarPos, ctx.BlockAccessor, out neighborEntryPoint, out neighborInteractable);

                        if (pathOcc < bestNeighborOcc)
                        {
                            bestNeighborOcc = pathOcc;
                            bestNeighborInteractable = neighborInteractable;
                            bestNeighborPos = rainPos;
                            bestNeighborEntry = neighborEntryPoint;
                        }
                        if (bestNeighborOcc < DDA_OCCLUDED_THRESHOLD) break;
                    }

                    if (bestNeighborOcc < DDA_OCCLUDED_THRESHOLD && bestNeighborPos != null)
                    {
                        ctx.FreshVerified.Add(new VerifiedRainPosition
                        {
                            WorldPos = bestNeighborPos,
                            EntryPos = bestNeighborEntry,
                            Occlusion = bestNeighborOcc + bestNeighborInteractable,
                            Distance = (float)bestNeighborPos.DistanceTo(ctx.PlayerEarPos),
                            ColumnX = nx,
                            ColumnZ = nz
                        });

                        ctx.DirectCount++;
                        ctx.DirectWeight += partial.Weight;
                        ctx.NeighborFinds++;
                        foundForThis = true;

                        ctx.Viz?.AddDda(nx, neighborRainH + 1, nz, EnclosureDebugVisualizer.VizColor.Neighbor);

                        if (ctx.DebugWeather)
                        {
                            WeatherAudioManager.WeatherDebugLog(
                                $"  NEIGHBOR HIT: partial ({partial.WorldX},{partial.WorldZ}) occ={partial.BestOcclusion:F2} " +
                                $"-> opening ({nx},{nz}) rainH={neighborRainH} occ={bestNeighborOcc:F2}");
                        }
                    }
                }
            }

            if (ctx.DebugWeather && ctx.NeighborFinds == 0)
            {
                WeatherAudioManager.WeatherDebugLog(
                    $"  Step3: no openings found in {toSearch} candidate neighborhoods");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 2C: Horizontal probe rays for cave exit detection
        //  When player is enclosed (high sky coverage), cast rays outward
        //  at various angles above horizontal. Each ray checks if its
        //  endpoint is in sky-exposed air with a clear DDA path back.
        //  This detects diagonal cave mouths, overhangs, and side openings
        //  that column-based scanning cannot find (those only look up).
        //
        //  Angles: 15, 45, 75 degrees above horizontal x 12 compass directions
        //  = 36 probe rays total.
        // ══════════════════════════════════════════════════════════════

        // Precomputed probe elevations (radians)
        private static readonly float[] ProbeElevations = {
            15f * (float)Math.PI / 180f,
            45f * (float)Math.PI / 180f,
            75f * (float)Math.PI / 180f
        };

        private void ProbeForCaveExits(ScanContext ctx)
        {
            if (SkyCoverage < PROBE_SKY_COVERAGE_MIN) return;

            float angleStep = 2f * (float)Math.PI / PROBE_RAY_DIRECTIONS;

            if (ctx.DebugWeather)
            {
                WeatherAudioManager.WeatherDebugLog(
                    $"  Step2C: probing {PROBE_RAY_DIRECTIONS} dirs x {ProbeElevations.Length} elevations, " +
                    $"skyCov={SkyCoverage:F2} >= {PROBE_SKY_COVERAGE_MIN}");
            }

            for (int d = 0; d < PROBE_RAY_DIRECTIONS; d++)
            {
                float azimuth = d * angleStep;
                float cosAz = (float)Math.Cos(azimuth);
                float sinAz = (float)Math.Sin(azimuth);

                bool foundForDirection = false;

                for (int e = 0; e < ProbeElevations.Length && !foundForDirection; e++)
                {
                    float elevation = ProbeElevations[e];
                    float cosEl = (float)Math.Cos(elevation);
                    float sinEl = (float)Math.Sin(elevation);

                    // Direction vector: horizontal component scaled by cos(elevation), Y by sin(elevation)
                    double endX = ctx.PlayerEarPos.X + cosAz * cosEl * PROBE_RAY_MAX_DIST;
                    double endY = ctx.PlayerEarPos.Y + sinEl * PROBE_RAY_MAX_DIST;
                    double endZ = ctx.PlayerEarPos.Z + sinAz * cosEl * PROBE_RAY_MAX_DIST;

                    int bx = (int)Math.Floor(endX);
                    int by = (int)Math.Floor(endY);
                    int bz = (int)Math.Floor(endZ);
                    int endRainH = ctx.BlockAccessor.GetRainMapHeightAt(bx, bz);

                    // Ray endpoint must be ABOVE rain height (exposed to sky)
                    if (by < endRainH)
                        continue;

                    // Ray endpoint block must be air (not inside solid)
                    Block endBlock = ctx.BlockAccessor.GetBlock(new BlockPos(bx, by, bz));
                    if (endBlock != null && endBlock.Id != 0 && endBlock.CollisionBoxes != null
                        && endBlock.CollisionBoxes.Length > 0)
                        continue;

                    // Verify: clear path from sky-exposed point back to the player
                    Vec3d rainSourcePos = new Vec3d(bx + 0.5, endRainH + 1.01, bz + 0.5);

                    Vec3d entryPoint;
                    float probeInteractable;
                    float pathOcc = OcclusionCalculator.CalculateWeatherPathOcclusionWithEntry(
                        rainSourcePos, ctx.PlayerEarPos, ctx.BlockAccessor, out entryPoint, out probeInteractable);

                    if (ctx.DebugWeather)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"    PROBE d={d} el={elevation * 180f / Math.PI:F0}deg " +
                            $"end=({bx},{by},{bz}) rainH={endRainH} occ={pathOcc:F2}");
                    }

                    if (pathOcc < DDA_OCCLUDED_THRESHOLD)
                    {
                        ctx.FreshVerified.Add(new VerifiedRainPosition
                        {
                            WorldPos = rainSourcePos,
                            EntryPos = entryPoint,
                            Occlusion = pathOcc + probeInteractable,
                            Distance = (float)rainSourcePos.DistanceTo(ctx.PlayerEarPos),
                            ColumnX = bx,
                            ColumnZ = bz
                        });

                        ctx.ProbeFinds++;
                        foundForDirection = true;

                        ctx.Viz?.AddDda(bx, endRainH + 1, bz, EnclosureDebugVisualizer.VizColor.Probe);

                        if (ctx.DebugWeather)
                        {
                            WeatherAudioManager.WeatherDebugLog(
                                $"  PROBE HIT: dir={d} el={elevation * 180f / Math.PI:F0}deg " +
                                $"pos=({bx},{bz}) rainH={endRainH} occ={pathOcc:F2}");
                        }
                    }
                }
            }

            if (ctx.DebugWeather)
            {
                WeatherAudioManager.WeatherDebugLog(
                    $"  Step2C: {ctx.ProbeFinds} probe ray openings found");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Cache merge + output calculation
        //  Merge freshly verified columns into persistent cache,
        //  rebuild verifiedOpenings, and compute OcclusionFactor.
        // ══════════════════════════════════════════════════════════════

        private void MergeCacheAndComputeOutput(ScanContext ctx)
        {
            // Step A: Merge fresh results into cache
            for (int f = 0; f < ctx.FreshVerified.Count; f++)
            {
                var fresh = ctx.FreshVerified[f];
                var key = (fresh.ColumnX, fresh.ColumnZ);

                if (openingCache.TryGetValue(key, out var existing))
                {
                    existing.Data = fresh;
                    existing.ConsecutiveFailCount = 0;
                    existing.ReVerifiedThisCycle = true;
                }
                else
                {
                    openingCache[key] = new CachedOpening
                    {
                        Data = fresh,
                        ConsecutiveFailCount = 0,
                        ReVerifiedThisCycle = true
                    };
                }
            }

            // Step B: Increment fail count for cached columns not re-verified this cycle
            var keysToRemove = new List<(int, int)>();
            foreach (var kvp in openingCache)
            {
                if (!kvp.Value.ReVerifiedThisCycle)
                {
                    kvp.Value.ConsecutiveFailCount++;
                    if (kvp.Value.ConsecutiveFailCount > MAX_CONSECUTIVE_FAILS)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
            }
            for (int r = 0; r < keysToRemove.Count; r++)
            {
                if (ctx.DebugWeather)
                {
                    WeatherAudioManager.WeatherDebugLog(
                        $"  CACHE EVICT: column ({keysToRemove[r].Item1},{keysToRemove[r].Item2}) " +
                        $"failed {MAX_CONSECUTIVE_FAILS} consecutive cycles");
                }
                openingCache.Remove(keysToRemove[r]);
            }

            // Step C: Rebuild verifiedOpenings from the stable cache
            verifiedOpenings.Clear();
            float maxCacheDistSq = (SCAN_RADIUS + 4f) * (SCAN_RADIUS + 4f);
            var distanceKeysToRemove = new List<(int, int)>();

            foreach (var kvp in openingCache)
            {
                var cached = kvp.Value;
                double dx = cached.Data.WorldPos.X - ctx.PlayerEarPos.X;
                double dz = cached.Data.WorldPos.Z - ctx.PlayerEarPos.Z;
                if (dx * dx + dz * dz > maxCacheDistSq)
                {
                    distanceKeysToRemove.Add(kvp.Key);
                    continue;
                }

                var updated = cached.Data;
                updated.Distance = (float)cached.Data.WorldPos.DistanceTo(ctx.PlayerEarPos);
                verifiedOpenings.Add(updated);
            }
            for (int r = 0; r < distanceKeysToRemove.Count; r++)
                openingCache.Remove(distanceKeysToRemove[r]);

            // Compute occlusion factor
            float rawDdaOcclusion = ctx.ExposedTotalWeight > 0f
                ? 1f - (ctx.DirectWeight / ctx.ExposedTotalWeight)
                : 1f;

            // ── DDA-informed bounds for SkyCoverage ──
            // The heightmap scan (radius 12) produces SkyCoverage that's accurate for the
            // scan area but not always representative of the player's acoustic environment.
            // DDA occlusion captures actual material between rain and player:
            //
            // Floor: high DDA occlusion (walls blocking) → SkyCov can't be artificially low.
            //   Fixes small rooms (3x4) where tiny roof covers few of ~480 scan columns.
            //   At rawDda=0.95 (room): floor=0.71, lifting 0.34 → 0.71
            //   Always active — independent of player column exposure.
            //
            // Ceiling: low DDA occlusion (clear air paths) → SkyCov can't be inflated.
            //   Fixes narrow alleys where distant building roofs inflate SkyCov to 0.90+.
            //   At rawDda=0.05 (alley): ceiling=0.19, capping 0.90 → 0.19
            //   ONLY active when player IS personally exposed to sky (standing in rain).
            //   When player is under cover (cavern, room), ceiling stays at 1.0 so the
            //   raw SkyCoverage isn't capped — the player IS enclosed, we want high values.
            //   2-block vertical gradient prevents audio pops at overhang edges.
            float skyFloor = rawDdaOcclusion * 0.75f;

            // Player exposure gate: check if the player's own column is exposed to rain
            int playerRainH = ctx.BlockAccessor.GetRainMapHeightAt(ctx.PlayerX, ctx.PlayerZ);
            int vertMargin = ctx.PlayerY - playerRainH;
            // margin >= 0: fully exposed (standing in rain) → gate=1.0
            // margin = -1: partially under overhang edge   → gate=0.5
            // margin <= -2: clearly under cover            → gate=0.0
            float exposureGate = vertMargin >= 0 ? 1.0f
                               : vertMargin >= -2 ? 1.0f + vertMargin / 2.0f
                               : 0.0f;

            // Scale gate by fraction of sky that's exposed: prevents sky holes in big
            // buildings from triggering aggressive ceiling cap. Alley = many exposed
            // columns (~20-75) → scale≈1.0. Building with 1 skylight → scale≈0.06.
            // Reaches 1.0 at ~5% exposure (16+ out of ~312 scan columns).
            float exposedFraction = (float)ctx.ExposedCandidates.Count / samplePoints.Length;
            exposureGate *= Math.Clamp(exposedFraction * 20f, 0f, 1f);

            float baseCeiling = 0.15f + 0.85f * rawDdaOcclusion;
            // lerp(1.0, baseCeiling, exposureGate): full cap when exposed, no cap when covered
            float skyCeiling = baseCeiling + (1.0f - baseCeiling) * (1.0f - exposureGate);

            float rawSky = SkyCoverage;
            SkyCoverage = Math.Clamp(SkyCoverage, skyFloor, skyCeiling);

            if (ctx.DebugWeather && Math.Abs(SkyCoverage - rawSky) > 0.01f)
            {
                WeatherAudioManager.WeatherDebugLog(
                    $"  DDA BOUNDS: rawDda={rawDdaOcclusion:F2} floor={skyFloor:F2} ceil={skyCeiling:F2} " +
                    $"gate={exposureGate:F2} vMargin={vertMargin} sky {rawSky:F2} -> {SkyCoverage:F2}");
            }

            // Safety floor: SkyCoverage and OcclusionFactor should be correlated.
            // High sky coverage (mostly roofed) with zero DDA occlusion is contradictory —
            // rain MUST pass through material to reach a covered player.
            // Quadratic: gentle at low coverage, strong at high.
            float skyCoverageFloor = SkyCoverage * SkyCoverage;
            OcclusionFactor = Math.Max(rawDdaOcclusion, skyCoverageFloor);
        }

        // ══════════════════════════════════════════════════════════════
        //  Debug status
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get a debug status string for /soundphysics weather command.
        /// </summary>
        public string GetDebugStatus()
        {
            string vizStats = debugViz.GetStatsString();
            return $"SkyCov={SmoothedSkyCoverage:F2}(raw={SkyCoverage:F2}) " +
                   $"Occl={SmoothedOcclusionFactor:F2}(raw={OcclusionFactor:F2}) " +
                   $"Openings={verifiedOpenings.Count}(cached={openingCache.Count}){vizStats}";
        }

        public void Dispose()
        {
            verifiedOpenings.Clear();
            openingCache.Clear();
            debugViz.Dispose();
        }

        // ── Internal types ──

        private struct SamplePoint
        {
            public int DX, DZ;
            public float Distance;
            public float Weight;
        }

        private struct ExposedCandidate
        {
            public int WorldX, WorldZ;
            public int RainY;
            public float HorizontalDist;
            public float Weight;
        }

        /// <summary>
        /// A DDA candidate that was blocked but with low occlusion — rain is nearby
        /// behind thin walls. Used by Step 3 to search neighbors for the actual opening.
        /// </summary>
        private struct PartialCandidate
        {
            public int WorldX, WorldZ;
            public int RainY;
            public float BestOcclusion;
            public float Weight;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Debug visualization — separated from scan logic for clarity
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Manages block highlight rendering for the enclosure calculator.
    /// Separated from the scan pipeline so visualization concerns don't
    /// interleave with acoustic logic. All viz calls are null-safe via
    /// the VizData pattern: when viz is disabled, BeginFrame returns null
    /// and all AddXxx calls are skipped via null-conditional (?.) operator.
    /// </summary>
    internal class EnclosureDebugVisualizer : IDisposable
    {
        private readonly ICoreClientAPI capi;

        // Block highlight slot IDs (unique per visualization layer)
        private const int HIGHLIGHT_SLOT_SAMPLES = 90;   // Heightmap sample grid
        private const int HIGHLIGHT_SLOT_DDA = 91;       // DDA candidate results

        // Visualization rate limit (don't update highlights every 100ms - too flashy)
        private const long VIZ_UPDATE_INTERVAL_MS = 500;
        private long lastVizUpdateMs = 0;

        // Last stats for chat dump
        private int lastExposedCount;
        private int lastCoveredCount;
        private int lastVerifiedCount;
        private int lastBlockedCount;
        private int lastPartialCount;
        private int lastNeighborCount;
        private int lastProbeCount;
        private int lastTotalSamples;
        private int lastToVerify;

        public enum VizColor
        {
            Covered,    // Blue — covered by roof
            Exposed,    // Yellow — exposed to sky
            Candidate,  // Dim Orange — DDA candidate (over budget)
            Confirmed,  // White — rain reaches player (audio source)
            Blocked,    // Red — blocked (occluded)
            Partial,    // Orange — partial (neighbor search)
            Neighbor,   // Cyan — neighbor hit
            Probe       // Green — probe ray cave exit
        }

        // VS uses ABGR byte order: ColorFromRgba(R, G, B, A)
        //
        // COLOR LEGEND:
        //   Heightmap grid (slot 90) - what sky looks like from above:
        //     Blue   = covered (roof blocks sky)
        //     Yellow = exposed (open to sky, rain can fall)
        //
        //   DDA results (slot 91) - can you HEAR rain from this column?:
        //     White  = confirmed source (rain sound reaches you - clear air path)
        //     Red    = blocked (wall in the way - no sound)
        //     Orange = partial (thin wall - searching neighbors for opening)
        //     Cyan   = neighbor hit (found opening laterally from partial)
        //     Green  = probe ray hit (cave exit - found sky via directional ray)
        //     Dim Orange = over budget (exposed but not checked this ring)
        private static readonly int[] Colors = {
            ColorUtil.ColorFromRgba(0, 0, 255, 128),     // Covered - Blue
            ColorUtil.ColorFromRgba(255, 255, 0, 100),   // Exposed - Yellow
            ColorUtil.ColorFromRgba(255, 200, 0, 120),   // Candidate - Dim Orange
            ColorUtil.ColorFromRgba(255, 255, 255, 220), // Confirmed - White
            ColorUtil.ColorFromRgba(255, 0, 0, 200),     // Blocked - Red
            ColorUtil.ColorFromRgba(255, 128, 0, 200),   // Partial - Orange
            ColorUtil.ColorFromRgba(0, 255, 255, 200),   // Neighbor - Cyan
            ColorUtil.ColorFromRgba(0, 255, 128, 200),   // Probe - Green
        };

        /// <summary>
        /// Per-frame visualization data. Null when viz is disabled.
        /// Passed through ScanContext so step methods can contribute
        /// without knowing about the visualizer.
        /// </summary>
        public class VizData
        {
            public List<BlockPos> SamplePositions = new();
            public List<int> SampleColors = new();
            public List<BlockPos> DdaPositions = new();
            public List<int> DdaColors = new();

            // Per-frame counters
            public int ExposedCount;
            public int CoveredCount;
            public int VerifiedCount;
            public int BlockedCount;
            public int PartialCount;
            public int NeighborCount;
            public int ProbeCount;

            public void AddSample(int x, int y, int z, VizColor color)
            {
                SamplePositions.Add(new BlockPos(x, y, z));
                SampleColors.Add(Colors[(int)color]);
                if (color == VizColor.Covered) CoveredCount++;
                else if (color == VizColor.Exposed) ExposedCount++;
            }

            public void AddDda(int x, int y, int z, VizColor color)
            {
                DdaPositions.Add(new BlockPos(x, y, z));
                DdaColors.Add(Colors[(int)color]);
                switch (color)
                {
                    case VizColor.Confirmed: VerifiedCount++; break;
                    case VizColor.Blocked: BlockedCount++; break;
                    case VizColor.Partial: PartialCount++; break;
                    case VizColor.Neighbor: NeighborCount++; break;
                    case VizColor.Probe: ProbeCount++; break;
                }
            }
        }

        public EnclosureDebugVisualizer(ICoreClientAPI api)
        {
            capi = api;
        }

        /// <summary>
        /// Returns VizData if visualization is due this frame, null otherwise.
        /// Callers use null-conditional (?.) to skip viz work when disabled.
        /// </summary>
        public VizData BeginFrame(long gameTimeMs)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            bool viz = config?.DebugWeatherVisualization == true;

            if (viz && (gameTimeMs - lastVizUpdateMs >= VIZ_UPDATE_INTERVAL_MS))
                return new VizData();

            return null;
        }

        /// <summary>
        /// Render collected viz data and store stats, or clear highlights if viz disabled.
        /// </summary>
        public void EndFrame(VizData data, long gameTimeMs)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            bool viz = config?.DebugWeatherVisualization == true;

            if (data != null)
            {
                lastVizUpdateMs = gameTimeMs;

                lastExposedCount = data.ExposedCount;
                lastCoveredCount = data.CoveredCount;
                lastVerifiedCount = data.VerifiedCount;
                lastBlockedCount = data.BlockedCount;
                lastPartialCount = data.PartialCount;
                lastNeighborCount = data.NeighborCount;
                lastProbeCount = data.ProbeCount;
                lastTotalSamples = data.SamplePositions.Count;
                lastToVerify = 0; // Not tracked per-viz anymore — available from debug log

                var player = capi.World.Player;
                if (player != null)
                {
                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_SAMPLES,
                        data.SamplePositions, data.SampleColors,
                        EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);

                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_DDA,
                        data.DdaPositions, data.DdaColors,
                        EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
                }
            }
            else if (!viz)
            {
                // Visualization just turned off — clear highlights
                var player = capi.World.Player;
                if (player != null && lastTotalSamples > 0)
                {
                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_SAMPLES,
                        new List<BlockPos>(), new List<int>());
                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_DDA,
                        new List<BlockPos>(), new List<int>());
                    lastTotalSamples = 0;
                }
            }
        }

        public string GetStatsString()
        {
            if (SoundPhysicsAdaptedModSystem.Config?.DebugWeatherVisualization != true)
                return "";

            return $"\nViz: samples={lastTotalSamples} covered={lastCoveredCount}(blue) " +
                   $"exposed={lastExposedCount}(yellow) toVerify={lastToVerify}/{WeatherEnclosureCalculator.MAX_DDA_CANDIDATES}\n" +
                   $"     confirmed={lastVerifiedCount}(white) blocked={lastBlockedCount}(red) " +
                   $"partial={lastPartialCount}(orange) neighbor={lastNeighborCount}(cyan) probe={lastProbeCount}(green)";
        }

        public void Dispose()
        {
            var player = capi?.World?.Player;
            if (player != null)
            {
                try
                {
                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_SAMPLES,
                        new List<BlockPos>(), new List<int>());
                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_DDA,
                        new List<BlockPos>(), new List<int>());
                }
                catch { /* Swallow — world may be unloading */ }
            }
        }
    }

    /// <summary>
    /// A verified rain impact position with clear air path to the player.
    /// Used by Phase 5B for positional source clustering.
    /// </summary>
    public struct VerifiedRainPosition
    {
        public Vec3d WorldPos;
        /// <summary>
        /// The last solid-to-air transition point on the DDA ray from rain to player.
        /// This is where sound enters the player's acoustic space (wall opening face).
        /// Null for clear sky openings (no solid blocks along path).
        /// </summary>
        public Vec3d EntryPos;
        public float Occlusion;
        public float Distance;

        /// <summary>
        /// Column identity for temporal tracking. Used by the cache to persist
        /// openings across scan cycles and apply Schmitt trigger hysteresis.
        /// </summary>
        public int ColumnX;
        public int ColumnZ;
    }
}
