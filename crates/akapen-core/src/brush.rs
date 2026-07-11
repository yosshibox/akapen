//! Brush pipeline: pressure → line-width mapping (spec §5.3).
//!
//! The core stores raw per-point pressure (`0..=1`) and never bakes the curve
//! into the stored point (spec §5.5). Width is derived at draw time:
//!
//! ```text
//! width = min_width + (max_width - min_width) * curve(pressure)
//! ```
//!
//! `curve` is the CSP-style "pressure setting": `Normal` is the identity,
//! `Soft` lifts the low-pressure end (thin strokes get thicker sooner), `Hard`
//! suppresses it (you have to press harder to get width). Mouse input (no
//! pressure) is fed `pressure = 1.0` by the shell and therefore draws at
//! `max_width` — a constant line, matching the reference behaviour.

/// Pressure response curve. Mirrors CSP's soft / normal / hard presets
/// (spec §5.3). The C ABI `PressureCurve` enum keeps this order.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PressureCurve {
    /// Identity: `curve(p) = p`.
    Normal,
    /// Lifts the low-pressure end: `curve(p) = p^0.5`.
    Soft,
    /// Suppresses the low-pressure end: `curve(p) = p^2`.
    Hard,
}

impl PressureCurve {
    /// Applies the curve to a pressure value. Input is clamped to `0..=1`.
    pub fn apply(self, pressure: f64) -> f64 {
        let p = pressure.clamp(0.0, 1.0);
        match self {
            PressureCurve::Normal => p,
            PressureCurve::Soft => p.sqrt(),
            PressureCurve::Hard => p * p,
        }
    }
}

/// Brush parameters that turn a raw pressure into a stroke width in px.
///
/// `min_width`/`max_width` are the explicit CSP-style bounds (spec §5.3). The
/// reference implementation used `size * max(0.3, pressure)`; we keep that as
/// the default relationship via [`Brush::from_size`] but expose the bounds so
/// the curve can be tuned independently.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Brush {
    pub min_width: f64,
    pub max_width: f64,
    pub curve: PressureCurve,
    /// Whether pressure also modulates opacity (spec §5.3, default off).
    pub opacity_from_pressure: bool,
}

impl Brush {
    /// Builds a brush from a nominal `size`, using the reference lower bound of
    /// `size * 0.3` as `min_width` and `size` as `max_width`.
    pub fn from_size(size: f64) -> Self {
        Brush {
            min_width: (size * 0.3).max(0.5),
            max_width: size.max(0.5),
            curve: PressureCurve::Normal,
            opacity_from_pressure: false,
        }
    }

    /// Width in px for a given raw pressure.
    pub fn width_for(&self, pressure: f64) -> f64 {
        let c = self.curve.apply(pressure);
        let w = self.min_width + (self.max_width - self.min_width) * c;
        w.max(0.5)
    }

    /// Opacity multiplier for a given raw pressure (1.0 unless
    /// `opacity_from_pressure` is set).
    pub fn opacity_for(&self, pressure: f64) -> f64 {
        if self.opacity_from_pressure {
            self.curve.apply(pressure).clamp(0.0, 1.0)
        } else {
            1.0
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const EPS: f64 = 1e-9;

    #[test]
    fn normal_curve_is_identity() {
        assert!((PressureCurve::Normal.apply(0.3) - 0.3).abs() < EPS);
        assert!((PressureCurve::Normal.apply(0.0) - 0.0).abs() < EPS);
        assert!((PressureCurve::Normal.apply(1.0) - 1.0).abs() < EPS);
    }

    #[test]
    fn soft_lifts_low_pressure_hard_suppresses_it() {
        // At mid pressure, soft > normal > hard.
        let p = 0.25;
        let soft = PressureCurve::Soft.apply(p);
        let normal = PressureCurve::Normal.apply(p);
        let hard = PressureCurve::Hard.apply(p);
        assert!(soft > normal, "soft {soft} should exceed normal {normal}");
        assert!(hard < normal, "hard {hard} should be below normal {normal}");
        // Endpoints are fixed for every curve.
        for c in [PressureCurve::Soft, PressureCurve::Hard] {
            assert!(c.apply(0.0).abs() < EPS);
            assert!((c.apply(1.0) - 1.0).abs() < EPS);
        }
    }

    #[test]
    fn width_increases_monotonically_with_pressure() {
        let b = Brush::from_size(10.0);
        let mut last = 0.0;
        for i in 0..=10 {
            let p = i as f64 / 10.0;
            let w = b.width_for(p);
            assert!(w >= last, "width must be monotonic in pressure");
            last = w;
        }
        // Bounds hold.
        assert!((b.width_for(0.0) - 3.0).abs() < 1e-6, "min = size*0.3");
        assert!((b.width_for(1.0) - 10.0).abs() < 1e-6, "max = size");
    }

    #[test]
    fn width_never_below_half_px() {
        let b = Brush::from_size(0.4);
        assert!(b.width_for(0.0) >= 0.5);
    }

    #[test]
    fn pressure_is_clamped() {
        let b = Brush::from_size(10.0);
        assert!((b.width_for(-1.0) - b.width_for(0.0)).abs() < EPS);
        assert!((b.width_for(2.0) - b.width_for(1.0)).abs() < EPS);
    }

    #[test]
    fn opacity_from_pressure_toggle() {
        let mut b = Brush::from_size(10.0);
        assert!((b.opacity_for(0.3) - 1.0).abs() < EPS, "off by default");
        b.opacity_from_pressure = true;
        assert!((b.opacity_for(0.5) - PressureCurve::Normal.apply(0.5)).abs() < EPS);
    }
}
