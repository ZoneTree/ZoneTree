import { defineComponent, html } from 'regor'

export function defineAnalyticsSpotlight() {
  return defineComponent(html`<aside class="zt-analytics" aria-labelledby="zt-analytics-title">
  <div class="zt-analytics-copy">
    <p class="zt-eyebrow">
      <Icon name="iconoir:cube-hole"/> BUILT ON ZONETREE
    </p>
    <h2 id="zt-analytics-title">Storage becomes insight.</h2>
    <p>
      <strong>ZoneTree.Analytics</strong> brings website analytics, product
      events, and service telemetry into your .NET application. Built on
      ZoneTree, with a dashboard you can host and data that stays on your
      infrastructure.
    </p>
    <a
      class="zt-text-link"
      href="https://analytics.zonetree.dev/"
      target="_blank"
      rel="noopener"
      >Explore ZoneTree.Analytics <Icon name="tabler:arrow-up-right"/></a>
  </div>
  <div class="zt-analytics-visual" aria-hidden="true">
    <div class="zt-analytics-wordmark">
      <Icon name="iconoir:cube-hole"/><span
        >ZoneTree<strong>.Analytics</strong></span>
    </div>
    <div class="zt-analytics-flow">
      <span>Events</span><span class="zt-flow-line"></span><Icon name="iconoir:cube"/><span class="zt-flow-line"></span><span>Insight</span>
    </div>
    <div class="zt-analytics-topics">
      <span>Website</span><span>Product</span><span>Telemetry</span>
    </div>
  </div>
</aside>`)
}
