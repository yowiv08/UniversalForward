# UniversalForward CPU microbenchmarks

Already-built plugin DLLs in separate .NET 10 processes; purely in-memory SSE sources; input and expected hashes generated outside timing; one warmup and three measured samples per case; forced GC outside each timed region; GC.GetTotalAllocatedBytes(precise:true) measures managed allocation; journal includes real CapturedContent read, metadata completion, and queue flush; original incoming body is null for both log variants; no network requests.

Runtime: 10.0.12; logical processors: 20; GC: server=False;latency=Interactive.
Plugin assembly versions: baseline 0.3.8.0; fixed 0.3.9.0.

All values below are medians of three measured samples after one warmup. All 8 downstream output hashes match the expected value and match across baseline/fixed.

| Case | Baseline ms | Fixed ms | Speedup | Baseline allocated MiB | Fixed allocated MiB | Allocation reduction |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| claude-100char-2500-deltas | 122.652 | 10.972 | 11.18x | 606.419 | 7.135 | 84.99x |
| claude-100char-5000-deltas | 434.716 | 23.009 | 18.89x | 2404.876 | 14.240 | 168.89x |
| claude-100char-10000-deltas | 1668.150 | 51.797 | 32.21x | 9577.999 | 28.461 | 336.53x |
| responses-single-line-1MiB | 22.438 | 1.355 | 16.55x | 146.012 | 14.050 | 10.39x |
| responses-single-line-2MiB | 105.208 | 3.253 | 32.34x | 548.035 | 28.057 | 19.53x |
| responses-single-line-4MiB | 409.029 | 7.660 | 53.40x | 2120.069 | 56.071 | 37.81x |
| responses-single-line-8MiB | 1548.597 | 16.477 | 93.99x | 8336.161 | 112.098 | 74.37x |
| journal-sse-512KiB | 191.543 | 1.016 | 188.53x | 557.161 | 8.066 | 69.08x |

Journal saved upstream response body: baseline 524288 bytes, fixed 0 bytes. Saved request body: baseline 56 bytes, fixed 0 bytes. Both retain correct HTTP status 200, reported model upstream-bench, reasoning effort high, first-byte metadata, and trace.

Microbenchmark wall time is affected by workstation activity and JIT; process CPU time has coarse timer resolution; allocation values describe allocation traffic, not retained memory. Fixed journal intentionally stores no upstream request/response body; equal downstream hashes and metadata are checked.

Reproduce from a v0.3.9 checkout after building both plugin versions:

```powershell
.\benchmarks\cpu\run-benchmarks.ps1 -BaselineBin '<v0.3.8 DLL directory>' -FixedBin '<v0.3.9 DLL directory>' -ContractsBin '<Router.Contracts DLL directory>'
```

This builds the benchmark executable only. The referenced baseline and fixed plugin DLLs must already exist. ContractsBin defaults to BaselineBin when it contains Router.Contracts.dll. See [the public benchmark README](../../../benchmarks/cpu/README.md) for checkout/build commands and output-directory options. Use -ClaudeMax 5000 to omit the largest Claude case for a shorter run. Full per-sample measurements, DLL SHA-256 hashes, downstream hashes, and synthetic journal metadata are in [benchmark-results.json](benchmark-results.json).
