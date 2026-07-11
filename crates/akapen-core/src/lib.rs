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

pub mod coord;
pub mod stroke;
pub mod vector;

pub use coord::{client_to_canvas_point, ClientToCanvasInput, Point2};
pub use stroke::{Point, PointerKind, Stroke, Tool};
pub use vector::{StrokeDoc, VectorDoc, VECTOR_SCHEMA};
