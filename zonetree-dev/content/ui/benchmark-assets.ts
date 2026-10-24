import { createHash } from 'node:crypto'
import { readFile, readdir } from 'node:fs/promises'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { definePlugin } from 'purestack'
import { chartDefinitions, parseResults, zoneTreeVersion, type BenchmarkChartAsset, type BenchmarkReport } from '../docs/benchmark/modules/model'

async function findReports(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true })
  const nested = await Promise.all(entries.map(entry => entry.isDirectory()
    ? findReports(join(directory, entry.name))
    : entry.name === 'latest.json' ? [join(directory, entry.name)] : []))
  return nested.flat().sort()
}

// Publish the original JSON and SVGs directly from the benchmark references.
export const benchmarkAssetsPlugin = definePlugin({
  name: 'benchmark-json',
  assets: config => ({
    namespace: 'benchmarks',
    async processPage(input) {
      const explorer = input.document.querySelector('[data-benchmark-catalog]')
      if (!explorer) return
      const root = resolve(config.contentDir, '../../benchmarks/profile-store/reference')
      const catalog: BenchmarkReport[] = []
      for (const file of await findReports(root)) {
        const source = await readFile(file)
        let results
        try { results = parseResults(JSON.parse(source.toString('utf8'))) }
        catch (error) { throw new Error(`Invalid benchmark report ${file}: ${error instanceof Error ? error.message : error}`) }
        const first = results[0]
        const id = relative(root, file).split(sep).slice(0, -1).join('/')
        const hash = createHash('sha256').update(source).digest('hex').slice(0, 16)
        const url = await input.emit(`${id}/${hash}.json`, source)
        const charts: BenchmarkChartAsset[] = []
        for (const chart of chartDefinitions) {
          const svg = await readFile(join(dirname(file), chart.file))
          const hash = createHash('sha256').update(svg).digest('hex').slice(0, 16)
          charts.push({ title: chart.title, url: await input.emit(`${id}/${hash}-${chart.file}`, svg) })
        }
        catalog.push({ id, platform: id.split('/')[0], profiles: first.Workload.Profiles, parallelism: first.Workload.Parallelism, startedAt: first.StartedAtUtc, version: zoneTreeVersion(results), url, charts })
      }
      catalog.sort((a, b) => a.platform.localeCompare(b.platform) || a.profiles - b.profiles || a.parallelism - b.parallelism)
      if (!catalog.length) throw new Error(`No benchmark JSON found in ${root}`)
      const data = Buffer.from(JSON.stringify(catalog))
      const hash = createHash('sha256').update(data).digest('hex').slice(0, 16)
      explorer.setAttribute('data-benchmark-catalog', await input.emit(`catalog-${hash}.json`, data))
    },
  }),
})
