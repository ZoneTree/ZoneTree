import { defineComponent, html } from 'regor'

export interface StackTechnology {
  name: string
  role: string
  summary: string
  website: string
  repository: string
  icon: string
}

export function defineStackTechnology() {
  return defineComponent<StackTechnology>(
    html`<article class="zt-technology">
      <div class="docs-card-meta"><span>{{ role }}</span><Icon :name="icon"/></div>
      <h3>{{ name }}</h3>
      <p>{{ summary }}</p>
      <div class="zt-technology-actions">
        <a class="zt-text-link" :href="website" target="_blank" rel="noopener" :aria-label="name + ' documentation'">Documentation <Icon name="tabler:arrow-up-right"/></a>
        <a class="zt-text-link" :href="repository" target="_blank" rel="noopener" :aria-label="name + ' source on GitHub'">Source <Icon name="tabler:brand-github"/></a>
      </div>
    </article>`,
    { props: ['name', 'role', 'summary', 'website', 'repository', 'icon'] },
  )
}
