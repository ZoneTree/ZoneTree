import { styleBuilder, themes } from '@purestack/ts-style'
import { zoneTreeColors } from './skin'

type GuideStyleContext = {
  root: ReturnType<typeof styleBuilder.get>
  colors: typeof zoneTreeColors.light | typeof zoneTreeColors.dark
}

export function registerGuideStyles() {
  themes.forEach((theme) => {
    const context = { root: styleBuilder.get(theme), colors: zoneTreeColors[theme] }
    registerGuideTypography(context)
    registerGuideExamples(context)
    registerGuideNavigation(context)
  })
}

function registerGuideTypography({ root, colors }: GuideStyleContext) {
  root.select('.zt-guide-intro').marginBottom('28px')
  root.select('.zt-guide-intro > .text-eyebrow').color(colors.accentInk).fontSize('10px')
    .fontWeight('700').letterSpacing('1.5px').marginBottom('16px')
  root.select('.zt-guide-intro > h1').margin('0').fontSize('clamp(32px, 3.5vw, 44px)')
    .lineHeight('1.15').letterSpacing('-1.3px').fontWeight('700').textWrap('balance')
  root.select('.zt-guide-intro > .text-tagline').marginBlock('20px 0').maxWidth('70ch')
    .fontSize('18px').lineHeight('1.8').color(colors.muted)
  root.select('.doc-content:has(.zt-guide-intro) > h2').paddingTop('24px').marginTop('40px')
    .borderTop(`1px solid ${colors.subtle}`).fontSize('25px').fontWeight('650')
  root.select('.zt-guide-outcomes').marginBlock('28px 32px')
  root.select('.zt-guide-outcomes strong').fontSize('14px').fontWeight('650')
  root.select('.zt-guide-outcomes p').fontSize('13px').lineHeight('1.7').margin('8px 0 0').color(colors.muted)
  root.select('.zt-guide-callout').marginBlock('28px').fontSize('14px').lineHeight('1.8')
  root.select('.zt-guide-callout p').margin('0')
  root.select('.zt-guide-callout .flex').minWidth('0')
  root.select('.doc-content:has(.zt-guide-intro) table').width('100%').fontSize('13px').lineHeight('1.7')
  root.select('.doc-content:has(.zt-guide-intro) :is(th, td)').verticalAlign('top')
    .minWidth('8rem').whiteSpace('normal')
  root.select('.doc-content:has(.zt-guide-intro) td code').fontSize('12px').overflowWrap('anywhere').whiteSpace('normal')
  root.media('(max-width: 600px)').select('.zt-guide-intro > .text-tagline').fontSize('16px')
  root.media('(max-width: 600px)').select('.doc-content:has(.zt-guide-intro) > h2').fontSize('22px')
}

function registerGuideExamples({ root, colors }: GuideStyleContext) {
  root.select('.doc-content:has(.zt-guide-intro) pre').maxWidth('100%').overflowX('auto')
    .fontSize('12.5px').lineHeight('1.8').border(`1px solid ${colors.subtle}`)
  root.select('.zt-guide-output pre').background(colors.surfaceAlt).borderColor(colors.accentBorder)
  root.select('.zt-guide-reference').marginBlock('16px')
  root.select('.zt-guide-reference :is(p, li)').fontSize('14px').lineHeight('1.8').overflowWrap('anywhere')
  root.select('.zt-guide-reference table').margin('0')
  root.media('(max-width: 600px)').select('.doc-content:has(.zt-guide-intro) pre').padding('16px').fontSize('12px')
}

function registerGuideNavigation({ root, colors }: GuideStyleContext) {
  root.select('.zt-guide-next').marginBlock('24px 32px')
  root.select('.zt-guide-next .docs-card').padding('24px')
  root.select('.zt-guide-next .docs-card-meta').marginBottom('20px')
  root.select('.zt-guide-next .docs-card h3').fontSize('20px').lineHeight('1.3')
  root.select('.zt-guide-next .docs-card p').fontSize('13px').marginBlock('14px 24px')
  root.select('.zt-guide-next-panel').marginBlock('40px 16px')
  root.select('.zt-guide-next-panel strong').fontSize('17px').lineHeight('1.4').fontWeight('650')
  root.select('.zt-guide-next-panel p').marginBlock('12px 20px').fontSize('14px').lineHeight('1.8').color(colors.muted)
  root.select('.doc-content:has(.zt-guide-intro) a:focus-visible').outline(`2px solid ${colors.accentInk}`)
    .outlineOffset('4px').borderRadius('4px')
}
