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

Keep benchmark claims tied to retained reports. The landing-page comparison
uses completed phase times from `content/docs/benchmark/reference/2m/2m.md`,
rounded to one decimal second; bar lengths use the same values. Its links lead
to the full report and methodology, including durability settings and caveats.
