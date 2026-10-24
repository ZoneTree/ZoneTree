import { defineStorageHero } from './hero'
import { defineStackTechnology } from './stack-technology'
import { defineDocCard } from './doc-card'
import { defineDistributedFoundation } from './distributed-foundation'
import { defineAnalyticsSpotlight } from './analytics-spotlight'
import { defineFullTextSearchSpotlight } from './full-text-search-spotlight'
import { defineLsmArchitecture } from './lsm-architecture'
import { defineLsmReadPath } from './lsm-read-path'
import { defineLsmPartReuse } from './lsm-part-reuse'

export { defineImageGallery, type GalleryImage, type ImageGalleryProps } from './image-gallery'

export function defineDocsComponents() {
  return {
    StorageHero: defineStorageHero(),
    StackTechnology: defineStackTechnology(),
    DocCard: defineDocCard(),
    DistributedFoundation: defineDistributedFoundation(),
    AnalyticsSpotlight: defineAnalyticsSpotlight(),
    FullTextSearchSpotlight: defineFullTextSearchSpotlight(),
    LsmArchitecture: defineLsmArchitecture(),
    LsmReadPath: defineLsmReadPath(),
    LsmPartReuse: defineLsmPartReuse(),
  }
}
