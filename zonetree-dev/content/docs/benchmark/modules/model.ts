export interface BenchmarkPhase {
  Name: string
  Operations: number
  ElapsedMs: number
  OperationsPerSecond: number
  Checksum: string
}

export interface BenchmarkResult {
  Engine: string
  Durability: string
  StartedAtUtc: string
  Workload: Record<string, number>
  EngineSettings: Record<string, string>
  Completed: boolean
  Status: string | null
  Phases: BenchmarkPhase[]
  RunElapsedMs: number
  PreReadStabilizeMs: number | null
  PostUpdateStabilizeMs: number | null
  SettleMs: number | null
  StorageSizeBytes: number | null
  ReopenMs: number | null
  VerifyMs: number | null
  VerifyChecksum: string | null
  PeakProcessWorkingSetBytes: number | null
  Environment: string
  InterruptedPhase: string | null
}

export interface BenchmarkChartAsset {
  title: string
  url: string
}

export const chartDefinitions = [
  { title: 'Execution Time', file: 'latest-execution-time.svg' },
  { title: 'Write Throughput', file: 'latest-write-throughput.svg' },
  { title: 'Lookup Throughput', file: 'latest-lookup-throughput.svg' },
  { title: 'Index Scan Throughput', file: 'latest-index-scan-throughput.svg' },
  { title: 'Query Throughput', file: 'latest-query-throughput.svg' },
  { title: 'Resource Footprint', file: 'latest-resources.svg' },
]

export interface BenchmarkReport {
  id: string
  platform: string
  profiles: number
  parallelism: number
  startedAt: string
  version: string | null
  url: string
  charts: BenchmarkChartAsset[]
}

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Expected a JSON object.')
  return Object.fromEntries(Object.entries(value))
}

function text(value: unknown): string {
  if (typeof value !== 'string') throw new Error('Expected a text field.')
  return value
}

function number(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw new Error('Expected a nonnegative finite number.')
  return value
}

function optionalNumber(value: unknown): number | null {
  return value === null || value === undefined ? null : number(value)
}

function optionalText(value: unknown): string | null {
  return value === null || value === undefined ? null : text(value)
}

export function parseResults(value: unknown): BenchmarkResult[] {
  if (!Array.isArray(value) || !value.length) throw new Error('A report must contain at least one engine result.')
  const results = value.map((item: unknown) => {
    const r = record(item)
    const workload = Object.fromEntries(Object.entries(record(r.Workload)).map(([key, value]) => [key, number(value)]))
    workload.Parallelism ??= 1
    if (!(workload.Profiles > 0) || !(workload.Parallelism > 0)) throw new Error('Profiles and parallelism must be positive.')
    if (typeof r.Completed !== 'boolean' || !Array.isArray(r.Phases)) throw new Error('Invalid result status or phases.')
    const phases = r.Phases.map((item: unknown): BenchmarkPhase => {
      const p = record(item)
      return { Name: text(p.Name), Operations: number(p.Operations), ElapsedMs: number(p.ElapsedMs), OperationsPerSecond: number(p.OperationsPerSecond), Checksum: text(p.Checksum) }
    })
    if (new Set(phases.map(p => p.Name)).size !== phases.length) throw new Error('Duplicate phase names.')
    return {
      Engine: text(r.Engine), Durability: text(r.Durability), StartedAtUtc: text(r.StartedAtUtc),
      Workload: workload,
      EngineSettings: Object.fromEntries(Object.entries(record(r.EngineSettings)).map(([key, value]) => [key, text(value)])),
      Completed: r.Completed, Status: optionalText(r.Status), Phases: phases,
      RunElapsedMs: number(r.RunElapsedMs),
      PreReadStabilizeMs: optionalNumber(r.PreReadStabilizeMs), PostUpdateStabilizeMs: optionalNumber(r.PostUpdateStabilizeMs),
      SettleMs: optionalNumber(r.SettleMs), StorageSizeBytes: optionalNumber(r.StorageSizeBytes),
      ReopenMs: optionalNumber(r.ReopenMs), VerifyMs: optionalNumber(r.VerifyMs), VerifyChecksum: optionalText(r.VerifyChecksum),
      PeakProcessWorkingSetBytes: optionalNumber(r.PeakProcessWorkingSetBytes), Environment: text(r.Environment),
      InterruptedPhase: optionalText(r.InterruptedPhase),
    }
  })
  if (new Set(results.map(r => r.Engine)).size !== results.length) throw new Error('Duplicate engine names.')
  return results
}

export function parseCatalog(value: unknown): BenchmarkReport[] {
  if (!Array.isArray(value) || !value.length) throw new Error('No benchmark reports are available.')
  return value.map((item: unknown) => {
    const r = record(item)
    if (!Array.isArray(r.charts)) throw new Error('Expected report charts.')
    const charts = r.charts.map((item: unknown) => {
      const chart = record(item)
      return { title: text(chart.title), url: text(chart.url) }
    })
    return { id: text(r.id), platform: text(r.platform), profiles: number(r.profiles), parallelism: number(r.parallelism), startedAt: text(r.startedAt), version: optionalText(r.version), url: text(r.url), charts }
  })
}

const integerFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 })
const decimalFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 })
export const integer = (value: number) => integerFormat.format(value)
export const decimal = (value: number) => decimalFormat.format(value)
export const seconds = (value: number | null) => value === null ? '—' : `${decimal(value / 1000)} s`
export const throughput = (value: number | null) => value === null ? '—' : `${integer(value)}/s`
export const profileLabel = (value: number) => value >= 1e6 ? `${decimal(value / 1e6)}M` : `${decimal(value / 1e3)}K`
export const platformLabel = (value: string) => ({ win: 'Windows', linux: 'Linux' })[value] ?? value
export const phaseTime = (r: BenchmarkResult) => r.Phases.reduce((sum, p) => sum + p.ElapsedMs, 0)
export const status = (r: BenchmarkResult) => r.Completed ? 'Completed' : r.Status || 'Incomplete'

export function zoneTreeVersion(results: BenchmarkResult[]): string | null {
  const environment = results.find(r => r.Engine === 'ZoneTree')?.Environment
  if (environment === undefined) return null
  // The Linux reference commits predate version metadata and use release-v1.9.4.
  return environment.match(/^[ \t]*(?:\*[ \t]*)?ZoneTree version:[ \t]*(\S[^\r\n]*)/mi)?.[1].trim() || '1.9.4.0'
}

export function compareVersions(a: string, b: string): number {
  const left = a.split('.').map(Number)
  const right = b.split('.').map(Number)
  for (let i = 0; i < Math.max(left.length, right.length); i++) {
    const difference = (left[i] ?? 0) - (right[i] ?? 0)
    if (difference) return difference
  }
  return 0
}

export function bytes(value: number | null): string {
  if (value === null) return '—'
  const unit = Math.min(4, Math.floor(Math.log2(Math.max(1, value)) / 10))
  return `${decimal(value / 1024 ** unit)} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}`
}

export function writeReadiness(result: BenchmarkResult, phaseName: string): number | null {
  const phase = result.Phases.find(p => p.Name === phaseName)
  if (!phase) return null
  const stabilize = phaseName === 'insert profiles' ? result.PreReadStabilizeMs : result.PostUpdateStabilizeMs
  const elapsed = phase.ElapsedMs + (stabilize ?? 0)
  return elapsed > 0 ? phase.Operations * 1000 / elapsed : null
}

export function correctness(results: BenchmarkResult[]): { tone: string; message: string } {
  const completed = results.filter(r => r.Completed)
  if (completed.length < 2) return { tone: 'warning', message: 'At least two completed engines are needed for checksum comparison.' }
  const first = completed[0]
  const comparableWorkload = (r: BenchmarkResult) => JSON.stringify(Object.entries(r.Workload).filter(([key]) => key !== 'TimeoutSeconds').sort(([a], [b]) => a.localeCompare(b)))
  if (completed.some(r => comparableWorkload(r) !== comparableWorkload(first))) return { tone: 'warning', message: 'Engine workloads differ. These results cannot be validated as equivalent work.' }
  if (completed.some(r => r.Phases.length !== first.Phases.length || first.Phases.some(p => !r.Phases.some(other => other.Name === p.Name && other.Operations === p.Operations)))) return { tone: 'warning', message: 'Completed engines have different phase coverage or operation counts.' }
  if (completed.some(r => first.Phases.some(p => r.Phases.find(other => other.Name === p.Name)?.Checksum !== p.Checksum))) return { tone: 'danger', message: 'Phase checksum mismatch. These results did not produce equivalent output.' }
  if (!first.Phases.length || completed.some(r => !r.VerifyChecksum || r.Phases.some(p => !p.Checksum))) return { tone: 'warning', message: 'Verification data is missing; correctness is unverified.' }
  if (completed.some(r => r.VerifyChecksum !== first.VerifyChecksum)) return { tone: 'danger', message: 'Final verification checksum mismatch.' }
  const excluded = results.length - completed.length
  return { tone: 'success', message: `Phase checksums, operation counts, and final verification match across ${completed.length} completed engines.${excluded ? ` ${excluded} incomplete engine(s) excluded.` : ''}` }
}

export interface ChartMetric { value: string; label: string; suffix: string }
export const totalMetrics: ChartMetric[] = [
  { value: 'phases', label: 'Completed phase time · lower is better', suffix: ' s' },
  { value: 'run', label: 'Full run time · lower is better', suffix: ' s' },
  { value: 'storage', label: 'Storage · lower is better', suffix: ' MiB' },
  { value: 'memory', label: 'Process peak memory · lower is better', suffix: ' MiB' },
  { value: 'insert-ready', label: 'Insert + stabilization · higher is better', suffix: '/s' },
  { value: 'update-ready', label: 'Update + stabilization · higher is better', suffix: '/s' },
]

export function metricValue(result: BenchmarkResult, metric: string): number | null {
  if (metric === 'phases') return phaseTime(result) / 1000
  if (metric === 'run') return result.RunElapsedMs / 1000
  if (metric === 'storage') return result.StorageSizeBytes === null ? null : result.StorageSizeBytes / 1024 ** 2
  if (metric === 'memory') return result.PeakProcessWorkingSetBytes === null ? null : result.PeakProcessWorkingSetBytes / 1024 ** 2
  if (metric === 'insert-ready') return writeReadiness(result, 'insert profiles')
  if (metric === 'update-ready') return writeReadiness(result, 'update profiles')
  return result.Phases.find(p => `phase:${p.Name}` === metric)?.OperationsPerSecond ?? null
}
