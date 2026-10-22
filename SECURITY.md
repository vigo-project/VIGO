# Security policy

## Supported versions

Only the latest release of VIGO receives security fixes.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private vulnerability reporting: open the **Security** tab of this repository and choose
**Report a vulnerability**. Include the VIGO version, the steps to reproduce and the impact you see.

You can expect an acknowledgement within a few days. Once a fix is released, the report is credited in the release
notes unless you prefer to stay anonymous.

## Scope notes

- VIGO stores hosting-provider tokens and the optional Anthropic API key in the Windows Credential Locker, never in a
  file or a log.
- VIGO runs the `git` executable installed on the machine; its credential helpers and SSH configuration apply.
