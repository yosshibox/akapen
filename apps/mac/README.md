# apps/mac

SwiftUI shell for macOS — **scaffold (implemented in M1).**

Responsibilities (thin, no business logic; spec §7.1):
- Window / palette / dialog UI.
- NSEvent tablet pen input → normalized pointer samples (pressure required) to
  the core (spec §5.1).
- ImageIO decode → RGBA bitmap into the core.
- Hosting the wgpu/Metal draw surface (CAMetalLayer-backed NSView).
- File dialogs and settings.

The Xcode project and Swift package land in M1 together with the swift-bridge /
UniFFI wrapper under `bindings/swift/`.
