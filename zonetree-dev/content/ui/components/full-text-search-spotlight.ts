import { defineComponent, html } from 'regor'

export function defineFullTextSearchSpotlight() {
  return defineComponent(html`
    <aside class="zt-fulltext" aria-labelledby="zt-fulltext-title">
      <div class="zt-fulltext-copy">
        <p class="zt-eyebrow"><Icon name="tabler:file-search"/> BUILT ON ZONETREE</p>
        <h2 id="zt-fulltext-title">Full-text search.<br/>Built into your app.</h2>
        <p><strong>ZoneTree.FullTextSearch</strong> adds persistent full-text search to your .NET application. Combine phrase searches, Boolean queries, and facet filters, with tokenization and normalization you can customize.</p>
        <div class="zt-fulltext-links">
          <a class="zt-text-link" href="https://github.com/ZoneTree/ZoneTree.FullTextSearch" target="_blank" rel="noopener">Explore ZoneTree.FullTextSearch <Icon name="tabler:arrow-up-right"/></a>
          <a class="zt-text-link" href="https://www.nuget.org/packages/ZoneTree.FullTextSearch/" target="_blank" rel="noopener">Get the NuGet package <Icon name="tabler:arrow-up-right"/></a>
        </div>
      </div>
      <figure class="zt-fulltext-visual" aria-labelledby="zt-fulltext-example">
        <figcaption id="zt-fulltext-example" class="zt-fulltext-wordmark">
          <Icon name="tabler:file-search"/><strong>ZoneTree<span>.FullTextSearch</span></strong>
        </figcaption>
        <div class="zt-fulltext-query">
          <Icon name="tabler:search"/>
          <div>
            <span class="zt-fulltext-label">QUERY</span>
            <code><span>"storage engine"</span><span class="zt-fulltext-operator">AND</span><span>category:docs</span></code>
          </div>
        </div>
        <div class="zt-fulltext-match">
          <Icon name="tabler:file-text"/>
          <div><span class="zt-fulltext-label">DOCS</span><strong>Choosing a <mark>storage engine</mark></strong></div>
        </div>
        <div class="zt-fulltext-capabilities"><span>Phrase search</span><span>Boolean logic</span><span>Facets</span></div>
      </figure>
    </aside>
  `)
}
