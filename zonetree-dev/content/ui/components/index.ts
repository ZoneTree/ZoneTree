import { defineStorageHero } from './hero'
import { defineStackTechnology } from './stack-technology'
import { defineDocCard } from './doc-card'
import { defineTransactionSpotlight } from './transaction-spotlight'
import { defineDistributedFoundation } from './distributed-foundation'
import { defineAnalyticsSpotlight } from './analytics-spotlight'
import { defineFullTextSearchSpotlight } from './full-text-search-spotlight'
import { defineLsmArchitecture } from './lsm-architecture'
import { defineLsmReadPath } from './lsm-read-path'
import { defineLsmPartReuse } from './lsm-part-reuse'
import { defineZoneTreeError } from './error-page'
import { defineErrorTree } from './error-tree'

export { defineImageGallery, type GalleryImage, type ImageGalleryProps } from './image-gallery'

export function defineDocsComponents() {
  return {
    StorageHero: defineStorageHero(),
    StackTechnology: defineStackTechnology(),
    DocCard: defineDocCard(),
    TransactionSpotlight: defineTransactionSpotlight(),
    DistributedFoundation: defineDistributedFoundation(),
    AnalyticsSpotlight: defineAnalyticsSpotlight(),
    FullTextSearchSpotlight: defineFullTextSearchSpotlight(),
    LsmArchitecture: defineLsmArchitecture(),
    LsmReadPath: defineLsmReadPath(),
    LsmPartReuse: defineLsmPartReuse(),
    ZoneTreeError: defineZoneTreeError(),
    ErrorTree: defineErrorTree(),
  }
}
