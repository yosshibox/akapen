//! コールドスタート実測ハーネス。
//!
//! [`akapen_render::run_headless_cold_start_blocking`] を**このプロセス内で
//! 1回だけ**走らせ、[`akapen_core::format_spans`] で整形した各段の所要時間を
//! stdout に出力する。コールド計測はプロセス単位 —
//! ドライバのシェーダキャッシュ等で2回目以降の実行は速くなるため、
//! 「複数回計測して比較する」場合は必ずこのプロセスを都度新規起動すること
//! (このバイナリ自身はループしない)。
//!
//! 代表的な background サイズ(既定 1920x1080)は `--width`/`--height` で
//! 上書きできる。
//!
//! Usage:
//!   cargo run -p akapen-render --example cold_start_measure
//!   cargo run -p akapen-render --example cold_start_measure --release
//!   cargo run -p akapen-render --example cold_start_measure -- --width 1920 --height 1080
//!
//! `AKAPEN_REQUIRE_GPU=1` を設定すると、GPUアダプタ/デバイスが取得できない
//! 場合にスキップではなくプロセス終了コード1で失敗する(この計測用途では
//! 既定でハードフェイルにする: 計測できていないのに緑相当で終わるのを防ぐ)。

use akapen_core::{format_spans, StartupTrace};
use akapen_render::startup::run_headless_cold_start_blocking;

fn parse_dim(args: &[String], flag: &str, default: u32) -> u32 {
    args.iter()
        .position(|a| a == flag)
        .and_then(|i| args.get(i + 1))
        .and_then(|v| v.parse::<u32>().ok())
        .unwrap_or(default)
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let width = parse_dim(&args, "--width", 1920);
    let height = parse_dim(&args, "--height", 1080);

    eprintln!(
        "[cold_start_measure] pid={} background={width}x{height}",
        std::process::id()
    );

    let mut trace = StartupTrace::new_with_system_clock();
    let result = run_headless_cold_start_blocking(&mut trace, (width, height));

    match result {
        Ok(pixels) => {
            eprintln!("[cold_start_measure] readback_len={}", pixels.len());
        }
        Err(e) => {
            if std::env::var_os("AKAPEN_REQUIRE_GPU").as_deref() == Some(std::ffi::OsStr::new("1"))
            {
                eprintln!(
                    "[cold_start_measure] AKAPEN_REQUIRE_GPU=1 だが GPU アダプタ/デバイスが\
                     取得できなかった: {e}"
                );
                std::process::exit(1);
            }
            eprintln!(
                "[cold_start_measure] no GPU adapter/device available, skipping ({e}); \
                 set AKAPEN_REQUIRE_GPU=1 to make this a hard failure."
            );
            return;
        }
    }

    let spans = match trace.finish() {
        Ok(spans) => spans,
        Err(e) => {
            eprintln!("[cold_start_measure] trace had unclosed spans: {e}");
            std::process::exit(1);
        }
    };

    println!("{}", format_spans(&spans));
}
