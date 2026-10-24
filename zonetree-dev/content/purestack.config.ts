import { defineConfig } from 'purestack'
import { zoneTreeDocsPlugin } from './ui/plugin'
import { benchmarkAssetsPlugin } from './ui/benchmark-assets'

export default defineConfig({ plugins: [zoneTreeDocsPlugin, benchmarkAssetsPlugin] })
