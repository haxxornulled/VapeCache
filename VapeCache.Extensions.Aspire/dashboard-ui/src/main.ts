import "./styles.css";

type UnknownRecord = Record<string, unknown>;

type ConnectionLevel = "ok" | "warn" | "danger";

interface RedisMuxLaneSnapshot {
  laneIndex: number;
  connectionId: number;
  role: string;
  writeQueueDepth: number;
  inFlight: number;
  maxInFlight: number;
  inFlightUtilization: number;
  operations: number;
  responses: number;
  failures: number;
  orphanedResponses: number;
  responseSequenceMismatches: number;
  transportResets: number;
  healthy: boolean;
}

interface SpillSnapshot {
  mode: string;
  supportsDiskSpill: boolean;
  spillToDiskConfigured: boolean;
  totalSpillFiles: number;
  activeShards: number;
  maxFilesInShard: number;
  imbalanceRatio: number;
}

interface AutoscalerSnapshot {
  currentConnections: number;
  targetConnections: number;
  currentReadLanes: number;
  currentWriteLanes: number;
  avgQueueDepth: number;
  maxQueueDepth: number;
  timeoutRatePerSec: number;
  rollingP95LatencyMs: number;
  rollingP99LatencyMs: number;
  frozen: boolean;
  lastScaleDirection: string;
  lastScaleReason: string;
}

interface LiveSample {
  timestampUtc: Date;
  currentBackend: string;
  hits: number;
  misses: number;
  setCalls: number;
  removeCalls: number;
  fallbackToMemory: number;
  redisBreakerOpened: number;
  stampedeKeyRejected: number;
  stampedeLockWaitTimeout: number;
  stampedeFailureBackoffRejected: number;
  hitRate: number;
  spill: SpillSnapshot | null;
  autoscaler: AutoscalerSnapshot | null;
  lanes: RedisMuxLaneSnapshot[];
  readsPerSec: number;
  writesPerSec: number;
  fallbacksPerSec: number;
}

function resolveEndpointPrefix(pathname: string): string {
  const withoutTrailingSlash = pathname.replace(/\/+$/, "");
  const marker = "/dashboard";
  const markerIndex = withoutTrailingSlash.lastIndexOf(marker);
  if (markerIndex < 0) {
    return withoutTrailingSlash || "";
  }

  return withoutTrailingSlash.slice(0, markerIndex);
}

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function getValue(obj: UnknownRecord | null | undefined, ...keys: string[]): unknown {
  if (!obj) {
    return undefined;
  }

  for (const key of keys) {
    if (Object.prototype.hasOwnProperty.call(obj, key)) {
      const value = obj[key];
      if (value !== null && value !== undefined) {
        return value;
      }
    }
  }

  return undefined;
}

function asNumber(value: unknown, fallback = 0): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function asString(value: unknown, fallback = ""): string {
  return typeof value === "string" && value.length > 0 ? value : fallback;
}

function asBoolean(value: unknown, fallback = false): boolean {
  return typeof value === "boolean" ? value : fallback;
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function byId<TElement extends HTMLElement>(id: string): TElement {
  const element = document.getElementById(id);
  if (!element) {
    throw new Error(`Missing dashboard element: ${id}`);
  }

  return element as TElement;
}

const appRoot = document.getElementById("app");
if (!appRoot) {
  throw new Error("Dashboard root element #app was not found.");
}

appRoot.innerHTML = `
  <div class="shell">
    <header class="header">
      <div>
        <div class="title-wrap">
          <div id="conn-dot" class="status-dot"></div>
          <div>
            <h1>VapeCache Realtime Dashboard</h1>
            <div class="header-sub">
              <span id="conn-state" class="badge danger">disconnected</span>
              <span class="muted" id="last-sample">no samples yet</span>
            </div>
          </div>
        </div>
      </div>
      <div class="controls">
        <button id="toggle-pause" type="button">Pause</button>
        <button id="pull-now" type="button">Pull Snapshot</button>
        <button id="reset-history" type="button">Reset Charts</button>
      </div>
    </header>

    <section class="grid-cards">
      <article class="card">
        <p class="card-title">Backend</p>
        <div class="card-value" id="backend">-</div>
        <div class="card-meta" id="breaker-state">breaker: unknown</div>
      </article>
      <article class="card">
        <p class="card-title">Hit Rate</p>
        <div class="card-value" id="hit-rate">0.00%</div>
        <div class="card-meta" id="reads-total">reads: 0</div>
      </article>
      <article class="card">
        <p class="card-title">Read Throughput</p>
        <div class="card-value" id="reads-per-sec">0.0 /s</div>
        <div class="card-meta" id="hits-misses">hits 0 | misses 0</div>
      </article>
      <article class="card">
        <p class="card-title">Write Throughput</p>
        <div class="card-value" id="writes-per-sec">0.0 /s</div>
        <div class="card-meta" id="sets-removes">sets 0 | removes 0</div>
      </article>
      <article class="card">
        <p class="card-title">Fallback Pressure</p>
        <div class="card-value" id="fallbacks-per-sec">0.0 /s</div>
        <div class="card-meta" id="fallback-total">fallback total: 0</div>
      </article>
      <article class="card">
        <p class="card-title">Stampede/Breaker</p>
        <div class="card-value" id="stampede">0 / 0 / 0</div>
        <div class="card-meta" id="breaker-opens">breaker opens: 0</div>
      </article>
    </section>

    <section class="charts">
      <article class="panel">
        <h2>Hit Rate Trend</h2>
        <canvas id="hit-rate-chart" width="900" height="280"></canvas>
      </article>
      <article class="panel">
        <h2>Read/Write Throughput</h2>
        <canvas id="throughput-chart" width="900" height="280"></canvas>
      </article>
    </section>

    <section class="meta-grid">
      <article class="panel">
        <h2>Autoscaler Snapshot</h2>
        <div id="autoscaler"></div>
      </article>
      <article class="panel">
        <h2>Spill Diagnostics</h2>
        <div id="spill"></div>
      </article>
      <article class="panel">
        <h2>Feed Configuration</h2>
        <div class="meta-row"><span>Stream endpoint</span><span id="stream-url" class="muted"></span></div>
        <div class="meta-row"><span>Stats endpoint</span><span id="stats-url" class="muted"></span></div>
        <div class="meta-row"><span>Status endpoint</span><span id="status-url" class="muted"></span></div>
        <div class="meta-row"><span>History samples</span><span id="history-count" class="muted">0</span></div>
      </article>
    </section>

    <section class="panel">
      <h2>MUX Lane Health</h2>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Lane</th>
              <th>Role</th>
              <th>Conn</th>
              <th>Queue</th>
              <th>InFlight</th>
              <th>Util%</th>
              <th>Ops</th>
              <th>Resp</th>
              <th>Fail</th>
              <th>Orphan</th>
              <th>SeqMismatch</th>
              <th>Reset</th>
              <th>Healthy</th>
            </tr>
          </thead>
          <tbody id="lane-rows">
            <tr><td colspan="13" class="muted">No lane samples yet.</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <div class="footer">Built-in VapeCache endpoint dashboard | realtime cache diagnostics</div>
  </div>`;

const endpointPrefix = resolveEndpointPrefix(window.location.pathname);
const streamUrl = `${endpointPrefix}/stream`;
const statsUrl = `${endpointPrefix}/stats`;
const statusUrl = `${endpointPrefix}/status`;

const numberFormatter = new Intl.NumberFormat();

const dom = {
  dot: byId<HTMLDivElement>("conn-dot"),
  connState: byId<HTMLSpanElement>("conn-state"),
  lastSample: byId<HTMLSpanElement>("last-sample"),
  backend: byId<HTMLDivElement>("backend"),
  breakerState: byId<HTMLDivElement>("breaker-state"),
  hitRate: byId<HTMLDivElement>("hit-rate"),
  readsTotal: byId<HTMLDivElement>("reads-total"),
  readsPerSec: byId<HTMLDivElement>("reads-per-sec"),
  hitsMisses: byId<HTMLDivElement>("hits-misses"),
  writesPerSec: byId<HTMLDivElement>("writes-per-sec"),
  setsRemoves: byId<HTMLDivElement>("sets-removes"),
  fallbacksPerSec: byId<HTMLDivElement>("fallbacks-per-sec"),
  fallbackTotal: byId<HTMLDivElement>("fallback-total"),
  stampede: byId<HTMLDivElement>("stampede"),
  breakerOpens: byId<HTMLDivElement>("breaker-opens"),
  autoscaler: byId<HTMLDivElement>("autoscaler"),
  spill: byId<HTMLDivElement>("spill"),
  lanes: byId<HTMLTableSectionElement>("lane-rows"),
  historyCount: byId<HTMLSpanElement>("history-count"),
  streamUrl: byId<HTMLSpanElement>("stream-url"),
  statsUrl: byId<HTMLSpanElement>("stats-url"),
  statusUrl: byId<HTMLSpanElement>("status-url"),
  pauseButton: byId<HTMLButtonElement>("toggle-pause"),
  pullButton: byId<HTMLButtonElement>("pull-now"),
  resetButton: byId<HTMLButtonElement>("reset-history"),
  hitRateChart: byId<HTMLCanvasElement>("hit-rate-chart"),
  throughputChart: byId<HTMLCanvasElement>("throughput-chart")
};

dom.streamUrl.textContent = streamUrl;
dom.statsUrl.textContent = statsUrl;
dom.statusUrl.textContent = statusUrl;

const history: LiveSample[] = [];
const maxHistory = 180;
let previous: LiveSample | null = null;
let latestStatus: UnknownRecord | null = null;
let eventSource: EventSource | null = null;
let reconnectDelayMs = 500;
let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
let pollTimer: ReturnType<typeof setInterval> | null = null;
let statusTimer: ReturnType<typeof setInterval> | null = null;
let paused = false;

function asPercent(value: number): string {
  return `${(value * 100).toFixed(2)}%`;
}

function asRate(value: number): string {
  return `${value.toFixed(1)} /s`;
}

function formatInt(value: number): string {
  return numberFormatter.format(Math.round(value));
}

function normalizeLane(raw: unknown): RedisMuxLaneSnapshot {
  const lane = isRecord(raw) ? raw : {};

  return {
    laneIndex: asNumber(getValue(lane, "LaneIndex", "laneIndex")),
    connectionId: asNumber(getValue(lane, "ConnectionId", "connectionId")),
    role: asString(getValue(lane, "Role", "role"), "n/a"),
    writeQueueDepth: asNumber(getValue(lane, "WriteQueueDepth", "writeQueueDepth")),
    inFlight: asNumber(getValue(lane, "InFlight", "inFlight")),
    maxInFlight: asNumber(getValue(lane, "MaxInFlight", "maxInFlight")),
    inFlightUtilization: asNumber(getValue(lane, "InFlightUtilization", "inFlightUtilization")),
    operations: asNumber(getValue(lane, "Operations", "operations")),
    responses: asNumber(getValue(lane, "Responses", "responses")),
    failures: asNumber(getValue(lane, "Failures", "failures")),
    orphanedResponses: asNumber(getValue(lane, "OrphanedResponses", "orphanedResponses")),
    responseSequenceMismatches: asNumber(getValue(lane, "ResponseSequenceMismatches", "responseSequenceMismatches")),
    transportResets: asNumber(getValue(lane, "TransportResets", "transportResets")),
    healthy: asBoolean(getValue(lane, "Healthy", "healthy"))
  };
}

function normalizeSpill(raw: unknown): SpillSnapshot | null {
  if (!isRecord(raw)) {
    return null;
  }

  return {
    mode: asString(getValue(raw, "Mode", "mode"), "unknown"),
    supportsDiskSpill: asBoolean(getValue(raw, "SupportsDiskSpill", "supportsDiskSpill")),
    spillToDiskConfigured: asBoolean(getValue(raw, "SpillToDiskConfigured", "spillToDiskConfigured")),
    totalSpillFiles: asNumber(getValue(raw, "TotalSpillFiles", "totalSpillFiles")),
    activeShards: asNumber(getValue(raw, "ActiveShards", "activeShards")),
    maxFilesInShard: asNumber(getValue(raw, "MaxFilesInShard", "maxFilesInShard")),
    imbalanceRatio: asNumber(getValue(raw, "ImbalanceRatio", "imbalanceRatio"))
  };
}

function normalizeAutoscaler(raw: unknown): AutoscalerSnapshot | null {
  if (!isRecord(raw)) {
    return null;
  }

  return {
    currentConnections: asNumber(getValue(raw, "CurrentConnections", "currentConnections")),
    targetConnections: asNumber(getValue(raw, "TargetConnections", "targetConnections")),
    currentReadLanes: asNumber(getValue(raw, "CurrentReadLanes", "currentReadLanes")),
    currentWriteLanes: asNumber(getValue(raw, "CurrentWriteLanes", "currentWriteLanes")),
    avgQueueDepth: asNumber(getValue(raw, "AvgQueueDepth", "avgQueueDepth")),
    maxQueueDepth: asNumber(getValue(raw, "MaxQueueDepth", "maxQueueDepth")),
    timeoutRatePerSec: asNumber(getValue(raw, "TimeoutRatePerSec", "timeoutRatePerSec")),
    rollingP95LatencyMs: asNumber(getValue(raw, "RollingP95LatencyMs", "rollingP95LatencyMs")),
    rollingP99LatencyMs: asNumber(getValue(raw, "RollingP99LatencyMs", "rollingP99LatencyMs")),
    frozen: asBoolean(getValue(raw, "Frozen", "frozen")),
    lastScaleDirection: asString(getValue(raw, "LastScaleDirection", "lastScaleDirection"), "n/a"),
    lastScaleReason: asString(getValue(raw, "LastScaleReason", "lastScaleReason"), "n/a")
  };
}

function normalizeSample(raw: unknown): LiveSample {
  const sample = isRecord(raw) ? raw : {};
  const lanesRaw = getValue(sample, "Lanes", "lanes");
  const timestampValue = getValue(sample, "TimestampUtc", "timestampUtc");
  const parsedTimestamp = new Date(typeof timestampValue === "string" ? timestampValue : Date.now());
  const timestampUtc = Number.isFinite(parsedTimestamp.getTime()) ? parsedTimestamp : new Date();

  return {
    timestampUtc,
    currentBackend: asString(getValue(sample, "CurrentBackend", "currentBackend"), "unknown"),
    hits: asNumber(getValue(sample, "Hits", "hits")),
    misses: asNumber(getValue(sample, "Misses", "misses")),
    setCalls: asNumber(getValue(sample, "SetCalls", "setCalls")),
    removeCalls: asNumber(getValue(sample, "RemoveCalls", "removeCalls")),
    fallbackToMemory: asNumber(getValue(sample, "FallbackToMemory", "fallbackToMemory")),
    redisBreakerOpened: asNumber(getValue(sample, "RedisBreakerOpened", "redisBreakerOpened")),
    stampedeKeyRejected: asNumber(getValue(sample, "StampedeKeyRejected", "stampedeKeyRejected")),
    stampedeLockWaitTimeout: asNumber(getValue(sample, "StampedeLockWaitTimeout", "stampedeLockWaitTimeout")),
    stampedeFailureBackoffRejected: asNumber(getValue(sample, "StampedeFailureBackoffRejected", "stampedeFailureBackoffRejected")),
    hitRate: asNumber(getValue(sample, "HitRate", "hitRate")),
    spill: normalizeSpill(getValue(sample, "Spill", "spill")),
    autoscaler: normalizeAutoscaler(getValue(sample, "Autoscaler", "autoscaler")),
    lanes: Array.isArray(lanesRaw) ? lanesRaw.map(normalizeLane) : [],
    readsPerSec: 0,
    writesPerSec: 0,
    fallbacksPerSec: 0
  };
}

function setConnectionState(label: string, level: ConnectionLevel): void {
  dom.connState.textContent = label;
  dom.connState.className = `badge ${level}`;

  const palette = {
    ok: ["var(--accent)", "0 0 0 6px rgba(52, 211, 153, 0.12)"],
    warn: ["var(--warn)", "0 0 0 6px rgba(245, 158, 11, 0.16)"],
    danger: ["var(--danger)", "0 0 0 6px rgba(239, 68, 68, 0.12)"]
  } as const;

  dom.dot.style.background = palette[level][0];
  dom.dot.style.boxShadow = palette[level][1];
}

function renderAutoscaler(sample: LiveSample): void {
  const autoscaler = sample.autoscaler;
  if (!autoscaler) {
    dom.autoscaler.innerHTML = "<div class='muted'>No autoscaler diagnostics present.</div>";
    return;
  }

  dom.autoscaler.innerHTML = `
    <div class="meta-row"><span>Connections</span><span>${formatInt(autoscaler.currentConnections)} / target ${formatInt(autoscaler.targetConnections)}</span></div>
    <div class="meta-row"><span>Lanes (R/W)</span><span>${formatInt(autoscaler.currentReadLanes)} / ${formatInt(autoscaler.currentWriteLanes)}</span></div>
    <div class="meta-row"><span>Queue (avg/max)</span><span>${autoscaler.avgQueueDepth.toFixed(2)} / ${formatInt(autoscaler.maxQueueDepth)}</span></div>
    <div class="meta-row"><span>Timeout rate</span><span>${autoscaler.timeoutRatePerSec.toFixed(2)} /s</span></div>
    <div class="meta-row"><span>Rolling latency</span><span>p95 ${autoscaler.rollingP95LatencyMs.toFixed(2)} ms | p99 ${autoscaler.rollingP99LatencyMs.toFixed(2)} ms</span></div>
    <div class="meta-row"><span>Frozen</span><span class="${autoscaler.frozen ? "warn" : "ok"}">${autoscaler.frozen ? "yes" : "no"}</span></div>
    <div class="meta-row"><span>Last scale</span><span>${escapeHtml(autoscaler.lastScaleDirection)}</span></div>
    <div class="meta-row"><span>Reason</span><span>${escapeHtml(autoscaler.lastScaleReason)}</span></div>`;
}

function renderSpill(sample: LiveSample): void {
  const spill = sample.spill;
  if (!spill) {
    dom.spill.innerHTML = "<div class='muted'>No spill diagnostics present.</div>";
    return;
  }

  dom.spill.innerHTML = `
    <div class="meta-row"><span>Mode</span><span>${escapeHtml(spill.mode)}</span></div>
    <div class="meta-row"><span>Supports disk spill</span><span class="${spill.supportsDiskSpill ? "ok" : "muted"}">${spill.supportsDiskSpill ? "yes" : "no"}</span></div>
    <div class="meta-row"><span>Disk spill configured</span><span class="${spill.spillToDiskConfigured ? "ok" : "muted"}">${spill.spillToDiskConfigured ? "yes" : "no"}</span></div>
    <div class="meta-row"><span>Total spill files</span><span>${formatInt(spill.totalSpillFiles)}</span></div>
    <div class="meta-row"><span>Active shards</span><span>${formatInt(spill.activeShards)}</span></div>
    <div class="meta-row"><span>Max files in shard</span><span>${formatInt(spill.maxFilesInShard)}</span></div>
    <div class="meta-row"><span>Shard imbalance</span><span>${spill.imbalanceRatio.toFixed(2)}</span></div>`;
}

function renderLanes(sample: LiveSample): void {
  if (sample.lanes.length === 0) {
    dom.lanes.innerHTML = "<tr><td colspan='13' class='muted'>No lane samples yet.</td></tr>";
    return;
  }

  const ordered = [...sample.lanes].sort((left, right) => left.laneIndex - right.laneIndex);
  dom.lanes.innerHTML = ordered
    .map(lane => {
      const healthClass = lane.healthy ? "ok" : "danger";
      return `<tr>
        <td>#${formatInt(lane.laneIndex)}</td>
        <td>${escapeHtml(lane.role)}</td>
        <td>${formatInt(lane.connectionId)}</td>
        <td>${formatInt(lane.writeQueueDepth)}</td>
        <td>${formatInt(lane.inFlight)} / ${formatInt(lane.maxInFlight)}</td>
        <td>${(lane.inFlightUtilization * 100).toFixed(1)}</td>
        <td>${formatInt(lane.operations)}</td>
        <td>${formatInt(lane.responses)}</td>
        <td>${formatInt(lane.failures)}</td>
        <td>${formatInt(lane.orphanedResponses)}</td>
        <td>${formatInt(lane.responseSequenceMismatches)}</td>
        <td>${formatInt(lane.transportResets)}</td>
        <td class="${healthClass}">${lane.healthy ? "yes" : "no"}</td>
      </tr>`;
    })
    .join("");
}

function drawChart(
  canvas: HTMLCanvasElement,
  series: Array<{ values: number[]; color: string }>,
  maxValueHint: number
): void {
  const context = canvas.getContext("2d");
  if (!context) {
    return;
  }

  const dpr = window.devicePixelRatio || 1;
  const cssWidth = Math.max(1, canvas.clientWidth);
  const cssHeight = Math.max(1, canvas.clientHeight);
  const width = Math.floor(cssWidth * dpr);
  const height = Math.floor(cssHeight * dpr);

  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }

  context.clearRect(0, 0, width, height);
  context.lineWidth = 1;
  context.strokeStyle = "rgba(145,163,182,0.20)";

  for (let x = 0; x <= 10; x += 1) {
    const px = (x / 10) * width;
    context.beginPath();
    context.moveTo(px, 0);
    context.lineTo(px, height);
    context.stroke();
  }

  for (let y = 0; y <= 6; y += 1) {
    const py = (y / 6) * height;
    context.beginPath();
    context.moveTo(0, py);
    context.lineTo(width, py);
    context.stroke();
  }

  const maxValue = Math.max(maxValueHint, ...series.flatMap(entry => entry.values), 1);
  const leftPad = 8 * dpr;
  const topPad = 8 * dpr;
  const innerWidth = width - (leftPad * 2);
  const innerHeight = height - (topPad * 2);

  for (const entry of series) {
    if (entry.values.length === 0) {
      continue;
    }

    context.beginPath();
    context.strokeStyle = entry.color;
    context.lineWidth = 2 * dpr;

    for (let index = 0; index < entry.values.length; index += 1) {
      const x = leftPad + ((entry.values.length === 1 ? 0 : index / (entry.values.length - 1)) * innerWidth);
      const y = topPad + ((1 - Math.min(1, entry.values[index] / maxValue)) * innerHeight);

      if (index === 0) {
        context.moveTo(x, y);
      } else {
        context.lineTo(x, y);
      }
    }

    context.stroke();
  }
}

function renderCharts(): void {
  const hitRates = history.map(sample => sample.hitRate * 100);
  const readsPerSec = history.map(sample => sample.readsPerSec);
  const writesPerSec = history.map(sample => sample.writesPerSec);

  drawChart(dom.hitRateChart, [{ values: hitRates, color: "#38bdf8" }], 100);

  const maxThroughput = Math.max(25, ...readsPerSec, ...writesPerSec);
  drawChart(
    dom.throughputChart,
    [
      { values: readsPerSec, color: "#34d399" },
      { values: writesPerSec, color: "#f59e0b" }
    ],
    maxThroughput
  );

  dom.historyCount.textContent = formatInt(history.length);
}

function renderCards(sample: LiveSample): void {
  const reads = sample.hits + sample.misses;

  dom.backend.textContent = sample.currentBackend;
  dom.hitRate.textContent = asPercent(sample.hitRate);
  dom.readsTotal.textContent = `reads: ${formatInt(reads)}`;
  dom.readsPerSec.textContent = asRate(sample.readsPerSec);
  dom.hitsMisses.textContent = `hits ${formatInt(sample.hits)} | misses ${formatInt(sample.misses)}`;
  dom.writesPerSec.textContent = asRate(sample.writesPerSec);
  dom.setsRemoves.textContent = `sets ${formatInt(sample.setCalls)} | removes ${formatInt(sample.removeCalls)}`;
  dom.fallbacksPerSec.textContent = asRate(sample.fallbacksPerSec);
  dom.fallbackTotal.textContent = `fallback total: ${formatInt(sample.fallbackToMemory)}`;
  dom.stampede.textContent = `${formatInt(sample.stampedeKeyRejected)} / ${formatInt(sample.stampedeLockWaitTimeout)} / ${formatInt(sample.stampedeFailureBackoffRejected)}`;
  dom.breakerOpens.textContent = `breaker opens: ${formatInt(sample.redisBreakerOpened)}`;
  dom.lastSample.textContent = `last sample ${sample.timestampUtc.toISOString()}`;

  const breaker = isRecord(latestStatus)
    ? ((getValue(latestStatus, "CircuitBreaker", "circuitBreaker") as UnknownRecord | undefined) ?? null)
    : null;

  if (breaker) {
    const forced = asBoolean(getValue(breaker, "IsForcedOpen", "isForcedOpen"));
    const open = asBoolean(getValue(breaker, "IsOpen", "isOpen"));
    const reason = asString(getValue(breaker, "Reason", "reason"), "none");

    const mode = forced ? "forced-open" : open ? "open" : "closed";
    const cssClass = forced || open ? "warn" : "ok";
    dom.breakerState.innerHTML = `breaker: <span class="${cssClass}">${mode}</span> (${escapeHtml(reason)})`;
  } else {
    dom.breakerState.textContent = "breaker: status unavailable";
  }
}

async function fetchJson(url: string): Promise<unknown> {
  const response = await fetch(url, {
    cache: "no-store",
    headers: { accept: "application/json" }
  });

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return response.json();
}

function handleSample(sample: LiveSample): void {
  if (paused) {
    return;
  }

  if (previous) {
    const deltaSeconds = Math.max(0.001, (sample.timestampUtc.getTime() - previous.timestampUtc.getTime()) / 1000);
    const previousReads = previous.hits + previous.misses;
    const nowReads = sample.hits + sample.misses;
    const previousWrites = previous.setCalls + previous.removeCalls;
    const nowWrites = sample.setCalls + sample.removeCalls;

    sample.readsPerSec = Math.max(0, (nowReads - previousReads) / deltaSeconds);
    sample.writesPerSec = Math.max(0, (nowWrites - previousWrites) / deltaSeconds);
    sample.fallbacksPerSec = Math.max(0, (sample.fallbackToMemory - previous.fallbackToMemory) / deltaSeconds);
  }

  previous = sample;
  history.push(sample);

  if (history.length > maxHistory) {
    history.shift();
  }

  renderCards(sample);
  renderAutoscaler(sample);
  renderSpill(sample);
  renderLanes(sample);
  renderCharts();
}

async function pullStatsSnapshot(): Promise<void> {
  try {
    const payload = await fetchJson(statsUrl);
    handleSample(normalizeSample(payload));
    setConnectionState("polling snapshot", "warn");
  } catch {
    setConnectionState("snapshot failed", "danger");
  }
}

async function refreshStatus(): Promise<void> {
  try {
    const payload = await fetchJson(statusUrl);
    latestStatus = isRecord(payload) ? payload : null;
    if (!paused && history.length > 0) {
      renderCards(history[history.length - 1]);
    }
  } catch {
    latestStatus = null;
  }
}

function stopPolling(): void {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

function startPolling(): void {
  stopPolling();
  void pullStatsSnapshot();
  pollTimer = setInterval(() => {
    void pullStatsSnapshot();
  }, 1000);
}

function closeEventSource(): void {
  if (eventSource) {
    eventSource.close();
    eventSource = null;
  }
}

function scheduleReconnect(): void {
  if (reconnectTimer) {
    return;
  }

  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    reconnectDelayMs = Math.min(reconnectDelayMs * 2, 15000);
    connectStream();
  }, reconnectDelayMs);
}

function connectStream(): void {
  closeEventSource();
  stopPolling();
  setConnectionState("connecting", "warn");

  try {
    eventSource = new EventSource(streamUrl);
  } catch {
    setConnectionState("stream unavailable", "danger");
    startPolling();
    scheduleReconnect();
    return;
  }

  eventSource.onopen = () => {
    reconnectDelayMs = 500;
    stopPolling();
    setConnectionState("streaming", "ok");
  };

  const onStreamPayload = (event: MessageEvent<string>): void => {
    try {
      const payload = JSON.parse(event.data) as unknown;
      handleSample(normalizeSample(payload));
      setConnectionState("streaming", "ok");
    } catch {
      setConnectionState("stream payload error", "danger");
    }
  };

  eventSource.addEventListener("vapecache-stats", onStreamPayload as EventListener);
  eventSource.onmessage = onStreamPayload;

  eventSource.onerror = () => {
    setConnectionState("reconnecting", "warn");
    closeEventSource();
    startPolling();
    scheduleReconnect();
  };
}

function resetDashboardHistory(): void {
  history.length = 0;
  previous = null;
  renderCharts();
  dom.lastSample.textContent = "history cleared";
  dom.lanes.innerHTML = "<tr><td colspan='13' class='muted'>No lane samples yet.</td></tr>";
}

dom.pauseButton.addEventListener("click", () => {
  paused = !paused;
  dom.pauseButton.textContent = paused ? "Resume" : "Pause";

  if (paused) {
    setConnectionState("paused", "warn");
  } else if (eventSource) {
    setConnectionState("streaming", "ok");
  }
});

dom.pullButton.addEventListener("click", () => {
  void pullStatsSnapshot();
});

dom.resetButton.addEventListener("click", resetDashboardHistory);

statusTimer = setInterval(() => {
  void refreshStatus();
}, 5000);

void refreshStatus();
connectStream();

window.addEventListener("beforeunload", () => {
  closeEventSource();

  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }

  if (statusTimer) {
    clearInterval(statusTimer);
    statusTimer = null;
  }

  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
});
