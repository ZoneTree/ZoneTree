import { defineStorageHero } from './hero'
import { defineStackTechnology } from './stack-technology'
import { defineDocCard } from './doc-card'
import { defineDistributedFoundation } from './distributed-foundation'
import { defineAnalyticsSpotlight } from './analytics-spotlight'
import { defineFullTextSearchSpotlight } from './full-text-search-spotlight'

export function defineDocsComponents() {
  return {
    StorageHero: defineStorageHero(),
    StackTechnology: defineStackTechnology(),
    DocCard: defineDocCard(),
    DistributedFoundation: defineDistributedFoundation(),
    AnalyticsSpotlight: defineAnalyticsSpotlight(),
    FullTextSearchSpotlight: defineFullTextSearchSpotlight(),
  }
}
