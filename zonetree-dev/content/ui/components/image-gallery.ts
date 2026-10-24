import { computed, defineComponent, html, observe, onMounted, onUnmounted, ref, sref, unref, type RefOrValue } from 'regor'

export interface GalleryImage {
  url: string
  title?: string
  alt?: string
}

export interface ImageGalleryProps {
  images: RefOrValue<GalleryImage[]>
  label?: RefOrValue<string>
}

let galleryId = 0
const scrollLocks = new Set<HTMLDialogElement>()
let previousOverflow = ''

export class ImageGallery {
  readonly titleId = `image-gallery-title-${++galleryId}`
  readonly dialog = sref<HTMLDialogElement | null>(null)
  readonly panel = sref<HTMLElement | null>(null)
  readonly opened = ref(false)
  readonly index = ref(0)
  readonly zoomed = ref(false)
  readonly fullscreen = ref(false)
  readonly zoomWidth = ref(0)
  get images() { return this.props.images }
  readonly items = computed(() => (unref(this.props.images) ?? []).map((entry, index) => {
    const image = unref(entry)
    return { url: unref(image.url), alt: unref(image.alt), index, title: unref(image.title) || unref(image.alt) || `Image ${index + 1}` }
  }))
  readonly label = computed(() => unref(this.props.label) || 'Image gallery')
  readonly current = computed(() => this.items()[this.index()])
  readonly trackStyle = computed(() => ({ transform: `translateX(-${this.index() * 100}%)` }))
  readonly zoomStyle = computed(() => ({ '--zt-gallery-zoom-width': `${this.zoomWidth()}px` }))
  readonly loaded = sref<Set<string>>(new Set())
  readonly failed = sref<Set<string>>(new Set())
  private activeDialog?: HTMLDialogElement
  private trigger?: HTMLElement
  private touch?: { x: number, y: number, time: number }
  private drag?: { x: number, y: number, left: number, top: number }
  private backdropPressed = false

  constructor(private readonly props: ImageGalleryProps) {
    // A report switch or replacement image list must never leave a stale viewer open.
    observe(this.items, () => this.close())
    onMounted(() => document.addEventListener('fullscreenchange', this.syncFullscreen))
    onUnmounted(() => {
      this.close()
      document.removeEventListener('fullscreenchange', this.syncFullscreen)
    })
  }

  readonly open = (index: number, event: Event) => {
    const dialog = this.dialog()
    if (!dialog || !this.items()[index]) return
    this.trigger = event.currentTarget as HTMLElement
    this.index(index)
    this.zoomed(false)
    this.opened(true)
    dialog.showModal()
    this.activeDialog = dialog
    if (!scrollLocks.size) {
      previousOverflow = document.documentElement.style.overflow
      document.documentElement.style.overflow = 'hidden'
    }
    scrollLocks.add(dialog)
    this.revealThumbnail()
  }

  readonly close = () => {
    const dialog = this.activeDialog ?? this.dialog()
    if (dialog?.open) dialog.close()
    this.onClose()
  }

  readonly onClose = () => {
    // Native close events are queued; an earlier one can arrive after reopening.
    if (this.activeDialog?.open) return
    this.opened(false)
    this.zoomed(false)
    this.fullscreen(false)
    this.drag = undefined
    const dialog = this.activeDialog
    if (dialog && scrollLocks.delete(dialog) && !scrollLocks.size) document.documentElement.style.overflow = previousOverflow
    this.activeDialog = undefined
    const trigger = this.trigger
    if (this.panel() && document.fullscreenElement === this.panel()) {
      void document.exitFullscreen().then(() => {
        if (!this.opened() && trigger?.isConnected) trigger.focus({ preventScroll: true })
      }).catch(() => {})
    }
    if (this.trigger?.isConnected) this.trigger.focus({ preventScroll: true })
    this.trigger = undefined
  }

  readonly select = (index: number) => {
    const count = this.items().length
    if (!count) return
    this.index((index + count) % count)
    this.zoomed(false)
    this.dialog()?.querySelectorAll('.zt-gallery-pan').forEach(element => element.scrollTo(0, 0))
    this.revealThumbnail()
  }

  readonly previous = () => this.select(this.index() - 1)
  readonly next = () => this.select(this.index() + 1)
  readonly toggleZoom = () => {
    const image = this.dialog()?.querySelector<HTMLImageElement>('.zt-gallery-slide[aria-hidden="false"] img')
    this.zoomWidth(Math.max(image?.naturalWidth ?? 0, (image?.parentElement?.clientWidth ?? 0) * 2))
    this.zoomed(!this.zoomed())
  }

  readonly syncFullscreen = () => this.fullscreen(!!this.panel() && document.fullscreenElement === this.panel())

  readonly toggleFullscreen = async () => {
    const panel = this.panel()
    if (!panel) return
    if (this.fullscreen()) {
      if (document.fullscreenElement === panel) {
        try { await document.exitFullscreen() } catch { return }
      }
      this.fullscreen(false)
      return
    }
    if (document.fullscreenEnabled && panel.requestFullscreen) {
      try {
        // Fullscreen a panel inside the dialog: browsers reject fullscreen requests on dialogs.
        await panel.requestFullscreen()
        if (!this.opened() && document.fullscreenElement === panel) await document.exitFullscreen()
        this.syncFullscreen()
        return
      } catch { /* Fall back to filling the viewport when browser fullscreen is unavailable. */ }
    }
    if (this.opened()) this.fullscreen(true)
  }

  readonly startPan = (event: PointerEvent) => {
    if (!this.zoomed() || event.pointerType !== 'mouse' || event.button !== 0) return
    const element = event.currentTarget as HTMLElement
    this.drag = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop }
    element.setPointerCapture(event.pointerId)
    event.preventDefault()
  }

  readonly pan = (event: PointerEvent) => {
    if (!this.drag) return
    const element = event.currentTarget as HTMLElement
    element.scrollLeft = this.drag.left + this.drag.x - event.clientX
    element.scrollTop = this.drag.top + this.drag.y - event.clientY
  }

  readonly endPan = () => { this.drag = undefined }

  readonly keydown = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      event.preventDefault()
      if (this.fullscreen()) void this.toggleFullscreen()
      else this.close()
      return
    }
    if (event.key === 'Tab' && !event.altKey && !event.ctrlKey && !event.metaKey) {
      const dialog = this.dialog()
      const controls = [...dialog?.querySelectorAll<HTMLElement>('button:not([disabled]), [href], [tabindex="0"]') ?? []]
        .filter(element => !element.closest('[inert]') && element.getClientRects().length)
      const first = controls[0]
      const last = controls[controls.length - 1]
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
      return
    }
    if (event.altKey || event.ctrlKey || event.metaKey || this.zoomed()) return
    const actions: Record<string, () => void> = {
      ArrowLeft: this.previous, ArrowRight: this.next,
      Home: () => this.select(0), End: () => this.select(this.items().length - 1),
    }
    const action = actions[event.key]
    if (action) { event.preventDefault(); action() }
  }

  readonly touchstart = (event: TouchEvent) => {
    const touch = event.touches[0]
    this.touch = !this.zoomed() && event.touches.length === 1
      ? { x: touch.clientX, y: touch.clientY, time: Date.now() } : undefined
  }

  readonly touchend = (event: TouchEvent) => {
    const start = this.touch
    this.touch = undefined
    if (!start || event.touches.length || this.zoomed()) return
    const touch = event.changedTouches[0]
    const dx = touch.clientX - start.x
    const dy = touch.clientY - start.y
    if (Date.now() - start.time < 750 && Math.abs(dx) > 56 && Math.abs(dx) > Math.abs(dy) * 1.3) {
      if (dx < 0) this.next()
      else this.previous()
    }
  }

  readonly touchcancel = () => { this.touch = undefined }

  readonly pointerdown = (event: PointerEvent) => { this.backdropPressed = this.outside(event) }
  readonly backdropClick = (event: MouseEvent) => {
    if (this.backdropPressed && this.outside(event)) this.close()
    this.backdropPressed = false
  }

  readonly imageLoaded = (url: string) => this.loaded(new Set([...this.loaded(), url]))
  readonly imageFailed = (url: string) => this.failed(new Set([...this.failed(), url]))

  private outside(event: MouseEvent) {
    const dialog = this.dialog()
    if (!dialog || event.target !== dialog) return false
    const rect = dialog.getBoundingClientRect()
    return event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom
  }

  private revealThumbnail() {
    requestAnimationFrame(() => {
      if (!this.opened()) return
      const thumbnail = this.dialog()?.querySelector<HTMLElement>('[aria-pressed="true"].zt-gallery-thumbnail')
      thumbnail?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
    })
  }
}

const expandIcon = html`<svg
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.7"
  aria-hidden="true"
>
  <path d="M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5"/>
</svg>`
const collapseIcon = html`<svg
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.7"
  aria-hidden="true"
>
  <path d="M3 8h5V3m8 0v5h5M3 16h5v5m8 0v-5h5"/>
</svg>`
const previousIcon = html`<svg
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.8"
  aria-hidden="true"
>
  <path d="m14 6-6 6 6 6"/>
</svg>`
const nextIcon = html`<svg
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.8"
  aria-hidden="true"
>
  <path d="m10 6 6 6-6 6"/>
</svg>`
const closeIcon = html`<svg
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.8"
  aria-hidden="true"
>
  <path d="m6 6 12 12M6 18 18 6"/>
</svg>`
const zoomIcon = html`<svg
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.7"
  aria-hidden="true"
>
  <circle cx="10.5" cy="10.5" r="6.5"/>
  <path d="m16 16 5 5M7 10.5h7"/>
  <path r-if="!zoomed" d="M10.5 7v7"/>
</svg>`

/** Register with a Regor app and supply { url, title?, alt? } images. No report dependencies. */
export function defineImageGallery() {
  return defineComponent<ImageGalleryProps>(html`<div class="zt-image-gallery" role="group" :aria-label="label">
  <figure class="zt-gallery-card" r-for="image in items" :key="image.url">
    <figcaption>{{ image.title }}</figcaption>
    <button
      type="button"
      class="zt-gallery-open"
      @click="open(image.index, $event)"
      aria-haspopup="dialog"
      :aria-label="'Enlarge ' + image.title"
    >
      <img
        :src="image.url"
        :alt="image.alt || image.title"
        loading="lazy"
        decoding="async"/>
      <span class="zt-gallery-open-label"
        >${expandIcon} View image</span>
    </button>
  </figure>
  <dialog
    class="zt-image-gallery-dialog"
    :class="{ 'zt-gallery-fullscreen': fullscreen }"
    :ref="dialog"
    :aria-labelledby="titleId"
    aria-modal="true"
    @close="onClose"
    @keydown="keydown"
    @pointerdown="pointerdown"
    @click="backdropClick"
  >
    <div class="zt-gallery-panel" :ref="panel">
      <header class="zt-gallery-header">
        <div class="zt-gallery-heading" aria-live="polite" aria-atomic="true">
          <p>
            {{ label }}
            <span aria-hidden="true">&middot;</span> {{ index + 1 }}
            / {{ items.length }}
          </p>
          <h2 :id="titleId">{{ current?.title }}</h2>
        </div>
        <button
          type="button"
          class="zt-gallery-control zt-gallery-close"
          @click="close"
          aria-label="Close image gallery"
          :title="fullscreen ? 'Close image gallery' : 'Close (Esc)'"
          autofocus
        >
          ${closeIcon}
        </button>
      </header>
      <div class="zt-gallery-viewer">
        <button
          type="button"
          r-if="items.length > 1"
          class="zt-gallery-control zt-gallery-previous"
          @click="previous"
          aria-label="Previous image"
          title="Previous image (Left arrow)"
        >
          ${previousIcon}
        </button>
        <div
          class="zt-gallery-viewport"
          :class="{ 'zt-gallery-zoomed': zoomed }"
          :style="zoomStyle"
          @touchstart="touchstart"
          @touchend="touchend"
          @touchcancel="touchcancel"
        >
          <div r-if="opened" class="zt-gallery-track" :style="trackStyle">
            <div
              class="zt-gallery-slide"
              r-for="image in items"
              :key="image.url"
              role="group"
              aria-roledescription="slide"
              :aria-label="image.title + ', ' + (image.index + 1) + ' of ' + items.length"
              :aria-hidden="image.index !== index"
              :inert="image.index !== index"
            >
              <div
                class="zt-gallery-pan"
                :tabindex="image.index === index ? 0 : -1"
                :aria-label="zoomed ? 'Zoomed image. Scroll to explore.' : image.title"
                @pointerdown="startPan"
                @pointermove="pan"
                @pointerup="endPan"
                @pointercancel="endPan"
                @lostpointercapture="endPan"
              >
                <img
                  r-if="!failed.has(image.url)"
                  :src="image.url"
                  :alt="image.alt || image.title"
                  decoding="async"
                  draggable="false"
                  @load="imageLoaded(image.url)"
                  @error="imageFailed(image.url)"/>
                <p
                  r-if="failed.has(image.url)"
                  class="zt-gallery-status"
                  role="status"
                >
                  This image could not be loaded. You can still browse the other
                  images.
                </p>
                <p
                  r-if="!loaded.has(image.url) && !failed.has(image.url)"
                  class="zt-gallery-status"
                  role="status"
                >
                  Loading image&hellip;
                </p>
              </div>
            </div>
          </div>
        </div>
        <button
          type="button"
          r-if="items.length > 1"
          class="zt-gallery-control zt-gallery-next"
          @click="next"
          aria-label="Next image"
          title="Next image (Right arrow)"
        >
          ${nextIcon}
        </button>
      </div>
      <footer class="zt-gallery-footer">
        <div class="zt-gallery-toolbar">
          <p class="zt-gallery-help">
            <span r-if="zoomed">Drag or scroll to explore</span><span r-if="!zoomed"
              ><span class="zt-gallery-keyboard-help"
                >Use &larr; &rarr; to browse
                <span aria-hidden="true">&middot;</span>
                {{ fullscreen ? 'Esc to exit fullscreen' : 'Esc to close' }}</span><span class="zt-gallery-touch-help">Swipe to browse</span></span>
          </p>
          <div class="zt-gallery-tools">
            <button
              type="button"
              class="zt-gallery-control zt-gallery-zoom"
              @click="toggleZoom"
              :aria-pressed="zoomed"
              :disabled="!loaded.has(current?.url) || failed.has(current?.url)"
            >
              ${zoomIcon} {{ zoomed ? 'Fit to window' : 'Zoom in' }}
            </button>
            <button
              type="button"
              class="zt-gallery-control zt-gallery-fullscreen-toggle"
              @click="toggleFullscreen"
              :aria-pressed="fullscreen"
              :title="fullscreen ? 'Exit fullscreen (Esc)' : 'Enter fullscreen'"
            >
              <span r-if="!fullscreen">${expandIcon}</span><span r-if="fullscreen">${collapseIcon}</span>
              {{ fullscreen ? 'Exit fullscreen' : 'Fullscreen' }}
            </button>
          </div>
        </div>
        <nav
          r-if="items.length > 1"
          class="zt-gallery-thumbnails"
          aria-label="Choose an image"
        >
          <button
            type="button"
            class="zt-gallery-thumbnail"
            r-for="image in items"
            :key="image.url"
            @click="select(image.index)"
            :aria-pressed="image.index === index"
            :aria-label="'View ' + image.title"
            :title="image.title"
          >
            <img :src="image.url" alt="" loading="lazy" decoding="async"/>
            <span>{{ image.title }}</span>
          </button>
        </nav>
      </footer>
    </div>
  </dialog>
</div>`, {
    props: ['images', 'label'],
    context: head => {
      head.autoProps = false
      return new ImageGallery(head.props)
    },
  })
}
