import { defineComponent, html, type RefOrValue } from 'regor'

export interface ZoneTreeError {
  code: RefOrValue<string>
  status: RefOrValue<string>
  heading: RefOrValue<string>
  summary: RefOrValue<string>
  caption: RefOrValue<string>
}

export function defineZoneTreeError() {
  return defineComponent<ZoneTreeError>(html`<div class="zt-error-page">
  <section class="zt-error-hero" aria-labelledby="zt-error-title">
    <div class="zt-error-copy">
      <Badge tone="accent" variant="surface" class="zt-error-status"
        >{{ status }}</Badge>
      <SectionHeader
        :title="heading"
        :subtitle="summary"
        titleTag="h1"
        titleId="zt-error-title"/>
      <div class="zt-error-actions">
        <BtnLink
          href="/docs/"
          tone="accent"
          size="lg"
          icon="tabler:arrow-right"
          iconPosition="end"
          >Browse documentation</BtnLink>
        <BtnLink
          href="/"
          tone="neutral"
          variant="outline"
          size="lg"
          icon="iconoir:home"
          >Back to home</BtnLink>
      </div>
    </div>
    <ErrorTree :code="code" :caption="caption"/>
  </section>
  <section class="zt-error-recovery" aria-labelledby="zt-error-recovery-title">
    <h2 id="zt-error-recovery-title">A few good places to start</h2>
    <div class="zt-error-links">
      <DocCard
        number="01 / START BUILDING"
        title="Get started"
        icon="iconoir:book"
        summary="Install ZoneTree and open your first tree."
        href="/docs/getting-started/"
        label="Read the quickstart"/>
      <DocCard
        number="02 / EXPLORE THE NUMBERS"
        title="Benchmarks"
        icon="iconoir:stats-report"
        summary="Explore the workloads, results, and tradeoffs."
        href="/docs/benchmark/"
        label="View the benchmarks"/>
      <DocCard
        number="03 / FIND AN ANSWER"
        title="Get support"
        icon="iconoir:chat-bubble"
        summary="Get help with architecture, tuning, and troubleshooting."
        href="/support/"
        label="Explore support"/>
    </div>
  </section>
</div>`, { props: ['code', 'status', 'heading', 'summary', 'caption'] })
}
