import { defineComponent, html, type RefOrValue } from 'regor'

export interface ErrorTree {
  code: RefOrValue<string>
  caption: RefOrValue<string>
}

export function defineErrorTree() {
  return defineComponent<ErrorTree>(html`<div class="zt-error-visual" aria-hidden="true">
  <div class="zt-error-visual-heading">
    <Icon name="iconoir:cube"/><span>ZONETREE</span><span class="zt-error-signal"></span>
  </div>
  <div class="zt-error-code">{{ code }}</div>
  <svg
    class="zt-error-tree"
    viewBox="0 0 440 160"
    fill="none"
    focusable="false"
  >
    <path class="zt-error-branch" d="M220 44V72H80V108M220 72V108M220 72H306"/>
    <path class="zt-error-missing-branch" d="M306 72H360V108"/>
    <rect class="zt-error-node" x="196" y="4" width="48" height="40" rx="9"/>
    <path
      class="zt-error-node-mark"
      d="M209 18L220 12L231 18V30L220 36L209 30ZM209 18L220 24L231 18M220 24V36"/>
    <rect class="zt-error-node" x="56" y="108" width="48" height="40" rx="9"/>
    <path class="zt-error-node-mark" d="M68 123H92M68 132H83"/>
    <rect class="zt-error-node" x="196" y="108" width="48" height="40" rx="9"/>
    <path class="zt-error-node-mark" d="M208 123H232M208 132H223"/>
    <rect
      class="zt-error-missing-node"
      x="336"
      y="108"
      width="48"
      height="40"
      rx="9"/>
    <circle class="zt-error-junction" cx="220" cy="72" r="5"/>
    <circle class="zt-error-endpoint" cx="306" cy="72" r="4"/>
  </svg>
  <p class="zt-error-visual-caption">{{ caption }}</p>
</div>`, { props: ['code', 'caption'] })
}
