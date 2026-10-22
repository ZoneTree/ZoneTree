import { defineComponent, html } from 'regor'

export function defineLsmReadPath() {
  return defineComponent(html`
    <figure class="zt-lsm-figure zt-lsm-lookup" aria-labelledby="zt-lsm-lookup-caption">
      <div class="zt-lsm-figure-heading"><span class="zt-lsm-overline">NEWEST VISIBLE VALUE WINS</span><code>TryGet(42)</code></div>
      <ol class="zt-lsm-lookup-steps">
        <li><span class="zt-lsm-stage-number">01</span><div><strong>Mutable segment</strong><span>Key 42 is absent</span></div><span class="zt-lsm-lookup-result">Continue <Icon name="tabler:arrow-down"/></span></li>
        <li class="zt-lsm-lookup-hit"><span class="zt-lsm-stage-number">02</span><div><strong>Newest read-only segment</strong><span>42 → "current"</span></div><span class="zt-lsm-lookup-result"><Icon name="tabler:check"/> Return value</span></li>
        <li class="zt-lsm-lookup-hidden"><span class="zt-lsm-stage-number">03</span><div><strong>Disk segment</strong><span>42 → "previous"</span></div><span class="zt-lsm-lookup-result">Not visited</span></li>
        <li class="zt-lsm-lookup-hidden"><span class="zt-lsm-stage-number">04</span><div><strong>Bottom segments</strong><span>42 → "original"</span></div><span class="zt-lsm-lookup-result">Not visited</span></li>
      </ol>
      <figcaption id="zt-lsm-lookup-caption">A single point lookup stops at its first matching record. Independent lookups can run concurrently. A matching deletion marker returns “not found” and also stops the search.</figcaption>
    </figure>
  `)
}
