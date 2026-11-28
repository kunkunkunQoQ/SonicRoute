# Portable updates (protocol 2)

## Automated GitHub Releases Workflow (CI/CD)

[`Build and Release`](../../.github/workflows/release.yml) builds and validates a candidate when release inputs change on `master`. This push-triggered run uses read-only credentials. The same workflow can be dispatched manually with [`Invoke-ReleasePipeline.ps1`](./Invoke-ReleasePipeline.ps1) handling candidate verification and publication.

See [RELEASE-CHECKLIST.md](./RELEASE-CHECKLIST.md) for candidate creation, the source-bound build receipt, read-only full DryRun, interruption recovery and the separate Store gate. The validation job has Contents read permission; only a non-DryRun master publication job obtains write permission. Hosted Actions execution must be verified separately from local tests.

### One-Click Release Usage

1. Open GitHub repository → **Actions** → select **Build and Release**.
2. Click **Run workflow** (branch: `master`).
3. **Dry Run is checked by default**. It builds all four portable executables and runs the checks. Only an explicit manual dispatch on `master` with Dry Run disabled can publish. An already published current version can be revalidated from a later commit; this never authorizes overwriting that release. To publish a new version, increase `Version.props`, select `master` and explicitly uncheck Dry Run.
4. Both validation and publication runs perform these checks:
   - Reads the target release version from `build/Version.props` (e.g. `1.25` → `v1.25`).
   - Asserts preflight conditions (strictly `master` branch, commit SHA binding, monotonic version increase against remote releases and local tags, absence of conflicting published releases).
   - `Build-PortableRelease.ps1` builds the Native AOT solution and calls `Native/Build-Product.ps1` for Full and Lite x64/ARM64. The versioned EXEs include the .NET 10 Native AOT runtime and are written to the candidate's `assets` directory. Full also embeds its phone helpers.
   - Validates PE architecture (`0x8664`/`0xaa64`), file version, edition and Native AOT image identity, `SHA256SUMS.txt`, and the Protocol 2 update manifest.
   - Generates release notes: prioritizes curated `docs/release-v<version>.md` if maintained, or automatically extracts and groups conventional commits and PRs since the previous official release tag.
5. Only a manually dispatched publication run continues to the steps below.
   - Creates or resumes a **Draft Release** pinned to the validated commit SHA and uploads all six assets.
   - Performs **Remote Cryptographic Re-verification**: re-queries the GitHub API for asset counts and sizes, downloads each asset back to an isolated temporary sandbox, and re-computes its SHA-256 hash locally.
   - Compares candidate notes with the reviewed UTF-8 Markdown, and compares the remote draft text again before publication. Native JSON/git output is decoded as UTF-8 on Windows PowerShell 5.
   - Promotes the draft to an official public **Latest Release** (`prerelease: false`, `latest: true`).
   - Generates a full markdown report into GitHub Actions Step Summary.

### Release description style

Use the [v1.36 release description](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36) as the presentation reference. Maintain each version in `docs/release-v<version>.md` with the title and navigation links, a short highlights section, grouped details, an English release notes foldout, the download and checksum table, Chinese and English upgrade instructions, and the GPL-3.0-only footer. Use that version’s observed changes and test evidence. Include a publication date only when it is confirmed. For versions that support Android app pairing, link to the Android Releases page and the official installation instructions.

The workflow preserves curated UTF-8 Markdown verbatim. If none exists, the fallback follows the same layout, uses up to six commit entries for highlights, retains all categorized entries in the details, and links the English foldout to the source changes. The download table uses the target version’s asset matrix. Its base is the greatest earlier numeric stable release from the existing paginated history reader; drafts, prereleases and API display order cannot truncate the range. Offline local DryRun generation warns before using numeric local tags, and a failed git log aborts rather than producing an apparently valid empty changelog. Version numbers and download URLs come from the target version. Automatically grouped commits do not establish functional or performance test results; include actual validation and limitations in the curated notes before release.

### Safeguards and Failure Recovery

- **Zero Unverified Releases**: Releases remain in draft state until all six assets pass remote download and cryptographic hash validation.
- **Idempotent Draft Takeover**: If a previous run failed during network upload or verification, re-running on the same commit safely updates the existing draft. Matching files are reused; a changed, known file is replaced by its exact asset ID. Unknown files are never removed.
- **Stable Draft Identity**: Draft creation retains the API response's release ID. Subsequent reads, edits, uploads, downloads and promotion use IDs, rather than assuming the new draft is immediately visible through its tag or the release list. Every edit explicitly preserves the tag and full target commit. A lost create response triggers bounded discovery, never a blind duplicate create; unresolved discovery stops safely with the draft unpublished.
- Reuse requires the exact full commit SHA and a known asset set. Unknown assets are retained and cause an abort. All four PE binaries, the manifest, file hashes and the clean-source receipt are checked again before production publication; a command-line success flag cannot substitute for the receipt.
- **Published Immutability**: If an official (non-draft) release already exists for `v<version>`, the pipeline immediately aborts to prevent overwriting or deleting published artifacts.
- The immutable-version rule applies to production publication. DryRun may revalidate the current published version without modifying its tag or assets; summaries identify this as `DRY_RUN_REVALIDATED_PUBLISHED_VERSION`. Older versions still fail the downgrade guard.
- Release policy is checked before compilation. A production request from a non-master branch fails explicitly instead of silently skipping all jobs. Checkout credentials are not persisted. Only the publish job has Contents write permission; its script step explicitly receives GH_TOKEN, while actions in that job also run within the job token's permission boundary.
- After publication, a temporarily stale Latest tag or release ID is retried through the existing three-attempt backoff. A persistent mismatch or query failure remains a failed, already-published outcome in the summary; it never causes duplicate draft creation or republishing.
- Summary timestamps use the UTC clock explicitly, so a local run cannot label local wall time with a misleading `Z` suffix.
- Candidate artifacts are named per validation attempt. The publish job retrieves the exact artifact ID and verifies the archive SHA-256 returned by the successful validation job. **Re-run failed jobs** after a publish failure to reuse the same validated bytes. **Re-run all jobs** or a new dispatch builds a new candidate.
- Validation also downloads its uploaded candidate into a fresh directory and runs the actual publish-stage checks in DryRun mode. This verifies artifact layout, source-bound receipt, six files and reviewed notes without granting release write permission.
- On validation failure, available candidate files and check results are retained as a separate diagnostic artifact for 14 days. Diagnostic artifacts are never publication inputs. No valid receipt means no publishable candidate.
- **Manual Verification / Dry-Run**: Run locally or in CI at any time using:
  ```powershell
  ./packaging/Updates/Invoke-ReleasePipeline.ps1 -DryRun
  ```

`Test-ReleasePipelineHarness.ps1 -AssetsDirectory <candidate>/assets` exercises actual release functions against real binaries; git/gh, binary-download and retry-delay boundaries are simulated and make no network changes. It retains malformed fixtures, results and simulated publication summaries under `build-artifacts`. The production download helper copies native stdout as bytes, avoiding PowerShell 5 text redirection corruption. Without an asset directory the harness builds a fresh four-package candidate once. Runtime isolation/staging tests are in `tests/RuntimeChecks` (net10/net48); historical NET8 protocol fixtures remain readable without building a NET8 program; transactional worker tests in `tests/WorkerChecks` use locally built fixture executables and real filesystem/process operations. They are not evidence of an installed Store update, an ARM64 run, or a NET8-only OS.

CI checks configuration serialization and localization, .NET 10 package staging, client runtime isolation and the existing worker fixtures. The .NET Framework 4.8 compatibility checks verify that historical clients refuse the current native packages. Protocol fixtures cover historical package matching. Restore graphs verify that each Native AOT build uses the target architecture's compiler pack and that only Full includes the Android project. CI launches both actual Native AOT x64 candidates in isolated profiles to verify edition identity, restart, update, and rollback. The ARM64 candidates are cross-compiled and verified as PE files. The Full x64 executable is also packaged as an unsigned MSIX and uploaded to Actions separately from the portable release.

---

## Local Manual Build

Build from the reviewed release source in the current checkout. Use a fresh absolute output directory:

```powershell
./packaging/Updates/Build-PortableRelease.ps1 -OutputDirectory E:/releases/SonicRoute-candidate
```

The script reads `build/Version.props`, selects the SDK from the repository directory so `global.json` also applies to calls from another working directory, and restores the caller's directory on exit. For 2.00 and later, it builds the four self-contained .NET 10 Native AOT Full and Lite EXEs for x64 and ARM64. It verifies PE architecture, file version and deployment type before generating the six candidate files.

- `SonicRoute-v<version>-Lite-x64.exe`
- `SonicRoute-v<version>-Lite-arm64.exe`
- `SonicRoute-v<version>-x64.exe`
- `SonicRoute-v<version>-arm64.exe`
- `SonicRoute-update.json` (explicit edition/runtime/architecture, protocol, package and per-file hashes/sizes)
- `SHA256SUMS.txt` (the four packages and manifest)

For an existing set of four built EXEs, `New-UpdateManifest.ps1 -AssetsDirectory <directory> -Version <version>` creates the manifest and checksum file and refuses to overwrite metadata. Follow the [release checklist](RELEASE-CHECKLIST.md) for authorized manual publication. The source-bound candidate, all six remote assets and their SHA-256 values are verified before promoting the draft to Latest. If a release has no matching package or no manifest, users can still use the release page for manual downloads.

Older release matrices vary. See the actual assets attached to each historical release, including [v1.36](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36) for the historical Legacy x64 ZIP.

The unsigned Actions MSIX uses the existing Microsoft Store packaging script. The portable update code rejects packaged execution, including its internal worker entry points. Manual extract, copy and run upgrades remain supported. Versions older than 1.25 must upgrade manually or via Microsoft Store.

Automatic checking is **off by default**. Users can check manually in Settings, or enable checking after startup. When enabled, a single asynchronous check runs after startup warmup and a 15-second delay, only if the previous attempt was at least 24 hours ago. There is no recurring idle polling. Existing explicit preferences are preserved; turning it off cancels a pending startup check. Download and restart/install each require a user action.

Check and full-changelog actions are in the Software update card heading. Release-note text is delivered only to an already-created update view; the application service retains installation metadata without the note body. Closing the main window uses its existing UI lifetime and deferred-page release to drop the body, without a separate clearing pass or notes cache. Reopening does not restore it; check again to retrieve it, or open the official full changelog. A verified downloaded package and its install metadata can still be reused after closing the window.

## Trust and compatibility

The client trusts only the fixed official repository through system-validated HTTPS. Asset redirects are restricted to GitHub's HTTPS asset hosts. GitHub API asset digests, SHA256SUMS, manifest package digests, file sizes, per-file hashes, PE machine, executable version and explicit edition/runtime must agree. Full and Lite replace one executable. The ZIP program-file allowlist is used for historical Legacy packages. Settings, automation, icons, language overrides and other user files are never installation targets.

### Runtime matching and .NET 8 retirement

The default SonicRoute single EXEs include Android and AirPlay and carry no edition suffix. The `Lite-x64.exe` / `Lite-arm64.exe` assets exclude phone code and dependencies. Both include the .NET 10 Native AOT runtime. Historical .NET 8 assets use `Lite-NET8-x64.exe` / `Lite-NET8-arm64.exe`. The historical Legacy x64 ZIP uses .NET Framework 4.8. See [EDITIONS.md](../EDITIONS.md). Protocol 2 adds an explicit `runtime` (`net10`, `net8`, `net48`) alongside edition and architecture. Old protocol-1 clients refuse protocol 2 rather than accidentally install a .NET 10 package as their former Lite/.NET 8 variant. Current clients interpret the original protocol-1 Lite contract only as .NET 8 and do not automatically install ambiguous dual-runtime protocol-1 manifests. Historical NET8 binaries also carry `SonicRoute Lite NET8` in PE file description, checked during manifest generation and staging; other binary identities remain unchanged.

The 2.00 release matrix contains Full and Lite packages with runtime `net10`. A Legacy client still matches only a historical `Legacy/net48/x64` package, and the discontinued NET8 client still requires a matching NET8 asset. When the release has no matching package, users can open the release page and manually switch to Full or Lite for their PC's architecture. Update selection continues to require the same edition, runtime and architecture.

**SHA-256 is integrity verification, not an independent publisher signature.** These packages are currently unsigned. HTTPS authenticates the GitHub connection; compromise of the publisher account, release workflow, GitHub or system TLS trust remains in the trust boundary. Authenticode would additionally identify a certificate holder, but needs a maintained code-signing certificate and verification policy; no signing credentials are created or read by these scripts. A signed manifest would require an offline protected signing key, an embedded trusted public key and a key-rotation/recovery policy. Such a key must be provisioned before claiming that protection; an unsigned manifest is not called signed. GitHub immutable releases and release attestations can strengthen the release workflow but are not claimed as client-side attestation verification. Evaluate signing before changing the threat model.

## Installation and recovery

The application copies its **current local** program to an updater-owned cache and starts that copy in internal worker mode, before WPF or audio initialization. The worker acquires the update gate before the application exits normally. Existing edition instance mutexes exclude other instances while files are replaced. Normal version 1.25+ startups defer during updates; internal startup uses a random transaction ID. No downloaded script is executed, no service is installed, no periodic updater is resident, and no elevation is attempted.

Backups and validated replacement files are staged under `.sonicroute-update-<id>` beside the actual running executable, on the same volume. The directory must be writable and support Windows file replacement; junction/symlink paths are refused. Renamed portable executables retain their filename. All files are backed up and flushed before the journal and pending marker are written, and before the first replacement. For a historical Legacy ZIP transaction, the worker replaces the dependencies before the main executable. Successful initialization and a live dispatcher acknowledge startup; the worker then observes five seconds without process exit, commits, and removes backups. That confirmation is a startup check, not proof that every future function will succeed.

If a new process fails or a replacement fails, the worker restores backups after confirming exclusive access. It never forcibly kills an unknown or hung process. If the new process cannot stop, backups and journal remain for startup recovery after it exits. A subsequent launch with a pending marker invokes the cached recovery runner. An interrupted multi-file replacement can prevent Legacy's managed dependencies from loading; in that case, after all SonicRoute instances exit, run the retained local runner explicitly:

```powershell
& '<data-directory>/Updates/transactions/<id>/runner/SonicRoute.exe' --update-recover '<data-directory>/Updates/transactions/<id>/plan.json'
```

The worker requires its matching local plan, edition and installation. Do not delete the pending marker, backups or transaction journal until recovery succeeds. If the cache was deleted or storage is damaged, manually restore the retained backup files or extract the original official release. Interrupted recovery and multi-file replacement are recoverable transactions, not a promise of power-loss atomicity across a whole directory.

Configuration remains in the existing data directory. The updater never replaces config.json, automation rules, language overrides, themes or icons. It also never restores an old user-data snapshot over newer user edits. Installer failures and outcome are recorded locally in `Updates/last-result.json` and the transaction `plan.json`. Completed/aborted transaction caches and old downloads are pruned only during subsequent user-requested downloads, after seven days; unfinished recovery records are retained. Protected/read-only directories fail before exit, leaving the running version intact. Move the complete program to a writable user folder, or explicitly run it with administrator privileges if appropriate; the updater does not bypass UAC. Network shares, unusual filesystems, reparse paths, an unavailable runtime, antivirus interference and actual ARM64 hardware require environment-specific validation.

Reference: [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release), [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases), [Windows file replacement](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
