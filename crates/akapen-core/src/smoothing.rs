//! Stroke smoothing — ported 1:1 from the VEDA reference `smoothPoints`
//! (`renderer/js/annotate.js`).
//!
//! A centred moving average over the point positions dampens hand jitter and
//! the wobble of freehand loops. **Only the coordinates are averaged; the
//! per-point pressure `p` is preserved unchanged** (spec §5.3 — smoothing must
//! not "flatten" pressure).

use crate::stroke::Point;

/// Smoothing strength. Matches the reference's `off | weak | strong` and the
/// half-window widths it uses (`weak` = 1, `strong` = 3).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Smoothing {
    Off,
    Weak,
    Strong,
}

impl Smoothing {
    /// Half-window width used by the moving average (reference: weak=1,
    /// strong=3; off short-circuits before this is read).
    fn half_window(self) -> usize {
        match self {
            Smoothing::Off => 0,
            Smoothing::Weak => 1,
            Smoothing::Strong => 3,
        }
    }

    /// Serialized tag written into the vector JSON (`smoothing` field).
    pub fn as_tag(self) -> &'static str {
        match self {
            Smoothing::Off => "off",
            Smoothing::Weak => "weak",
            Smoothing::Strong => "strong",
        }
    }

    /// Parses the vector JSON tag; unknown/empty values mean `Off`.
    pub fn from_tag(tag: &str) -> Self {
        match tag {
            "weak" => Smoothing::Weak,
            "strong" => Smoothing::Strong,
            _ => Smoothing::Off,
        }
    }
}

/// Centred moving average of point positions, preserving pressure.
///
/// Equivalent to `smoothPoints(pts, level)` in the reference: for fewer than
/// three points, or `Off`, the input is returned unchanged.
pub fn smooth_points(pts: &[Point], level: Smoothing) -> Vec<Point> {
    if level == Smoothing::Off || pts.len() < 3 {
        return pts.to_vec();
    }
    let win = level.half_window() as isize;
    let n = pts.len() as isize;
    let mut out = Vec::with_capacity(pts.len());
    for i in 0..n {
        let mut sx = 0.0;
        let mut sy = 0.0;
        let mut count = 0.0;
        let lo = (i - win).max(0);
        let hi = (i + win).min(n - 1);
        for j in lo..=hi {
            let p = &pts[j as usize];
            sx += p.x;
            sy += p.y;
            count += 1.0;
        }
        out.push(Point {
            x: sx / count,
            y: sy / count,
            // Pressure carried through untouched (spec §5.3).
            p: pts[i as usize].p,
        });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pts(coords: &[(f64, f64, f64)]) -> Vec<Point> {
        coords
            .iter()
            .map(|&(x, y, p)| Point::new(x, y, p))
            .collect()
    }

    #[test]
    fn off_returns_input_unchanged() {
        let input = pts(&[(0.0, 0.0, 0.1), (5.0, 5.0, 0.9), (10.0, 0.0, 0.3)]);
        assert_eq!(smooth_points(&input, Smoothing::Off), input);
    }

    #[test]
    fn short_strokes_are_untouched() {
        let input = pts(&[(0.0, 0.0, 0.1), (10.0, 10.0, 0.5)]);
        assert_eq!(smooth_points(&input, Smoothing::Weak), input);
    }

    #[test]
    fn pressure_is_preserved_exactly() {
        let input = pts(&[
            (0.0, 0.0, 0.11),
            (2.0, 8.0, 0.42),
            (4.0, 0.0, 0.77),
            (6.0, 8.0, 0.95),
            (8.0, 0.0, 0.33),
        ]);
        let out = smooth_points(&input, Smoothing::Strong);
        for (a, b) in input.iter().zip(out.iter()) {
            assert_eq!(a.p, b.p, "pressure must survive smoothing");
        }
    }

    #[test]
    fn weak_matches_reference_moving_average() {
        // Reference formula, half-window 1: out[i] = mean of neighbours.
        let input = pts(&[
            (0.0, 0.0, 0.5),
            (6.0, 0.0, 0.5),
            (0.0, 0.0, 0.5),
            (6.0, 0.0, 0.5),
        ]);
        let out = smooth_points(&input, Smoothing::Weak);
        // i=0: mean of {0,6} = 3; i=1: mean of {0,6,0}=2; i=2: mean {6,0,6}=4;
        // i=3: mean of {0,6} = 3.
        let expect_x = [3.0, 2.0, 4.0, 3.0];
        for (o, ex) in out.iter().zip(expect_x.iter()) {
            assert!((o.x - ex).abs() < 1e-9, "got {}, want {ex}", o.x);
        }
    }

    #[test]
    fn tag_roundtrip() {
        for s in [Smoothing::Off, Smoothing::Weak, Smoothing::Strong] {
            assert_eq!(Smoothing::from_tag(s.as_tag()), s);
        }
        assert_eq!(Smoothing::from_tag("bogus"), Smoothing::Off);
    }
}
