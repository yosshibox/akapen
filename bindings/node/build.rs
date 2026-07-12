fn main() {
    // napi-build injects the platform-specific link flags Node needs (notably
    // the `-undefined dynamic_lookup` symbol-resolution mode on mac so the
    // .node loader can supply napi symbols at load time).
    napi_build::setup();
}
