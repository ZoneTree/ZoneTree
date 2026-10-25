import { computed, defineComponent, html } from 'regor'
import { integer, seconds, throughput } from './model'
import { BenchmarkState } from './state'

export class BenchmarkDetails {
  readonly engines = computed(() => this.report.results().map(r => ({
    ...r,
    id: `benchmark-engine-${r.Engine.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`,
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
  return defineComponent<BenchmarkDetails>(html`<section class="zt-report-evidence" aria-labelledby="benchmark-details-title">
  <h2 id="benchmark-details-title">Configuration &amp; evidence</h2>
  <p class="zt-report-note">
    Expand an engine to inspect its workload, effective settings, environment,
    and checksum evidence.
  </p>
  <details
    r-for="r in engines"
    class="zt-report-engine"
    :aria-labelledby="r.id"
  >
    <summary><h3 :id="r.id">{{ r.Engine }}</h3></summary>
    <div class="zt-report-engine-body">
      <p class="zt-report-recorded">
        Recorded
        <time :datetime="r.StartedAtUtc">{{ r.StartedAtUtc }}</time>
      </p>
      <p>{{ r.Durability }}</p>
      <p r-if="r.InterruptedPhase">
        <strong>Interrupted phase:</strong> {{ r.InterruptedPhase }}
      </p>
      <div class="zt-report-engine-grid">
        <section>
          <h4>Workload</h4>
          <dl class="zt-report-facts">
            <template r-for="setting in r.workload"
              ><dt>{{ setting.name }}</dt>
              <dd>{{ setting.value }}</dd></template>
          </dl>
        </section>
        <section>
          <h4>Engine settings</h4>
          <dl class="zt-report-facts">
            <template r-for="setting in r.settings"
              ><dt>{{ setting.name }}</dt>
              <dd>{{ setting.value }}</dd></template>
          </dl>
        </section>
      </div>
      <h4>Environment</h4>
      <ul>
        <li r-for="line in r.environment">{{ line }}</li>
      </ul>
      <h4>Operations &amp; checksums</h4>
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
      <p class="zt-report-verification">
        Final verification: <code>{{ r.VerifyChecksum || 'Not recorded' }}</code>
      </p>
    </div>
  </details>
</section>`, { context: head => new BenchmarkDetails(head.requireContext(BenchmarkState)) })
}
