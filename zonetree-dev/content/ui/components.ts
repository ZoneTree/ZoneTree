import { defineComponent, html } from 'regor'
import { defineStorageHero } from './hero'

export interface DocCard {
  number: string
  title: string
  summary: string
  href: string
  icon: string
  label: string
}

export interface StackTechnology {
  name: string
  role: string
  summary: string
  website: string
  repository: string
  icon: string
}

export function defineDocsComponents() {
  return {
    StorageHero: defineStorageHero(),
    StackTechnology: defineComponent<StackTechnology>(
      html`<article class="zt-technology">
        <div class="docs-card-meta"><span>{{ role }}</span><Icon :name="icon"/></div>
        <h3>{{ name }}</h3>
        <p>{{ summary }}</p>
        <div class="zt-technology-actions">
          <a class="zt-text-link" :href="website" target="_blank" rel="noopener" :aria-label="name + ' documentation'">Documentation <Icon name="tabler:arrow-up-right"/></a>
          <a class="zt-text-link" :href="repository" target="_blank" rel="noopener" :aria-label="name + ' source on GitHub'">Source <Icon name="tabler:brand-github"/></a>
        </div>
      </article>`,
      { props: ['name', 'role', 'summary', 'website', 'repository', 'icon'] },
    ),
    DocCard: defineComponent<DocCard>(
      html`<a class="docs-card" :href="href">
        <div class="docs-card-meta"><span>{{ number }}</span><Icon :name="icon"/></div>
        <h3>{{ title }}</h3>
        <p>{{ summary }}</p>
        <span class="docs-card-link">{{ label }} <Icon name="tabler:arrow-up-right"/></span>
      </a>`,
      { props: ['number', 'title', 'summary', 'href', 'icon', 'label'] },
    ),
    AnalyticsSpotlight: defineComponent(html`
      <aside class="zt-analytics" aria-labelledby="zt-analytics-title">
        <div class="zt-analytics-copy">
          <p class="zt-eyebrow"><Icon name="iconoir:cube-hole"/> BUILT ON ZONETREE</p>
          <h2 id="zt-analytics-title">Storage becomes insight.</h2>
          <p><strong>ZoneTree.Analytics</strong> brings website analytics, product events, and service telemetry into your .NET application. Built on ZoneTree, with a dashboard you can host and data that stays on your infrastructure.</p>
          <a class="zt-text-link" href="https://analytics.zonetree.dev/" target="_blank" rel="noopener">Explore ZoneTree.Analytics <Icon name="tabler:arrow-up-right"/></a>
        </div>
        <div class="zt-analytics-visual" aria-hidden="true">
          <div class="zt-analytics-wordmark"><Icon name="iconoir:cube-hole"/><span>ZoneTree<strong>.Analytics</strong></span></div>
          <div class="zt-analytics-flow">
            <span>Events</span><span class="zt-flow-line"></span><Icon name="iconoir:cube"/><span class="zt-flow-line"></span><span>Insight</span>
          </div>
          <div class="zt-analytics-topics"><span>Website</span><span>Product</span><span>Telemetry</span></div>
        </div>
      </aside>
    `),
  }
}
