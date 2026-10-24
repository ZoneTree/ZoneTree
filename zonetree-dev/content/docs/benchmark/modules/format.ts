const magnitudes = ['', 'K', 'M', 'B', 'T']
const rounded = (value: number) => Number(value.toFixed(2)).toString()

export function humanizeNumber(value: number): string {
  if (!Number.isFinite(value)) return '—'
  const sign = value < 0 ? '-' : ''
  let scaled = Math.abs(value)
  let unit = 0
  while (scaled >= 1000 && unit < magnitudes.length - 1) {
    scaled /= 1000
    unit++
  }
  if (Number(scaled.toFixed(2)) >= 1000 && unit < magnitudes.length - 1) {
    scaled /= 1000
    unit++
  }
  return `${sign}${rounded(scaled)}${magnitudes[unit]}`
}

export function humanizeChartValue(value: number, suffix: string): string {
  if (!Number.isFinite(value)) return '—'
  if (suffix === ' s') {
    const seconds = Math.round(value * 100) / 100
    if (value > 0 && value < 1) return `${rounded(value * 1000)} ms`
    if (seconds < 60) return `${rounded(seconds)} s`
    const minutes = Math.floor(seconds / 60)
    if (minutes < 60) return `${minutes}m ${rounded(seconds % 60)}s`
    return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
  }
  if (suffix === ' MiB') {
    if (Math.abs(value) >= 1024) return `${rounded(value / 1024)} GiB`
    if (value > 0 && value < 1) return `${rounded(value * 1024)} KiB`
    return `${rounded(value)} MiB`
  }
  return `${humanizeNumber(value)}${suffix}`
}
