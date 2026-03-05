(function(){const t=document.createElement("link").relList;if(t&&t.supports&&t.supports("modulepreload"))return;for(const i of document.querySelectorAll('link[rel="modulepreload"]'))n(i);new MutationObserver(i=>{for(const d of i)if(d.type==="childList")for(const S of d.addedNodes)S.tagName==="LINK"&&S.rel==="modulepreload"&&n(S)}).observe(document,{childList:!0,subtree:!0});function a(i){const d={};return i.integrity&&(d.integrity=i.integrity),i.referrerPolicy&&(d.referrerPolicy=i.referrerPolicy),i.crossOrigin==="use-credentials"?d.credentials="include":i.crossOrigin==="anonymous"?d.credentials="omit":d.credentials="same-origin",d}function n(i){if(i.ep)return;i.ep=!0;const d=a(i);fetch(i.href,d)}})();function se(e){const t=e.replace(/\/+$/,""),n=t.lastIndexOf("/dashboard");return n<0?t||"":t.slice(0,n)}function w(e){return typeof e=="object"&&e!==null}function s(e,...t){if(e){for(const a of t)if(Object.prototype.hasOwnProperty.call(e,a)){const n=e[a];if(n!=null)return n}}}function o(e,t=0){const a=Number(e);return Number.isFinite(a)?a:t}function R(e,t=""){return typeof e=="string"&&e.length>0?e:t}function C(e,t=!1){return typeof e=="boolean"?e:t}function $(e){return e.replaceAll("&","&amp;").replaceAll("<","&lt;").replaceAll(">","&gt;").replaceAll('"',"&quot;").replaceAll("'","&#39;")}function c(e){const t=document.getElementById(e);if(!t)throw new Error(`Missing dashboard element: ${e}`);return t}const E=document.getElementById("app");if(!E)throw new Error("Dashboard root element #app was not found.");E.innerHTML=`
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
  </div>`;const B=se(window.location.pathname),Q=`${B}/stream`,j=`${B}/stats`,q=`${B}/status`,ae=new Intl.NumberFormat,r={dot:c("conn-dot"),connState:c("conn-state"),lastSample:c("last-sample"),backend:c("backend"),breakerState:c("breaker-state"),hitRate:c("hit-rate"),readsTotal:c("reads-total"),readsPerSec:c("reads-per-sec"),hitsMisses:c("hits-misses"),writesPerSec:c("writes-per-sec"),setsRemoves:c("sets-removes"),fallbacksPerSec:c("fallbacks-per-sec"),fallbackTotal:c("fallback-total"),stampede:c("stampede"),breakerOpens:c("breaker-opens"),autoscaler:c("autoscaler"),spill:c("spill"),lanes:c("lane-rows"),historyCount:c("history-count"),streamUrl:c("stream-url"),statsUrl:c("stats-url"),statusUrl:c("status-url"),pauseButton:c("toggle-pause"),pullButton:c("pull-now"),resetButton:c("reset-history"),hitRateChart:c("hit-rate-chart"),throughputChart:c("throughput-chart")};r.streamUrl.textContent=Q;r.statsUrl.textContent=j;r.statusUrl.textContent=q;const h=[],ne=180;let f=null,P=null,v=null,T=500,x=null,b=null,M=null,y=!1;function re(e){return`${(e*100).toFixed(2)}%`}function L(e){return`${e.toFixed(1)} /s`}function l(e){return ae.format(Math.round(e))}function ie(e){const t=w(e)?e:{};return{laneIndex:o(s(t,"LaneIndex","laneIndex")),connectionId:o(s(t,"ConnectionId","connectionId")),role:R(s(t,"Role","role"),"n/a"),writeQueueDepth:o(s(t,"WriteQueueDepth","writeQueueDepth")),inFlight:o(s(t,"InFlight","inFlight")),maxInFlight:o(s(t,"MaxInFlight","maxInFlight")),inFlightUtilization:o(s(t,"InFlightUtilization","inFlightUtilization")),operations:o(s(t,"Operations","operations")),responses:o(s(t,"Responses","responses")),failures:o(s(t,"Failures","failures")),orphanedResponses:o(s(t,"OrphanedResponses","orphanedResponses")),responseSequenceMismatches:o(s(t,"ResponseSequenceMismatches","responseSequenceMismatches")),transportResets:o(s(t,"TransportResets","transportResets")),healthy:C(s(t,"Healthy","healthy"))}}function oe(e){return w(e)?{mode:R(s(e,"Mode","mode"),"unknown"),supportsDiskSpill:C(s(e,"SupportsDiskSpill","supportsDiskSpill")),spillToDiskConfigured:C(s(e,"SpillToDiskConfigured","spillToDiskConfigured")),totalSpillFiles:o(s(e,"TotalSpillFiles","totalSpillFiles")),activeShards:o(s(e,"ActiveShards","activeShards")),maxFilesInShard:o(s(e,"MaxFilesInShard","maxFilesInShard")),imbalanceRatio:o(s(e,"ImbalanceRatio","imbalanceRatio"))}:null}function le(e){return w(e)?{currentConnections:o(s(e,"CurrentConnections","currentConnections")),targetConnections:o(s(e,"TargetConnections","targetConnections")),currentReadLanes:o(s(e,"CurrentReadLanes","currentReadLanes")),currentWriteLanes:o(s(e,"CurrentWriteLanes","currentWriteLanes")),avgQueueDepth:o(s(e,"AvgQueueDepth","avgQueueDepth")),maxQueueDepth:o(s(e,"MaxQueueDepth","maxQueueDepth")),timeoutRatePerSec:o(s(e,"TimeoutRatePerSec","timeoutRatePerSec")),rollingP95LatencyMs:o(s(e,"RollingP95LatencyMs","rollingP95LatencyMs")),rollingP99LatencyMs:o(s(e,"RollingP99LatencyMs","rollingP99LatencyMs")),frozen:C(s(e,"Frozen","frozen")),lastScaleDirection:R(s(e,"LastScaleDirection","lastScaleDirection"),"n/a"),lastScaleReason:R(s(e,"LastScaleReason","lastScaleReason"),"n/a")}:null}function K(e){const t=w(e)?e:{},a=s(t,"Lanes","lanes"),n=s(t,"TimestampUtc","timestampUtc"),i=new Date(typeof n=="string"?n:Date.now());return{timestampUtc:Number.isFinite(i.getTime())?i:new Date,currentBackend:R(s(t,"CurrentBackend","currentBackend"),"unknown"),hits:o(s(t,"Hits","hits")),misses:o(s(t,"Misses","misses")),setCalls:o(s(t,"SetCalls","setCalls")),removeCalls:o(s(t,"RemoveCalls","removeCalls")),fallbackToMemory:o(s(t,"FallbackToMemory","fallbackToMemory")),redisBreakerOpened:o(s(t,"RedisBreakerOpened","redisBreakerOpened")),stampedeKeyRejected:o(s(t,"StampedeKeyRejected","stampedeKeyRejected")),stampedeLockWaitTimeout:o(s(t,"StampedeLockWaitTimeout","stampedeLockWaitTimeout")),stampedeFailureBackoffRejected:o(s(t,"StampedeFailureBackoffRejected","stampedeFailureBackoffRejected")),hitRate:o(s(t,"HitRate","hitRate")),spill:oe(s(t,"Spill","spill")),autoscaler:le(s(t,"Autoscaler","autoscaler")),lanes:Array.isArray(a)?a.map(ie):[],readsPerSec:0,writesPerSec:0,fallbacksPerSec:0}}function m(e,t){r.connState.textContent=e,r.connState.className=`badge ${t}`;const a={ok:["var(--accent)","0 0 0 6px rgba(52, 211, 153, 0.12)"],warn:["var(--warn)","0 0 0 6px rgba(245, 158, 11, 0.16)"],danger:["var(--danger)","0 0 0 6px rgba(239, 68, 68, 0.12)"]};r.dot.style.background=a[t][0],r.dot.style.boxShadow=a[t][1]}function ce(e){const t=e.autoscaler;if(!t){r.autoscaler.innerHTML="<div class='muted'>No autoscaler diagnostics present.</div>";return}r.autoscaler.innerHTML=`
    <div class="meta-row"><span>Connections</span><span>${l(t.currentConnections)} / target ${l(t.targetConnections)}</span></div>
    <div class="meta-row"><span>Lanes (R/W)</span><span>${l(t.currentReadLanes)} / ${l(t.currentWriteLanes)}</span></div>
    <div class="meta-row"><span>Queue (avg/max)</span><span>${t.avgQueueDepth.toFixed(2)} / ${l(t.maxQueueDepth)}</span></div>
    <div class="meta-row"><span>Timeout rate</span><span>${t.timeoutRatePerSec.toFixed(2)} /s</span></div>
    <div class="meta-row"><span>Rolling latency</span><span>p95 ${t.rollingP95LatencyMs.toFixed(2)} ms | p99 ${t.rollingP99LatencyMs.toFixed(2)} ms</span></div>
    <div class="meta-row"><span>Frozen</span><span class="${t.frozen?"warn":"ok"}">${t.frozen?"yes":"no"}</span></div>
    <div class="meta-row"><span>Last scale</span><span>${$(t.lastScaleDirection)}</span></div>
    <div class="meta-row"><span>Reason</span><span>${$(t.lastScaleReason)}</span></div>`}function de(e){const t=e.spill;if(!t){r.spill.innerHTML="<div class='muted'>No spill diagnostics present.</div>";return}r.spill.innerHTML=`
    <div class="meta-row"><span>Mode</span><span>${$(t.mode)}</span></div>
    <div class="meta-row"><span>Supports disk spill</span><span class="${t.supportsDiskSpill?"ok":"muted"}">${t.supportsDiskSpill?"yes":"no"}</span></div>
    <div class="meta-row"><span>Disk spill configured</span><span class="${t.spillToDiskConfigured?"ok":"muted"}">${t.spillToDiskConfigured?"yes":"no"}</span></div>
    <div class="meta-row"><span>Total spill files</span><span>${l(t.totalSpillFiles)}</span></div>
    <div class="meta-row"><span>Active shards</span><span>${l(t.activeShards)}</span></div>
    <div class="meta-row"><span>Max files in shard</span><span>${l(t.maxFilesInShard)}</span></div>
    <div class="meta-row"><span>Shard imbalance</span><span>${t.imbalanceRatio.toFixed(2)}</span></div>`}function ue(e){if(e.lanes.length===0){r.lanes.innerHTML="<tr><td colspan='13' class='muted'>No lane samples yet.</td></tr>";return}const t=[...e.lanes].sort((a,n)=>a.laneIndex-n.laneIndex);r.lanes.innerHTML=t.map(a=>{const n=a.healthy?"ok":"danger";return`<tr>
        <td>#${l(a.laneIndex)}</td>
        <td>${$(a.role)}</td>
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
      </tr>`}).join("")}function A(e,t,a){const n=e.getContext("2d");if(!n)return;const i=window.devicePixelRatio||1,d=Math.max(1,e.clientWidth),S=Math.max(1,e.clientHeight),g=Math.floor(d*i),k=Math.floor(S*i);(e.width!==g||e.height!==k)&&(e.width=g,e.height=k),n.clearRect(0,0,g,k),n.lineWidth=1,n.strokeStyle="rgba(145,163,182,0.20)";for(let u=0;u<=10;u+=1){const p=u/10*g;n.beginPath(),n.moveTo(p,0),n.lineTo(p,k),n.stroke()}for(let u=0;u<=6;u+=1){const p=u/6*k;n.beginPath(),n.moveTo(0,p),n.lineTo(g,p),n.stroke()}const _=Math.max(a,...t.flatMap(u=>u.values),1),O=8*i,H=8*i,ee=g-O*2,te=k-H*2;for(const u of t)if(u.values.length!==0){n.beginPath(),n.strokeStyle=u.color,n.lineWidth=2*i;for(let p=0;p<u.values.length;p+=1){const U=O+(u.values.length===1?0:p/(u.values.length-1))*ee,W=H+(1-Math.min(1,u.values[p]/_))*te;p===0?n.moveTo(U,W):n.lineTo(U,W)}n.stroke()}}function V(){const e=h.map(i=>i.hitRate*100),t=h.map(i=>i.readsPerSec),a=h.map(i=>i.writesPerSec);A(r.hitRateChart,[{values:e,color:"#38bdf8"}],100);const n=Math.max(25,...t,...a);A(r.throughputChart,[{values:t,color:"#34d399"},{values:a,color:"#f59e0b"}],n),r.historyCount.textContent=l(h.length)}function J(e){const t=e.hits+e.misses;r.backend.textContent=e.currentBackend,r.hitRate.textContent=re(e.hitRate),r.readsTotal.textContent=`reads: ${l(t)}`,r.readsPerSec.textContent=L(e.readsPerSec),r.hitsMisses.textContent=`hits ${l(e.hits)} | misses ${l(e.misses)}`,r.writesPerSec.textContent=L(e.writesPerSec),r.setsRemoves.textContent=`sets ${l(e.setCalls)} | removes ${l(e.removeCalls)}`,r.fallbacksPerSec.textContent=L(e.fallbacksPerSec),r.fallbackTotal.textContent=`fallback total: ${l(e.fallbackToMemory)}`,r.stampede.textContent=`${l(e.stampedeKeyRejected)} / ${l(e.stampedeLockWaitTimeout)} / ${l(e.stampedeFailureBackoffRejected)}`,r.breakerOpens.textContent=`breaker opens: ${l(e.redisBreakerOpened)}`,r.lastSample.textContent=`last sample ${e.timestampUtc.toISOString()}`;const a=w(P)?s(P,"CircuitBreaker","circuitBreaker")??null:null;if(a){const n=C(s(a,"IsForcedOpen","isForcedOpen")),i=C(s(a,"IsOpen","isOpen")),d=R(s(a,"Reason","reason"),"none"),S=n?"forced-open":i?"open":"closed",g=n||i?"warn":"ok";r.breakerState.innerHTML=`breaker: <span class="${g}">${S}</span> (${$(d)})`}else r.breakerState.textContent="breaker: status unavailable"}async function X(e){const t=await fetch(e,{cache:"no-store",headers:{accept:"application/json"}});if(!t.ok)throw new Error(`HTTP ${t.status}`);return t.json()}function G(e){if(!y){if(f){const t=Math.max(.001,(e.timestampUtc.getTime()-f.timestampUtc.getTime())/1e3),a=f.hits+f.misses,n=e.hits+e.misses,i=f.setCalls+f.removeCalls,d=e.setCalls+e.removeCalls;e.readsPerSec=Math.max(0,(n-a)/t),e.writesPerSec=Math.max(0,(d-i)/t),e.fallbacksPerSec=Math.max(0,(e.fallbackToMemory-f.fallbackToMemory)/t)}f=e,h.push(e),h.length>ne&&h.shift(),J(e),ce(e),de(e),ue(e),V()}}async function F(){try{const e=await X(j);G(K(e)),m("polling snapshot","warn")}catch{m("snapshot failed","danger")}}async function Y(){try{const e=await X(q);P=w(e)?e:null,!y&&h.length>0&&J(h[h.length-1])}catch{P=null}}function I(){b&&(clearInterval(b),b=null)}function N(){I(),F(),b=setInterval(()=>{F()},1e3)}function D(){v&&(v.close(),v=null)}function z(){x||(x=setTimeout(()=>{x=null,T=Math.min(T*2,15e3),Z()},T))}function Z(){D(),I(),m("connecting","warn");try{v=new EventSource(Q)}catch{m("stream unavailable","danger"),N(),z();return}v.onopen=()=>{T=500,I(),m("streaming","ok")};const e=t=>{try{const a=JSON.parse(t.data);G(K(a)),m("streaming","ok")}catch{m("stream payload error","danger")}};v.addEventListener("vapecache-stats",e),v.onmessage=e,v.onerror=()=>{m("reconnecting","warn"),D(),N(),z()}}function pe(){h.length=0,f=null,V(),r.lastSample.textContent="history cleared",r.lanes.innerHTML="<tr><td colspan='13' class='muted'>No lane samples yet.</td></tr>"}r.pauseButton.addEventListener("click",()=>{y=!y,r.pauseButton.textContent=y?"Resume":"Pause",y?m("paused","warn"):v&&m("streaming","ok")});r.pullButton.addEventListener("click",()=>{F()});r.resetButton.addEventListener("click",pe);M=setInterval(()=>{Y()},5e3);Y();Z();window.addEventListener("beforeunload",()=>{D(),b&&(clearInterval(b),b=null),M&&(clearInterval(M),M=null),x&&(clearTimeout(x),x=null)});
