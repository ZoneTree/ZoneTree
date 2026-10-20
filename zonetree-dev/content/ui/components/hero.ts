import { defineComponent, html } from 'regor'

export function defineStorageHero() {
  const segmentFrame = html`<rect class="zt-segment-body" x="154" y="96" width="252" height="60" rx="9"/>
<path
  d="M168 109H392"
  stroke="currentColor"
  stroke-opacity=".25"
  stroke-width="3"
  stroke-linecap="round"/>
<rect
  x="168"
  y="122"
  width="224"
  height="20"
  rx="4"
  fill="currentColor"
  opacity=".05"/>
<path
  d="M204 122V142M242 122V142M280 122V142M318 122V142M356 122V142"
  stroke="currentColor"
  stroke-opacity=".2"/>`
  const records = html`<rect
  x="168"
  y="122"
  width="224"
  height="20"
  rx="4"
  fill="currentColor"
  opacity=".22"/>
<path
  d="M176 132H194M214 132H232M252 132H270M290 132H308M328 132H346M366 132H384"
  stroke="currentColor"
  stroke-width="2"
  stroke-linecap="round"/>`
  const readOnlySegment = html`<rect class="zt-segment-body" x="154" width="252" height="24" rx="6"/>
<rect
  x="168"
  y="7"
  width="224"
  height="10"
  rx="3"
  fill="currentColor"
  opacity=".22"/>
<path
  d="M176 12H194M214 12H232M252 12H270M290 12H308M328 12H346M366 12H384"
  stroke="currentColor"
  stroke-width="2"
  stroke-linecap="round"/>`
  const mergedRecords = html`<rect
  width="50"
  height="12"
  rx="3"
  fill="currentColor"
  fill-opacity=".2"
  stroke="currentColor"/>
<path
  d="M7 6H13M21 6H28M36 6H43"
  stroke="currentColor"
  stroke-width="2"
  stroke-linecap="round"/>`
  const queryPackets = ['mutable', 'readonly', 'disk'].map(layer => html`<circle
  class="zt-motion zt-query-packet zt-query-${layer}"
  r="2.75"/>
<circle
  class="zt-motion zt-query-packet zt-query-${layer} zt-query-follow"
  r="2.75"/>
<rect
  class="zt-motion zt-query-packet zt-query-${layer} zt-query-result"
  x="-4"
  y="-2"
  width="8"
  height="4"
  rx="2"/>
<rect
  class="zt-motion zt-query-packet zt-query-${layer} zt-query-result zt-query-follow"
  x="-4"
  y="-2"
  width="8"
  height="4"
  rx="2"/>`).join('')
  return defineComponent(html`<section class="zt-hero" aria-labelledby="zt-title">
  <div class="zt-hero-copy">
    <p class="zt-eyebrow">
      <span class="zt-status-dot"></span> FASTEST ORDERED STORAGE FOR .NET
    </p>
    <h1 id="zt-title">Your data.<br/><span>In order.</span></h1>
    <p class="zt-hero-description">
      An ordered key-value engine that runs inside your .NET application. Write
      at speed, scan by range, and keep control of how your data is stored.
    </p>
    <div class="zt-actions">
      <BtnLink
        href="/docs/getting-started/"
        tone="accent"
        variant="solid"
        icon="tabler:arrow-right"
        iconPosition="end"
        >Start building</BtnLink
      >
      <BtnLink
        href="https://github.com/ZoneTree/ZoneTree"
        target="_blank"
        rel="noopener"
        tone="neutral"
        variant="outline"
        icon="tabler:brand-github"
        >Explore the source</BtnLink
      >
    </div>
    <div class="zt-facts">
      <span>.NET 6–10</span><span>Open source · MIT</span
      ><span>Runs in your process</span>
    </div>
  </div>
  <figure class="zt-engine" aria-labelledby="zt-engine-caption">
    <div class="zt-engine-heading">
      <span><Icon name="iconoir:cube"/> Inside the engine</span>
      <span class="zt-engine-index">01 / STORAGE</span>
    </div>
    <div class="zt-engine-art">
      <svg
        viewBox="0 0 560 450"
        fill="none"
        role="img"
        aria-labelledby="zt-engine-title zt-engine-description"
        focusable="false"
      >
        <title id="zt-engine-title">
          ZoneTree: writes, compaction, and queries in parallel
        </title>
        <desc id="zt-engine-description">
          Parallel writes fill the mutable segment. At capacity, the entire
          segment moves into the read-only queue and a new mutable segment
          accepts writes. The read-only layer starts empty and fills with
          segments 1, 2, and 3. Once all three have arrived, background
          compaction merges them with the existing disk segment into a
          replacement multipart disk segment. Parallel live queries continue
          across the layers throughout.
        </desc>
        <defs>
          <pattern
            id="zt-grid"
            width="20"
            height="20"
            patternUnits="userSpaceOnUse"
          >
            <circle cx="1" cy="1" r=".7" fill="currentColor" opacity=".13"/>
          </pattern>
          <clipPath id="zt-current-capacity" clipPathUnits="userSpaceOnUse">
            <rect
              class="zt-motion zt-current-fill zt-capacity"
              x="168"
              y="122"
              width="224"
              height="20"/>
          </clipPath>
        </defs>
        <rect width="560" height="450" fill="url(#zt-grid)"/>
        <!-- Independent writers converge only at the current mutable segment. -->
        <g class="zt-write-tracks" stroke="currentColor" stroke-width="1.25">
          <path d="M24 78H76Q98 78 116 100L154 118"/>
          <path d="M24 106H86Q108 106 126 120L154 128"/>
          <path d="M24 134H96Q120 134 154 138"/>
          <path
            d="M148 114L154 118L147 120M148 124L154 128L147 130M148 134L154 138L148 142"/>
        </g>
        <g fill="currentColor">
          <rect class="zt-writer" x="24" y="74" width="20" height="8" rx="3"/>
          <rect class="zt-writer" x="24" y="102" width="20" height="8" rx="3"/>
          <rect class="zt-writer" x="24" y="130" width="20" height="8" rx="3"/>
          <rect
            class="zt-motion zt-write-packet zt-write-one"
            x="-4"
            y="-2"
            width="8"
            height="4"
            rx="2"/>
          <rect
            class="zt-motion zt-write-packet zt-write-two"
            x="-4"
            y="-2"
            width="8"
            height="4"
            rx="2"/>
          <rect
            class="zt-motion zt-write-packet zt-write-three"
            x="-4"
            y="-2"
            width="8"
            height="4"
            rx="2"/>
        </g>
        <!-- A fresh mutable stays writable while the full segment moves. -->
        <rect
          class="zt-mutable-slot"
          x="154"
          y="96"
          width="252"
          height="60"
          rx="9"/>
        <g class="zt-active-mutable">
          ${segmentFrame}
          <g clip-path="url(#zt-current-capacity)">${records}</g>
        </g>
        <!-- Three distinct runs converge into compaction together. -->
        <g class="zt-motion zt-readonly-segment zt-readonly-three">
          ${readOnlySegment}
          <text class="zt-segment-number" x="136" y="17">3</text>
        </g>
        <g class="zt-motion zt-readonly-segment zt-readonly-two">
          ${readOnlySegment}
          <text class="zt-segment-number" x="136" y="17">2</text>
        </g>
        <g class="zt-motion zt-readonly-segment zt-readonly-one">
          ${readOnlySegment}
          <text class="zt-segment-number" x="136" y="17">1</text>
        </g>
        <path
          class="zt-rollover-guide"
          d="M134 160V225M130 219L134 225L138 219"
          stroke="currentColor"
          stroke-width="1.25"/>
        <g class="zt-motion zt-rollover zt-rollover-one">
          ${segmentFrame}${records}
          <rect
            class="zt-full-indicator"
            x="374"
            y="105"
            width="18"
            height="7"
            rx="3.5"
            fill="currentColor"/>
        </g>
        <g class="zt-motion zt-rollover zt-rollover-two">
          ${segmentFrame}${records}
          <rect
            class="zt-full-indicator"
            x="374"
            y="105"
            width="18"
            height="7"
            rx="3.5"
            fill="currentColor"/>
        </g>
        <g class="zt-motion zt-rollover zt-rollover-three">
          ${segmentFrame}${records}
          <rect
            class="zt-full-indicator"
            x="374"
            y="105"
            width="18"
            height="7"
            rx="3.5"
            fill="currentColor"/>
        </g>
        <!-- Read-only runs and the existing disk feed the replacement disk. -->
        <g class="zt-compaction" stroke="currentColor" stroke-width="1.25">
          <path
            class="zt-maintenance-track"
            d="M190 292V298Q190 316 210 316H230M280 292V305M370 292V298Q370 316 350 316H330"/>
          <path
            class="zt-maintenance-track"
            d="M154 394H134Q122 394 122 382V330Q122 316 138 316H230"
            stroke-dasharray="3 4"/>
          <path
            class="zt-maintenance-track"
            d="M280 328V334M280 334H210Q200 334 200 344V354M280 334V354M280 334H350Q360 334 360 344V354"/>
          <rect
            class="zt-compaction-body"
            x="230"
            y="305"
            width="100"
            height="23"
            rx="11.5"/>
        </g>
        <g
          class="zt-motion zt-compaction zt-compaction-flow"
          fill="currentColor"
        >
          <circle class="zt-motion zt-merge-packet zt-merge-existing" r="2.5"/>
        </g>
        <!-- One logical disk segment contains ordered immutable parts. -->
        <rect
          class="zt-disk-body"
          x="154"
          y="354"
          width="252"
          height="72"
          rx="10"/>
        <path
          class="zt-disk-bracket"
          d="M166 434V440H394V434M242 434V440M318 434V440"
          stroke="currentColor"
          stroke-opacity=".35"/>
        <g class="zt-disk-parts">
          <g class="zt-disk-part">
            <rect
              class="zt-part-body"
              x="164"
              y="380"
              width="70"
              height="36"
              rx="5"/>
            <text x="175" y="393">PART 01</text>
            <path d="M175 404H183M188 404H198M203 404H222"/>
            <rect
              class="zt-motion zt-part-update zt-part-one"
              x="164"
              y="380"
              width="70"
              height="36"
              rx="5"/>
          </g>
          <g class="zt-disk-part">
            <rect
              class="zt-part-body"
              x="245"
              y="380"
              width="70"
              height="36"
              rx="5"/>
            <text x="256" y="393">PART 02</text>
            <path d="M256 404H264M269 404H279M284 404H303"/>
            <rect
              class="zt-motion zt-part-update zt-part-two"
              x="245"
              y="380"
              width="70"
              height="36"
              rx="5"/>
          </g>
          <g class="zt-disk-part">
            <rect
              class="zt-part-body"
              x="326"
              y="380"
              width="70"
              height="36"
              rx="5"/>
            <text x="337" y="393">PART 03</text>
            <path d="M337 404H345M350 404H360M365 404H384"/>
            <rect
              class="zt-motion zt-part-update zt-part-three"
              x="326"
              y="380"
              width="70"
              height="36"
              rx="5"/>
          </g>
        </g>
        <!-- Compacted records visibly fan out and land inside the disk parts. -->
        <g class="zt-compaction">
          <g class="zt-motion zt-output-run zt-output-one">
            ${mergedRecords}
          </g>
          <g class="zt-motion zt-output-run zt-output-two">
            ${mergedRecords}
          </g>
          <g class="zt-motion zt-output-run zt-output-three">
            ${mergedRecords}
          </g>
        </g>
        <!-- Independent query lanes carry overlapping requests and results throughout. -->
        <g class="zt-query" stroke="currentColor" stroke-width="1.25">
          <path class="zt-query-track" d="M526 78H454V128H406"/>
          <path class="zt-query-track" d="M526 100H478V238H406"/>
          <path class="zt-query-track" d="M526 122H502V394H406"/>
          <circle class="zt-query-port" cx="406" cy="128" r="3"/>
          <circle class="zt-query-port" cx="406" cy="238" r="3"/>
          <circle class="zt-query-port" cx="406" cy="394" r="3"/>
          <path
            d="M413 124L407 128L413 132M413 234L407 238L413 242M413 390L407 394L413 398"/>
        </g>
        <g class="zt-query" fill="currentColor">
          <rect
            class="zt-query-client"
            x="518"
            y="74"
            width="16"
            height="8"
            rx="3"/>
          <rect
            class="zt-query-client"
            x="518"
            y="96"
            width="16"
            height="8"
            rx="3"/>
          <rect
            class="zt-query-client"
            x="518"
            y="118"
            width="16"
            height="8"
            rx="3"/>
          ${queryPackets}
        </g>
      </svg>
      <div class="zt-engine-label zt-engine-writes" aria-hidden="true">
        <span>PARALLEL WRITES</span>
      </div>
      <div class="zt-engine-label zt-engine-memory" aria-hidden="true">
        <span>01 / MUTABLE SEGMENT</span>
      </div>
      <div class="zt-engine-label zt-engine-rollover" aria-hidden="true">
        <strong>When full</strong><span>moves to read-only</span>
      </div>
      <div class="zt-engine-label zt-engine-readonly" aria-hidden="true">
        <span>02 / READ-ONLY SEGMENTS</span>
      </div>
      <div class="zt-engine-label zt-engine-disk" aria-hidden="true">
        <span>03 / DISK SEGMENT</span>
      </div>
      <div class="zt-engine-compaction" aria-hidden="true">Compaction</div>
      <div class="zt-engine-label zt-engine-query" aria-hidden="true">
        <strong>Live queries</strong><span>In parallel</span>
      </div>
    </div>
    <div class="zt-engine-concurrency" aria-hidden="true">
      <span><i></i> Parallel writes</span><span><i></i> Live queries</span
      ><span><i></i> Compaction</span>
    </div>
    <figcaption id="zt-engine-caption">
      <span>LSM-tree architecture</span
      ><a href="/docs/concepts/lsm-tree/"
        >Follow the data <Icon name="tabler:arrow-up-right"/></a>
    </figcaption>
  </figure>
</section>`)
}
