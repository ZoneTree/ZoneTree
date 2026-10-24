import { computed, observe, onMounted, onUnmounted, persist, ref, sref } from 'regor'
import { parseCatalog, parseResults, platformLabel, profileLabel, type BenchmarkReport, type BenchmarkResult } from './model'

const defaultReportId = 'win/profiles-2000000-p16'

async function readJson(url: string, signal: AbortSignal): Promise<unknown> {
  const response = await fetch(url, { signal })
  if (!response.ok) throw new Error(`Could not load the report (HTTP ${response.status}).`)
  return response.json()
}

export class BenchmarkState {
  readonly catalog = sref<BenchmarkReport[]>([])
  readonly results = sref<BenchmarkResult[]>([])
  readonly platform = ref('')
  readonly reportId = persist(ref(defaultReportId), 'zonetree:benchmark:report')
  readonly loading = ref(true)
  readonly error = ref('')
  readonly metric = ref('phases')
  readonly selected = computed(() => this.catalog().find(r => r.id === this.reportId()))
  readonly platforms = computed(() => [...new Set(this.catalog().map(r => r.platform))].map(value => ({ value, label: platformLabel(value) })))
  readonly reports = computed(() => this.catalog().filter(r => r.platform === this.platform()).map(r => ({
    value: r.id, label: `${profileLabel(r.profiles)} profiles · ${r.parallelism === 1 ? 'single thread' : `${r.parallelism} threads`}`,
  })))
  private request?: AbortController
  private readonly cache = new Map<string, BenchmarkResult[]>()

  constructor(readonly catalogUrl: string) {
    observe(this.platform, () => {
      const current = this.catalog().find(r => r.id === this.reportId())
      if (current?.platform !== this.platform()) this.reportId(this.catalog().find(r => r.platform === this.platform())?.id ?? '')
    })
    observe(this.reportId, () => { void this.loadReport() })
    onMounted(() => {
      void this.loadCatalog(this.restoreSelection)
      window.addEventListener('popstate', this.restoreSelection)
    })
    onUnmounted(() => {
      this.request?.abort()
      window.removeEventListener('popstate', this.restoreSelection)
    })
  }

  readonly retry = () => {
    if (this.catalog().length) void this.loadReport()
    else void this.loadCatalog(this.restoreSelection)
  }

  private readonly restoreSelection = () => {
    const requested = new URL(location.href).searchParams.get('report')
    const selected = this.catalog().find(r => r.id === requested)
      ?? this.catalog().find(r => r.id === this.reportId())
      ?? this.catalog().find(r => r.id === defaultReportId)
      ?? this.catalog()[0]
    if (!selected) return
    const unchanged = this.reportId() === selected.id
    this.reportId(selected.id)
    this.platform(selected.platform)
    // A persisted selection may already match, so no change observer will run.
    if (unchanged) void this.loadReport()
  }

  private async loadCatalog(select: () => void) {
    const request = new AbortController()
    this.request?.abort()
    this.request = request
    this.loading(true)
    this.error('')
    try {
      this.catalog(parseCatalog(await readJson(this.catalogUrl, request.signal)))
      select()
    } catch (error) {
      if (request.signal.aborted) return
      this.error(error instanceof Error ? error.message : 'Could not load benchmark reports.')
      this.loading(false)
    }
  }

  private async loadReport() {
    // Read the source refs here: observers can run before dependent computed
    // values have invalidated when a select updates its model synchronously.
    const selected = this.catalog().find(r => r.id === this.reportId())
    if (!selected) return
    this.request?.abort()
    const request = new AbortController()
    this.request = request
    this.loading(true)
    this.error('')
    this.results([])
    const url = new URL(location.href)
    url.searchParams.set('report', selected.id)
    history.replaceState(null, '', url)
    try {
      const results = this.cache.get(selected.id) ?? parseResults(await readJson(selected.url, request.signal))
      if (request.signal.aborted) return
      this.cache.set(selected.id, results)
      this.results(results)
      if (this.metric().startsWith('phase:') && !results.some(r => r.Phases.some(p => `phase:${p.Name}` === this.metric()))) this.metric('phases')
    } catch (error) {
      if (!request.signal.aborted) this.error(error instanceof Error ? error.message : 'Could not load the selected report.')
    } finally {
      if (!request.signal.aborted) this.loading(false)
    }
  }
}
