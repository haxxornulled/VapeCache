(function(){const t=document.createElement("link").relList;if(t&&t.supports&&t.supports("modulepreload"))return;for(const o of document.querySelectorAll('link[rel="modulepreload"]'))n(o);new MutationObserver(o=>{for(const d of o)if(d.type==="childList")for(const S of d.addedNodes)S.tagName==="LINK"&&S.rel==="modulepreload"&&n(S)}).observe(document,{childList:!0,subtree:!0});function a(o){const d={};return o.integrity&&(d.integrity=o.integrity),o.referrerPolicy&&(d.referrerPolicy=o.referrerPolicy),o.crossOrigin==="use-credentials"?d.credentials="include":o.crossOrigin==="anonymous"?d.credentials="omit":d.credentials="same-origin",d}function n(o){if(o.ep)return;o.ep=!0;const d=a(o);fetch(o.href,d)}})();function se(e){const t=e.replace(/\/+$/,""),n=t.lastIndexOf("/dashboard");return n<0?t||"":t.slice(0,n)}function $(e){return typeof e=="object"&&e!==null}function s(e,...t){if(e){for(const a of t)if(Object.prototype.hasOwnProperty.call(e,a)){const n=e[a];if(n!=null)return n}}}function r(e,t=0){const a=Number(e);return Number.isFinite(a)?a:t}function R(e,t=""){return typeof e=="string"&&e.length>0?e:t}function T(e,t=!1){return typeof e=="boolean"?e:t}function ae(e,t="Redis"){return typeof e!="string"||e.length===0?t:e==="Redis"||e==="InMemory"?e:e.toLowerCase()==="redis"?"Redis":e.toLowerCase()==="memory"||e.toLowerCase()==="in-memory"?"InMemory":t}function C(e){return e.replaceAll("&","&amp;").replaceAll("<","&lt;").replaceAll(">","&gt;").replaceAll('"',"&quot;").replaceAll("'","&#39;")}function c(e){const t=document.getElementById(e);if(!t)throw new Error(`Missing dashboard element: ${e}`);return t}const E=document.getElementById("app");if(!E)throw new Error("Dashboard root element #app was not found.");E.innerHTML=`
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
  </div>`;const B=se(window.location.pathname),Q=`${B}/stream`,j=`${B}/stats`,q=`${B}/status`,ne=new Intl.NumberFormat,i={dot:c("conn-dot"),connState:c("conn-state"),lastSample:c("last-sample"),backend:c("backend"),breakerState:c("breaker-state"),hitRate:c("hit-rate"),readsTotal:c("reads-total"),readsPerSec:c("reads-per-sec"),hitsMisses:c("hits-misses"),writesPerSec:c("writes-per-sec"),setsRemoves:c("sets-removes"),fallbacksPerSec:c("fallbacks-per-sec"),fallbackTotal:c("fallback-total"),stampede:c("stampede"),breakerOpens:c("breaker-opens"),autoscaler:c("autoscaler"),spill:c("spill"),lanes:c("lane-rows"),historyCount:c("history-count"),streamUrl:c("stream-url"),statsUrl:c("stats-url"),statusUrl:c("status-url"),pauseButton:c("toggle-pause"),pullButton:c("pull-now"),resetButton:c("reset-history"),hitRateChart:c("hit-rate-chart"),throughputChart:c("throughput-chart")};i.streamUrl.textContent=Q;i.statsUrl.textContent=j;i.statusUrl.textContent=q;const h=[],re=180;let f=null,P=null,v=null,w=500,x=null,y=null,M=null,k=!1;function ie(e){return`${(e*100).toFixed(2)}%`}function F(e){return`${e.toFixed(1)} /s`}function l(e){return ne.format(Math.round(e))}function oe(e){const t=$(e)?e:{};return{laneIndex:r(s(t,"LaneIndex","laneIndex")),connectionId:r(s(t,"ConnectionId","connectionId")),role:R(s(t,"Role","role"),"n/a"),writeQueueDepth:r(s(t,"WriteQueueDepth","writeQueueDepth")),inFlight:r(s(t,"InFlight","inFlight")),maxInFlight:r(s(t,"MaxInFlight","maxInFlight")),inFlightUtilization:r(s(t,"InFlightUtilization","inFlightUtilization")),operations:r(s(t,"Operations","operations")),responses:r(s(t,"Responses","responses")),failures:r(s(t,"Failures","failures")),orphanedResponses:r(s(t,"OrphanedResponses","orphanedResponses")),responseSequenceMismatches:r(s(t,"ResponseSequenceMismatches","responseSequenceMismatches")),transportResets:r(s(t,"TransportResets","transportResets")),healthy:T(s(t,"Healthy","healthy"))}}function le(e){return $(e)?{mode:R(s(e,"Mode","mode"),"unknown"),supportsDiskSpill:T(s(e,"SupportsDiskSpill","supportsDiskSpill")),spillToDiskConfigured:T(s(e,"SpillToDiskConfigured","spillToDiskConfigured")),totalSpillFiles:r(s(e,"TotalSpillFiles","totalSpillFiles")),activeShards:r(s(e,"ActiveShards","activeShards")),maxFilesInShard:r(s(e,"MaxFilesInShard","maxFilesInShard")),imbalanceRatio:r(s(e,"ImbalanceRatio","imbalanceRatio"))}:null}function ce(e){return $(e)?{currentConnections:r(s(e,"CurrentConnections","currentConnections")),targetConnections:r(s(e,"TargetConnections","targetConnections")),currentReadLanes:r(s(e,"CurrentReadLanes","currentReadLanes")),currentWriteLanes:r(s(e,"CurrentWriteLanes","currentWriteLanes")),avgQueueDepth:r(s(e,"AvgQueueDepth","avgQueueDepth")),maxQueueDepth:r(s(e,"MaxQueueDepth","maxQueueDepth")),timeoutRatePerSec:r(s(e,"TimeoutRatePerSec","timeoutRatePerSec")),rollingP95LatencyMs:r(s(e,"RollingP95LatencyMs","rollingP95LatencyMs")),rollingP99LatencyMs:r(s(e,"RollingP99LatencyMs","rollingP99LatencyMs")),frozen:T(s(e,"Frozen","frozen")),lastScaleDirection:R(s(e,"LastScaleDirection","lastScaleDirection"),"n/a"),lastScaleReason:R(s(e,"LastScaleReason","lastScaleReason"),"n/a"),spillSignalCount:r(s(e,"SpillSignalCount","spillSignalCount")),spillTotalFiles:r(s(e,"SpillTotalFiles","spillTotalFiles")),spillActiveShards:r(s(e,"SpillActiveShards","spillActiveShards")),spillImbalanceRatio:r(s(e,"SpillImbalanceRatio","spillImbalanceRatio")),pressureScore:r(s(e,"PressureScore","pressureScore")),pressureTier:R(s(e,"PressureTier","pressureTier"),"normal")}:null}function K(e){const t=$(e)?e:{},a=s(t,"Lanes","lanes"),n=s(t,"TimestampUtc","timestampUtc"),o=new Date(typeof n=="string"?n:Date.now());return{timestampUtc:Number.isFinite(o.getTime())?o:new Date,currentBackend:ae(s(t,"CurrentBackend","currentBackend")),hits:r(s(t,"Hits","hits")),misses:r(s(t,"Misses","misses")),setCalls:r(s(t,"SetCalls","setCalls")),removeCalls:r(s(t,"RemoveCalls","removeCalls")),fallbackToMemory:r(s(t,"FallbackToMemory","fallbackToMemory")),redisBreakerOpened:r(s(t,"RedisBreakerOpened","redisBreakerOpened")),stampedeKeyRejected:r(s(t,"StampedeKeyRejected","stampedeKeyRejected")),stampedeLockWaitTimeout:r(s(t,"StampedeLockWaitTimeout","stampedeLockWaitTimeout")),stampedeFailureBackoffRejected:r(s(t,"StampedeFailureBackoffRejected","stampedeFailureBackoffRejected")),hitRate:r(s(t,"HitRate","hitRate")),spill:le(s(t,"Spill","spill")),autoscaler:ce(s(t,"Autoscaler","autoscaler")),lanes:Array.isArray(a)?a.map(oe):[],readsPerSec:0,writesPerSec:0,fallbacksPerSec:0}}function m(e,t){i.connState.textContent=e,i.connState.className=`badge ${t}`;const a={ok:["var(--accent)","0 0 0 6px rgba(52, 211, 153, 0.12)"],warn:["var(--warn)","0 0 0 6px rgba(245, 158, 11, 0.16)"],danger:["var(--danger)","0 0 0 6px rgba(239, 68, 68, 0.12)"]};i.dot.style.background=a[t][0],i.dot.style.boxShadow=a[t][1]}function de(e){const t=e.autoscaler;if(!t){i.autoscaler.innerHTML="<div class='muted'>No autoscaler diagnostics present.</div>";return}i.autoscaler.innerHTML=`
    <div class="meta-row"><span>Connections</span><span>${l(t.currentConnections)} / target ${l(t.targetConnections)}</span></div>
    <div class="meta-row"><span>Lanes (R/W)</span><span>${l(t.currentReadLanes)} / ${l(t.currentWriteLanes)}</span></div>
    <div class="meta-row"><span>Queue (avg/max)</span><span>${t.avgQueueDepth.toFixed(2)} / ${l(t.maxQueueDepth)}</span></div>
    <div class="meta-row"><span>Timeout rate</span><span>${t.timeoutRatePerSec.toFixed(2)} /s</span></div>
    <div class="meta-row"><span>Rolling latency</span><span>p95 ${t.rollingP95LatencyMs.toFixed(2)} ms | p99 ${t.rollingP99LatencyMs.toFixed(2)} ms</span></div>
    <div class="meta-row"><span>Pressure</span><span>${C(t.pressureTier)} (${t.pressureScore.toFixed(3)})</span></div>
    <div class="meta-row"><span>Spill signals</span><span>${l(t.spillSignalCount)} | files ${l(t.spillTotalFiles)} | shards ${l(t.spillActiveShards)}</span></div>
    <div class="meta-row"><span>Spill imbalance</span><span>${t.spillImbalanceRatio.toFixed(2)}</span></div>
    <div class="meta-row"><span>Frozen</span><span class="${t.frozen?"warn":"ok"}">${t.frozen?"yes":"no"}</span></div>
    <div class="meta-row"><span>Last scale</span><span>${C(t.lastScaleDirection)}</span></div>
    <div class="meta-row"><span>Reason</span><span>${C(t.lastScaleReason)}</span></div>`}function pe(e){const t=e.spill;if(!t){i.spill.innerHTML="<div class='muted'>No spill diagnostics present.</div>";return}i.spill.innerHTML=`
    <div class="meta-row"><span>Mode</span><span>${C(t.mode)}</span></div>
    <div class="meta-row"><span>Supports disk spill</span><span class="${t.supportsDiskSpill?"ok":"muted"}">${t.supportsDiskSpill?"yes":"no"}</span></div>
    <div class="meta-row"><span>Disk spill configured</span><span class="${t.spillToDiskConfigured?"ok":"muted"}">${t.spillToDiskConfigured?"yes":"no"}</span></div>
    <div class="meta-row"><span>Total spill files</span><span>${l(t.totalSpillFiles)}</span></div>
    <div class="meta-row"><span>Active shards</span><span>${l(t.activeShards)}</span></div>
    <div class="meta-row"><span>Max files in shard</span><span>${l(t.maxFilesInShard)}</span></div>
    <div class="meta-row"><span>Shard imbalance</span><span>${t.imbalanceRatio.toFixed(2)}</span></div>`}function ue(e){if(e.lanes.length===0){i.lanes.innerHTML="<tr><td colspan='13' class='muted'>No lane samples yet.</td></tr>";return}const t=[...e.lanes].sort((a,n)=>a.laneIndex-n.laneIndex);i.lanes.innerHTML=t.map(a=>{const n=a.healthy?"ok":"danger";return`<tr>
        <td>#${l(a.laneIndex)}</td>
        <td>${C(a.role)}</td>
        <td>${l(a.connectionId)}</td>
        <td>${l(a.writeQueueDepth)}</td>
        <td>${l(a.inFlight)} / ${l(a.maxInFlight)}</td>
        <td>${(a.inFlightUtilization*100).toFixed(1)}</td>
        <td>${l(a.operations)}</td>
        <td>${l(a.responses)}</td>
        <td>${l(a.failures)}</td>
        <td>${l(a.orphanedResponses)}</td>
        <td>${l(a.responseSequenceMismatches)}</td>
        <td>${l(a.transportResets)}</td>
        <td class="${n}">${a.healthy?"yes":"no"}</td>
      </tr>`}).join("")}function W(e,t,a){const n=e.getContext("2d");if(!n)return;const o=window.devicePixelRatio||1,d=Math.max(1,e.clientWidth),S=Math.max(1,e.clientHeight),g=Math.floor(d*o),b=Math.floor(S*o);(e.width!==g||e.height!==b)&&(e.width=g,e.height=b),n.clearRect(0,0,g,b),n.lineWidth=1,n.strokeStyle="rgba(145,163,182,0.20)";for(let p=0;p<=10;p+=1){const u=p/10*g;n.beginPath(),n.moveTo(u,0),n.lineTo(u,b),n.stroke()}for(let p=0;p<=6;p+=1){const u=p/6*b;n.beginPath(),n.moveTo(0,u),n.lineTo(g,u),n.stroke()}const _=Math.max(a,...t.flatMap(p=>p.values),1),O=8*o,H=8*o,ee=g-O*2,te=b-H*2;for(const p of t)if(p.values.length!==0){n.beginPath(),n.strokeStyle=p.color,n.lineWidth=2*o;for(let u=0;u<p.values.length;u+=1){const U=O+(p.values.length===1?0:u/(p.values.length-1))*ee,A=H+(1-Math.min(1,p.values[u]/_))*te;u===0?n.moveTo(U,A):n.lineTo(U,A)}n.stroke()}}function V(){const e=h.map(o=>o.hitRate*100),t=h.map(o=>o.readsPerSec),a=h.map(o=>o.writesPerSec);W(i.hitRateChart,[{values:e,color:"#38bdf8"}],100);const n=Math.max(25,...t,...a);W(i.throughputChart,[{values:t,color:"#34d399"},{values:a,color:"#f59e0b"}],n),i.historyCount.textContent=l(h.length)}function J(e){const t=e.hits+e.misses;i.backend.textContent=e.currentBackend==="InMemory"?"in-memory":"redis",i.hitRate.textContent=ie(e.hitRate),i.readsTotal.textContent=`reads: ${l(t)}`,i.readsPerSec.textContent=F(e.readsPerSec),i.hitsMisses.textContent=`hits ${l(e.hits)} | misses ${l(e.misses)}`,i.writesPerSec.textContent=F(e.writesPerSec),i.setsRemoves.textContent=`sets ${l(e.setCalls)} | removes ${l(e.removeCalls)}`,i.fallbacksPerSec.textContent=F(e.fallbacksPerSec),i.fallbackTotal.textContent=`fallback total: ${l(e.fallbackToMemory)}`,i.stampede.textContent=`${l(e.stampedeKeyRejected)} / ${l(e.stampedeLockWaitTimeout)} / ${l(e.stampedeFailureBackoffRejected)}`,i.breakerOpens.textContent=`breaker opens: ${l(e.redisBreakerOpened)}`,i.lastSample.textContent=`last sample ${e.timestampUtc.toISOString()}`;const a=$(P)?s(P,"CircuitBreaker","circuitBreaker")??null:null;if(a){const n=T(s(a,"IsForcedOpen","isForcedOpen")),o=T(s(a,"IsOpen","isOpen")),d=R(s(a,"Reason","reason"),"none"),S=n?"forced-open":o?"open":"closed",g=n||o?"warn":"ok";i.breakerState.innerHTML=`breaker: <span class="${g}">${S}</span> (${C(d)})`}else i.breakerState.textContent="breaker: status unavailable"}async function X(e){const t=await fetch(e,{cache:"no-store",headers:{accept:"application/json"}});if(!t.ok)throw new Error(`HTTP ${t.status}`);return t.json()}function G(e){if(!k){if(f){const t=Math.max(.001,(e.timestampUtc.getTime()-f.timestampUtc.getTime())/1e3),a=f.hits+f.misses,n=e.hits+e.misses,o=f.setCalls+f.removeCalls,d=e.setCalls+e.removeCalls;e.readsPerSec=Math.max(0,(n-a)/t),e.writesPerSec=Math.max(0,(d-o)/t),e.fallbacksPerSec=Math.max(0,(e.fallbackToMemory-f.fallbackToMemory)/t)}f=e,h.push(e),h.length>re&&h.shift(),J(e),de(e),pe(e),ue(e),V()}}async function L(){try{const e=await X(j);G(K(e)),m("polling snapshot","warn")}catch{m("snapshot failed","danger")}}async function Y(){try{const e=await X(q);P=$(e)?e:null,!k&&h.length>0&&J(h[h.length-1])}catch{P=null}}function I(){y&&(clearInterval(y),y=null)}function N(){I(),L(),y=setInterval(()=>{L()},1e3)}function D(){v&&(v.close(),v=null)}function z(){x||(x=setTimeout(()=>{x=null,w=Math.min(w*2,15e3),Z()},w))}function Z(){D(),I(),m("connecting","warn");try{v=new EventSource(Q)}catch{m("stream unavailable","danger"),N(),z();return}v.onopen=()=>{w=500,I(),m("streaming","ok")};const e=t=>{try{const a=JSON.parse(t.data);G(K(a)),m("streaming","ok")}catch{m("stream payload error","danger")}};v.addEventListener("vapecache-stats",e),v.onmessage=e,v.onerror=()=>{m("reconnecting","warn"),D(),N(),z()}}function he(){h.length=0,f=null,V(),i.lastSample.textContent="history cleared",i.lanes.innerHTML="<tr><td colspan='13' class='muted'>No lane samples yet.</td></tr>"}i.pauseButton.addEventListener("click",()=>{k=!k,i.pauseButton.textContent=k?"Resume":"Pause",k?m("paused","warn"):v&&m("streaming","ok")});i.pullButton.addEventListener("click",()=>{L()});i.resetButton.addEventListener("click",he);M=setInterval(()=>{Y()},5e3);Y();Z();window.addEventListener("beforeunload",()=>{D(),y&&(clearInterval(y),y=null),M&&(clearInterval(M),M=null),x&&(clearTimeout(x),x=null)});
