# M4 Profiling Data

> Collected 2026-07-23. Unity 6000.5.3f1, URP 17.5, Entities 1.0, Burst, editor Play mode,
> default scene (`CitizenSimScene.unity`), default spawn seed. Hotkeys: `1/2/3/4` scale,
> `T` threat toggle, `WASD` move threat zone.

## Method

- Scale switched at runtime by updating `CitizenBootstrap.count` and calling `Spawn()` (the same path used by the `ScaleDial` hotkeys).
- Frame timing from `FrameTimingManager` (`manage_profiler get_frame_timing`), sampled 3x per scale
  after a short stabilization window; median reported. Editor Play mode includes some editor overhead,
  so absolute numbers are slightly pessimistic vs a player build — but the *curve* and the ceiling are
  the point.
- "Main thread" = `cpu_main_thread_frame_time_ms` (the GO-spine work: `SnapshotSystem` + `ResolveSystem`).
- Threat OFF for the scale curve (isolates the daily-loop ceiling). A separate threat-ON observation
  is reported at 5000.

## Scale curve (Burst ON, threat OFF)

| Citizens | CPU frame (ms) | ~FPS | Main thread (ms) | GPU (ms) |
|----------|---------------|------|------------------|----------|
| 100      | 6.0           | 167  | 2.3              | 0.37     |
| 500      | 9.0           | 111  | 4.0              | 0.67     |
| 2000     | 19.4          | 52   | 11.7             | 1.25     |
| 5000     | 34.0          | 29   | 23.0             | 1.70     |

Spawn cost (one-time, on scale switch): **5000 ≈ 1006 ms**, 2000 ≈ 110 ms, 500 ≈ 12 ms, 100 ≈ 3 ms.

### Reading the curve

- **100 → 500**: ~1.5x frame time for 5x agents. Cheap region; mostly fixed overhead.
- **500 → 2000**: ~2.2x frame time for 4x agents. Linear-ish; jobs + GO loops scale with N.
- **2000 → 5000**: ~1.75x frame time for 2.5x agents. **Ceiling region.** Main thread hits 23 ms
  (69% of a 33 ms budget). The frame is now main-thread-bound on GO writeback.

### Where the main-thread time goes (architecture, not per-system markers)

The 23 ms main-thread figure at 5000 is dominated by the two GO-spine loops, both O(N) on the main
thread by design (GO is source of truth):

- `SnapshotSystem` — GO→ECS: reads `transform.position` + authoring state for every citizen, writes
  `SimPosition`/`SimNeeds`/`SimGoal`.
- `ResolveSystem` — ECS→GO: writes `transform.position += vel*dt` and mirrors `Threatened` bit back to
  `ca.threatened` for every citizen.

The Burst jobs (`SpatialGridSystem`, `ThreatDetectionSystem`, `NeedsDecaySystem`, `SteeringSystem`) run
on worker threads and are **not** on the main-thread critical path. At 5000 their cost is a small
fraction of the frame and is absorbed by the job scheduler while the main thread does the GO loops.
This is why the ceiling tracks main-thread GO writeback, not job throughput.

## 5000 ceiling + threat ON

With the threat zone active at origin (radius 5 m) and 5000 citizens spawned in a 40 m radius:

| Metric | Value |
|--------|-------|
| Threatened citizens | 174 / 5000 |
| BT ticks / frame | 335 (174 preempted + ~161 round-robin) |
| CPU frame (ms) | ~38 steady, spikes to ~80 |
| Main thread (ms) | ~24 steady, spikes to ~58 |
| ~FPS | ~26 steady |

The spikes align with BT tick batches: `BtScheduler` round-robin ticks `N/30` agents/frame plus all
threatened agents preemptively, so every ~30th frame carries a larger batch. The preemption path is
visible in the profiler as periodic main-thread spikes, not a constant overhead — exactly the
time-slicing design from M4 T2.

## Burst ON / OFF

**Burst ON** is the baseline for every number above. All four ECS jobs carry `[BurstCompile]`.

**Burst OFF was not cleanly measurable in this session.** Two paths were attempted:

1. **Editor menu** — the standard runtime toggle is `Jobs > Use Burst Jobs`. This menu item is **not
   registered** in this Unity 6000.5.3f1 setup (`MenuItemExists` returns false for `Jobs/Use Burst Jobs`,
   `Burst/Enable Compilation`, and variants; `ExecuteMenuItem` fails for all). The Burst package's
   editor menu appears absent or relocated in this version.
2. **Runtime mutation of `BurstCompilerOptions.EnableBurstCompilation`** — set to `false` via reflection
   on the static `BurstCompiler.Options` field. This flag controls `FunctionPointer<Burst>` compilation,
   **not** `IJobEntity` scheduling. Mid-session it stalled the live job pipeline (frame submit stopped,
   `cpu_frame_time` read ~108 ms with `main_thread ≈ 0` and `first_submit_timestamp = 0`). Re-enabling
   did not recover the live session; a stop/replay was required. `BurstCompiler.IsEnabled` is read-only
   and `SetExecutionMode` only selects the JIT environment (SIMD/precision), not enable/disable.

**Conclusion (architectural, not measured):** disabling Burst would not move the 5000 ceiling. The
ceiling is the 23 ms main-thread GO-writeback loop (`SnapshotSystem` + `ResolveSystem`), which is
managed C# on the main thread by design and never runs under Burst. Burst only affects the four worker-
thread jobs; at 5000 those are a small fraction of the frame and run concurrently with the main-thread
loops. A Burst-off run would show a modest regression at 100–2000 (where job time is a larger share)
and ~no change at 5000 (main-thread-bound). A clean measured comparison would require the editor menu
(absent here) or a player build with `EnableBurstCompilation = false` set before compile — left as a
follow-up.

## Honest ceiling statement

- **5000 citizens ≈ 29 FPS** (34 ms/frame), main-thread-bound on GO Transform writeback. Below the
  30 FPS bar by a small margin. **No mitigation applied** in M4 — this is the honest GO-centric
  architecture ceiling and the intended demo talking point.
- **2000 citizens ≈ 52 FPS** — comfortable. This is the practical "looks great" scale for the demo.
- The architecture scales linearly in the ECS layer; the ceiling is the GO-spine sync cost, which is
  the explicit trade-off of the GO-as-spine / ECS-as-optimization-layer design (GO source of truth,
  ECS mirrors high-freq sim). A pure-ECS build (no GO writeback) would remove this ceiling entirely.
