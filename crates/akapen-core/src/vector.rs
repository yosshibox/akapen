//! Vector JSON document schema (spec §4.3 / §7.2).
//!
//! This is the **compatibility key** with VEDA's `SUBMISSION_ANNOTATED`
//! (`vectorFile`). One stroke is
//! `{kind, points:[{x,y,p}], size, color, erase, opacity, smoothing}` and
//! **`points[].p` (per-point pressure) is required** (spec §5.5). A stroke that
//! omits pressure is treated as non-compatible.
//!
//! `schema` carries a version tag so future readers can stay backward
//! compatible (VEDA's "old app can ignore" discipline). Kept `veda-annot-1`
//! for drop-in interchange with the reference implementation, plus the
//! mandatory-pressure contract layered on top.

use serde::{Deserialize, Serialize};

/// Schema tag written into every document (spec §7.2: schema carries a version).
pub const VECTOR_SCHEMA: &str = "veda-annot-1";

/// A single pressure-bearing point in the serialized document.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct VectorPoint {
    pub x: f64,
    pub y: f64,
    /// Per-point pressure, required (spec §5.5). No default: absence is an error.
    pub p: f64,
}

/// A single serialized stroke.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct StrokeDoc {
    /// Tool kind, e.g. "pen", "eraser", "line", "arrow", "rect", "ellipse", "text".
    pub kind: String,
    pub points: Vec<VectorPoint>,
    pub size: f64,
    /// Color as a CSS-style string (kept string for VEDA interchange), e.g. "#ff0000".
    pub color: String,
    #[serde(default)]
    pub erase: bool,
    pub opacity: f64,
    /// Smoothing setting used when drawn: "off" | "weak" | "strong".
    #[serde(default)]
    pub smoothing: String,
    /// Optional timecode (seconds) for video-frame-derived strokes (spec §4.6).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timecode: Option<f64>,
}

/// The whole vector document for one reviewed image.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct VectorDoc {
    pub schema: String,
    /// Natural (original) image width the coordinates are expressed in.
    pub natural_w: u32,
    /// Natural (original) image height.
    pub natural_h: u32,
    pub strokes: Vec<StrokeDoc>,
}

impl VectorDoc {
    pub fn new(natural_w: u32, natural_h: u32) -> Self {
        VectorDoc {
            schema: VECTOR_SCHEMA.to_string(),
            natural_w,
            natural_h,
            strokes: Vec::new(),
        }
    }

    /// Serialize to a JSON string.
    pub fn to_json(&self) -> serde_json::Result<String> {
        serde_json::to_string(self)
    }

    /// Parse from a JSON string.
    pub fn from_json(s: &str) -> serde_json::Result<Self> {
        serde_json::from_str(s)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_doc() -> VectorDoc {
        let mut doc = VectorDoc::new(1920, 1080);
        doc.strokes.push(StrokeDoc {
            kind: "pen".to_string(),
            points: vec![
                VectorPoint {
                    x: 10.0,
                    y: 20.0,
                    p: 0.2,
                },
                VectorPoint {
                    x: 12.5,
                    y: 22.0,
                    p: 0.55,
                },
                VectorPoint {
                    x: 15.0,
                    y: 24.0,
                    p: 0.9,
                },
            ],
            size: 6.0,
            color: "#ff0000".to_string(),
            erase: false,
            opacity: 1.0,
            smoothing: "weak".to_string(),
            timecode: None,
        });
        doc
    }

    #[test]
    fn roundtrip_preserves_document() {
        let doc = sample_doc();
        let json = doc.to_json().unwrap();
        let back = VectorDoc::from_json(&json).unwrap();
        assert_eq!(doc, back);
    }

    #[test]
    fn schema_tag_is_written() {
        let doc = sample_doc();
        let json = doc.to_json().unwrap();
        assert!(json.contains("veda-annot-1"));
    }

    #[test]
    fn per_point_pressure_is_serialized() {
        let doc = sample_doc();
        let json = doc.to_json().unwrap();
        // Each point must carry "p".
        assert_eq!(json.matches("\"p\":").count(), 3);
    }

    #[test]
    fn missing_pressure_is_rejected() {
        // A point without "p" is non-compatible and must fail to parse.
        let json = r##"{"schema":"veda-annot-1","natural_w":100,"natural_h":100,
            "strokes":[{"kind":"pen","points":[{"x":1.0,"y":2.0}],
            "size":6.0,"color":"#ff0000","opacity":1.0}]}"##;
        let parsed = VectorDoc::from_json(json);
        assert!(
            parsed.is_err(),
            "missing per-point pressure must be rejected"
        );
    }

    #[test]
    fn video_frame_timecode_roundtrips() {
        let mut doc = sample_doc();
        doc.strokes[0].timecode = Some(12.5);
        let json = doc.to_json().unwrap();
        assert!(json.contains("timecode"));
        let back = VectorDoc::from_json(&json).unwrap();
        assert_eq!(back.strokes[0].timecode, Some(12.5));
    }
}
