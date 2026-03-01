# Aspire Lane Query Pack

Use this pack to visualize mux lane skew immediately in the Aspire Metrics explorer.
All queries map to the lane metrics emitted by `VapeCache.Redis`.

## Panel Pack

1. Lane Inflight Utilization
- Metric: `redis.mux.lane.inflight.utilization`
- Chart: line
- Aggregation: last
- Group by: `connection.id`
- Y-axis: ratio (`0..1`)
- Goal: spot saturated or starved lanes quickly.

2. Lane Inflight Count
- Metric: `redis.mux.lane.inflight`
- Chart: line
- Aggregation: last
- Group by: `connection.id`
- Goal: detect persistent in-flight imbalance between lanes.

3. Lane Write Throughput
- Metric: `redis.mux.lane.bytes.sent`
- Chart: line
- Aggregation: rate/sec
- Group by: `connection.id`
- Unit: bytes/sec
- Goal: find write-heavy lanes and skew.

4. Lane Read Throughput
- Metric: `redis.mux.lane.bytes.received`
- Chart: line
- Aggregation: rate/sec
- Group by: `connection.id`
- Unit: bytes/sec
- Goal: confirm response load is balanced.

5. Lane Operation Rate
- Metric: `redis.mux.lane.operations`
- Chart: line
- Aggregation: rate/sec
- Group by: `connection.id`
- Unit: operations/sec
- Goal: compare scheduling fairness.

6. Lane Failure Rate
- Metric: `redis.mux.lane.failures`
- Chart: line
- Aggregation: rate/sec
- Group by: `connection.id`
- Unit: failures/sec
- Goal: isolate unhealthy transport lanes.

## PromQL Equivalents

If OTLP is exported to Prometheus/Grafana, these are ready:

```promql
# Inflight utilization per lane
redis_mux_lane_inflight_utilization

# Inflight count per lane
redis_mux_lane_inflight

# Lane write/read throughput (bytes/s)
sum by (connection_id) (rate(redis_mux_lane_bytes_sent_total[1m]))
sum by (connection_id) (rate(redis_mux_lane_bytes_received_total[1m]))

# Lane ops and failures rate
sum by (connection_id) (rate(redis_mux_lane_operations_total[1m]))
sum by (connection_id) (rate(redis_mux_lane_failures_total[1m]))

# Simple skew index: max lane ops rate / avg lane ops rate
max(rate(redis_mux_lane_operations_total[1m]))
/
avg(rate(redis_mux_lane_operations_total[1m]))
```

## Oscilloscope Look

For the old-school oscilloscope feel in Aspire:

1. Use line charts for the six panels above.
2. Keep time window tight (`30s` to `2m`).
3. Group by `connection.id` and keep legend visible.
4. Use a dark canvas and bright series colors (green/cyan/amber/red).

For a true neon scope-style UI (grid + glow traces), use the SSE example in:
- `docs/BLAZOR_DASHBOARD_EXAMPLE.md` (`Oscilloscope Lane Graph` section).
