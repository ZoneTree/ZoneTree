import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, test } from 'vitest'
import { bytes, correctness, metricValue, parseResults, writeReadiness, zoneTreeVersion } from '../content/docs/benchmark/modules/model.ts'
import { humanizeChartValue, humanizeNumber } from '../content/docs/benchmark/modules/format.ts'

const reference = new URL('../../benchmarks/profile-store/reference/', import.meta.url)
const fixture = () => parseResults(JSON.parse(readFileSync(new URL('win/profiles-100000/latest.json', reference), 'utf8')))

test('all retained reports parse and validate, including legacy single-thread workloads', () => {
  let reports = 0
  for (const platform of readdirSync(reference)) {
    for (const directory of readdirSync(new URL(`${platform}/`, reference))) {
      const file = join(fileURLToPath(reference), platform, directory, 'latest.json')
      const results = parseResults(JSON.parse(readFileSync(file, 'utf8')))
      expect(correctness(results).tone, file).toBe('success')
      expect(results.every(r => r.Workload.Parallelism >= 1)).toBe(true)
      reports++
    }
  }
  expect(reports).toBeGreaterThanOrEqual(21)
})

test('phase and final checksum mismatches cannot pass verification', () => {
  const results = fixture()
  results[1].Phases[0].Checksum = 'WRONG'
  expect(correctness(results).tone).toBe('danger')
  results[1].Phases[0].Checksum = results[0].Phases[0].Checksum
  results[1].VerifyChecksum = 'WRONG'
  expect(correctness(results).tone).toBe('danger')
})

test('different workloads, missing phases, and missing evidence are unverified', () => {
  const workload = fixture()
  workload[1].Workload.QueryLimit++
  expect(correctness(workload).tone).toBe('warning')
  const phases = fixture()
  phases[1].Phases.pop()
  expect(correctness(phases).tone).toBe('warning')
  const missing = fixture()
  missing[1].VerifyChecksum = null
  expect(correctness(missing).tone).toBe('warning')
})

test('incomplete engines are excluded explicitly instead of failing or passing silently', () => {
  const results = fixture()
  results[2].Completed = false
  results[2].Phases = []
  const validation = correctness(results)
  expect(validation.tone).toBe('success')
  expect(validation.message).toMatch(/1 incomplete engine/)
  results[1].Completed = false
  expect(correctness(results).tone).toBe('warning')
})

test('write readiness includes stabilization and handles missing phases', () => {
  const result = fixture()[0]
  result.Phases[0].ElapsedMs = 1000
  result.PreReadStabilizeMs = 1000
  expect(writeReadiness(result, 'insert profiles')).toBe(50000)
  result.Phases = []
  expect(writeReadiness(result, 'insert profiles')).toBeNull()
})

test('missing metrics stay missing while measured zeroes remain zero', () => {
  const result = fixture()[0]
  result.StorageSizeBytes = null
  expect(metricValue(result, 'storage')).toBeNull()
  result.StorageSizeBytes = 0
  expect(metricValue(result, 'storage')).toBe(0)
  expect(metricValue(result, 'phase:not measured')).toBeNull()
  expect(bytes(null)).toBe('—')
  expect(bytes(0)).toBe('0 B')
})

test('malformed reports and duplicate engine names are rejected', () => {
  expect(() => parseResults([])).toThrow()
  expect(() => parseResults([{ Engine: 'ZoneTree' }])).toThrow()
  const results = fixture()
  results[0].RunElapsedMs = Number.NaN
  expect(() => parseResults(results)).toThrow()
  const duplicate = fixture()
  duplicate[1].Engine = duplicate[0].Engine
  expect(() => parseResults(duplicate)).toThrow(/Duplicate engine/)
})

test('the version comes from the selected ZoneTree result, not a different engine', () => {
  const results = fixture()
  expect(zoneTreeVersion(results)).toBe('2.0.4.0')
  results[0].Environment = '* ZoneTree version: 1.9.6.0\r\n* CPU: example'
  expect(zoneTreeVersion(results)).toBe('1.9.6.0')
  results[0].Environment = '* .NET: 10.0.6'
  expect(zoneTreeVersion(results)).toBe('1.9.4.0')
  results[0].Environment = '* ZoneTree version: \r\n* CPU: example'
  expect(zoneTreeVersion(results)).toBe('1.9.4.0')
  expect(zoneTreeVersion(results.slice(1))).toBeNull()
  const linux = parseResults(JSON.parse(readFileSync(new URL('linux/profiles-100000/latest.json', reference), 'utf8')))
  expect(zoneTreeVersion(linux)).toBe('1.9.4.0')
})

test('compact quantities round cleanly across magnitude boundaries', () => {
  expect(humanizeNumber(0)).toBe('0')
  expect(humanizeNumber(999)).toBe('999')
  expect(humanizeNumber(1500)).toBe('1.5K')
  expect(humanizeNumber(3419647.05)).toBe('3.42M')
  expect(humanizeNumber(999999)).toBe('1M')
  expect(humanizeNumber(-1250000000)).toBe('-1.25B')
  expect(humanizeNumber(1e12)).toBe('1T')
  expect(humanizeNumber(Number.NaN)).toBe('—')
})

test('chart labels humanize throughput, duration, and memory units', () => {
  expect(humanizeChartValue(3419647.05, '/s')).toBe('3.42M/s')
  expect(humanizeChartValue(0, ' s')).toBe('0 s')
  expect(humanizeChartValue(0.05737, ' s')).toBe('57.37 ms')
  expect(humanizeChartValue(85.913, ' s')).toBe('1m 25.91s')
  expect(humanizeChartValue(59.999, ' s')).toBe('1m 0s')
  expect(humanizeChartValue(3720, ' s')).toBe('1h 2m')
  expect(humanizeChartValue(2048, ' MiB')).toBe('2 GiB')
  expect(humanizeChartValue(0.5, ' MiB')).toBe('512 KiB')
})
