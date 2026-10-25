import { styleBuilder, themes } from '@purestack/ts-style'
import { zoneTreeColors } from './skin'

export function registerErrorPageStyles() {
  themes.forEach((theme) => {
    const root = styleBuilder.get(theme)
    const c = zoneTreeColors[theme]

    root.select('.doc-main:has(.zt-error-page)').width('min(1120px, calc(100% - 80px))').marginInline('auto')
    root.select('.doc-main:has(.zt-error-page) > .doc-content').paddingInline('0')
    root.select('.zt-error-page').paddingBlock('clamp(32px, 6vh, 72px) 32px').color(c.ink)
    root.select('.zt-error-hero').display('grid').gridTemplateColumns('1.15fr 1fr').alignItems('center').gap('64px')
    root.select('.zt-error-copy').minWidth('0')
    root.select('.zt-error-status').fontSize('11px').fontWeight('700').letterSpacing('1.5px').textTransform('uppercase').padding('8px 12px').marginBottom('24px')
    root.select('.zt-error-copy .text-title').fontSize('clamp(38px, 4.5vw, 58px)').fontWeight('700').letterSpacing('-2px').lineHeight('1.1').margin('0').maxWidth('15ch')
    root.select('.zt-error-copy .text-tagline').fontSize('17px').lineHeight('1.8').color(c.muted).maxWidth('43ch').marginBlock('24px 0')
    root.select('.zt-error-actions').display('flex').flexWrap('wrap').gap('12px').marginTop('32px')
    root.select('.zt-error-actions .btn').fontSize('14px').padding('14px 18px').fontWeight('600')

    root.select('.zt-error-visual').position('relative').overflow('hidden').border(`1px solid ${c.subtle}`).borderRadius('24px').padding('24px 28px').background(c.surface)
      .backgroundImage(`radial-gradient(ellipse at 50% 38%, ${c.accentSurface}, transparent 72%)`)
    root.select('.zt-error-visual-heading').display('flex').alignItems('center').gap('9px').fontSize('10px').fontWeight('700').letterSpacing('2px').color(c.muted)
    root.select('.zt-error-visual-heading .icon').width('20px').height('20px').color(c.accentInk)
    root.select('.zt-error-signal').width('7px').height('7px').borderRadius('50%').background(c.signal).marginLeft('auto')
    root.select('.zt-error-code').fontSize('clamp(100px, 12vw, 160px)').fontWeight('700').letterSpacing('-8px').lineHeight('1.2').textAlign('center').color(c.accentInk).marginBlock('12px 0')
    root.select('.zt-error-tree').display('block').width('100%').height('auto').maxHeight('150px')
    root.select('.zt-error-branch, .zt-error-node-mark').stroke(c.signal).strokeWidth('1.5').strokeLinecap('round').strokeLinejoin('round')
    root.select('.zt-error-missing-branch').stroke(c.border).strokeWidth('1.5').strokeDasharray('4 6')
    root.select('.zt-error-node').fill(c.surface).stroke(c.accentBorder).strokeWidth('1.5')
    root.select('.zt-error-missing-node').fill(c.surfaceAlt).stroke(c.border).strokeWidth('1.5').strokeDasharray('4 4')
    root.select('.zt-error-junction, .zt-error-endpoint').fill(c.signal).stroke(c.surface).strokeWidth('3')
    root.select('.zt-error-visual-caption').textAlign('center').color(c.muted).fontSize('12px').marginBlock('18px 0')

    root.select('.zt-error-recovery').marginTop('64px').paddingTop('28px').borderTop(`1px solid ${c.subtle}`)
    root.select('.zt-error-recovery > h2').fontSize('17px').fontWeight('600').letterSpacing('-.3px').marginBlock('0 20px')
    root.select('.zt-error-links').display('grid').gridTemplateColumns('repeat(3, minmax(0, 1fr))').gap('16px')
    root.select('.zt-error-links .docs-card').padding('24px')
    root.select('.zt-error-links .docs-card-meta').marginBottom('20px')
    root.select('.zt-error-links .docs-card h3').fontSize('20px')
    root.select('.zt-error-links .docs-card p').marginBlock('12px 20px')
    root.select('.zt-error-page a:focus-visible').outline(`2px solid ${c.accentInk}`).outlineOffset('4px')

    const tablet = root.media('(max-width: 900px)')
    tablet.select('.zt-error-hero').gap('32px')
    tablet.select('.zt-error-actions').flexDirection('column').alignItems('flex-start')
    tablet.select('.zt-error-links').gridTemplateColumns('1fr')
    const mobile = root.media('(max-width: 640px)')
    mobile.select('.doc-main:has(.zt-error-page)').width('calc(100% - 40px)')
    mobile.select('.zt-error-hero').gridTemplateColumns('1fr').gap('32px')
    mobile.select('.zt-error-page').paddingTop('16px')
    mobile.select('.zt-error-copy .text-title').fontSize('40px').letterSpacing('-1.5px')
    mobile.select('.zt-error-copy .text-tagline').fontSize('16px')
    mobile.select('.zt-error-actions').alignItems('stretch')
    mobile.select('.zt-error-visual').padding('20px')
    mobile.select('.zt-error-code').fontSize('112px')
    mobile.select('.zt-error-recovery').marginTop('40px')
  })
}
