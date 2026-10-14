# Upstream source

This bridge vendors only xatlas' implementation, public header, and license from `jpcy/xatlas` at commit `f700c7790aaa030e794b52ba7791a05c085faf0c`.

- Upstream: https://github.com/jpcy/xatlas
- Implementation: `source/xatlas/xatlas.cpp`
- Header: `source/xatlas/xatlas.h`
- License: `LICENSE` (MIT)

SHA-256 checksums of the vendored files:

- `vendor/xatlas/xatlas.cpp`: `0ed0283aad005c94738cb0cc4612dbA264379d29dea5b3c9b242f2d4752d5df4`
- `vendor/xatlas/xatlas.h`: `e7675335ad8ab1c1cc9060ad153cf6b8ba2ee914282044eb5f02c49590218fbd`
- `vendor/xatlas/LICENSE`: `2c16d5b1c2808277fe975b4f70f0fd9afc9b3bcf04e6c676665a82cb2d5579e3`

The standalone CMake target compiles xatlas single-threaded and with its debug allocator disabled. The bridge serializes jobs because xatlas exposes its allocator as process-global state. The wrapper supplies its own job-scoped allocator, cancellation callback, and C ABI.
