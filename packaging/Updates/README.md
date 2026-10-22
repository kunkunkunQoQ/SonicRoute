# Portable updates (protocol 2)

## Automated GitHub Releases Workflow (CI/CD)

The repository provides a fully automated, one-click GitHub Actions workflow [`.github/workflows/release.yml`](../../.github/workflows/release.yml) and an orchestration script [`Invoke-ReleasePipeline.ps1`](./Invoke-ReleasePipeline.ps1).

### One-Click Release Usage

1. Open GitHub repository → **Actions** → select **Release** workflow.
2. Click **Run workflow** (branch: `master`).
3. (Optional) Check **Dry Run** to simulate all preflight checks, all five package builds, and local validation without publishing or modifying GitHub releases.
4. The workflow automatically:
   - Reads the target release version from `build/Version.props` (e.g. `1.25` → `v1.25`).
   - Asserts preflight conditions (strictly `master` branch, commit SHA binding, monotonic version increase against remote releases and local tags, absence of conflicting published releases).
   - Compiles and packages Lite .NET 10 x64/ARM64, Lite .NET 8 x64/ARM64 and Legacy x64 via `Build-PortableRelease.ps1`.
   - Validates local artifacts (PE machine types `0x8664`/`0xaa64`, FileVersion, 13-file allowlist for Legacy ZIP, `SHA256SUMS.txt`, `SonicRoute-update.json` protocol 2 schema).
   - Generates release notes: prioritizes curated `docs/release-v<version>.md` if maintained, or automatically extracts and groups conventional commits and PRs since the previous official release tag.
   - Creates a **Draft Release** pinned to the validated commit SHA and uploads all 7 assets.
   - Performs **Remote Cryptographic Re-verification**: re-queries the GitHub API for asset counts and sizes, downloads each asset back to an isolated temporary sandbox, and re-computes its SHA-256 hash locally.
   - Promotes the draft to an official public **Latest Release** (`prerelease: false`, `latest: true`).
   - Generates a full markdown report into GitHub Actions Step Summary.

### Safeguards and Failure Recovery

- **Zero Unverified Releases**: Releases remain in draft state until all 7 assets pass remote download and cryptographic hash validation.
- **Idempotent Draft Takeover**: If a previous run failed during network upload or verification, re-running on the same commit safely updates the existing draft release and overwrites assets (`--clobber`) without tag collisions.
- **Published Immutability**: If an official (non-draft) release already exists for `v<version>`, the pipeline immediately aborts to prevent overwriting or deleting published artifacts.
- **Manual Verification / Dry-Run**: Run locally or in CI at any time using:
  ```powershell
  ./packaging/Updates/Invoke-ReleasePipeline.ps1 -DryRun
  ```

---

## Local Manual Build

Build from the reviewed release source in the isolated public worktree. Use a fresh absolute output directory:

```powershell
./packaging/Updates/Build-PortableRelease.ps1 -OutputDirectory E:/releases/SonicRoute-1.25
```

The script reads `build/Version.props`, builds the four Lite runtime/architecture combinations and Legacy x64 in sequence, retains LICENSE in the Legacy ZIP, validates PE architecture, file version and edition/runtime, and generates:

- `SonicRoute-v<version>-Lite-x64.exe`
- `SonicRoute-v<version>-Lite-arm64.exe`
- `SonicRoute-v<version>-Lite-NET8-x64.exe`
- `SonicRoute-v<version>-Lite-NET8-arm64.exe`
- `SonicRoute-v<version>-Legacy-x64.zip`
- `SonicRoute-update.json` (explicit edition/runtime/architecture, protocol, package and per-file hashes/sizes)
- `SHA256SUMS.txt` (the five packages and manifest)

Create an official **draft** GitHub Release with a numeric `v<version>` tag, upload all seven assets, check their server-side `size` and `digest` against local SHA-256, then publish as a stable Latest release. Never publish an incomplete draft. Missing packages or older releases without a manifest remain available for manual downloads; the client will not guess a replacement package. No updater code change is needed for the next version. For an existing set of five built packages, run `New-UpdateManifest.ps1 -AssetsDirectory <directory> -Version <version>` before uploading. It refuses to overwrite existing metadata.

MSIX packaging and Microsoft Store updates are unchanged. The portable update code rejects packaged execution, including its internal worker entry points. The original manual extract/copy/run workflow is retained. A version older than 1.25 does not contain this updater: its first upgrade to 1.25 is manual or via Microsoft Store.

Automatic checking is **off by default**. Users can check manually in Settings, or enable checking after startup. When enabled, a single asynchronous check runs after startup warmup and a 15-second delay, only if the previous attempt was at least 24 hours ago. There is no recurring idle polling. Existing explicit preferences are preserved; turning it off cancels a pending startup check. Download and restart/install each require a user action.

Check and full-changelog actions are in the Software update card heading. Release-note text is delivered only to an already-created update view; the application service retains installation metadata without the note body. Closing the main window uses its existing UI lifetime and deferred-page release to drop the body, without a separate clearing pass or notes cache. Reopening does not restore it; check again to retrieve it, or open the official full changelog. A verified downloaded package and its install metadata can still be reused after closing the window.

## Trust and compatibility

The client trusts only the fixed official repository through system-validated HTTPS. Asset redirects are restricted to GitHub's HTTPS asset hosts. GitHub API asset digests, SHA256SUMS, manifest package digests, file sizes, per-file hashes, PE machine, executable version and explicit edition/runtime must agree. ZIP entries are restricted to known flat program files; settings, automation, icons, language overrides and other user files are never installation targets. Future additional Legacy dependencies require an explicit reviewed protocol allowlist change.

### Runtime matching and .NET 8 retirement

The default `Lite-x64.exe` / `Lite-arm64.exe` assets use .NET 10 without a NET filename suffix. The .NET 8 compatibility assets retain `Lite-NET8-x64.exe` / `Lite-NET8-arm64.exe`; Legacy remains the x64 .NET Framework 4.8 ZIP. Protocol 2 adds an explicit `runtime` (`net10`, `net8`, `net48`) alongside edition and architecture. Old protocol-1 clients refuse protocol 2 rather than accidentally install a .NET 10 package as their former Lite/.NET 8 variant. Current clients interpret the original protocol-1 Lite contract only as .NET 8 and do not automatically install ambiguous dual-runtime protocol-1 manifests. New NET8 binaries also carry `SonicRoute Lite NET8` in PE file description, checked during manifest generation and staging; other binary identities remain unchanged.

The .NET 8 version receives no new releases or maintenance after November 2026. Its update card states the installed runtime and provides the existing styled button to open the official latest release download page. x64 users can manually migrate to .NET 10 or Legacy 4.8; ARM64 users are directed to .NET 10 ARM64 only. The updater continues to install matching-runtime updates when offered and never silently migrates runtimes/editions. A release without a matching .NET 8 package stays a missing-package/manual-download case, not an automatic .NET 10 fallback. No date polling or forced retirement switch is added; this is a maintained release policy, not a local-clock-based shutdown.

**SHA-256 is integrity verification, not an independent publisher signature.** These packages are currently unsigned. HTTPS authenticates the GitHub connection; compromise of the publisher account, release workflow, GitHub or system TLS trust remains in the trust boundary. Authenticode would additionally identify a certificate holder, but needs a maintained code-signing certificate and verification policy; no signing credentials are created or read by these scripts. A signed manifest would require an offline protected signing key, an embedded trusted public key and a key-rotation/recovery policy. Such a key must be provisioned before claiming that protection; an unsigned manifest is not called signed. GitHub immutable releases and release attestations can strengthen the release workflow but are not claimed as client-side attestation verification. Evaluate signing before changing the threat model.

## Installation and recovery

The application copies its **current local** program to an updater-owned cache and starts that copy in internal worker mode, before WPF or audio initialization. The worker acquires the update gate before the application exits normally. Existing edition instance mutexes exclude other instances while files are replaced. Normal version 1.25+ startups defer during updates; internal startup uses a random transaction ID. No downloaded script is executed, no service is installed, no periodic updater is resident, and no elevation is attempted.

Backups and validated replacement files are staged under `.sonicroute-update-<id>` beside the actual running executable, on the same volume. The directory must be writable and support Windows file replacement; junction/symlink paths are refused. Renamed portable executables retain their filename. All files are backed up and flushed before the journal and pending marker are written, and before the first replacement. Legacy dependencies are replaced first, the main executable last. Successful initialization and a live dispatcher acknowledge startup; the worker then observes five seconds without process exit, commits, and removes backups. That confirmation is a startup check, not proof that every future function will succeed.

If a new process fails or a replacement fails, the worker restores backups after confirming exclusive access. It never forcibly kills an unknown or hung process. If the new process cannot stop, backups and journal remain for startup recovery after it exits. A subsequent launch with a pending marker invokes the cached recovery runner. An interrupted multi-file replacement can prevent Legacy's managed dependencies from loading; in that case, after all SonicRoute instances exit, run the retained local runner explicitly:

```powershell
& '<data-directory>/Updates/transactions/<id>/runner/SonicRoute.exe' --update-recover '<data-directory>/Updates/transactions/<id>/plan.json'
```

The worker requires its matching local plan, edition and installation. Do not delete the pending marker, backups or transaction journal until recovery succeeds. If the cache was deleted or storage is damaged, manually restore the retained backup files or extract the original official release. Interrupted recovery and multi-file replacement are recoverable transactions, not a promise of power-loss atomicity across a whole directory.

Configuration remains in the existing data directory. The updater never replaces config.json, automation rules, language overrides, themes or icons. It also never restores an old user-data snapshot over newer user edits. Installer failures and outcome are recorded locally in `Updates/last-result.json` and the transaction `plan.json`. Completed/aborted transaction caches and old downloads are pruned only during subsequent user-requested downloads, after seven days; unfinished recovery records are retained. Protected/read-only directories fail before exit, leaving the running version intact. Move the complete program to a writable user folder, or explicitly run it with administrator privileges if appropriate; the updater does not bypass UAC. Network shares, unusual filesystems, reparse paths, an unavailable runtime, antivirus interference and actual ARM64 hardware require environment-specific validation.

Reference: [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release), [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases), [Windows file replacement](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
