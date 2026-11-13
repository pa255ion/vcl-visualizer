{
  description = "VCL — visual communication language + WebXR visualizer";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAll = f: nixpkgs.lib.genAttrs systems (s: f nixpkgs.legacyPackages.${s});
    in {
      devShells = forAll (pkgs: {
        default = pkgs.mkShell {
          packages = [
            pkgs.zig_0_16   # parser, CLI, stream server, wasm build
            pkgs.nodejs     # viewer unit tests (node --test)
            pkgs.android-tools # adb reverse for Quest over USB
          ];
        };
      });
    };
}
