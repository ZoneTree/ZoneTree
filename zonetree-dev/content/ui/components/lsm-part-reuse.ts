import { defineComponent, html } from 'regor'

export function defineLsmPartReuse() {
  return defineComponent(html`
    <figure class="zt-lsm-figure zt-lsm-reuse" aria-labelledby="zt-lsm-reuse-caption">
      <div class="zt-lsm-figure-heading"><span class="zt-lsm-overline">COMPACTION WITH PART REUSE</span><Icon name="tabler:layers-linked"/></div>
      <p class="zt-lsm-reuse-label">Current disk segment</p>
      <div class="zt-lsm-reuse-parts"><span><strong>A</strong><small>00–24</small></span><span class="zt-lsm-part-affected"><strong>B</strong><small>25–49</small></span><span><strong>C</strong><small>50–74</small></span><span><strong>D</strong><small>75–99</small></span></div>
      <div class="zt-lsm-reuse-incoming"><Icon name="tabler:git-merge"/><span>Incoming updates overlap the range in B.</span></div>
      <div class="zt-lsm-reuse-arrows" aria-hidden="true"><span><Icon name="tabler:arrow-down"/> Reuse</span><span><Icon name="tabler:arrow-down"/> Rewrite</span><span><Icon name="tabler:arrow-down"/> Reuse</span><span><Icon name="tabler:arrow-down"/> Reuse</span></div>
      <div class="zt-lsm-reuse-parts"><span><strong>A</strong><small>Carried</small></span><span class="zt-lsm-part-new"><strong>B′</strong><small>New output</small></span><span><strong>C</strong><small>Carried</small></span><span><strong>D</strong><small>Carried</small></span></div>
      <p class="zt-lsm-reuse-label">Next disk segment · reused parts + new output</p>
      <figcaption id="zt-lsm-reuse-caption">An illustrative local update. Eligible clean parts can be carried into the next logical segment without rewriting their records. A changed range may produce one or several replacement parts.</figcaption>
    </figure>
  `)
}
