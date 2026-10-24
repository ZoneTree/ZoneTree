import { defineComponent, html } from 'regor'

export function defineLsmArchitecture() {
  return defineComponent(html`<figure
  class="zt-lsm-figure zt-lsm-pipeline"
  aria-labelledby="zt-lsm-pipeline-caption"
>
  <div class="zt-lsm-figure-heading">
    <span class="zt-lsm-overline">THE STORAGE PIPELINE</span>
    <span class="zt-lsm-figure-note">One ZoneTree instance</span>
  </div>
  <div class="zt-lsm-writers" aria-label="Concurrent application writers">
    <span><Icon name="tabler:arrow-down"/> Writer 01</span>
    <span><Icon name="tabler:arrow-down"/> Writer 02</span>
    <span><Icon name="tabler:arrow-down"/> Writer 03</span>
  </div>
  <div class="zt-lsm-stage zt-lsm-stage--mutable">
    <div class="zt-lsm-stage-heading">
      <span class="zt-lsm-stage-number">01</span><strong>Mutable segment</strong><span class="zt-lsm-medium">MEMORY</span>
    </div>
    <div class="zt-lsm-mutable-body">
      <div>
        <div class="zt-lsm-key-cells" aria-label="Keys in comparer order">
          <span>10</span><span>20</span><span>30</span><span>40</span><span>50</span>
        </div>
        <p>Concurrent, ordered B+Tree</p>
      </div>
      <div class="zt-lsm-wal">
        <Icon name="tabler:file-text"/>
        <div><strong>WAL</strong><span>Recent-write recovery</span></div>
      </div>
    </div>
  </div>
  <div class="zt-lsm-transfer">
    <Icon name="tabler:arrow-down"/><span
      >When full, move to read-only.<br/>New writes enter a fresh mutable
      segment.</span>
  </div>
  <div class="zt-lsm-stage">
    <div class="zt-lsm-stage-heading">
      <span class="zt-lsm-stage-number">02</span><strong>Read-only segments</strong><span class="zt-lsm-medium">MEMORY</span>
    </div>
    <div class="zt-lsm-batches">
      <span><strong>Batch 01</strong><small>Frozen</small></span><span><strong>Batch 02</strong><small>Frozen</small></span><span><strong>Batch 03</strong><small>Frozen · newest</small></span>
    </div>
    <p>Ordered batches waiting for compaction</p>
  </div>
  <div class="zt-lsm-compaction">
    <svg
      viewBox="0 0 600 52"
      preserveAspectRatio="none"
      fill="none"
      aria-hidden="true"
      focusable="false"
    >
      <path
        d="M100 0V12H500V0M300 12V34M100 52V34H500V52M300 34V52M96 46L100 52L104 46M296 46L300 52L304 46M496 46L500 52L504 46"
        stroke="currentColor"
        stroke-width="1.25"
        vector-effect="non-scaling-stroke"/>
    </svg>
    <span
      >Compact read-only batches + current disk segment.<br/>Publish the merged
      disk segment.</span>
  </div>
  <div class="zt-lsm-stage zt-lsm-stage--disk">
    <div class="zt-lsm-stage-heading">
      <span class="zt-lsm-stage-number">03</span><strong>Disk segment</strong><span class="zt-lsm-medium">DISK</span>
    </div>
    <div class="zt-lsm-parts">
      <span><strong>Part A</strong><small>10–29</small></span><span><strong>Part B</strong><small>30–49</small></span><span><strong>Part C</strong><small>50–69</small></span>
    </div>
    <p>One logical segment · ordered, immutable parts</p>
  </div>
  <div class="zt-lsm-transfer">
    <Icon name="tabler:arrow-down"/><span
      >When the merged record count exceeds<br/><code
        >DiskSegmentMaxItemCount</code></span>
  </div>
  <div class="zt-lsm-stage">
    <div class="zt-lsm-stage-heading">
      <span class="zt-lsm-stage-number">04</span><strong>Bottom segments</strong><span class="zt-lsm-medium">DISK</span>
    </div>
    <div class="zt-lsm-bottom-runs">
      <div>
        <strong>Newer disk segment</strong><span class="zt-lsm-mini-parts"><i></i><i></i><i></i></span>
      </div>
      <div>
        <strong>Older disk segment</strong><span class="zt-lsm-mini-parts"><i></i><i></i><i></i></span>
      </div>
    </div>
    <p>A queue of disk segments · compacted separately</p>
  </div>
  <div class="zt-lsm-concurrency">
    <Icon name="tabler:arrows-exchange"/><span
      >Parallel reads and writes run alongside background compaction.</span>
  </div>
  <figcaption id="zt-lsm-pipeline-caption">
    Vertical layers batch and move data. Horizontal parts divide a disk segment
    by key range. Keys and batches shown here are illustrative.
  </figcaption>
</figure>`)
}
