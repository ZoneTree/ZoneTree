import { definePlugin } from 'purestack'
import { defineDocsComponents } from './components'
import { zoneTreeSkin } from './skin'
import { registerDocsStyles } from './styles'

export const zoneTreeDocsPlugin = definePlugin({
  name: 'zonetree-docs',
  skins: { zonetree: zoneTreeSkin },
  components: () => defineDocsComponents(),
  hooks: { onConfigResolved: () => registerDocsStyles() },
})
