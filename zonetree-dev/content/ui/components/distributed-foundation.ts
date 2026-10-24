import { defineComponent, html } from 'regor'

export function defineDistributedFoundation() {
  return defineComponent(html`<section
  class="zt-section zt-distributed"
  aria-labelledby="zt-distributed-title"
>
  <div class="zt-distributed-layout">
    <div class="zt-distributed-copy">
      <p class="zt-eyebrow">DESIGNED TO SCALE OUT</p>
      <h2 id="zt-distributed-title">
        Build the node.<br/>Scale the database.
      </h2>
      <p>
        ZoneTree is designed to be the local storage engine inside a distributed
        database. Its ordered LSM-tree architecture and foundational APIs let a
        separate layer compose nodes into a scalable system, handling
        partitioning, replication, request routing, and cluster coordination.
      </p>
      <div class="zt-distributed-progress">
        <span>IN DEVELOPMENT</span>
        <p>
          We’re building a dedicated distribution layer on top of these
          foundations.
        </p>
      </div>
      <a
        class="zt-text-link"
        href="/docs/building-systems/partitioning-and-replication/"
        >Explore partitioning &amp; replication <Icon name="tabler:arrow-right"/></a>
    </div>
    <figure class="zt-cluster" aria-labelledby="zt-cluster-caption">
      <div class="zt-cluster-layer">
        <span class="zt-cluster-label">SEPARATE DISTRIBUTION LAYER</span>
        <strong>Connect and coordinate nodes.</strong>
        <div class="zt-cluster-responsibilities">
          <span>Partitioning</span><span>Replication</span><span>Routing</span><span>Coordination</span>
        </div>
      </div>
      <svg
        class="zt-cluster-connections"
        viewBox="0 0 480 56"
        preserveAspectRatio="none"
        fill="none"
        aria-hidden="true"
        focusable="false"
      >
        <path
          d="M240 0V22M80 56V22H400V56M240 22V56"
          stroke="currentColor"
          stroke-width="1.5"/>
        <circle cx="240" cy="22" r="3" fill="currentColor"/>
      </svg>
      <div class="zt-cluster-nodes">
        <div class="zt-cluster-node">
          <span>NODE 01</span><Icon name="iconoir:cube"/><strong
            >ZoneTree</strong><small>Local storage</small>
        </div>
        <div class="zt-cluster-node">
          <span>NODE 02</span><Icon name="iconoir:cube"/><strong
            >ZoneTree</strong><small>Local storage</small>
        </div>
        <div class="zt-cluster-node">
          <span>NODE 03</span><Icon name="iconoir:cube"/><strong
            >ZoneTree</strong><small>Local storage</small>
        </div>
      </div>
      <figcaption id="zt-cluster-caption">
        ZoneTree stores and queries data within each node.
      </figcaption>
    </figure>
  </div>
  <dl class="zt-distributed-primitives">
    <div>
      <dt>Ordered ranges &amp; data movement</dt>
      <dd>
        Range scans and snapshot iterators support partition export and
        rebuilds. Backup and restore APIs help seed new nodes.
      </dd>
    </div>
    <div>
      <dt>Operation indexes &amp; replica apply</dt>
      <dd>
        Write operation indexes and a replica-apply helper let your replication
        layer track updates and reject stale same-key replays.
      </dd>
    </div>
    <div>
      <dt>Local consistency &amp; lifecycle control</dt>
      <dd>
        Atomic updates, local transactions, and maintenance events give each
        node explicit control over its storage.
      </dd>
    </div>
  </dl>
</section>`)
}
