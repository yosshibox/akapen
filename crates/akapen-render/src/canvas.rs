//! Phase e: the window-attached render path (spec §7.4-6 「シェルが用意した
//! ネイティブビュー…へコアが wgpu で直接描く」). This is the surface-side
//! counterpart to the headless offscreen path in [`crate::renderer`]:
//! [`SurfaceRenderer`] owns the `wgpu::Surface` bring-up (instance → surface →
//! adapter → device → configure) that [`Renderer`] deliberately left out, and
//! [`GpuCanvas`] bundles that surface with the background / baked / stroke
//! pipelines into a single "attach → render frame → resize → detach" object
//! the C ABI ([`akapen-ffi`]) can drive with only thin marshalling calls.
//!
//! The FFI layer stays a thin `extern "C"` shim (spec §7.2): every non-trivial
//! decision (surface format choice, recoverable-swapchain-state handling, the
//! background→baked→wet composite order) lives here, in the UI-agnostic core,
//! so the mac and (later) Windows shells share exactly one implementation.

use std::sync::{Arc, Mutex};

use akapen_core::coord::ViewTransform;
use akapen_core::engine::{BakeDelta, CommittedStrokeRef};

use crate::background::{BackgroundPipeline, BackgroundTexture};
use crate::bake::{apply_bake_delta, composite_frame, plan_bake, BakedTexture};
use crate::error::RendererError;
use crate::renderer::{surface_configuration, Renderer, DEFAULT_PRESENT_MODE, OFFSCREEN_FORMAT};
use crate::stroke::StrokePipeline;
use crate::surface::{self, SurfaceDesc};

/// Picks the swapchain texture format to configure the surface with.
///
/// Prefers a non-sRGB format when the surface offers one: this crate's
/// pipelines output premultiplied-alpha values that are meant to land in the
/// framebuffer verbatim (matching the non-sRGB [`crate::OFFSCREEN_FORMAT`]
/// the offscreen/`baked_tex` path uses — spec instruction "sRGB差でハマらない
/// よう非sRGBで統一"). A hardware sRGB target would apply an extra
/// linear→sRGB encode on write and shift every color, so we avoid it when a
/// linear-store format is available, falling back to the surface's own
/// preferred format (`formats[0]`) otherwise.
fn choose_surface_format(caps: &wgpu::SurfaceCapabilities) -> wgpu::TextureFormat {
    caps.formats
        .iter()
        .copied()
        .find(|f| !f.is_srgb())
        .unwrap_or_else(|| caps.formats[0])
}

/// A [`Renderer`] plus the on-screen surface it presents to. This is the
/// "surface作成〜configure" entry point [`Renderer`] left for Phase e: it
/// creates a wgpu instance, an OS surface from `desc` (via
/// [`crate::surface::create`]), requests an adapter *compatible with that
/// surface*, a device/queue, and configures the swapchain.
pub struct SurfaceRenderer {
    pub renderer: Renderer,
    pub surface: wgpu::Surface<'static>,
    pub config: wgpu::SurfaceConfiguration,
    /// Set by the `on_uncaptured_error` handler installed in [`Self::new`]
    /// whenever a `wgpu::Surface::configure` call reports a validation
    /// error. `Surface::configure`'s public API returns `()`, not a
    /// `Result` — without this handler installed, the *default* uncaptured-
    /// error behavior is to panic the whole process (discovered during the
    /// Windows HWND presentation de-risk run: a target HWND with no
    /// attached desktop/compositor session fails configure with "Invalid
    /// surface", which otherwise took the process down). [`Self::new`]
    /// checks this immediately after the first `configure` and turns it
    /// into a returned [`RendererError::SurfaceConfigure`]; [`Self::resize`]
    /// checks it too (logging rather than failing, since it has no `Result`
    /// return to propagate through).
    configure_error: Arc<Mutex<Option<String>>>,
}

impl SurfaceRenderer {
    /// Brings up a surface-attached renderer.
    ///
    /// # Safety
    /// `desc.handle`/`desc.display` must be valid native handles for
    /// `desc.kind` and must outlive the returned `SurfaceRenderer` (same
    /// contract as [`crate::surface::create`], which this forwards to).
    pub async unsafe fn new(desc: SurfaceDesc) -> Result<Self, RendererError> {
        let width = desc.width.max(1);
        let height = desc.height.max(1);

        let instance = crate::instance::create_instance();
        // SAFETY: forwarded to this function's caller (see doc comment).
        let surface = unsafe { surface::create(&instance, desc)? };

        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::default(),
                force_fallback_adapter: false,
                compatible_surface: Some(&surface),
                apply_limit_buckets: false,
            })
            .await
            .map_err(|_| RendererError::NoAdapter)?;
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("akapen-render surface device"),
                ..Default::default()
            })
            .await
            .map_err(RendererError::RequestDevice)?;

        let caps = surface.get_capabilities(&adapter);
        if caps.formats.is_empty() {
            // The adapter is not compatible with this surface at all.
            return Err(RendererError::NoAdapter);
        }
        let format = choose_surface_format(&caps);
        let config = surface_configuration(format, width, height, DEFAULT_PRESENT_MODE);

        // Install the handler *before* the first `configure` call so that a
        // validation failure there is captured, not panicked (see the
        // `configure_error` doc comment above).
        let configure_error: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
        {
            let captured = Arc::clone(&configure_error);
            device.on_uncaptured_error(Arc::new(move |err: wgpu::Error| {
                *captured.lock().expect("configure_error mutex poisoned") = Some(err.to_string());
            }));
        }
        surface.configure(&device, &config);
        if let Some(msg) = configure_error
            .lock()
            .expect("configure_error mutex poisoned")
            .take()
        {
            return Err(RendererError::SurfaceConfigure(msg));
        }

        let renderer = Renderer {
            instance,
            adapter,
            device,
            queue,
        };
        Ok(Self {
            renderer,
            surface,
            config,
            configure_error,
        })
    }

    /// Blocking wrapper over [`Self::new`] for the (non-async) C ABI call site.
    ///
    /// # Safety
    /// Same contract as [`Self::new`].
    pub unsafe fn new_blocking(desc: SurfaceDesc) -> Result<Self, RendererError> {
        // SAFETY: forwarded to caller.
        pollster::block_on(unsafe { Self::new(desc) })
    }

    pub fn format(&self) -> wgpu::TextureFormat {
        self.config.format
    }

    /// Re-configures the swapchain for a new pixel size. Both dimensions are
    /// clamped to at least 1 (a zero-sized surface is invalid and would panic
    /// wgpu's `configure`).
    pub fn resize(&mut self, width: u32, height: u32) {
        self.config.width = width.max(1);
        self.config.height = height.max(1);
        self.surface.configure(&self.renderer.device, &self.config);
        // Same handler as `new` (installed once, on the device, for its
        // lifetime) — a resize-time configure failure is caught here rather
        // than panicking, but `resize` has no `Result` to propagate through
        // (the FFI's `akapen_render_resize` is `void`), so this is logged
        // rather than surfaced to the caller. The next `render()` call will
        // simply fail to acquire a texture and skip the frame (see
        // `SurfaceRenderer::acquire`) rather than crash.
        if let Some(msg) = self
            .configure_error
            .lock()
            .expect("configure_error mutex poisoned")
            .take()
        {
            log::error!("SurfaceRenderer::resize: configure failed: {msg}");
        }
    }

    /// Acquires the next swapchain texture, transparently reconfiguring and
    /// retrying once on the recoverable `Outdated`/`Lost` states. Returns
    /// `None` for the transient `Timeout`/`Occluded` states (and a failed
    /// retry) so the caller cleanly skips a frame instead of crashing.
    fn acquire(&mut self) -> Option<wgpu::SurfaceTexture> {
        use wgpu::CurrentSurfaceTexture as C;
        match self.surface.get_current_texture() {
            C::Success(t) | C::Suboptimal(t) => Some(t),
            C::Outdated | C::Lost => {
                self.surface.configure(&self.renderer.device, &self.config);
                match self.surface.get_current_texture() {
                    C::Success(t) | C::Suboptimal(t) => Some(t),
                    _ => None,
                }
            }
            // Timeout / Occluded / Validation: skip this frame.
            _ => None,
        }
    }
}

/// The full GPU drawing surface for one open image: a [`SurfaceRenderer`] plus
/// the background/stroke pipelines (built for the *swapchain* format, per the
/// pitfall in the Phase e brief — never [`crate::OFFSCREEN_FORMAT`]), the
/// uploaded background texture, and the offscreen `baked_tex` of committed
/// strokes. One per open image; recreated on the next `attach`.
///
/// Kept decoupled from [`akapen_core::engine::Engine`]: it consumes
/// [`CommittedStrokeRef`] iterators and [`BakeDelta`] values, exactly like the
/// headless `crate::bake` path, so this crate never depends on how the engine
/// mutates its history.
pub struct GpuCanvas {
    sr: SurfaceRenderer,
    /// Draws the background + baked textures onto the on-screen surface — built
    /// for the swapchain format.
    background_pipeline: BackgroundPipeline,
    /// Draws the in-progress "wet" stroke onto the on-screen surface — built
    /// for the swapchain format (fragment target = the surface texture).
    stroke_pipeline_screen: StrokePipeline,
    /// Draws committed strokes *into* `baked_tex` — built for
    /// [`OFFSCREEN_FORMAT`] (the baked texture's own format), which on real
    /// hardware differs from the swapchain format. Using the screen pipeline
    /// here fails wgpu's render-pass/pipeline format-compatibility validation.
    stroke_pipeline_baked: StrokePipeline,
    background: BackgroundTexture,
    baked: BakedTexture,
    /// Natural (image) size — the baked texture and the ViewTransform's
    /// `buffer_w`/`buffer_h` both come from here, independent of the (possibly
    /// zoomed/panned) on-screen surface size in `sr.config`.
    buffer_w: u32,
    buffer_h: u32,
}

impl GpuCanvas {
    /// Attaches a GPU canvas to the surface described by `desc`, seeding it
    /// with the engine's background bitmap and its already-committed strokes.
    ///
    /// `background_rgba` is the straight-alpha RGBA8 background at
    /// `buffer_w x buffer_h` (from
    /// [`akapen_core::engine::Engine::background`]); it is uploaded as the
    /// bottom layer. `committed` is the engine's current committed-stroke
    /// history ([`akapen_core::engine::Engine::committed_strokes`]), baked once
    /// up front so re-attaching after strokes already exist shows them.
    ///
    /// # Safety
    /// Same native-handle contract as [`SurfaceRenderer::new`].
    pub unsafe fn attach<'a, I>(
        desc: SurfaceDesc,
        background_rgba: &[u8],
        buffer_w: u32,
        buffer_h: u32,
        committed: I,
    ) -> Result<Self, RendererError>
    where
        I: DoubleEndedIterator<Item = CommittedStrokeRef<'a>>,
    {
        // SAFETY: forwarded to caller.
        let sr = unsafe { SurfaceRenderer::new_blocking(desc)? };
        let format = sr.format();
        let device = &sr.renderer.device;
        let queue = &sr.renderer.queue;

        // On-screen pipelines MUST target the swapchain format: they draw into
        // the surface texture, whose format wgpu chose above. (Sampling FROM an
        // Rgba8Unorm baked/background texture into a Bgra8Unorm target is fine
        // — only the fragment *output* format has to match the attachment.)
        let background_pipeline = BackgroundPipeline::new(device, format);
        let stroke_pipeline_screen = StrokePipeline::new(device, format);
        // The bake pipeline draws into baked_tex (OFFSCREEN_FORMAT), which on
        // real hardware differs from the swapchain format — it needs its own
        // pipeline built for that format or wgpu rejects the render pass.
        let stroke_pipeline_baked = StrokePipeline::new(device, OFFSCREEN_FORMAT);

        let background =
            background_pipeline.set_background(device, queue, background_rgba, buffer_w, buffer_h);
        let baked = BakedTexture::new(device, queue, buffer_w, buffer_h);

        let mut canvas = Self {
            sr,
            background_pipeline,
            stroke_pipeline_screen,
            stroke_pipeline_baked,
            background,
            baked,
            buffer_w,
            buffer_h,
        };

        // Bake whatever strokes already exist (a fresh engine has none; this
        // is a full Rebuild of the committed history).
        canvas.apply_bake(BakeDelta::Rebuild, committed);
        Ok(canvas)
    }

    /// Bakes a [`BakeDelta`] worth of committed-stroke change into `baked_tex`.
    /// Thin wrapper over [`plan_bake`] + [`apply_bake_delta`]; the FFI passes
    /// the delta the engine reported plus the engine's current committed
    /// iterator.
    pub fn apply_bake<'a, I>(&mut self, delta: BakeDelta, committed: I)
    where
        I: DoubleEndedIterator<Item = CommittedStrokeRef<'a>>,
    {
        let plan = plan_bake(delta, committed);
        apply_bake_delta(
            &self.sr.renderer.device,
            &self.sr.renderer.queue,
            &self.stroke_pipeline_baked,
            &self.baked,
            plan,
        );
    }

    /// Re-configures the swapchain to a new pixel size (backing-store /
    /// window resize).
    pub fn resize(&mut self, width: u32, height: u32) {
        self.sr.resize(width, height);
    }

    pub fn buffer_size(&self) -> (u32, u32) {
        (self.buffer_w, self.buffer_h)
    }

    /// A short human-readable line identifying the attached surface's actual
    /// wgpu backend / swapchain configuration (spec §7.4-6 presentation
    /// de-risk: a Windows HWND/DX12 present-path acceptance run needs to
    /// prove *which* backend/present mode/frame-latency were actually
    /// selected, not just assume the platform default held — this is the
    /// evidence a headless SSH session can capture without a screenshot).
    pub fn backend_info(&self) -> String {
        let info = self.sr.renderer.adapter.get_info();
        format_backend_info(
            info.backend,
            self.sr.config.present_mode,
            self.sr.config.desired_maximum_frame_latency,
        )
    }

    /// Draws one on-screen frame: acquire the swapchain texture, composite
    /// background → `baked_tex` → wet ink through `view`, and present.
    /// `wet` is the in-progress stroke, if any
    /// ([`akapen_core::engine::Engine::current_stroke`]).
    ///
    /// Silently skips the frame if the swapchain texture can't be acquired
    /// (transient `Timeout`/`Occluded`) — never panics on a recoverable
    /// surface state.
    pub fn render(&mut self, wet: Option<CommittedStrokeRef<'_>>, view: &ViewTransform) {
        let Some(frame) = self.sr.acquire() else {
            return;
        };
        let target_view = frame
            .texture
            .create_view(&wgpu::TextureViewDescriptor::default());
        let (output_w, output_h) = (self.sr.config.width, self.sr.config.height);
        composite_frame(
            &self.sr.renderer.device,
            &self.sr.renderer.queue,
            &target_view,
            output_w,
            output_h,
            &self.background_pipeline,
            &self.background,
            &self.baked,
            &self.stroke_pipeline_screen,
            wet,
            view,
        );
        self.sr.renderer.queue.present(frame);
    }
}

/// Pure formatting for [`GpuCanvas::backend_info`], split out so it is
/// unit-testable without a real adapter/surface (mirrors this crate's
/// convention of keeping GPU-independent logic testable on any machine).
fn format_backend_info(
    backend: wgpu::Backend,
    present_mode: wgpu::PresentMode,
    max_frame_latency: u32,
) -> String {
    format!(
        "backend={backend:?} present_mode={present_mode:?} max_frame_latency={max_frame_latency}"
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_backend_info_reports_all_three_fields() {
        let s = format_backend_info(wgpu::Backend::Dx12, wgpu::PresentMode::Fifo, 1);
        assert_eq!(s, "backend=Dx12 present_mode=Fifo max_frame_latency=1");
    }
}
