import { computed, defineComponent, html } from 'regor'
import { bytes, correctness, phaseTime, seconds, status } from './model'
import { BenchmarkState } from './state'

export class BenchmarkOverview {
  readonly validation = computed(() => correctness(this.report.results()))
  readonly bytes = bytes
  readonly seconds = seconds
  readonly phaseTime = phaseTime
  readonly status = status
  constructor(readonly report: BenchmarkState) {}
}

export function defineBenchmarkOverview() {
  return defineComponent<BenchmarkOverview>(html`<section aria-labelledby="benchmark-overview-title">
  <h2 id="benchmark-overview-title">Results at a glance</h2>
  <AlertBox :tone="validation.tone" title="Correctness"
    >{{ validation.message }}</AlertBox>
  <div
    class="zt-report-table"
    role="region"
    aria-label="Engine totals"
    tabindex="0"
  >
    <table>
      <caption>
        Engine totals · time and resource values are lower-is-better
      </caption>
      <thead>
        <tr>
          <th scope="col">Engine</th>
          <th scope="col">Status</th>
          <th scope="col">Phase time</th>
          <th scope="col">Full run</th>
          <th scope="col">Storage</th>
          <th scope="col">Peak memory</th>
        </tr>
      </thead>
      <tbody>
        <tr
          r-for="r in report.results"
          :class="{'zt-report-zonetree': r.Engine === 'ZoneTree'}"
        >
          <th scope="row">{{ r.Engine }}</th>
          <td>{{ status(r) }}</td>
          <td>{{ seconds(phaseTime(r)) }}</td>
          <td>{{ seconds(r.RunElapsedMs) }}</td>
          <td>{{ bytes(r.StorageSizeBytes) }}</td>
          <td>{{ bytes(r.PeakProcessWorkingSetBytes) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
  <p class="zt-report-note">
    Phase time sums measured workload phases. Full run time also includes setup,
    stabilization, settle, reopen, and verification. Incomplete runs contain
    only finished phases and cannot be compared as full workloads. MySQL peak
    memory excludes its server.
  </p>
  <ExpandablePanel title="Stabilization, reopen, and verification timings">
    <div
      class="zt-report-table"
      role="region"
      aria-label="Lifecycle timings"
      tabindex="0"
    >
      <table>
        <thead>
          <tr>
            <th scope="col">Engine</th>
            <th scope="col">Pre-read stabilize</th>
            <th scope="col">Post-update stabilize</th>
            <th scope="col">Settle</th>
            <th scope="col">Reopen</th>
            <th scope="col">Verify</th>
          </tr>
        </thead>
        <tbody>
          <tr r-for="r in report.results">
            <th scope="row">{{ r.Engine }}</th>
            <td>{{ seconds(r.PreReadStabilizeMs) }}</td>
            <td>{{ seconds(r.PostUpdateStabilizeMs) }}</td>
            <td>{{ seconds(r.SettleMs) }}</td>
            <td>{{ seconds(r.ReopenMs) }}</td>
            <td>{{ seconds(r.VerifyMs) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </ExpandablePanel>
</section>`, { context: head => new BenchmarkOverview(head.requireContext(BenchmarkState)) })
}
