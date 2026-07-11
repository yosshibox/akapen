//! コールドスタート計測基盤(「開く→一筆目」)の軽量トレース構造。
//!
//! 外部crateは使わず `std::time` のみで組む(Fable答申スコープ厳守: 最適化
//! はしない、計測のみ)。実運用の描画経路([`akapen_render`](../../akapen_render)
//! クレート)から各段の開始/終了を記録してもらい、[`StartupTrace::finish`]
//! で「段名 → 所要時間」の一覧([`Span`]の並び)を得る。
//!
//! # クロック注入
//! [`StartupTrace::new`] は `now: impl FnMut() -> Instant` を受け取る。実運用
//! では [`StartupTrace::new_with_system_clock`] が `Instant::now` を渡す既定
//! 経路になるが、テストでは決定的な仮想クロックを注入して「順序・
//! duration計算が仕様通りか」を実GPU無しで固定できる。
//!
//! # 不変条件
//! - 全ての `begin` された段は `end` されて初めて記録に載る(閉じ忘れは
//!   [`StartupTrace::finish`] が `Err` を返す形で検出する)。
//! - 段の開始時刻は単調増加(注入クロックが逆行しない限り自明。ただし
//!   同名の段が二重に開始されるのは禁止 = 重複段名エラー)。
//! - 記録された `spans` は開始時刻順。

use std::time::{Duration, Instant};

/// 1段の記録: 段名と所要時間。
pub type Span = (&'static str, Duration);

/// `begin`/`end` の不変条件違反。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StartupTraceError {
    /// 同じ段名が既に開始されている(前回分がまだ `end` されていない)。
    DuplicateSpanName(&'static str),
    /// `end` が呼ばれたが対応する `begin` が無い、またはその段は既に
    /// `end` 済み。
    EndWithoutBegin(&'static str),
    /// [`StartupTrace::finish`] 時点で `begin` されたまま `end` されて
    /// いない段が残っている(閉じ忘れ)。
    UnclosedSpans(Vec<&'static str>),
}

impl std::fmt::Display for StartupTraceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            StartupTraceError::DuplicateSpanName(name) => {
                write!(f, "span '{name}' was already begun (not yet ended)")
            }
            StartupTraceError::EndWithoutBegin(name) => {
                write!(f, "end('{name}') has no matching open begin")
            }
            StartupTraceError::UnclosedSpans(names) => {
                write!(f, "unclosed spans at finish(): {names:?}")
            }
        }
    }
}

impl std::error::Error for StartupTraceError {}

/// 進行中の1段: 段名と開始時刻。
struct OpenSpan {
    name: &'static str,
    start: Instant,
}

/// コールドスタートの各段を名前つきで記録するトレース。
///
/// クロックは `now: Box<dyn FnMut() -> Instant>` として注入される(実運用は
/// [`Self::new_with_system_clock`] が `Instant::now` を渡す既定経路)。テスト
/// はこの関数差し替えで仮想時刻を進め、順序/duration計算を決定的に固定
/// できる。
pub struct StartupTrace {
    now: Box<dyn FnMut() -> Instant>,
    open: Vec<OpenSpan>,
    closed: Vec<Span>,
}

impl StartupTrace {
    /// 任意のクロック関数を注入して構築する。実運用は
    /// [`Self::new_with_system_clock`] を使うこと。
    pub fn new(now: impl FnMut() -> Instant + 'static) -> Self {
        Self {
            now: Box::new(now),
            open: Vec::new(),
            closed: Vec::new(),
        }
    }

    /// 実運用既定: `std::time::Instant::now` をクロックに使う。
    pub fn new_with_system_clock() -> Self {
        Self::new(Instant::now)
    }

    /// 段 `name` の計測を開始する。同名の段が既に開始中(未`end`)なら
    /// [`StartupTraceError::DuplicateSpanName`]。
    pub fn begin(&mut self, name: &'static str) -> Result<(), StartupTraceError> {
        if self.open.iter().any(|s| s.name == name) {
            return Err(StartupTraceError::DuplicateSpanName(name));
        }
        let start = (self.now)();
        self.open.push(OpenSpan { name, start });
        Ok(())
    }

    /// 段 `name` の計測を終了し、`spans` へ確定させる。対応する `begin` が
    /// 無ければ [`StartupTraceError::EndWithoutBegin`]。
    pub fn end(&mut self, name: &'static str) -> Result<(), StartupTraceError> {
        let idx = self
            .open
            .iter()
            .position(|s| s.name == name)
            .ok_or(StartupTraceError::EndWithoutBegin(name))?;
        let open = self.open.remove(idx);
        let end = (self.now)();
        let duration = end.saturating_duration_since(open.start);
        self.closed.push((open.name, duration));
        Ok(())
    }

    /// `begin`/`end` を1回で呼ぶスコープガードの代わりに使える便利関数:
    /// クロージャ `f` の実行時間を段 `name` として記録する。
    pub fn measure<T>(&mut self, name: &'static str, f: impl FnOnce() -> T) -> T {
        // begin/end はこの構造体の不変条件(重複段名なし)を守る限り失敗
        // しない呼び方なので、ここでは呼び出し側のミス(同名ネスト)だけを
        // panicで顕在化させる — 計測基盤自体が黙って壊れたトレースを返さ
        // ないようにするため。
        self.begin(name)
            .expect("StartupTrace::measure: duplicate span name");
        let result = f();
        self.end(name)
            .expect("StartupTrace::measure: end() lost its begin()");
        result
    }

    /// 確定済みの段を開始時刻順に返す。未 `end` の段が残っていれば
    /// [`StartupTraceError::UnclosedSpans`] を返す(記録を握り潰さない)。
    pub fn finish(self) -> Result<Vec<Span>, StartupTraceError> {
        if !self.open.is_empty() {
            let names = self.open.iter().map(|s| s.name).collect();
            return Err(StartupTraceError::UnclosedSpans(names));
        }
        Ok(self.closed)
    }
}

/// [`StartupTrace::finish`] で得た段の一覧を、人間が実機ログで読める1行/段
/// の形式に整形する(spec: 「ログ整形1関数」)。`std` のみ。
///
/// 例:
/// ```text
/// instance_create: 1.2ms
/// adapter_request: 8.4ms
/// ```
pub fn format_spans(spans: &[Span]) -> String {
    let mut out = String::new();
    for (name, duration) in spans {
        out.push_str(&format!(
            "{name}: {:.3}ms\n",
            duration.as_secs_f64() * 1000.0
        ));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 仮想クロック: 呼ぶたびに固定刻みで進む決定的な `Instant` を返す。
    /// 実 GPU/実時間に依存せず順序・duration計算を固定するためのテスト
    /// 専用ヘルパ。
    fn virtual_clock(step: Duration) -> impl FnMut() -> Instant {
        let base = Instant::now();
        let mut elapsed = Duration::ZERO;
        move || {
            let t = base + elapsed;
            elapsed += step;
            t
        }
    }

    #[test]
    fn records_all_spans_in_start_order_with_expected_durations() {
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("a").unwrap();
        trace.end("a").unwrap();
        trace.begin("b").unwrap();
        trace.end("b").unwrap();
        trace.begin("c").unwrap();
        trace.end("c").unwrap();

        let spans = trace.finish().unwrap();
        let names: Vec<_> = spans.iter().map(|(n, _)| *n).collect();
        assert_eq!(names, vec!["a", "b", "c"], "spans must be in start order");
        // virtual_clock advances by exactly 1ms per call, so every span's
        // begin->end is exactly 1ms.
        for (_, d) in &spans {
            assert_eq!(*d, Duration::from_millis(1));
        }
    }

    #[test]
    fn overlapping_spans_are_recorded_in_the_order_they_close() {
        // begin(outer) -> begin(inner) -> end(inner) -> end(outer): a nested
        // span still records both, closed-order (inner first).
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("outer").unwrap();
        trace.begin("inner").unwrap();
        trace.end("inner").unwrap();
        trace.end("outer").unwrap();

        let spans = trace.finish().unwrap();
        let names: Vec<_> = spans.iter().map(|(n, _)| *n).collect();
        assert_eq!(names, vec!["inner", "outer"]);
    }

    #[test]
    fn duplicate_span_name_while_open_is_rejected() {
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("a").unwrap();
        let err = trace.begin("a").unwrap_err();
        assert_eq!(err, StartupTraceError::DuplicateSpanName("a"));
    }

    #[test]
    fn same_span_name_can_be_reused_after_it_is_closed() {
        // Not a "duplicate": begin/end/begin/end for the same name is a
        // legitimate re-measurement, and both recordings must survive.
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("a").unwrap();
        trace.end("a").unwrap();
        trace.begin("a").unwrap();
        trace.end("a").unwrap();

        let spans = trace.finish().unwrap();
        assert_eq!(spans.len(), 2);
        assert_eq!(spans[0].0, "a");
        assert_eq!(spans[1].0, "a");
    }

    #[test]
    fn end_without_matching_begin_is_rejected() {
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        let err = trace.end("never_begun").unwrap_err();
        assert_eq!(err, StartupTraceError::EndWithoutBegin("never_begun"));
    }

    #[test]
    fn finish_detects_unclosed_spans() {
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("a").unwrap();
        trace.begin("b").unwrap();
        trace.end("a").unwrap();
        // "b" is never ended.
        let err = trace.finish().unwrap_err();
        assert_eq!(err, StartupTraceError::UnclosedSpans(vec!["b"]));
    }

    #[test]
    fn measure_records_the_closure_as_a_span() {
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        let result = trace.measure("work", || 42);
        assert_eq!(result, 42);
        let spans = trace.finish().unwrap();
        assert_eq!(spans, vec![("work", Duration::from_millis(1))]);
    }

    #[test]
    fn format_spans_renders_one_line_per_span_with_millisecond_duration() {
        let spans = vec![
            ("instance_create", Duration::from_micros(1200)),
            ("adapter_request", Duration::from_millis(8)),
        ];
        let out = format_spans(&spans);
        assert_eq!(out, "instance_create: 1.200ms\nadapter_request: 8.000ms\n");
    }

    #[test]
    fn new_with_system_clock_produces_monotonic_real_durations() {
        // Not a threshold assertion (no absolute value checked) — only that
        // the real-clock wiring produces a non-negative, recorded span.
        let mut trace = StartupTrace::new_with_system_clock();
        trace.begin("real").unwrap();
        std::thread::sleep(Duration::from_millis(1));
        trace.end("real").unwrap();
        let spans = trace.finish().unwrap();
        assert_eq!(spans.len(), 1);
        assert_eq!(spans[0].0, "real");
        assert!(spans[0].1 >= Duration::from_millis(1));
    }
}
