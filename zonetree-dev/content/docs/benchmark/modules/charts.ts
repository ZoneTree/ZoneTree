import { defineComponent, html } from 'regor'
import { BenchmarkState } from './state'

export interface BenchmarkCharts {
  report: BenchmarkState
}

export function defineBenchmarkCharts() {
  return defineComponent<BenchmarkCharts>(html`<section class="zt-report-charts" aria-labelledby="benchmark-charts-title">
  <h2 id="benchmark-charts-title">Charts</h2>
  <p class="zt-report-note">
    Select a chart to enlarge it and browse all six charts.
  </p>
  <ImageGallery :images="report.selected.charts" label="Benchmark charts"/>
</section>`, { context: head => ({ report: head.requireContext(BenchmarkState) }) })
}
