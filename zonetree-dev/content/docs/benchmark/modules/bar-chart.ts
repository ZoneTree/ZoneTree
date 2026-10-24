import { defineBarChartComponents, type BarChart } from '@purestack/ts-components'
import { computed, defineComponent, unref } from 'regor'
import { humanizeChartValue } from './format'

export interface BenchmarkBarChart extends BarChart {}

// Reuse PureStack's chart geometry and template, formatting only its labels.
export function defineBenchmarkBarChart() {
  const native = defineBarChartComponents().barChart
  const template = document.createElement('template')
  template.content.appendChild(native.template.cloneNode(true))
  return defineComponent<BenchmarkBarChart>({ element: template }, {
    props: native.props,
    context: head => {
      const chart = native.context(head)
      const format = (value: number) => humanizeChartValue(value, unref(head.props.valueSuffix) ?? '')
      return {
        ...chart,
        chartItems: computed(() => (unref(chart.chartItems) ?? []).map(item => ({ ...item, formattedValue: format(item.value) }))),
        gridLines: computed(() => (unref(chart.gridLines) ?? []).map(line => ({ ...line, formattedValue: format(line.value) }))),
      }
    },
  })
}
