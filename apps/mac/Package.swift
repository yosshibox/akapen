// swift-tools-version:5.9
//
// Akapen macOS shell (spec §7.1). Thin SwiftUI/AppKit layer over the Rust core,
// linked through the hand-written C ABI (`CAkapen`). Business logic stays in the
// Rust core; this package only opens files, captures NSEvent tablet pressure,
// shows the composited image, and drives save / next / prev.
//
// Build prerequisite: the Rust static library must exist at
// `../../target/debug/libakapen.a` (run `cargo build -p akapen-ffi` at the repo
// root first; the Makefile / README document this). The linker flags below pull
// it in for both the SwiftUI app and the headless harness.
import PackageDescription

let rustLibDir = "../../target/debug"

let linkRust: [LinkerSetting] = [
    .unsafeFlags(["-L", rustLibDir]),
    .linkedLibrary("akapen"),
    // Rust std on macOS pulls in these system frameworks/libs.
    .linkedFramework("CoreFoundation"),
    .linkedFramework("Security"),
    // Phase e: the wgpu Metal backend in libakapen needs these at link time.
    .linkedFramework("Metal"),
    .linkedFramework("QuartzCore"),
    .linkedFramework("Foundation"),
]

let package = Package(
    name: "Akapen",
    platforms: [.macOS(.v13)],
    targets: [
        .target(name: "AkapenUIContract"),

        // C ABI surface (hand-written header kept in sync with crates/akapen-ffi).
        .target(name: "CAkapen"),

        // Idiomatic Swift wrapper around the C ABI.
        .target(name: "AkapenKit", dependencies: ["CAkapen"]),

        // The SwiftUI editor app (the real shell). Run with `swift run AkapenApp`.
        .executableTarget(
            name: "AkapenApp",
            dependencies: ["AkapenKit", "CAkapen", "AkapenUIContract"],
            // Resources/akapen-manual-ja.html is a synced copy of
            // docs/manual/akapen-manual-ja.html (SwiftPM resources must live
            // inside the target); Resources/akapen-icon.png mirrors
            // assets/app-icon/akapen-pixel-source.png.
            resources: [
                .copy("Resources/akapen-manual-ja.html"),
                .copy("Resources/akapen-icon.png"),
            ],
            linkerSettings: linkRust
        ),

        // Headless proof harness: opens/creates an image, draws pressure-varying
        // strokes through the FFI, and writes the 3-file export. Used to verify
        // the Swift↔Rust boundary on macOS without a GUI (`swift run akapen-harness`).
        .executableTarget(
            name: "akapen-harness",
            dependencies: ["AkapenKit"],
            linkerSettings: linkRust
        ),

        .testTarget(
            name: "AkapenUIContractTests",
            dependencies: ["AkapenUIContract"]
        ),
    ]
)
