import {
  defineAlertComponents, defineButtonComponents, defineExpandablePanelComponents,
  defineFlexComponents, defineFormSelectField, defineIconComponents, definePanelComponents, defineBadgeComponents,
} from '@purestack/ts-components'
import { iconoir_nav_arrow_down, lucide_chevron_down } from '@purestack/ts-svg-icons'
import { computed, createApp, defineComponent, html, unref } from 'regor'
import { defineBenchmarkBarChart } from './bar-chart'
import { defineBenchmarkChart } from './chart'
import { defineBenchmarkCharts } from './charts'
import { defineBenchmarkDetails } from './details'
import { defineBenchmarkOverview } from './overview'
import { defineBenchmarkPhases } from './phases'
import { defineBenchmarkSelectors } from './selectors'
import { compareVersions, integer, platformLabel, profileLabel, zoneTreeVersion } from './model'
import { BenchmarkState } from './state'
import { defineImageGallery } from '../../../ui/components/image-gallery'

export class BenchmarkExplorer extends BenchmarkState {
  readonly version = computed(() => zoneTreeVersion(this.results()))
  readonly latestVersion = computed(() => this.catalog().flatMap(report => report.version ? [report.version] : []).sort(compareVersions).at(-1) ?? null)
  readonly outdated = computed(() => {
    const version = this.version(), latest = this.latestVersion()
    return !!version && !!latest && compareVersions(version, latest) < 0
  })
  readonly integer = integer
  readonly platformLabel = platformLabel
  readonly profileLabel = profileLabel
}

export function defineBenchmarkExplorer() {
  return defineComponent<BenchmarkExplorer>(html`<div class="zt-report" :aria-busy="loading">
  <header r-if="selected" class="zt-report-heading">
    <h1>Profiles {{ profileLabel(selected.profiles) }} P{{ integer(selected.parallelism) }}</h1>
    <div class="zt-report-actions">
      <span>{{ platformLabel(selected.platform) }}</span>
      <a :href="selected.url" download="benchmark.json">Download JSON</a>
      <Badge
        r-if="!loading && !error"
        tone="accent"
        variant="solid"
        class="zt-report-version"
        >{{ version ? 'ZoneTree v' + version : 'ZoneTree · version not recorded' }}</Badge>
    </div>
  </header>
  <BenchmarkSelectors/>
  <p r-if="loading" role="status">Loading benchmark results…</p>
  <div r-if="error" role="alert" class="zt-report-error">
    <p>{{ error }}</p>
    <Btn @click="retry">Try again</Btn>
  </div>
  <template r-if="!loading && !error && selected">
    <AlertBox
      r-if="outdated"
      tone="warning"
      title="Older ZoneTree benchmark"
      role="status"
      class="zt-report-version-warning"
    >
      This benchmark uses <strong>ZoneTree v{{ version }}</strong>.
      The newest version in these reports is
      <strong>v{{ latestVersion }}</strong>. Results may differ between
      versions.
    </AlertBox>
    <BenchmarkCharts/>
    <BenchmarkChart/>
    <BenchmarkOverview/>
    <BenchmarkPhases/>
    <BenchmarkDetails/>
  </template>
</div>`, { props: ['catalogUrl'], context: head => new BenchmarkExplorer(unref(head.props.catalogUrl)) })
}

const element = document.getElementById('benchmark-explorer')
if (element) {
  const catalogUrl = element.getAttribute('data-benchmark-catalog')
  if (catalogUrl) createApp({
    catalogUrl,
    components: {
      ...defineAlertComponents(), ...defineButtonComponents(), ...defineExpandablePanelComponents(),
      ...defineFlexComponents(), ...defineFormSelectField(), ...definePanelComponents(), ...defineBadgeComponents(),
      ...defineIconComponents(name => ({ 'lucide:chevron-down': lucide_chevron_down, 'iconoir:nav-arrow-down': iconoir_nav_arrow_down })[name] ?? ''),
      BenchmarkExplorer: defineBenchmarkExplorer(), BenchmarkSelectors: defineBenchmarkSelectors(),
      BenchmarkBarChart: defineBenchmarkBarChart(),
      BenchmarkOverview: defineBenchmarkOverview(), BenchmarkChart: defineBenchmarkChart(),
      BenchmarkCharts: defineBenchmarkCharts(),
      ImageGallery: defineImageGallery(),
      BenchmarkPhases: defineBenchmarkPhases(), BenchmarkDetails: defineBenchmarkDetails(),
    },
  }, { element, template: html`<BenchmarkExplorer :catalogUrl="catalogUrl"/>` })
}
