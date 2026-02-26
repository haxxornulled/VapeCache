# CI/CD Workflows

This document maps VapeCache automation from first commit to release artifacts.

## 1. End-to-End Flow (Top to Bottom)

```mermaid
flowchart TB
    classDef trigger fill:#e7f5ff,stroke:#1971c2,stroke-width:1px,color:#0b3d75
    classDef gate fill:#fff3bf,stroke:#f08c00,stroke-width:1px,color:#7c4d00
    classDef success fill:#ebfbee,stroke:#2b8a3e,stroke-width:1px,color:#1b5e20
    classDef fail fill:#fff5f5,stroke:#c92a2a,stroke-width:1px,color:#7f1d1d
    classDef action fill:#f8f9fa,stroke:#495057,stroke-width:1px,color:#212529

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
    K -->|Yes| M[Release Workflow<br/>.github/workflows/release.yml]:::action
    M --> N[Build + Test + Vulnerability Scan + Pack]:::action
    N --> O[Publish GitHub Release<br/>with .nupkg + checksums]:::success
```

## 2. CI Workflow Internals

```mermaid
flowchart TB
    classDef job fill:#f1f3f5,stroke:#343a40,stroke-width:1px,color:#212529
    classDef step fill:#ffffff,stroke:#868e96,stroke-width:1px,color:#212529
    classDef gate fill:#fff3bf,stroke:#f08c00,stroke-width:1px,color:#7c4d00

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
    classDef step fill:#ffffff,stroke:#868e96,stroke-width:1px,color:#212529
    classDef gate fill:#fff3bf,stroke:#f08c00,stroke-width:1px,color:#7c4d00
    classDef done fill:#ebfbee,stroke:#2b8a3e,stroke-width:1px,color:#1b5e20

    A[Trigger<br/>push tag v* or manual dispatch] --> B[Checkout + .NET 10 setup]:::step
    B --> C[Restore + Build Release]:::step
    C --> D[Run unit tests + perf-gate tests]:::step
    D --> E[Vulnerability scan]:::step
    E --> F[Pack NuGet artifacts]:::step
    F --> G[Collect .nupkg files]:::step
    G --> H[Generate SHA256SUMS.txt]:::step
    H --> I[Upload workflow artifact]:::step
    I --> J[Create GitHub Release<br/>attach packages + checksums]:::done
```

## 4. PR Auto-Approve Policy

```mermaid
flowchart TB
    A[pull_request_target event] --> B{Draft PR?}
    B -->|Yes| C[Skip]
    B -->|No| D{Matches policy?}
    D -->|Label auto-approve| E[Approve PR]
    D -->|dependabot[bot]| E
    D -->|renovate[bot]| E
    D -->|No match| F[No auto-approval]
```
