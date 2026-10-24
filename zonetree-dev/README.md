# ZoneTree website

PureStack builds the landing page and documentation from `content`. The landing
page lives at `/`, and the existing documentation routes remain under `/docs/`.

## Develop

Use Node.js 22.17 or newer and Yarn 4. From this directory:

```sh
yarn install
yarn serve
```

The development server runs at `http://localhost:4900`.

## Validate and build

```sh
yarn check
yarn test
yarn build
```

The build writes to `dist/web-dev`. `yarn publish` generates production files,
including Pagefind search, sitemap, and robots, in `dist/web`. Deployment is a
separate action through the existing `yarn deploy` command.

The legal pages import Git-ignored notices from
`content/legal/_private/imprint.mdx` and `content/legal/_private/privacy.mdx`.
Restore those from your private copy before building in a fresh checkout.

## Frontend structure

- `content/index.mdx` defines the landing-page content and section order.
- `content/purestack.config.ts` registers the local documentation plugin.
- `content/ui/plugin.ts` connects the skin, components, and style registration.
- `content/ui/skin.ts` defines the shared light and dark emerald palettes.
- `content/ui/styles.ts` generates responsive landing and documentation styles
  through PureStack's typed style builder.
- `content/ui/components/hero.ts` contains the SVG storage lifecycle: parallel writes, mutable
  segment rollover, read-only segments, compaction, and multipart disk storage.
  Live query requests and results run alongside the storage transitions.
- `content/ui/hero-motion.ts` registers typed styles and keyframes through
  PureStack, with a reduced-motion fallback and no animation JavaScript.
- `content/ui/components/index.ts` registers components from the same folder:
  `doc-card.ts`, `stack-technology.ts`, `distributed-foundation.ts`,
  `analytics-spotlight.ts`, `full-text-search-spotlight.ts`, and `hero.ts`.
- Short landing-page examples use fenced code blocks directly in
  `content/index.mdx`.
- `content/docs/concepts/lsm-tree.mdx` explains the storage architecture with
  dedicated `lsm-architecture.ts`, `lsm-read-path.ts`, and `lsm-part-reuse.ts`
  components. `content/ui/lsm-styles.ts` registers their typed styles.

## Benchmark reports

Benchmark SVGs open in the reusable `ImageGallery` viewer, with slide navigation,
thumbnails, keyboard controls, touch swipes, zoom, and a fullscreen toggle. The native dialog keeps
focus inside the viewer, closes with Escape, and returns focus to the image that
opened it. Styles follow the site's light/dark theme and reduced-motion setting.

To use it in another client-side Regor app, import `defineImageGallery` from
`content/ui/components/image-gallery`, register it in the app's `components`,
and supply any SVG or raster image URLs:

```ts
components: { ImageGallery: defineImageGallery() }
// In the app template:
// <ImageGallery :images="images" label="Product screenshots"/>
// images = [{ url: '/images/example.png', title: 'Overview', alt: 'Product overview' }]
```

The component has no benchmark dependencies. `images` accepts plain arrays or
Regor refs. Replacing the images closes an open viewer. Shared styling lives in
`content/ui/image-gallery-styles.ts`, registered by the documentation plugin.

`/docs/benchmark/` is a single Regor report explorer. Its PureStack asset plugin
(`content/ui/benchmark-assets.ts`) discovers every
`benchmarks/profile-store/reference/**/latest.json` directly from the repository,
validates it, and emits content-hashed JSON and the six accompanying reference
SVGs plus a small catalog. No report Markdown, SVG charts, or JSON copies belong
in the site's content tree.

The browser fetches the catalog and selected report only, caches reports during
the visit, and starts with the six existing SVG charts. The interactive PureStack
comparison chart, detailed tables, and configuration follow below. Share a selection
with `?report=win/profiles-2000000-p16` (or any catalog report ID). New
visitors start with Windows, 2M, P16. Regor `persist` remembers the selected report;
an explicit `?report=` link takes precedence over the saved preference. New reference
directories become available on the next build without editing the site. Restart
the development server after changing reference JSON outside the content tree.

The page-specific TypeScript components live beside `benchmark.mdx` in
`content/docs/benchmark/modules/`. They derive engine totals, throughput charts,
write readiness, phase comparisons, and checksum validation from the JSON.
Incomplete runs and missing measurements remain explicit. Each engine's full
workload, settings, environment, and checksums are available in expandable panels.
Reports using an older ZoneTree version show a warning. The latest version is the
highest version recorded across the report catalog; no external version lookup is needed.

Keep benchmark claims tied to retained reports. The landing-page comparison uses
completed phase times from `benchmarks/profile-store/reference/win/profiles-2000000-p16/latest.json`,
rounded to one decimal second; bar lengths use the same values. Its link selects
that report in the explorer, alongside durability settings and methodology.
