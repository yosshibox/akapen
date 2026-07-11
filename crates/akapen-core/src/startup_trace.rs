//! コールドスタート計測基盤(「開く→一筆目」)の軽量トレース構造。
//!
//! 外部crateは使わず `std::time` のみで組む(Fable答申スコープ厳守: 最適化
//! はしない、計測のみ)。実運用の描画経路([`akapen_render`](../../akapen_render)
//! クレート)から各段の開始/終了を記録してもらい、[`StartupTrace::finish`]
//! で「段名 → 所要時間」の一覧([`Span`]の並び)を得る。
//!
//! # クロック注入
//! [`StartupTrace::new`] は `now: impl FnMut() -> Instant + Send` を受け取る。
//! 実運用では [`StartupTrace::new_with_system_clock`] が `Instant::now` を渡す
//! 既定経路になるが、テストでは決定的な仮想クロックを注入して「順序・
//! duration計算が仕様通りか」を実GPU無しで固定できる。`+ Send` は
//! `async fn` の await 跨ぎ(実運用の headless cold-start は async)や、将来
//! 別スレッドへ `StartupTrace` ごと受け渡す用途を塞がないための制約で、
//! 単一スレッド専用にする理由がない限り付けておくのが無難。
//!
//! # 不変条件
//! - 全ての `begin` された段は `end` されて初めて記録に載る(閉じ忘れは
//!   [`StartupTrace::finish`] が `Err` を返す形で検出する)。
//! - 段の入れ子は LIFO のみ許可: `end(name)` は「現在開いている段のうち
//!   最後に `begin` された段」の名前と一致しなければならず、一致しなければ
//!   [`StartupTraceError::EndOutOfOrder`]。同名の段の再利用(閉じてから
//!   再度 `begin`)は許可される。
//! - 記録された `spans` は([`end`] された順ではなく)**開始時刻順**
//!   ([`StartupTrace::finish`] が開始 sequence でソートして返す)。
//! - クロックが逆行した場合(`end` の時刻が `begin` より前)は黙って0に
//!   丸めず [`StartupTraceError::ClockWentBackwards`] を返す。

use std::time::{Duration, Instant};

/// 1段の記録: 段名と所要時間。
pub type Span = (&'static str, Duration);

/// `begin`/`end` の不変条件違反。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StartupTraceError {
    /// 同じ段名が既に開始されている(前回分がまだ `end` されていない)。
    DuplicateSpanName(&'static str),
    /// `end` が呼ばれたが対応する `begin` が無い(開いている段が無い)。
    EndWithoutBegin(&'static str),
    /// `end(name)` が呼ばれたが、現在開いている段のうち最後に `begin`
    /// された段(LIFOの先頭)が `name` と一致しない。入れ子は LIFO のみ
    /// 許可(タプルは「呼んだ名前」「実際に開いていた最後の段名」)。
    EndOutOfOrder(&'static str, &'static str),
    /// [`StartupTrace::finish`] 時点で `begin` されたまま `end` されて
    /// いない段が残っている(閉じ忘れ)。
    UnclosedSpans(Vec<&'static str>),
    /// 注入クロックが逆行した(`end` 時刻が `begin` 時刻より前)。計測
    /// 基盤としては黙って0に丸めず、契約違反として顕在化させる。
    ClockWentBackwards(&'static str),
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
            StartupTraceError::EndOutOfOrder(name, expected) => {
                write!(
                    f,
                    "end('{name}') called out of order: innermost open span is '{expected}' \
                     (nesting must close LIFO)"
                )
            }
            StartupTraceError::UnclosedSpans(names) => {
                write!(f, "unclosed spans at finish(): {names:?}")
            }
            StartupTraceError::ClockWentBackwards(name) => {
                write!(f, "clock went backwards while measuring span '{name}'")
            }
        }
    }
}

impl std::error::Error for StartupTraceError {}

/// 進行中の1段: 段名・開始時刻・開始 sequence(finish時の並び替え用)。
struct OpenSpan {
    name: &'static str,
    start: Instant,
    seq: u64,
}

/// コールドスタートの各段を名前つきで記録するトレース。
///
/// クロックは `now: Box<dyn FnMut() -> Instant + Send>` として注入される
/// (実運用は [`Self::new_with_system_clock`] が `Instant::now` を渡す既定
/// 経路)。テストはこの関数差し替えで仮想時刻を進め、順序/duration計算を
/// 決定的に固定できる。
pub struct StartupTrace {
    now: Box<dyn FnMut() -> Instant + Send>,
    open: Vec<OpenSpan>,
    /// 確定済みの段。開始 sequence を添えて保持し、[`Self::finish`] で
    /// 開始時刻順にソートしてから公開の [`Span`] へ変換する(close順は
    /// 入れ子次第でstart順と一致しないため)。
    closed: Vec<(u64, Span)>,
    next_seq: u64,
}

impl StartupTrace {
    /// 任意のクロック関数を注入して構築する。実運用は
    /// [`Self::new_with_system_clock`] を使うこと。
    pub fn new(now: impl FnMut() -> Instant + Send + 'static) -> Self {
        Self {
            now: Box::new(now),
            open: Vec::new(),
            closed: Vec::new(),
            next_seq: 0,
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
        let seq = self.next_seq;
        self.next_seq += 1;
        self.open.push(OpenSpan { name, start, seq });
        Ok(())
    }

    /// 段 `name` の計測を終了し、`spans` へ確定させる。
    ///
    /// 開いている段が無ければ [`StartupTraceError::EndWithoutBegin`]。
    /// 開いている段はあるが、その最後(LIFOの先頭)が `name` と違えば
    /// [`StartupTraceError::EndOutOfOrder`](入れ子は LIFO のみ許可)。
    /// クロックが逆行していれば [`StartupTraceError::ClockWentBackwards`]。
    pub fn end(&mut self, name: &'static str) -> Result<(), StartupTraceError> {
        let innermost = self
            .open
            .last()
            .ok_or(StartupTraceError::EndWithoutBegin(name))?;
        if innermost.name != name {
            return Err(StartupTraceError::EndOutOfOrder(name, innermost.name));
        }
        let open = self.open.pop().expect("just checked via last()");
        let end = (self.now)();
        let duration = end
            .checked_duration_since(open.start)
            .ok_or(StartupTraceError::ClockWentBackwards(name))?;
        self.closed.push((open.seq, (open.name, duration)));
        Ok(())
    }

    /// クロージャ `f` の実行時間を段 `name` として記録する便利関数。
    ///
    /// `f` が panic しても span は必ず閉じる(`catch_unwind` で捕捉して
    /// `end` を呼んでから `resume_unwind` で panic を伝播し直す)。これに
    /// より panic 経路でトレースが閉じ忘れのまま壊れることはない。
    pub fn measure<T>(&mut self, name: &'static str, f: impl FnOnce() -> T) -> T {
        // begin/end はこの構造体の不変条件(重複段名なし・LIFO)を守る限り
        // 失敗しない呼び方なので、ここでは呼び出し側のミス(同名ネスト等)
        // だけを panic で顕在化させる。
        self.begin(name)
            .expect("StartupTrace::measure: duplicate span name");
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(f));
        self.end(name)
            .expect("StartupTrace::measure: end() lost its begin()");
        match result {
            Ok(value) => value,
            Err(payload) => std::panic::resume_unwind(payload),
        }
    }

    /// 確定済みの段を開始時刻順に返す。未 `end` の段が残っていれば
    /// [`StartupTraceError::UnclosedSpans`] を返す(記録を握り潰さない)。
    pub fn finish(self) -> Result<Vec<Span>, StartupTraceError> {
        if !self.open.is_empty() {
            let names = self.open.iter().map(|s| s.name).collect();
            return Err(StartupTraceError::UnclosedSpans(names));
        }
        let mut closed = self.closed;
        closed.sort_by_key(|(seq, _)| *seq);
        Ok(closed.into_iter().map(|(_, span)| span).collect())
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
    fn nested_spans_are_recorded_in_start_order_not_close_order() {
        // begin(outer) -> begin(inner) -> end(inner) -> end(outer): inner
        // closes first, but finish() must report start order (outer, then
        // inner), per the "start order is the contract" fix.
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("outer").unwrap();
        trace.begin("inner").unwrap();
        trace.end("inner").unwrap();
        trace.end("outer").unwrap();

        let spans = trace.finish().unwrap();
        let names: Vec<_> = spans.iter().map(|(n, _)| *n).collect();
        assert_eq!(names, vec!["outer", "inner"]);
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
    fn end_out_of_lifo_order_is_rejected() {
        // outer/inner both open; ending "outer" while "inner" is still the
        // innermost open span must be rejected, not silently accepted.
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("outer").unwrap();
        trace.begin("inner").unwrap();
        let err = trace.end("outer").unwrap_err();
        assert_eq!(err, StartupTraceError::EndOutOfOrder("outer", "inner"));
        // trace is left with both spans still open; clean up so this test
        // doesn't leak an assumption about internal state.
        trace.end("inner").unwrap();
        trace.end("outer").unwrap();
    }

    #[test]
    fn finish_detects_unclosed_spans() {
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        trace.begin("a").unwrap();
        trace.begin("b").unwrap();
        trace.end("b").unwrap();
        // "a" is never ended.
        let err = trace.finish().unwrap_err();
        assert_eq!(err, StartupTraceError::UnclosedSpans(vec!["a"]));
    }

    #[test]
    fn clock_going_backwards_is_reported_not_rounded_to_zero() {
        // A clock that returns an earlier Instant on the second call (e.g. a
        // buggy or non-monotonic injected clock) must surface as an error,
        // not silently saturate to Duration::ZERO.
        let base = Instant::now();
        let mut calls = vec![base + Duration::from_millis(10), base].into_iter();
        let mut trace = StartupTrace::new(move || calls.next().unwrap());
        trace.begin("a").unwrap();
        let err = trace.end("a").unwrap_err();
        assert_eq!(err, StartupTraceError::ClockWentBackwards("a"));
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
    fn measure_closes_its_span_even_when_the_closure_panics() {
        // A panicking closure must not leave the span open (which would
        // otherwise surface as UnclosedSpans and hide the real panic, or
        // leave the trace unusable for whatever measurement runs next).
        let mut trace = StartupTrace::new(virtual_clock(Duration::from_millis(1)));
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            trace.measure("will_panic", || {
                panic!("boom");
            });
        }));
        assert!(result.is_err(), "panic must still propagate to the caller");

        let spans = trace.finish().unwrap();
        assert_eq!(spans.len(), 1);
        assert_eq!(spans[0].0, "will_panic");
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
