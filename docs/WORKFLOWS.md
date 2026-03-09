# CI/CD Workflows

This document maps VapeCache automation from first commit to release artifacts.

Style note: diagrams use transparent fills and high-contrast strokes to stay readable in both GitHub light and dark themes.

## 1. End-to-End Flow (Top to Bottom)

```mermaid
flowchart TB
    classDef trigger fill:transparent,stroke:#1971c2,stroke-width:2px
    classDef gate fill:transparent,stroke:#f08c00,stroke-width:2px,stroke-dasharray: 6 4
    classDef success fill:transparent,stroke:#2b8a3e,stroke-width:2px
    classDef fail fill:transparent,stroke:#c92a2a,stroke-width:2px
    classDef action fill:transparent,stroke:#495057,stroke-width:2px

    A[Developer Pushes Branch<br/>or Opens PR]:::trigger --> B{GitHub Event}:::gate
    B -->|pull_request / push| C[CI Workflow<br/>.github/workflows/ci.yml]:::action
    B -->|pull_request_target| D[PR Auto Approve<br/>.github/workflows/pr-auto-approve.yml]:::action

    C --> E[build-test job<br/>windows-latest]:::action
    C --> F[perf-contention-gate job<br/>ubuntu + Redis service]:::action

    E --> G{All Required Checks Pass?}:::gate
    F --> G
    G -->|No| H[PR Stays Blocked<br/>Fix + Push Again]:::fail
    H --> A
    G -->|Yes| I[Merge to main]:::success

    D --> J[Optional Approval Signal<br/>label: auto-approve<br/>or bot actor]:::action
    I --> K{Tag v* Created?}:::gate
    K -->|No| L[Main is continuously validated]:::success
    K -->|Yes| M[Release Workflow<br/>.github/workflows/build.yml]:::action
    M --> N[Build + Test + Vulnerability Scan + Pack]:::action
    N --> O[Publish GitHub Release<br/>with .nupkg + checksums]:::success
```

## 2. CI Workflow Internals

```mermaid
flowchart TB
    classDef job fill:transparent,stroke:#343a40,stroke-width:2px
    classDef step fill:transparent,stroke:#868e96,stroke-width:2px
    classDef gate fill:transparent,stroke:#f08c00,stroke-width:2px,stroke-dasharray: 6 4

    A[CI Trigger<br/>push / pull_request] --> B{Run Jobs in Parallel}:::gate

    subgraph W1[Job: build-test (windows-latest)]
      direction TB
      C1[Checkout]:::step --> C2[Setup .NET 10]:::step --> C3[Restore]:::step --> C4[Build Release]:::step --> C5[Run Unit Tests]:::step --> C6[Transport Regression Tests]:::step --> C7[Perf Gate Script]:::step
    end

    subgraph W2[Job: perf-contention-gate (ubuntu-latest)]
      direction TB
      D1[Start Redis service container]:::step --> D2[Checkout]:::step --> D3[Setup .NET 10]:::step --> D4[Restore]:::step --> D5[Run contention perf gate]:::step --> D6[Run grocery tail perf gate]:::step
    end

    B --> W1
    B --> W2
    W1 --> E{Both Jobs Pass?}:::gate
    W2 --> E
    E -->|Yes| F[Status: CI Green]
    E -->|No| G[Status: CI Red]
```

## 3. Release Workflow Internals

```mermaid
flowchart TB
    classDef step fill:transparent,stroke:#868e96,stroke-width:2px
    classDef gate fill:transparent,stroke:#f08c00,stroke-width:2px,stroke-dasharray: 6 4
    classDef done fill:transparent,stroke:#2b8a3e,stroke-width:2px

    A[Trigger<br/>push tag v* or manual dispatch with release_tag]:::gate --> B[Resolve release tag + package version]:::step
    B --> C[Checkout + .NET 10 setup]:::step
    C --> D[Restore + Build Release]:::step
    D --> E[Run unit tests + perf-gate tests]:::step
    E --> F[Vulnerability scan]:::step
    F --> G[Pack NuGet artifacts<br/>using tag-derived version]:::step
    G --> H[Smoke-install built package]:::step
    H --> I[Generate SHA256SUMS.txt]:::step
    I --> J[Upload workflow artifact]:::step
    J --> K[Create GitHub Release<br/>attach packages + checksums]:::done
```

## 4. PR Auto-Approve Policy

```mermaid
flowchart TB
    classDef trigger fill:transparent,stroke:#1971c2,stroke-width:2px
    classDef gate fill:transparent,stroke:#f08c00,stroke-width:2px,stroke-dasharray: 6 4
    classDef success fill:transparent,stroke:#2b8a3e,stroke-width:2px
    classDef fail fill:transparent,stroke:#c92a2a,stroke-width:2px

    A[pull_request_target event]:::trigger --> B{Draft PR?}:::gate
    B -->|Yes| C[Skip]:::fail
    B -->|No| D{Matches policy?}:::gate
    D -->|Label auto-approve| E[Approve PR]:::success
    D -->|dependabot[bot]| E
    D -->|renovate[bot]| E
    D -->|No match| F[No auto-approval]:::fail
```

## 5. Commit Notification Feed

- Workflow: `.github/workflows/commit-notify.yml`
- Trigger: `push` to `main`, `master`, or tags matching `v*`, plus manual dispatch for verification.
- Delivery: branch pushes go to the `Commit Notifications` issue; release tags go to `Release Notifications`.
- Each run adds a comment summarizing the push or tag event.
- Recipients: handles from `.github/commit-notify-subscribers.txt` plus contributors whose git author email uses GitHub's noreply format.
- Result: contributors receive standard GitHub notifications from issue comment mentions without requiring external mail or chat infrastructure.

## 6. Release Notes

- Release workflow manual dispatch now requires a `release_tag` input (for example `v1.2.0` or `v1.2.0-rc1`).
- Package artifacts are versioned from the resolved tag, so prerelease tags generate matching prerelease `.nupkg` versions.
- `tools/pack-release-packages.ps1` validates that all packable projects share the same base version before packing.
- `tools/publish-release-packages.ps1` pushes packages in dependency-safe order when you are ready to publish to a NuGet feed.

