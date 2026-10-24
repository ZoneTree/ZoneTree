import { computed, defineComponent, html } from 'regor'
import { integer, seconds, throughput } from './model'
import { BenchmarkState } from './state'

export class BenchmarkDetails {
  readonly engines = computed(() => this.report.results().map(r => ({
    ...r,
    settings: Object.entries(r.EngineSettings).map(([name, value]) => ({ name, value })),
    workload: Object.entries(r.Workload).map(([name, value]) => ({ name, value: integer(value) })),
    environment: r.Environment.split(/\r?\n/).filter(Boolean).map(line => line.replace(/^\*\s*/, '')),
  })))
  readonly integer = integer
  readonly seconds = seconds
  readonly throughput = throughput
  constructor(readonly report: BenchmarkState) {}
}

export function defineBenchmarkDetails() {
  return defineComponent<BenchmarkDetails>(html`<section aria-labelledby="benchmark-details-title">
  <h2 id="benchmark-details-title">Configuration &amp; evidence</h2>
  <ExpandablePanel
    r-for="r in engines"
    :title="r.Engine"
    :description="r.StartedAtUtc"
    class="zt-report-engine"
  >
    <p>{{ r.Durability }}</p>
    <p r-if="r.InterruptedPhase">
      <strong>Interrupted phase:</strong> {{ r.InterruptedPhase }}
    </p>
    <h3>Workload</h3>
    <dl class="zt-report-facts">
      <template r-for="setting in r.workload"
        ><dt>{{ setting.name }}</dt>
        <dd>{{ setting.value }}</dd></template>
    </dl>
    <h3>Engine settings</h3>
    <dl class="zt-report-facts">
      <template r-for="setting in r.settings"
        ><dt>{{ setting.name }}</dt>
        <dd>{{ setting.value }}</dd></template>
    </dl>
    <h3>Environment</h3>
    <ul>
      <li r-for="line in r.environment">{{ line }}</li>
    </ul>
    <h3>Operations &amp; checksums</h3>
    <div
      class="zt-report-table"
      role="region"
      :aria-label="r.Engine + ' phase evidence'"
      tabindex="0"
    >
      <table>
        <thead>
          <tr>
            <th scope="col">Phase</th>
            <th scope="col">Operations</th>
            <th scope="col">Elapsed</th>
            <th scope="col">Throughput</th>
            <th scope="col">Checksum</th>
          </tr>
        </thead>
        <tbody>
          <tr r-for="p in r.Phases">
            <th scope="row">{{ p.Name }}</th>
            <td>{{ integer(p.Operations) }}</td>
            <td>{{ seconds(p.ElapsedMs) }}</td>
            <td>{{ throughput(p.OperationsPerSecond) }}</td>
            <td><code>{{ p.Checksum }}</code></td>
          </tr>
        </tbody>
      </table>
    </div>
    <p>Final verification: <code>{{ r.VerifyChecksum || 'Not recorded' }}</code></p>
  </ExpandablePanel>
</section>`, { context: head => new BenchmarkDetails(head.requireContext(BenchmarkState)) })
}
