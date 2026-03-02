# Coalesced Writes - Flow Diagrams

This document contains visual flow diagrams for VapeCache's coalesced socket writes feature.

## Complete Request Flow (Client → Redis)

```mermaid
flowchart TB
    subgraph Application["Application Layer"]
        A1[await cache.GetAsync key1]
        A2[await cache.SetAsync key2, value]
        A3[await cache.HSetAsync key3, field, value]
    end

    subgraph Executor["RedisCommandExecutor"]
        E1[GetAsync: Build header<br/>*2\r\n$3\r\nGET\r\n$4\r\nkey1\r\n]
        E2[SetAsync: Build header + payload<br/>Header: *3\r\n$3\r\nSET...<br/>Payload: 1024 bytes<br/>AppendCrlf: true]
        E3[HSetAsync: Build header + payload<br/>Header: *4\r\n$4\r\nHSET...<br/>Payload: value bytes<br/>AppendCrlf: true]
    end

    subgraph Multiplexer["RedisMultiplexedConnection"]
        M1[ExecuteAsync]
        M2[Create PendingOperation]
        M3[Create PendingRequest]
        M4[_writes.EnqueueAsync]
        M5[WriterLoopAsync<br/>Background Task]
        M6{useCoalescedPath?}
        M7[SendCoalescedAsync<br/>HOT PATH]
        M8[SendDirectAsync<br/>Direct Path]
    end

    subgraph Coalescing["Coalescing Layer"]
        C1[Drain _writes queue<br/>up to 8 requests]
        C2[Convert to CoalescedPendingRequest]
        C3[Coalescer.TryBuildBatch]

        subgraph BatchBuild["Batch Building Algorithm"]
            B1[For each request in queue]
            B2[Calculate reqTotalBytes]
            B3{Fits in<br/>current batch?}
            B4[Process segments]
            B5{Segment size?}
            B6[≤512B: Copy to scratch]
            B7[>512B: Add directly]
            B8[CommitScratch]
            B9[Return batch]
        end
    end

    subgraph SocketIO["Socket I/O Layer"]
        S1[SocketIoAwaitableEventArgs]
        S2[Create subset BufferList]
        S3[Socket.SendAsync<br/>Scatter/Gather I/O]
        S4[Kernel: writev syscall]
    end

    subgraph Network["Network & Redis"]
        N1[TCP/IP Stack]
        N2[Network Transmission]
        N3[Redis Server]
        N4[Process Commands]
        N5[Send Responses]
    end

    A1 --> E1
    A2 --> E2
    A3 --> E3

    E1 --> M1
    E2 --> M1
    E3 --> M1

    M1 --> M2
    M2 --> M3
    M3 --> M4
    M4 --> M5
    M5 --> M6

    M6 -->|YES| M7
    M6 -->|NO| M8

    M7 --> C1
    C1 --> C2
    C2 --> C3
    C3 --> B1

    B1 --> B2
    B2 --> B3
    B3 -->|YES| B4
    B3 -->|NO| B8
    B4 --> B5
    B5 -->|Small| B6
    B5 -->|Large| B7
    B6 --> B1
    B7 --> B1
    B1 -->|Queue empty| B8
    B8 --> B9

    B9 --> S1
    S1 --> S2
    S2 --> S3
    S3 --> S4
    S4 --> N1
    N1 --> N2
    N2 --> N3
    N3 --> N4
    N4 --> N5

    style M7 fill:#90EE90
    style C3 fill:#FFE4B5
    style B8 fill:#FFB6C1
    style S3 fill:#87CEEB
```

## Coalescer Algorithm (Detailed)

```mermaid
flowchart TD
    Start([TryBuildBatch])
    Reset[batch.Reset<br/>totalBytes = 0]

    CheckQueue{_queue.Count > 0?}
    Peek[req = _queue.Peek]

    CalcSize[Calculate reqTotalBytes<br/>and reqSegmentCount]

    CheckBatch{batch has data?}
    CheckLimits{Would exceed<br/>limits?}
    CommitReturn[CommitScratch<br/>return true]

    ProcessSegs[for i in 0..req.Count]
    SkipEmpty{segLen == 0?}
    CheckSize{segLen ≤ 512?}

    SmallPath[Small Segment Path]
    EnsureScratch[EnsureScratch]
    CheckRoom{Room in scratch?}
    CommitScratch1[CommitScratch]
    CopySmall[Copy to Scratch<br/>BaseOffset + Used]
    IncUsed[ScratchUsed += segLen]

    LargePath[Large Segment Path]
    CommitScratch2[CommitScratch]
    AddDirect[Add segment directly]

    AddOwner[Add PayloadOwner if present]
    Dequeue[_queue.Dequeue]

    FinalCommit[CommitScratch]
    Return{SegmentsToWrite.Count > 0?}
    ReturnTrue([return true])
    ReturnFalse([return false])

    Start --> Reset
    Reset --> CheckQueue

    CheckQueue -->|YES| Peek
    CheckQueue -->|NO| FinalCommit

    Peek --> CalcSize
    CalcSize --> CheckBatch

    CheckBatch -->|YES| CheckLimits
    CheckBatch -->|NO| ProcessSegs

    CheckLimits -->|YES| CommitReturn
    CheckLimits -->|NO| ProcessSegs

    ProcessSegs --> SkipEmpty
    SkipEmpty -->|YES| ProcessSegs
    SkipEmpty -->|NO| CheckSize

    CheckSize -->|YES| SmallPath
    CheckSize -->|NO| LargePath

    SmallPath --> EnsureScratch
    EnsureScratch --> CheckRoom
    CheckRoom -->|NO| CommitScratch1
    CheckRoom -->|YES| CopySmall
    CommitScratch1 --> EnsureScratch
    CopySmall --> IncUsed
    IncUsed --> ProcessSegs

    LargePath --> CommitScratch2
    CommitScratch2 --> AddDirect
    AddDirect --> ProcessSegs

    ProcessSegs -->|Done| AddOwner
    AddOwner --> Dequeue
    Dequeue --> CheckQueue

    FinalCommit --> Return
    Return -->|YES| ReturnTrue
    Return -->|NO| ReturnFalse

    style CommitScratch1 fill:#FFB6C1
    style CommitScratch2 fill:#FFB6C1
    style FinalCommit fill:#FFB6C1
    style SmallPath fill:#90EE90
    style LargePath fill:#FFE4B5
```

## Scratch Buffer Management (The Fix)

```mermaid
flowchart TD
    subgraph Before["❌ BEFORE FIX - Buffer Corruption"]
        B1[Write Header 69B<br/>to Scratch 0..69]
        B2[CommitScratch:<br/>Add Scratch 0..69<br/>ScratchUsed = 0]
        B3[Write CRLF 2B<br/>to Scratch 0..2<br/>❌ OVERWRITES!]
        B4[Result: Corrupted<br/>Segment 0: \\r\\nDER...<br/>Redis error!]

        B1 --> B2
        B2 --> B3
        B3 --> B4
    end

    subgraph After["✅ AFTER FIX - Correct Buffer Management"]
        A1[Write Header 69B<br/>to Scratch 0..69]
        A2[CommitScratch:<br/>Add Scratch 0..69<br/>BaseOffset = 69<br/>ScratchUsed = 0]
        A3[Write CRLF 2B<br/>to Scratch 69..71<br/>✅ Separate region!]
        A4[Result: Correct<br/>Segment 0: HEADER...<br/>Segment 1: \\r\\n]

        A1 --> A2
        A2 --> A3
        A3 --> A4
    end

    style B4 fill:#FF6B6B
    style A4 fill:#90EE90
```

## Response Flow (Redis → Client)

```mermaid
flowchart TB
    subgraph Redis["Redis Server"]
        R1[Process batched commands]
        R2[Send responses in order]
        R3[Response 1: $4\\r\\njohn\\r\\n]
        R4[Response 2: +OK\\r\\n]
        R5[Response 3: $12\\r\\nvalue123\\r\\n]
    end

    subgraph Reader["ReaderLoopAsync - Background Task"]
        RD1[Socket.ReceiveAsync]
        RD2[RedisRespSocketReaderState<br/>Zero-copy RESP parsing]
        RD3[ReadNextAsync]
        RD4[Parse response]
        RD5[Dequeue PendingOperation<br/>from _pending queue]
        RD6[Match response to operation]
        RD7[op.TrySetResult response]
    end

    subgraph Operations["PendingOperations"]
        O1[Operation 1: GET user:123]
        O2[Operation 2: SET user:456]
        O3[Operation 3: HGET session:abc]
    end

    subgraph AppReturn["Application Completion"]
        AR1[GetAsync returns john]
        AR2[SetAsync returns true]
        AR3[HGetAsync returns value123]
    end

    R1 --> R2
    R2 --> R3
    R2 --> R4
    R2 --> R5

    R3 --> RD1
    R4 --> RD1
    R5 --> RD1

    RD1 --> RD2
    RD2 --> RD3
    RD3 --> RD4

    RD4 --> RD5
    RD5 --> RD6
    RD6 --> RD7

    RD7 -.->|Complete| O1
    RD7 -.->|Complete| O2
    RD7 -.->|Complete| O3

    O1 --> AR1
    O2 --> AR2
    O3 --> AR3

    style RD2 fill:#87CEEB
    style RD7 fill:#90EE90
```

## Performance Comparison

```mermaid
flowchart LR
    subgraph Without["Without Coalescing - StackExchange.Redis"]
        W1[SET user:123]
        W2[GET user:456]
        W3[HSET key field value]

        WS1[Socket.SendAsync<br/>10-20μs overhead]
        WS2[Socket.SendAsync<br/>10-20μs overhead]
        WS3[Socket.SendAsync<br/>10-20μs overhead]

        WT[Total: 3 syscalls<br/>30-60μs overhead]

        W1 --> WS1
        W2 --> WS2
        W3 --> WS3
        WS1 --> WT
        WS2 --> WT
        WS3 --> WT
    end

    subgraph With["With Coalescing - VapeCache"]
        V1[SET user:123]
        V2[GET user:456]
        V3[HSET key field value]

        VB[Batch into single send]
        VS[Socket.SendAsync<br/>10-20μs overhead]
        VT[Total: 1 syscall<br/>10-20μs overhead<br/>✅ 66% reduction!]

        V1 --> VB
        V2 --> VB
        V3 --> VB
        VB --> VS
        VS --> VT
    end

    style WT fill:#FF6B6B
    style VT fill:#90EE90
```

## Batch Building Example

```mermaid
flowchart TD
    subgraph Input["Input Queue"]
        I1[Request 1: GET user:123<br/>Segments: Header 24B]
        I2[Request 2: SET user:456<br/>Segments: Header 69B, Payload 1024B, CRLF 2B]
        I3[Request 3: HGET session:abc field1<br/>Segments: Header 38B]
    end

    subgraph Process1["Process Request 1"]
        P11[Header 24B ≤ 512]
        P12[Copy to Scratch 0..24]
        P13[ScratchUsed = 24]
    end

    subgraph Process2["Process Request 2"]
        P21[Header 69B ≤ 512]
        P22[Copy to Scratch 24..93]
        P23[ScratchUsed = 93]
        P24[Payload 1024B > 512]
        P25[CommitScratch:<br/>Add Scratch 0..93<br/>BaseOffset = 93]
        P26[Add Payload directly]
        P27[CRLF 2B ≤ 512]
        P28[Copy to Scratch 93..95]
        P29[ScratchUsed = 2]
    end

    subgraph Process3["Process Request 3"]
        P31[Header 38B ≤ 512]
        P32[Copy to Scratch 95..133]
        P33[ScratchUsed = 40]
    end

    subgraph Final["Final Batch"]
        F1[CommitScratch:<br/>Add Scratch 93..133<br/>BaseOffset = 133]
        F2[SegmentsToWrite:<br/>0: Scratch 0..93<br/>1: Payload 0..1024<br/>2: Scratch 93..133]
        F3[Total: 3 segments<br/>1157 bytes<br/>1 socket send]
    end

    I1 --> P11
    P11 --> P12
    P12 --> P13

    I2 --> P21
    P21 --> P22
    P22 --> P23
    P23 --> P24
    P24 --> P25
    P25 --> P26
    P26 --> P27
    P27 --> P28
    P28 --> P29

    I3 --> P31
    P31 --> P32
    P32 --> P33

    P33 --> F1
    F1 --> F2
    F2 --> F3

    style P25 fill:#FFB6C1
    style F1 fill:#FFB6C1
    style F3 fill:#90EE90
```

## State Machine: CoalescedWriteBatch Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Initialized: new CoalescedWriteBatch()

    Initialized --> Ready: Reset()

    Ready --> Building: TryBuildBatch() called

    Building --> WritingSmall: Segment ≤ 512B
    WritingSmall --> Building: More segments

    Building --> CommittingScratch: Segment > 512B
    CommittingScratch --> WritingLarge: Add scratch to segments
    WritingLarge --> Building: More segments

    Building --> FinalCommit: Queue empty
    FinalCommit --> ReadyToSend: CommitScratch()

    ReadyToSend --> Sending: SendWithArgsAsync()

    Sending --> Recycled: RecycleAfterSend()
    Recycled --> Ready: ScratchUsed = 0<br/>BaseOffset = 0

    Recycled --> Disposed: Dispose()
    Disposed --> [*]

    note right of CommittingScratch
        Add Scratch[BaseOffset..BaseOffset+Used]
        BaseOffset += Used
        ScratchUsed = 0
    end note

    note right of Recycled
        Scratch buffer retained
        for next batch
    end note
```

---

**Legend**:
- 🟢 Green: Optimized hot path
- 🟡 Yellow: Batch building logic
- 🔴 Red: Critical fix areas
- 🔵 Blue: Socket I/O operations

**Related Documentation**:
- [COALESCED_WRITES.md](COALESCED_WRITES.md) - Complete documentation
- [BENCHMARK_DEBUGGING_SUMMARY.md](../BENCHMARK_DEBUGGING_SUMMARY.md) - Debugging timeline

**Last Updated**: December 25, 2025
