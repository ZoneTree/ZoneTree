import { computed, defineComponent, html } from 'regor'
import { metricValue, totalMetrics } from './model'
import { BenchmarkState } from './state'

export class BenchmarkChart {
  readonly options = computed(() => [
    ...totalMetrics,
    ...[...new Set(this.report.results().flatMap(r => r.Phases.map(p => p.Name)))].map(name => ({ value: `phase:${name}`, label: `${name} · higher is better`, suffix: '/s' })),
  ])
  readonly current = computed(() => this.options().find(m => m.value === this.report.metric()) ?? totalMetrics[0])
  readonly items = computed(() => this.report.results().filter(r => r.Completed).flatMap(r => {
    const value = metricValue(r, this.report.metric())
    return value === null ? [] : [{ label: r.Engine, value, color: engineColor(r.Engine) }]
  }))
  constructor(readonly report: BenchmarkState) {}
}

function engineColor(engine: string) {
  return ({ ZoneTree: '#169b69', RocksDB: '#bd7935', SQLite: '#6189c4', MySQL: '#a17ac4' })[engine] ?? '#7d8794'
}

export function defineBenchmarkChart() {
  return defineComponent<BenchmarkChart>(html`<section aria-labelledby="benchmark-chart-title" class="zt-report-chart">
  <h2 id="benchmark-chart-title">Detailed comparison</h2>
  <FormSelectField
    id="benchmark-metric"
    label="Measurement"
    :model="report.metric"
    :options="options"/>
  <BenchmarkBarChart
    :items="items"
    :title="current.label"
    :ariaLabel="current.label"
    :valueSuffix="current.suffix"
    :animated="false"
    width="100%"/>
  <p class="zt-report-note">
    Charts include completed engines with a recorded value. Insert/update +
    stabilization measures write readiness by adding the following stabilization
    time to the write phase.
  </p>
</section>`, { context: head => new BenchmarkChart(head.requireContext(BenchmarkState)) })
}
