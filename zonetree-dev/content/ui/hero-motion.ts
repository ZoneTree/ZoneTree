import type { StyleBuilder, ThemeMode } from '@purestack/ts-style'
import { zoneTreeColors } from './skin'

// SVG-only lifecycle. All motion is generated into the theme stylesheets.
export function registerHeroMotionStyles(
  root: ReturnType<StyleBuilder['get']>,
  theme: ThemeMode,
) {
  const colors = zoneTreeColors[theme]
  const query = theme === 'dark' ? '#83d5eb' : '#267699'
  const maintenance = theme === 'dark' ? '#dfba70' : '#946321'

  root.select('.zt-engine').position('relative')
  root.select('.zt-engine-index').fontSize('9px').letterSpacing('1px').opacity('.65')
  root.select('.zt-engine .zt-motion')
    .animationDuration('12s')
    .animationIterationCount('infinite')
    .animationTimingFunction('linear')
  root.select('.zt-segment-body, .zt-disk-body')
    .fill(colors.surface)
    .stroke('currentColor')
    .strokeWidth('1.25')
    .strokeOpacity('.65')
  root.select('.zt-mutable-slot')
    .fill(colors.accentSurface)
    .fillOpacity('.35')
    .stroke('currentColor')
    .strokeOpacity('.2')
    .strokeDasharray('3 4')
  root.select('.zt-write-tracks').opacity('.4')
  root.select('.zt-writer').opacity('.65')
  root.select('.zt-rollover-guide').opacity('.3')
  root.select('.zt-capacity')
    .transformBox('fill-box')
    .transformOrigin('left center')
  root.select('.zt-engine .zt-current-fill')
    .animationName('zt-fill-current')
    .animationTimingFunction('linear')
  root.select('.zt-engine .zt-rollover')
    .opacity('0')
    .transformOrigin('154px 96px')
    .animationTimingFunction('cubic-bezier(.4, 0, .2, 1)')
  root.select('.zt-rollover-one').animationName('zt-rollover-one')
  root.select('.zt-rollover-two').animationName('zt-rollover-two')
  root.select('.zt-rollover-three').animationName('zt-rollover-three')

  root.select('.zt-engine .zt-write-packet')
    .opacity('0')
    .offsetRotate('auto')
    .animationName('zt-record-travel')
    .animationDuration('1.35s')
    .animationTimingFunction('linear')
  root.select('.zt-write-one')
    .offsetPath('path("M24 78H76Q98 78 116 100L154 118")')
    .animationDelay('-.2s')
  root.select('.zt-write-two')
    .offsetPath('path("M24 106H86Q108 106 126 120L154 128")')
    .animationDelay('-.65s')
  root.select('.zt-write-three')
    .offsetPath('path("M24 134H96Q120 134 154 138")')
    .animationDelay('-1s')

  root.select('.zt-compaction').color(maintenance)
  root.select('.zt-maintenance-track').opacity('.4')
  root.select('.zt-compaction-body')
    .fill(colors.canvas)
    .stroke('currentColor')
    .strokeOpacity('.6')
  root.select('.zt-engine-compaction')
    .position('absolute')
    .left('41.07%')
    .top('67.78%')
    .width('17.86%')
    .height('5.11%')
    .display('flex')
    .alignItems('center')
    .justifyContent('center')
    .color(maintenance)
    .fontSize('11px')
    .fontWeight('500')
    .pointerEvents('none')
  root.select('.zt-compaction-flow').animationName('zt-compaction-active')
  root.select('.zt-engine .zt-merge-existing')
    .opacity('0')
    .offsetPath('path("M154 394H134Q122 394 122 382V330Q122 316 138 316H230")')
    .offsetRotate('0deg')
    .animationName('zt-record-travel')
    .animationDuration('2.1s')
    .animationDelay('-.4s')
  root.select('.zt-engine .zt-readonly-segment')
    .transformOrigin('154px 0')
    .animationTimingFunction('cubic-bezier(.4, 0, .2, 1)')
  root.select('.zt-readonly-one').transform('translateY(208px)').animationName('zt-compact-one')
  root.select('.zt-readonly-two').transform('translateY(238px)').animationName('zt-compact-two')
  root.select('.zt-readonly-three').transform('translateY(268px)').animationName('zt-compact-three')
  root.select('.zt-segment-number').fill('currentColor').fontSize('14px').fontWeight('600')
  root.select('.zt-engine .zt-output-run')
    .opacity('0')
    .animationTimingFunction('cubic-bezier(.4, 0, .2, 1)')
  root.select('.zt-output-one').animationName('zt-disk-output-one')
  root.select('.zt-output-two').animationName('zt-disk-output-two')
  root.select('.zt-output-three').animationName('zt-disk-output-three')
  root.select('.zt-part-body')
    .fill(colors.accentSurface)
    .stroke('currentColor')
    .strokeWidth('1')
    .strokeOpacity('.35')
  root.select('.zt-disk-part text')
    .fill('currentColor')
    .fontSize('10px')
    .fontWeight('600')
    .letterSpacing('.5px')
  root.select('.zt-disk-part path')
    .stroke('currentColor')
    .strokeWidth('2')
    .strokeLinecap('round')
    .strokeOpacity('.6')
  root.select('.zt-part-update')
    .fill(maintenance)
    .fillOpacity('.16')
    .stroke(maintenance)
    .strokeWidth('1.5')
    .opacity('0')
    .animationName('zt-part-publish')
  root.select('.zt-part-two').animationDelay('.18s')
  root.select('.zt-part-three').animationDelay('.36s')

  // Three independent query lanes each carry two requests and two results.
  root.select('.zt-query').color(query)
  root.select('.zt-query-track').opacity('.35')
  root.select('.zt-query-client').opacity('.65')
  root.select('.zt-query-port').fill(colors.canvas)
  root.select('.zt-engine .zt-query-packet')
    .opacity('0')
    .offsetRotate('auto')
    .animationName('zt-record-travel')
    .animationDuration('2.6s')
  root.select('.zt-engine .zt-query-result').animationDirection('reverse')
  for (const [layer, path, requestDelay, resultDelay] of [
    ['mutable', 'M526 78H454V128H406', .25, 1.5],
    ['readonly', 'M526 100H478V238H406', 1, 2.25],
    ['disk', 'M526 122H502V394H406', 1.8, .45],
  ] as const) {
    root.select(`.zt-query-${layer}`)
      .offsetPath(`path("${path}")`)
      .animationDelay(`-${requestDelay}s`)
    root.select(`.zt-engine .zt-query-${layer}.zt-query-result`)
      .animationDelay(`-${resultDelay}s`)
    root.select(`.zt-engine .zt-query-${layer}.zt-query-follow`)
      .animationDelay(`-${requestDelay + 1.3}s`)
    root.select(`.zt-engine .zt-query-${layer}.zt-query-result.zt-query-follow`)
      .animationDelay(`-${resultDelay + 1.3}s`)
  }

  root.select('.zt-engine-writes').left('4.3%').top('8%')
  root.select('.zt-engine-memory').left('27.5%').top('16.4%')
  root.select('.zt-engine-readonly').left('27.5%').top('41.3%')
  root.select('.zt-engine-disk').left('29.6%').top('80.3%')
  root.select('.zt-engine-rollover').left('4.3%').top('39%')
  root.select('.zt-engine-rollover span').letterSpacing('0')
  root.select('.zt-engine-query').left('76%').top('7%')
  root.select('.zt-engine-query span, .zt-engine-query strong').color(query)
  root.select('.zt-engine-concurrency')
    .display('flex')
    .alignItems('center')
    .justifyContent('center')
    .flexWrap('wrap')
    .gap('8px 18px')
    .padding('2px 0 20px')
    .color(colors.muted)
    .fontSize('10px')
  root.select('.zt-engine-concurrency span')
    .display('inline-flex')
    .alignItems('center')
    .gap('6px')
  root.select('.zt-engine-concurrency i')
    .width('5px')
    .height('5px')
    .borderRadius('50%')
    .background(colors.signal)
  root.select('.zt-engine-concurrency span:nth-child(2) i').background(query)
  root.select('.zt-engine-concurrency span:nth-child(3) i').background(maintenance)

  root.select('.zt-engine-memory span, .zt-engine-readonly span')
    .background(colors.canvas)
    .paddingInline('2px')
    .marginInline('-2px')
  root.media('max-width: 1000px').select('.zt-engine-query').top('1%')
  const compact = root.media('max-width: 500px')
  compact.select('.zt-engine-rollover span').display('none')
  compact.select('.zt-engine-compaction').fontSize('8px')
  compact.select('.zt-engine-label span').lineHeight('1.2')

  // Equal fill intervals keep write throughput constant, including during compaction.
  const fillInterval = 100 / 3
  const firstFull = 2
  const stages = [
    { name: 'one', full: firstFull, position: 208, merged: 85 },
    { name: 'two', full: firstFull + fillInterval, position: 238, merged: 87 },
    { name: 'three', full: firstFull + 2 * fillInterval, position: 268, merged: 89 },
  ] as const

  // Start empty, then accumulate three complete mutable segments before merging.
  for (const { name, full, position, merged } of stages) {
    const arrived = full + 6
    const destination = `translateY(${position - 96}px) scaleY(.4)`
    const rollover = root.keyframes(`zt-rollover-${name}`)
    rollover.select(`0%, ${full - .01}%`).transform('translateY(0) scaleY(1)').opacity('0')
    rollover.select(`${full}%`).transform('translateY(0) scaleY(1)').opacity('1')
    rollover.select(`${arrived}%`).transform(destination).opacity('1')
    rollover.select(`${arrived + 1}%, 100%`).transform(destination).opacity('0')

    const segment = root.keyframes(`zt-compact-${name}`)
    segment.select(`0%, ${arrived}%`).transform(`translateY(${position}px)`).opacity('0')
    segment.select(`${arrived + 1}%, 80%`).transform(`translateY(${position}px)`).opacity('1')
    segment.select(`${merged}%`).transform('translate(98px, 309px) scale(.22, .5)').opacity('1')
    segment.select(`${merged + 2}%, 100%`).transform('translate(98px, 309px) scale(.22, .5)').opacity('0')
  }

  // The compacted run separates into ordered outputs that reach the actual parts.
  for (const [name, destination] of [
    ['zt-disk-output-one', 'translate(174px, 398px)'],
    ['zt-disk-output-two', 'translate(255px, 398px)'],
    ['zt-disk-output-three', 'translate(336px, 398px)'],
  ] as const) {
    const output = root.keyframes(name)
    output.select('0%, 88%').transform('translate(267px, 312px) scale(.5)').opacity('0')
    output.select('90%').transform('translate(255px, 332px) scale(1)').opacity('1')
    output.select('97%').transform(destination).opacity('1')
    output.select('100%').transform(destination).opacity('0')
  }

  // The partial fill at either end also matches across the loop boundary.
  const fill = root.keyframes('zt-fill-current')
  fill.select('0%, 100%').transform(`scaleX(${1 - firstFull / fillInterval})`)
  for (const { full } of stages) {
    fill.select(`${full}%`).transform('scaleX(1)')
    fill.select(`${full + .01}%`).transform('scaleX(0)')
  }
  const compaction = root.keyframes('zt-compaction-active')
  compaction.select('0%, 79%, 92%, 100%').opacity('0')
  compaction.select('81%, 90%').opacity('1')
  const publish = root.keyframes('zt-part-publish')
  publish.select('0%, 94%, 100%').opacity('0')
  publish.select('97%, 99%').opacity('1')

  const travel = root.keyframes('zt-record-travel')
  travel.select('0%').offsetDistance('0%').opacity('0')
  travel.select('8%, 88%').opacity('1')
  travel.select('100%').offsetDistance('100%').opacity('0')

  // The static fallback shows all three layers and a writable replacement.
  root.media('prefers-reduced-motion: reduce')
    .select('.zt-engine .zt-motion').animation('none !important')
}
