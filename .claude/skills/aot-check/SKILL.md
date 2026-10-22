---
name: aot-check
description: Native AOT milestone check for VIGO - Release publish, IL warnings compared to the accepted baseline, then launch of the published exe with the startup probe. Run after each milestone (Jx.y) before the handoff.
disable-model-invocation: true
argument-hint: "[--skip-publish] [--update-baseline] [--runs N]"
---

# AOT check

CLAUDE.md rule: after each milestone, `dotnet publish -c Release` must not introduce any new IL warning, and the
published exe must start.

## Steps

1. Run the script (it can take several minutes; prefer a long timeout, 600000 ms):
   ```
   rtk proxy pwsh -NoProfile -ExecutionPolicy Bypass -File .claude/skills/aot-check/aot-check.ps1
   ```
   Map the arguments: `--skip-publish` → `-SkipPublish` (probe only), `--update-baseline` → `-UpdateBaseline`,
   `--runs N` → `-Runs N`.
2. Exit codes: `0` OK, `1` publish failed or exe missing, `3` new IL warnings.
3. Report in French, in a few lines: total / new / resolved IL warnings, publish size, first-frame times (min/max,
   target < 1000 ms).
4. If there are new IL warnings: analyze each one (code, assembly, cause) and propose a fix
   (source generator, `[DynamicallyAccessedMembers]`, replacing the API…). The `dotnet-skills:aot-trimming` skill can help.
   **Never** run `-UpdateBaseline` to make a warning disappear without explicit user approval; when it is accepted,
   explain the reason in the pull request.
5. If the probe fails or times out: read the latest `%LOCALAPPDATA%\VIGO\logs\vigo-*.log`.

## Files

- `aot-check.ps1`: the script. Full publish log: `artifacts/publish/aot-publish.log`.
- `il-baseline.txt`: accepted IL warnings, one `ILxxxx: message` per line (`#` = comment).
