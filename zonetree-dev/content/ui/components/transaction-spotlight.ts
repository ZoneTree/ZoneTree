import { defineComponent, html } from 'regor'

export function defineTransactionSpotlight() {
  return defineComponent(html`<section
  class="zt-section zt-transactions"
  aria-labelledby="zt-transactions-title"
>
  <div class="zt-transactions-layout">
    <div class="zt-transactions-copy">
      <p class="zt-eyebrow">MVCC TRANSACTIONS</p>
      <h2 id="zt-transactions-title">Many keys.<br/>One consistent change.</h2>
      <p>
        Keep records, indexes, and counters in agreement. Commit related writes
        together while concurrent readers keep a consistent snapshot of your
        data. ZoneTree brings transactions into the same ordered storage engine
        your application already uses.
      </p>
      <a class="zt-text-link" href="/docs/usage/transactions/"
        >Explore transactions <Icon name="tabler:arrow-right"/></a>
    </div>
    <figure class="zt-transaction-example" aria-labelledby="zt-transaction-caption">
      <figcaption id="zt-transaction-caption">
        <Icon name="tabler:arrows-exchange"/> TRANSFER 25 BETWEEN ACCOUNTS
      </figcaption>
      <table class="zt-transaction-balances">
        <thead>
          <tr><th scope="col">Account</th><th scope="col">Before</th><th scope="col">After</th></tr>
        </thead>
        <tbody>
          <tr><th scope="row">Alice</th><td>100</td><td>75</td></tr>
          <tr><th scope="row">Bob</th><td>50</td><td>75</td></tr>
        </tbody>
        <tfoot>
          <tr><th scope="row">Total</th><td>150</td><td>150</td></tr>
        </tfoot>
      </table>
      <div class="zt-transaction-commit">
        <Icon name="tabler:check"/><strong>One commit. Both balances.</strong>
      </div>
      <p>
        Open snapshots keep the earlier balances. New snapshots see the
        committed transfer.
      </p>
    </figure>
  </div>
  <dl class="zt-transaction-features">
    <div>
      <dt>Serializable by default</dt>
      <dd>
        Prevent write skew and phantom reads by validating keys and scanned
        ranges. Execute retries conflicts with a fresh snapshot.
      </dd>
    </div>
    <div>
      <dt>Consistent reads, in order</dt>
      <dd>
        Point reads and forward or reverse range scans share one snapshot.
        Read-only transactions never conflict or keep a read set.
      </dd>
    </div>
    <div>
      <dt>Room for large transactions</dt>
      <dd>
        Writes go straight into the storage tree. Memory tracks keys and read
        ranges while values can grow beyond RAM and move to disk.
      </dd>
    </div>
    <div>
      <dt>Durability on your terms</dt>
      <dd>
        Recover all or none of each transaction. Wait for a durable commit,
        sync a batch, or choose a periodic sync interval. Concurrent syncs share
        the disk work.
      </dd>
    </div>
  </dl>
</section>`)
}
