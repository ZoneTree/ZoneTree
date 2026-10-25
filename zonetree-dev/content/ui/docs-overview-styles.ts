import { styleBuilder, themes } from '@purestack/ts-style'
import { zoneTreeColors } from './skin'

type OverviewStyleContext = {
  root: ReturnType<typeof styleBuilder.get>
  colors: typeof zoneTreeColors.light | typeof zoneTreeColors.dark
}

export function registerDocsOverviewStyles() {
  themes.forEach((theme) => {
    const context = { root: styleBuilder.get(theme), colors: zoneTreeColors[theme] }
    registerOverviewLayout(context)
    registerOverviewIntro(context)
    registerOverviewExample(context)
    registerOverviewGuides(context)
  })
}

function registerOverviewLayout({ root, colors }: OverviewStyleContext) {
  root.select('.zt-docs-overview').width('100%').maxWidth('1160px').marginInline('auto')
    .padding('12px 28px 48px').boxSizing('border-box').color(colors.ink)
  root.select('.zt-docs-overview :is(h1, h2, h3, p, dl)').margin('0')
  root.select('.zt-docs-overview .section-header > .text-eyebrow')
    .color(colors.accentInk).fontSize('10px').fontWeight('700').letterSpacing('1.5px').marginBottom('14px')
  root.select('.zt-docs-overview .section-header > h2')
    .fontSize('clamp(24px, 2.5vw, 30px)').lineHeight('1.25').letterSpacing('-.8px').fontWeight('650')
  root.select('.zt-docs-overview .section-header > .text-tagline')
    .fontSize('15px').lineHeight('1.8').color(colors.muted).marginTop('12px')
  root.select('.zt-docs-section').marginTop('56px').paddingTop('32px').borderTop(`1px solid ${colors.subtle}`)
  root.select('.zt-docs-section > .section-header').marginBottom('24px')
  root.select('.zt-docs-overview a:focus-visible').outline(`2px solid ${colors.accentInk}`).outlineOffset('4px').borderRadius('4px')
  root.media('(max-width: 600px)').select('.zt-docs-overview').padding('0 0 32px')
  root.media('(max-width: 600px)').select('.zt-docs-section').marginTop('36px').paddingTop('24px')
}

function registerOverviewIntro({ root, colors }: OverviewStyleContext) {
  root.select('.zt-docs-intro').gap('36px').paddingBottom('8px')
  root.select('.zt-docs-intro > *').minWidth('0')
  root.select('.zt-docs-welcome h1').fontSize('clamp(36px, 4vw, 52px)').lineHeight('1.08').letterSpacing('-2px').fontWeight('700')
  root.select('.zt-docs-welcome .section-header > .text-tagline').fontSize('17px').lineHeight('1.8').marginTop('20px').maxWidth('48ch')
  root.select('.zt-docs-actions').marginBlock('24px 32px')
  root.select('.zt-docs-principles').display('grid').gap('18px').paddingLeft('16px').borderLeft(`2px solid ${colors.accentBorder}`)
  root.select('.zt-docs-principles dt').fontSize('13px').fontWeight('650')
  root.select('.zt-docs-principles dd').margin('4px 0 0').fontSize('13px').lineHeight('1.7').color(colors.muted).maxWidth('48ch')
  root.media('(max-width: 600px)').select('.zt-docs-welcome h1').letterSpacing('-1.3px')
}

function registerOverviewExample({ root, colors }: OverviewStyleContext) {
  root.select('.zt-docs-example').minWidth('0').overflow('hidden').boxShadow(`0 12px 36px ${colors.signal}08`)
  root.select('.zt-docs-example-heading').padding('16px 20px').borderBottom(`1px solid ${colors.subtle}`).background(colors.surfaceAlt)
  root.select('.zt-docs-example-heading h2').display('flex').alignItems('center').gap('8px').fontSize('14px').lineHeight('1.4').fontWeight('650')
  root.select('.zt-docs-example-heading > span').fontSize('10px').letterSpacing('1px').color(colors.muted)
  root.select('.zt-docs-overview .zt-docs-example pre').margin('0').padding('18px 20px').fontSize('12px').lineHeight('1.8')
    .borderRadius('0').border('0').background(colors.surface).maxWidth('100%').overflowX('auto')
  root.select('.zt-docs-install').borderBottom(`1px solid ${colors.subtle}`)
  root.select('.zt-docs-overview .zt-docs-install pre').background(colors.surfaceAlt).paddingBlock('14px')
  root.select('.zt-docs-example .zt-docs-example-note').padding('16px 20px').borderTop(`1px solid ${colors.subtle}`)
    .fontSize('12px').lineHeight('1.8').color(colors.muted)
  root.select('.zt-docs-example-note a').display('inline-flex').alignItems('center').gap('4px').color(colors.accentInk).fontWeight('600')
  root.select('.zt-docs-example-note .icon').width('14px').height('14px')
}

function registerOverviewGuides({ root, colors }: OverviewStyleContext) {
  root.select('.zt-docs-paths .docs-card').padding('24px')
  root.select('.zt-docs-paths .docs-card-meta').marginBottom('24px')
  root.select('.zt-docs-paths .docs-card h3').fontSize('20px').lineHeight('1.3').letterSpacing('-.4px')
  root.select('.zt-docs-paths .docs-card p').fontSize('13px').marginBlock('14px 24px')
  root.select('.zt-docs-topic').minWidth('0')
  root.select('.zt-docs-topic-icon').width('26px').height('26px').marginBottom('16px').color(colors.accentInk)
  root.select('.zt-docs-topic h3').fontSize('18px').lineHeight('1.3').letterSpacing('-.4px').fontWeight('650')
  root.select('.zt-docs-topic p').fontSize('13px').lineHeight('1.8').color(colors.muted).marginBlock('12px 16px')
  root.select('.zt-docs-topic a').display('block').paddingBlock('6px').fontSize('13px').lineHeight('1.6').color(colors.ink).textDecoration('none')
  root.select('.zt-docs-topic a:hover').color(colors.accentInk).textDecoration('underline').textUnderlineOffset('4px')
  root.select('.zt-docs-reference').marginTop('32px')
  root.select('.zt-docs-reference .section-header > h2').fontSize('20px').letterSpacing('-.4px')
  root.select('.zt-docs-reference .section-header > p').fontSize('13px').marginTop('8px')
}
