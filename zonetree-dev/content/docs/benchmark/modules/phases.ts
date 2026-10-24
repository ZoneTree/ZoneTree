import { computed, defineComponent, html } from 'regor'
import { seconds, throughput } from './model'
import { BenchmarkState } from './state'

export class BenchmarkPhases {
  readonly rows = computed(() => [...new Set(this.report.results().flatMap(r => r.Phases.map(p => p.Name)))].map(name => ({
    name,
    engines: this.report.results().map(r => {
      const phase = r.Phases.find(p => p.Name === name)
      return { engine: r.Engine, time: seconds(phase?.ElapsedMs ?? null), rate: throughput(phase?.OperationsPerSecond ?? null) }
    }),
  })))
  constructor(readonly report: BenchmarkState) {}
}

export function defineBenchmarkPhases() {
  return defineComponent<BenchmarkPhases>(html`<section aria-labelledby="benchmark-phases-title">
  <h2 id="benchmark-phases-title">Every measured phase</h2>
  <div
    class="zt-report-table"
    role="region"
    aria-label="Phase throughput and elapsed time by engine"
    tabindex="0"
  >
    <table>
      <caption>
        Throughput (higher is better), followed by elapsed time
      </caption>
      <thead>
        <tr>
          <th scope="col">Phase</th>
          <th scope="col" r-for="r in report.results">
            {{ r.Engine }}
          </th>
        </tr>
      </thead>
      <tbody>
        <tr r-for="row in rows">
          <th scope="row">{{ row.name }}</th>
          <td r-for="cell in row.engines">
            <strong>{{ cell.rate }}</strong><small>{{ cell.time }}</small>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</section>`, { context: head => new BenchmarkPhases(head.requireContext(BenchmarkState)) })
}
