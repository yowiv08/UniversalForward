# UniversalForward v0.3.9 CPU measurements

[benchmark-results.md](benchmark-results.md) contains the comparison table from the measured v0.3.8/v0.3.9 run. [benchmark-results.json](benchmark-results.json) preserves the individual samples, expected/downstream SHA-256 values, measured DLL SHA-256 values, versions, runtime information, and synthetic logger metadata.

The measurements use generated in-memory inputs. They contain no credentials or real request/response content. The public JSON copy replaces the two machine-local DLL paths with relative benchmark output paths; all measurement values, hashes, versions, and timestamps are unchanged. The Markdown reproduction instructions point to the public runner instead of the original local artifact directory.

Source and reproduction instructions: [benchmarks/cpu/README.md](../../../benchmarks/cpu/README.md).
