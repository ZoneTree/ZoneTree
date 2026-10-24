import { defineComponent, html } from 'regor'
import { BenchmarkState } from './state'

export interface BenchmarkSelectors { report: BenchmarkState }

export function defineBenchmarkSelectors() {
  return defineComponent<BenchmarkSelectors>(html`<div class="zt-report-selectors" aria-label="Choose a benchmark report">
  <FormSelectField
    id="benchmark-platform"
    label="Platform"
    :model="report.platform"
    :options="report.platforms"/>
  <FormSelectField
    id="benchmark-workload"
    label="Workload"
    :model="report.reportId"
    :options="report.reports"/>
</div>`, { context: head => ({ report: head.requireContext(BenchmarkState) }) })
}
