import { defineComponent, html } from 'regor'

export interface DocCard {
  number: string
  title: string
  summary: string
  href: string
  icon: string
  label: string
}

export function defineDocCard() {
  return defineComponent<DocCard>(
    html`<a class="docs-card" :href="href">
  <div class="docs-card-meta">
    <span>{{ number }}</span><Icon :name="icon"/>
  </div>
  <h3>{{ title }}</h3>
  <p>{{ summary }}</p>
  <span class="docs-card-link"
    >{{ label }} <Icon name="tabler:arrow-up-right"/></span>
</a>`,
    { props: ['number', 'title', 'summary', 'href', 'icon', 'label'] },
  )
}
