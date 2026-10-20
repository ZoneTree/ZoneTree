import {
  type ThemePalette,
  type ThemeSkin,
  themeSkins,
} from '@purestack/ts-style'
import { merge } from '@purestack/ts-util'

type Tone = ThemePalette['semanticTone']['neutral']

// Emerald and fresh lime on warm ivory; mint accents on graphite after dark.
export const zoneTreeColors = {
  light: {
    signal: '#08794e',
    signalHover: '#05633f',
    signalActive: '#034e32',
    onSignal: '#ffffff',
    canvas: '#faf9f1',
    surface: '#fffffb',
    surfaceAlt: '#edf2df',
    hover: '#e6eed7',
    active: '#d8e5c5',
    ink: '#183c2c',
    muted: '#536552',
    border: '#b6c8a9',
    subtle: '#d6dfc7',
    accentInk: '#076c44',
    accentSurface: '#e1f3c3',
    accentHover: '#cceaa2',
    accentActive: '#b6dd85',
    accentBorder: '#8bb46a',
    companionInk: '#4b622f',
    companionFill: '#526b34',
    companionHover: '#43592a',
    companionActive: '#354722',
    companionSurface: '#eef3df',
    companionBorder: '#b6c89a',
    onCompanion: '#ffffff',
  },
  dark: {
    signal: '#62d9a3',
    signalHover: '#84e6b8',
    signalActive: '#45c68b',
    onSignal: '#092a1b',
    canvas: '#151515',
    surface: '#212121',
    surfaceAlt: '#1b1b1b',
    hover: '#2d2d2d',
    active: '#393939',
    ink: '#f2f2f2',
    muted: '#b9b9b9',
    border: '#505050',
    subtle: '#353535',
    accentInk: '#76e2af',
    accentSurface: '#152e24',
    accentHover: '#1c4030',
    accentActive: '#26523e',
    accentBorder: '#3d8a66',
    companionInk: '#dadada',
    companionFill: '#c7c7c7',
    companionHover: '#dddddd',
    companionActive: '#b0b0b0',
    companionSurface: '#303030',
    companionBorder: '#666666',
    onCompanion: '#252525',
  },
} as const

function state(background: string, border: string, text: string) {
  return { background, bgcolor: background, border, text }
}

function createNeutral(dark: boolean): Tone {
  const c = dark ? zoneTreeColors.dark : zoneTreeColors.light
  const text = { default: c.ink, subtle: c.muted }
  const border = { default: c.border, subtle: c.subtle, focus: c.accentInk }
  const disabled = state(c.surfaceAlt, c.subtle, c.muted)
  const surface = {
    rest: state(c.surface, c.subtle, c.ink),
    hover: state(c.hover, c.border, c.ink),
    active: state(c.active, c.border, c.ink),
    disabled,
    focusRing: c.accentInk,
  }
  return {
    tone: c.ink,
    canvas: c.canvas,
    canvascolor: c.canvas,
    spotlight: { field: c.canvas, light: `${c.signal}12` },
    overlay: dark ? '#0a0a0acc' : '#26262644',
    text,
    border,
    root: { text, border },
    surface,
    surfaceAlt: { ...surface, rest: state(c.surfaceAlt, c.subtle, c.ink) },
    button: {
      ...surface,
      rest: state(c.surfaceAlt, c.border, c.ink),
    },
  }
}

function createAccent(dark: boolean, neutral: Tone): Tone {
  const c = dark ? zoneTreeColors.dark : zoneTreeColors.light
  const text = { default: c.accentInk, subtle: c.muted }
  const border = {
    default: c.accentBorder,
    subtle: c.subtle,
    focus: c.accentInk,
  }
  const surface = {
    rest: state(c.accentSurface, c.accentBorder, c.ink),
    hover: state(c.accentHover, c.accentInk, c.ink),
    active: state(c.accentActive, c.accentInk, c.ink),
    disabled: neutral.surface.disabled,
    focusRing: c.accentInk,
  }
  return {
    tone: c.signal,
    canvas: c.accentSurface,
    canvascolor: c.accentSurface,
    spotlight: { field: c.accentSurface, light: `${c.signal}20` },
    overlay: neutral.overlay,
    text,
    border,
    root: { text, border },
    surface,
    surfaceAlt: surface,
    button: {
      rest: state(c.signal, c.signal, c.onSignal),
      hover: state(c.signalHover, c.signalHover, c.onSignal),
      active: state(c.signalActive, c.signalActive, c.onSignal),
      disabled: neutral.button.disabled,
      focusRing: c.accentInk,
    },
  }
}

function createCompanion(dark: boolean, neutral: Tone): Tone {
  const c = dark ? zoneTreeColors.dark : zoneTreeColors.light
  const ink = c.companionInk
  const fill = c.companionFill
  const field = c.companionSurface
  const edge = c.companionBorder
  const text = { default: ink, subtle: neutral.text.subtle }
  const border = { default: edge, subtle: edge, focus: ink }
  const surface = {
    rest: state(field, edge, ink),
    hover: state(field, ink, ink),
    active: state(field, ink, ink),
    disabled: neutral.surface.disabled,
    focusRing: ink,
  }
  return {
    tone: fill,
    canvas: field,
    canvascolor: field,
    spotlight: { field, light: `${fill}20` },
    overlay: neutral.overlay,
    text,
    border,
    root: { text, border },
    surface,
    surfaceAlt: surface,
    button: {
      rest: state(fill, fill, c.onCompanion),
      hover: state(c.companionHover, c.companionHover, c.onCompanion),
      active: state(c.companionActive, c.companionActive, c.onCompanion),
      disabled: neutral.button.disabled,
      focusRing: ink,
    },
  }
}

function createPalette(dark: boolean): ThemePalette {
  const c = dark ? zoneTreeColors.dark : zoneTreeColors.light
  const standard = themeSkins.standard.create()
  const neutral = createNeutral(dark)
  const accent = createAccent(dark, neutral)
  const companion = createCompanion(dark, neutral)
  const invisible = state('transparent', 'transparent', neutral.text.default)
  const ghost = {
    ...neutral,
    canvas: 'transparent',
    canvascolor: 'transparent',
    surface: { ...neutral.surface, rest: invisible },
    button: { ...neutral.button, rest: invisible },
  }
  return merge(dark ? standard.dark : standard.light, {
    accent: c.signal,
    font: {
      family: {
        base: "'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif",
      },
    },
    radii: { sm: '6px', md: '10px', lg: '16px' },
    semanticTone: {
      neutral,
      accent,
      ghost,
      secondary: companion,
      feature: companion,
      info: companion,
      custom: accent,
    },
    effect: {
      panelShadow: 'none',
      softShadow: dark ? '0 8px 28px #00000022' : '0 8px 28px #00000008',
      floatingShadow: dark ? '0 28px 80px #00000055' : '0 28px 80px #00000014',
      glowPrimary: `0 0 48px ${c.signal}18`,
      glowSecondary: `0 0 32px ${companion.tone}12`,
      accentShadow: `0 8px 24px ${c.signal}18`,
      focusGlow: `0 0 0 3px ${c.accentInk}44`,
    },
  })
}

export const zoneTreeSkin: ThemeSkin = {
  create: () => ({ light: createPalette(false), dark: createPalette(true) }),
}

