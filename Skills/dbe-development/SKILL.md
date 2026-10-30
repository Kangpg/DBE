---
name: dbe-development
description: Implement and verify the DBE Windows MSVC distributed-build experiment, including compiler wrapping, MSBuild integration, Worker scheduling, process ownership and monitoring.
---

# DBE development

Use the active repository containing `native/`, `worker/` and `scripts/`. Read README, docs/issues.md and docs/benchmark.md. Keep this maintained source and the installed user skill synchronized.

## Publication boundary

Only DBE-owned code, synthetic fixtures and generalized documentation belong in Git or issues. Never include external project names, source filenames, paths, internal structure, project-specific scripts, source, raw logs or machine identifiers. Private repository visibility does not authorize internal content. An authorized benchmark target is described only as “현재 프로젝트”, with approved timing/outcome data. Inspect filenames, content, commit descriptions and issue text before publishing; scans are supplementary. Preserve local private evidence outside the upload tree.

## Execution contracts

- Preserve native CL Task/tracker and raw wrapper arguments, encodings, cwd, exit status and caller inheritable diagnostic handles. Wrapper-owned handles are non-inheritable.
- Assign owned Jobs atomically at creation. Capture process handles before cancellation/owner-death checks; PID/path alone does not prove ownership. Retain uncertain cleanup rather than silently releasing resources.
- Resolve the project-selected compiler with Toolchain.ps1; do not guess the newest toolchain. Use fresh adapters and preserve existing pairing. Never overwrite active runtimes or force-close user IDEs.
- Schannel uses UserKeySet rather than EphemeralKeySet; retain certificate lifetime through active connections. Worker-only ZIP must not contain client private identity.
- Preserve option interpretation in ClInvocation/CompileOptions. Unsupported profiles may go local; actual remote compiler errors must not be hidden by local retry. Binary-directory identity is not complete SDK closure.
- PCH generation/link stay local. Remote PCH users expand headers locally; this is not PCH reuse. Local PCH consumers retain original Fd. Share a PDB only inside one native MP Job.
- Prepare before acquiring remote execution slots. Keep identity-based worker limits and bounded preparation tickets. Release remote lease before local fallback waits. Required-local PDB waiters yield ahead of new MP groups within their scheduler; this is not global fairness.
- MSBuild runtime input exclusions match exact parent directories, including historical attempt directories from read tlogs. Preserve original PCH/header tracking and include membership changes. No-op means no compile/preprocess/request, not only unchanged object hashes.

## Verification

Build-Worker.ps1 and Build-Wrapper.ps1 build the runtime. Test-Worker.ps1 covers real TLS/compile/error/cancel boundaries. Test-MSBuildBaseline.ps1 and Test-Wrapper.ps1 cover actual CL/tracking/diagnostics. Test-CompilerProfiles.ps1 uses synthetic PCH/PDB/MP/JMC/encoding fixtures. Test-HybridIncremental.ps1 checks changed/new/removed/forced headers, errors, recovery and no-op. Test-LocalBatchCancellation.ps1 captures actual MP driver/child handles. Test-BuildHost.ps1 requires generated VSIX host and Worker test child.

Use ordinary MSBuild verbosity; diagnostic verbosity can persist ambient environment. All generated artifacts stay in ignored out/. For benchmarks preserve native PCH/MP and normal apps. Distinguish fresh outputs from cold cache, failed timing from speedup, and initiator Job commit/CPU from unmeasured remote costs. Fixture success is not external-project support.

## UI and resource policy

VSIX install success does not establish menu visibility. Verify resource/pkgdef registration, persistent configuration refresh and actual UI execution. Use UTF-8 for redirected output. Project commands preserve solution dependencies/configuration mapping and selected loaded C++ project. If automation flickers, stop repeated input and use user-assisted screenshots.

Protect receiving PC users first. Fixed slots and initiator sampling do not implement activity protection, personal/dedicated purpose, bidirectional enrollment or machine-wide fairness. Keep incoming and own-build budgets distinct. Current development is paused; resume only when requested.
