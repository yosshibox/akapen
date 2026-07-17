//! Akapen core (M0 skeleton).
//!
//! This crate is the UI-framework-agnostic heart of Akapen (see spec §7.1).
//! It only knows about normalized pointer samples, stroke geometry and the
//! vector document schema — never about SwiftUI / WinUI / Electron types.
//!
//! M0 scope (spec §9 / roadmap): stroke model with **mandatory per-point
//! pressure**, the first coordinate-transform functions ported from the VEDA
//! reference implementation (`lib/annotate-geometry.js`), and the
//! serde types for the `veda-annot-1`-compatible vector JSON schema.

pub mod brush;
pub mod coord;
pub mod engine;
pub mod export;
pub mod keymap;
pub mod palm;
pub mod raster;
pub mod smoothing;
pub mod startup_trace;
pub mod stroke;
pub mod tessellate;
pub mod vector;

pub use brush::{Brush, PressureCurve};
pub use coord::{client_to_canvas_point, ClientToCanvasInput, Point2, ViewTransform};
pub use engine::{BakeDelta, CommittedStrokeRef, Engine, Phase, PointerSample};
pub use export::ExportSet;
pub use keymap::{
    resolve as resolve_key, resolve_preset as resolve_key_preset, Action, KeyInput, KeymapPreset,
    Modifiers, PhysicalKey,
};
pub use palm::{route as palm_route, PalmState, Routing, PEN_PRIORITY_LOCK_MS};
pub use raster::RgbaBuffer;
pub use smoothing::{smooth_points, Smoothing};
pub use startup_trace::{format_spans, Span, StartupTrace, StartupTraceError};
pub use stroke::{Point, PointerKind, Stroke, Tool};
pub use tessellate::{bounding_box, tessellate_stroke, Vertex};
pub use vector::{StrokeDoc, VectorDoc, VECTOR_SCHEMA};
